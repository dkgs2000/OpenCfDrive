using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenCfDrive.Core.Interop;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// Implements Pillar 3: connect to the sync root, receive FETCH_DATA callbacks
/// and stream the requested byte range from rclone into the Windows filesystem
/// through CfExecute.
/// </summary>
public sealed class SyncRootConnection : IDisposable
{
    private const int StatusSuccess = 0;
    private const int StatusUnsuccessful = unchecked((int)0xC0000001);
    /// <summary>
    /// Bytes per TRANSFER_DATA call, and the most a hydration holds in memory.
    /// A multiple of 4 KiB, which every chunk but the last of a range must be.
    /// </summary>
    private const int TransferChunkSize = 4 * 1024 * 1024;

    /// <summary>
    /// Grace period after a hydration completes. Windows finishes flushing the
    /// placeholder slightly after CfExecute returns; the watcher must not treat
    /// those writes as local user edits and push a partial file upstream.
    /// </summary>
    private static readonly TimeSpan HydrationGuard = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long a deleted or moved-out item may stay in place, with no
    /// download running for it, before the delete or move counts as failed
    /// and the server copy is kept.
    /// </summary>
    private static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a deleted item's path must stay empty before the delete is
    /// final. Some programs replace a file by deleting it and then renaming
    /// another into place; that is an update, not a delete.
    /// </summary>
    private static readonly TimeSpan ReplaceGrace = TimeSpan.FromSeconds(1);

    private readonly SqliteMetadataStore _store;
    private readonly RcloneServeClient _serve;
    private readonly Action<string>? _log;
    private readonly Func<string, Task>? _deleteRemote;
    private readonly Func<string, string, RenameSource?, Task>? _renameRemote;
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<PlaceholderEntry>>>? _listDirectory;

    /// <summary>
    /// How long a folder's remote listing may take before the shell is
    /// answered from what is already local. Offline usually means rclone is
    /// up but cannot reach the server, and it keeps retrying; without a limit
    /// the shell would wait for the platform's 60 s timeout and show an error.
    /// </summary>
    private static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(10);

    // Full local paths of deletes, moves and renames the user made that have
    // not reached the server yet (count per path).
    private readonly ConcurrentDictionary<string, int> _localChangesPending =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _listingReuseWindow;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastListed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _listingGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _repopulationPending = new(StringComparer.OrdinalIgnoreCase);
    private readonly CloudPlaceholderService _placeholders = new();
    private readonly List<CF_CALLBACK> _callbackRoots = [];
    // Full local path -> hydrations in flight and when the last one began or ended.
    private readonly ConcurrentDictionary<string, (int Active, DateTimeOffset LastActivity)> _hydratingPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;
    private string _syncRootPath = string.Empty;
    private string _syncRootVolumeRelative = string.Empty;

    /// <param name="listDirectory">
    /// Answers FETCH_PLACEHOLDERS: given a sync-root-relative directory ('/'
    /// separated, empty for the root), returns the remote entries that do not
    /// have a placeholder yet.
    /// </param>
    /// <param name="listingReuseWindow">
    /// How long one remote listing answers further FETCH_PLACEHOLDERS for the
    /// same directory (default 1 s), and how long a directory stays marked
    /// fully populated after an answer before refreshes are switched back on.
    /// A re-list after the window sees the live remote again.
    /// </param>
    public SyncRootConnection(
        SqliteMetadataStore store,
        RcloneServeClient serve,
        Action<string>? log = null,
        Func<string, Task>? deleteRemote = null,
        Func<string, string, RenameSource?, Task>? renameRemote = null,
        Func<string, CancellationToken, Task<IReadOnlyList<PlaceholderEntry>>>? listDirectory = null,
        TimeSpan? listingReuseWindow = null)
    {
        _store = store;
        _serve = serve;
        _log = log;
        _deleteRemote = deleteRemote;
        _renameRemote = renameRemote;
        _listDirectory = listDirectory;
        _listingReuseWindow = listingReuseWindow ?? TimeSpan.FromSeconds(1);
    }

    public bool IsConnected => _connected;

    /// <summary>Raised after a successful hydration so callers/tests can observe progress.</summary>
    public event Action<HydrationCompleted>? Hydrated;

    /// <summary>Raised after a local delete was propagated to the remote.</summary>
    public event Action<RemoteDeleted>? Deleted;

    /// <summary>Raised after a local rename was propagated to the remote.</summary>
    public event Action<RemoteRenamed>? Renamed;

    /// <summary>
    /// Raised on the callback thread after a directory was listed from the
    /// remote and the platform received its new entries. Handlers must not
    /// block: the shell is still waiting for the enumeration to finish.
    /// </summary>
    public event Action<string>? DirectoryListed;

    /// <summary>
    /// Raised when refreshes are switched back on for a directory after an
    /// answer (see <see cref="ScheduleRepopulation"/>): from then on its next
    /// listing asks the server again. The argument is sync-root-relative.
    /// </summary>
    public event Action<string>? RefreshesResumed;

    public sealed record HydrationCompleted(string LocalPath, string RemotePath, long Offset, long Length);

    public sealed record RemoteDeleted(string LocalPath, string RemotePath);

    public sealed record RemoteRenamed(string OldLocalPath, string NewLocalPath, string OldRemotePath, string NewRemotePath);

    /// <summary>
    /// A placeholder's state just before a rename. The platform clears
    /// in-sync on every rename, so afterwards only this tells whether the
    /// item still had local changes to upload.
    /// </summary>
    public sealed record RenameSource(bool InSync, DateTime LastWriteUtc, long Length);

    public bool Connect(string syncRootPath)
    {
        // CF_CALLBACK delegates must outlive the native registration: the
        // function pointers handed to CfConnectSyncRoot stay valid only while
        // the managed delegate is rooted. Without this the GC can collect them
        // and callbacks stop being delivered (hydration then times out).
        if (_callbackRoots.Count == 0)
        {
            _callbackRoots.Add(OnFetchData);
            _callbackRoots.Add(OnNotifyDelete);
            _callbackRoots.Add(OnNotifyRename);
            _callbackRoots.Add(OnFetchPlaceholders);
        }

        // Callbacks report NormalizedPath without the volume ("\Users\..\Root").
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(syncRootPath));
        _syncRootPath = fullRoot;
        _syncRootVolumeRelative = fullRoot[(Path.GetPathRoot(fullRoot)?.Length ?? 0)..];

        var table = new[]
        {
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = Marshal.GetFunctionPointerForDelegate(_callbackRoots[0]),
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE,
                Callback = Marshal.GetFunctionPointerForDelegate(_callbackRoots[1]),
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME,
                Callback = Marshal.GetFunctionPointerForDelegate(_callbackRoots[2]),
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                Callback = Marshal.GetFunctionPointerForDelegate(_callbackRoots[3]),
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE,
                Callback = IntPtr.Zero,
            },
        };

        var hr = CfApi.CfConnectSyncRoot(
            syncRootPath,
            table,
            IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE,
            out _connectionKey);

        _connected = CfApi.Succeeded(hr);
        _log?.Invoke(_connected
            ? $"Connected sync root callbacks for '{syncRootPath}'."
            : $"CfConnectSyncRoot failed with {HResult.Describe(hr)}.");

        return _connected;
    }

    private void OnFetchData(IntPtr callbackInfoPtr, IntPtr callbackParametersPtr)
    {
        // Nothing may escape a native callback boundary.
        CF_CALLBACK_INFO info = default;
        CF_CALLBACK_FETCH_DATA_PARAMETERS fetch = default;
        var delivered = 0L;
        try
        {
            info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(callbackInfoPtr);
            fetch = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(callbackParametersPtr).FetchData;

            var remotePath = ResolveRemotePath(info);
            if (remotePath is null)
            {
                _log?.Invoke($"FETCH_DATA for unknown file id {info.FileId}; completing with failure.");
                CompleteWithFailure(info, fetch.RequiredFileOffset, fetch.RequiredLength);
                return;
            }

            _log?.Invoke(
                $"[{DateTimeOffset.Now:HH:mm:ss.fff}] FETCH_DATA '{remotePath}' offset={fetch.RequiredFileOffset} length={fetch.RequiredLength} " +
                $"optional={fetch.OptionalFileOffset}+{fetch.OptionalLength}");

            var normalizedPath = Marshal.PtrToStringUni(info.NormalizedPath) ?? string.Empty;

            // Keyed by the full path, which is what IsHydrating is asked about
            // (NormalizedPath has no drive letter).
            var guardKey = string.IsNullOrEmpty(normalizedPath) ? remotePath : ToLocalPath(normalizedPath);
            _hydratingPaths.AddOrUpdate(
                guardKey,
                _ => (1, DateTimeOffset.UtcNow),
                (_, mark) => (mark.Active + 1, DateTimeOffset.UtcNow));

            try
            {
                // Only the required range is transferred; the platform issues
                // its own callback when it needs the optional bytes.
                if (fetch.OptionalLength > 0)
                {
                    _log?.Invoke($"  optional range {fetch.OptionalFileOffset}+{fetch.OptionalLength} deferred.");
                }

                TransferRange(info, remotePath, fetch.RequiredFileOffset, fetch.RequiredLength, ref delivered);
            }
            finally
            {
                _hydratingPaths.AddOrUpdate(
                    guardKey,
                    _ => (0, DateTimeOffset.UtcNow),
                    (_, mark) => (Math.Max(0, mark.Active - 1), DateTimeOffset.UtcNow));
            }

            _store.MarkHydrated(remotePath);
            Hydrated?.Invoke(new HydrationCompleted(
                normalizedPath,
                remotePath,
                fetch.RequiredFileOffset,
                fetch.RequiredLength));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FETCH_DATA failed: {ex}");
            try
            {
                // Bytes already accepted stay; only the rest is failed.
                if (delivered < fetch.RequiredLength)
                {
                    CompleteWithFailure(
                        info,
                        fetch.RequiredFileOffset + delivered,
                        fetch.RequiredLength - delivered);
                }
            }
            catch
            {
                // ignored: never throw across the native boundary
            }
        }
    }

    /// <summary>
    /// Streams one required range into the platform a chunk at a time, so a
    /// hydration holds at most one chunk in memory whatever the file size.
    /// The framing is pinned by <c>CfExecuteContractTest</c> against the
    /// running OS:
    ///   * a range may arrive over several TRANSFER_DATA calls, each starting
    ///     on a 4 KiB boundary; only the call that ends the file may be
    ///     unaligned at its end;
    ///   * there is no completion call: a trailing zero-length TRANSFER_DATA
    ///     is rejected with ERROR_CLOUD_FILE_INVALID_REQUEST (0x8007017C).
    /// <paramref name="delivered"/> counts the bytes the platform accepted.
    /// </summary>
    private void TransferRange(
        CF_CALLBACK_INFO info,
        string remotePath,
        long offset,
        long length,
        ref long delivered)
    {
        if (length <= 0)
        {
            return;
        }

        using var source = _serve
            .OpenRangeAsync(remotePath, offset, length)
            .GetAwaiter()
            .GetResult();

        var clock = Stopwatch.StartNew();
        var executeMs = 0L;
        var chunks = 0;
        var end = offset + length;
        var chunk = new byte[(int)Math.Min(TransferChunkSize, length)];
        var pinned = GCHandle.Alloc(chunk, GCHandleType.Pinned);
        try
        {
            var position = offset;
            while (position < end)
            {
                // Chunks end on multiples of TransferChunkSize, so each one
                // after the first starts aligned whatever offset Windows asked for.
                var chunkEnd = Math.Min(end, (position / TransferChunkSize + 1) * TransferChunkSize);
                var size = (int)(chunkEnd - position);

                var filled = 0;
                while (filled < size)
                {
                    var read = source.Read(chunk, filled, size - filled);
                    if (read <= 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (filled != size)
                {
                    throw new IOException(
                        $"Short read for '{remotePath}' range {offset}+{length}: " +
                        $"got {position - offset + filled} bytes.");
                }

                var executeStart = clock.ElapsedMilliseconds;
                ExecuteTransfer(info, pinned.AddrOfPinnedObject(), position, size, StatusSuccess);
                executeMs += clock.ElapsedMilliseconds - executeStart;
                delivered += size;
                chunks++;
                position = chunkEnd;
            }

            _log?.Invoke(
                $"  transferred {length} bytes at {offset} in {clock.ElapsedMilliseconds} ms " +
                $"({chunks} chunk(s), CfExecute {executeMs} ms).");
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// Fails the undelivered part of a required range. It must name those
    /// bytes: a zero-length failure is rejected (0x8007017C) and the reading
    /// program then waits for the platform timeout (pinned by
    /// <c>CfExecuteContractTest</c>).
    /// </summary>
    private static void CompleteWithFailure(CF_CALLBACK_INFO info, long offset, long length)
    {
        ExecuteTransfer(info, IntPtr.Zero, offset, length, StatusUnsuccessful);
    }

    private static void ExecuteTransfer(CF_CALLBACK_INFO info, IntPtr buffer, long offset, long length, int status)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey,
            CorrelationVector = IntPtr.Zero,
        };

        var opParams = new CF_OPERATION_PARAMETERS
        {
            // Verified against the OS by CfExecuteContractTest: ParamSize is
            // offsetof(TransferData) + sizeof(TransferData) = 40, NOT
            // sizeof(CF_OPERATION_PARAMETERS) (48). Using 48 fails with
            // ERROR_CLOUD_FILE_INVALID_REQUEST, as does a 32-byte
            // CF_OPERATION_INFO.
            ParamSize = 8 + (uint)Marshal.SizeOf<CF_OPERATION_TRANSFER_DATA>(),
            TransferData = new CF_OPERATION_TRANSFER_DATA
            {
                Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE,
                CompletionStatus = status,
                Buffer = buffer,
                Offset = offset,
                Length = length,
            },
        };

        var hr = CfApi.CfExecute(ref opInfo, ref opParams);
        if (!CfApi.Succeeded(hr))
        {
            throw new InvalidOperationException(
                $"CfExecute(TRANSFER_DATA) failed with {HResult.Describe(hr)} " +
                $"(structSize={opInfo.StructSize}, paramSize={opParams.ParamSize}, " +
                $"connectionKey=0x{opInfo.ConnectionKey.Internal:X}, transferKey=0x{opInfo.TransferKey:X}, " +
                $"offset={offset}, length={length}, buffer=0x{buffer.ToInt64():X}).");
        }
    }

    /// <summary>
    /// Answers a directory enumeration (or a name lookup, such as creating a
    /// new file) with a live remote listing. The answer marks the directory
    /// fully populated, which the platform needs before it lets a new name be
    /// created there: left "partially populated", it re-issues the create
    /// after every answer until it gives up with ERROR_CANT_RESOLVE_FILENAME
    /// ("The name of the file cannot be resolved by the system"). Shortly
    /// afterwards on-demand population is switched back on (see
    /// <see cref="ScheduleRepopulation"/>), so the next listing asks the
    /// server again and server-side changes are still picked up.
    /// </summary>
    private void OnFetchPlaceholders(IntPtr callbackInfoPtr, IntPtr callbackParametersPtr)
    {
        // Nothing may escape a native callback boundary, and the callback must
        // always be completed: an unanswered request blocks the shell for the
        // 60 s cfapi timeout.
        CF_CALLBACK_INFO info = default;
        try
        {
            info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(callbackInfoPtr);
            var normalized = Marshal.PtrToStringUni(info.NormalizedPath) ?? string.Empty;
            var directory = ResolveDirectory(info, normalized);

            if (directory is null || _listDirectory is null)
            {
                _log?.Invoke($"FETCH_PLACEHOLDERS for unknown directory '{normalized}'; completing with failure.");
                TransferPlaceholders(info, [], StatusUnsuccessful);
                return;
            }

            var clock = Stopwatch.StartNew();
            var (entries, listed) = ListCoalesced(directory);
            TransferPlaceholders(info, entries, StatusSuccess);
            ScheduleRepopulation(directory);
            if (listed)
            {
                _log?.Invoke(
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] FETCH_PLACEHOLDERS '{directory}' -> " +
                    $"{entries.Count} new in {clock.ElapsedMilliseconds} ms" +
                    (entries.Count > 0 ? $": {string.Join(", ", entries.Select(e => e.RelativeName))}" : "."));
                DirectoryListed?.Invoke(directory);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"FETCH_PLACEHOLDERS failed: {ex.Message}");
            try
            {
                TransferPlaceholders(info, [], StatusUnsuccessful);
            }
            catch
            {
                // ignored: never throw across the native boundary
            }
        }
    }

    /// <summary>
    /// At most one remote listing per directory per reuse window. Callbacks
    /// inside the window return nothing new: whatever the last listing found
    /// was already transferred. A listing that fails or takes longer than
    /// ListingTimeout (remote offline) also returns nothing, so the shell
    /// shows the local entries (empty if there are none) instead of an error.
    /// </summary>
    private (IReadOnlyList<PlaceholderEntry> Entries, bool Listed) ListCoalesced(string directory)
    {
        var gate = _listingGates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (_lastListed.TryGetValue(directory, out var at) &&
                DateTimeOffset.UtcNow - at < _listingReuseWindow)
            {
                return ([], false);
            }

            using var timeout = new CancellationTokenSource(ListingTimeout);
            try
            {
                return (_listDirectory!(directory, timeout.Token).GetAwaiter().GetResult(), true);
            }
            catch (Exception ex)
            {
                var reason = timeout.IsCancellationRequested ? $"no answer within {ListingTimeout.TotalSeconds:0} s" : ex.Message;
                _log?.Invoke($"FETCH_PLACEHOLDERS '{directory}': remote listing failed, showing local entries: {reason}");
                return ([], false);
            }
            finally
            {
                _lastListed[directory] = DateTimeOffset.UtcNow;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Switches on-demand population back on for a directory one reuse window
    /// after it was answered, so the next listing goes to the server again.
    /// The pending marker is cleared before the switch, so an answer that
    /// lands while it happens schedules its own switch and the directory can
    /// never stay marked fully populated.
    /// </summary>
    private void ScheduleRepopulation(string directory)
    {
        if (!_repopulationPending.TryAdd(directory, 0))
        {
            return;
        }

        var path = directory.Length == 0
            ? _syncRootPath
            : Path.Combine(_syncRootPath, directory.Replace('/', Path.DirectorySeparatorChar));

        _ = Task.Run(async () =>
        {
            await Task.Delay(_listingReuseWindow).ConfigureAwait(false);
            _repopulationPending.TryRemove(directory, out _);

            // Retried: the switch needs a short exclusive handle, which a
            // program holding the folder open can briefly prevent.
            for (var attempt = 1; attempt <= 10; attempt++)
            {
                if (!_connected || !Directory.Exists(path))
                {
                    return;
                }

                if (_placeholders.EnableOnDemandPopulation(path))
                {
                    RefreshesResumed?.Invoke(directory);
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(attempt)).ConfigureAwait(false);
            }

            _log?.Invoke($"Could not switch refreshes back on for '{directory}'; it shows server changes again after a restart.");
        });
    }

    /// <summary>Sync-root-relative directory ('/' separated, empty for the root).</summary>
    private string? ResolveDirectory(CF_CALLBACK_INFO info, string normalized)
    {
        if (info.FileId != 0 && info.FileId == info.SyncRootFileId)
        {
            return string.Empty;
        }

        // Directory placeholders carry their remote path with a trailing '/'.
        if (info.FileIdentity != IntPtr.Zero && info.FileIdentityLength > 0)
        {
            var identity = Marshal.PtrToStringUTF8(info.FileIdentity, (int)info.FileIdentityLength);
            if (!string.IsNullOrEmpty(identity) && identity.EndsWith('/'))
            {
                return identity.TrimEnd('/');
            }
        }

        if (_syncRootVolumeRelative.Length > 0 &&
            normalized.StartsWith(_syncRootVolumeRelative, StringComparison.OrdinalIgnoreCase))
        {
            var rest = normalized[_syncRootVolumeRelative.Length..];
            if (rest.Length == 0 || rest[0] == Path.DirectorySeparatorChar)
            {
                return rest.Trim(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
            }
        }

        return null;
    }

    private void TransferPlaceholders(CF_CALLBACK_INFO info, IReadOnlyList<PlaceholderEntry> entries, int status)
    {
        const int HRESULT_ALREADY_EXISTS = unchecked((int)0x800700B7);

        var stride = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>();
        var array = entries.Count == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(stride * entries.Count);
        var identities = new List<IntPtr>(entries.Count);
        var marshalled = 0;

        try
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var identity = CloudPlaceholderService.BuildFileIdentity(entries[i].FileIdentity);
                var identityPtr = Marshal.AllocHGlobal(identity.Length == 0 ? 1 : identity.Length);
                Marshal.Copy(identity, 0, identityPtr, identity.Length);
                identities.Add(identityPtr);

                var createInfo = CloudPlaceholderService.BuildCreateInfo(entries[i], identityPtr, (uint)identity.Length);
                Marshal.StructureToPtr(createInfo, array + (i * stride), fDeleteOld: false);
                marshalled++;
            }

            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = info.ConnectionKey,
                TransferKey = info.TransferKey,
                CorrelationVector = IntPtr.Zero,
            };

            var opParams = new CF_OPERATION_PARAMETERS_PLACEHOLDERS
            {
                // Same framing rule as TRANSFER_DATA: offsetof(member) + sizeof(member).
                ParamSize = 8 + (uint)Marshal.SizeOf<CF_OPERATION_TRANSFER_PLACEHOLDERS>(),
                TransferPlaceholders = new CF_OPERATION_TRANSFER_PLACEHOLDERS
                {
                    // A successful answer marks the directory fully populated
                    // (see OnFetchPlaceholders); ScheduleRepopulation turns
                    // refreshes back on afterwards.
                    Flags = status == StatusSuccess
                        ? CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION
                        : CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE,
                    CompletionStatus = status,
                    PlaceholderTotalCount = entries.Count,
                    PlaceholderArray = array,
                    PlaceholderCount = (uint)entries.Count,
                },
            };

            var hr = CfApi.CfExecute(ref opInfo, ref opParams);

            // As with CfCreatePlaceholders, ERROR_ALREADY_EXISTS only means
            // some entry was already on disk; the rest were still created.
            if (!CfApi.Succeeded(hr) && hr != HRESULT_ALREADY_EXISTS)
            {
                throw new InvalidOperationException(
                    $"CfExecute(TRANSFER_PLACEHOLDERS) failed with {HResult.Describe(hr)} " +
                    $"({opParams.TransferPlaceholders.EntriesProcessed}/{entries.Count} processed).");
            }

            for (var i = 0; i < marshalled; i++)
            {
                var result = Marshal.PtrToStructure<CF_PLACEHOLDER_CREATE_INFO>(array + (i * stride)).Result;
                if (!CfApi.Succeeded(result) && result != HRESULT_ALREADY_EXISTS)
                {
                    _log?.Invoke($"  placeholder error: {entries[i].RelativeName}: {HResult.Describe(result)}");
                }
            }
        }
        finally
        {
            for (var i = 0; i < marshalled; i++)
            {
                Marshal.DestroyStructure<CF_PLACEHOLDER_CREATE_INFO>(array + (i * stride));
            }

            if (array != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(array);
            }

            foreach (var ptr in identities)
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
    }

    /// <summary>
    /// True while this process is writing hydration bytes for the full local
    /// path, however long that takes, plus a short grace period after. The
    /// upload watcher must ignore these writes.
    /// </summary>
    public bool IsHydrating(string localPath)
    {
        if (!_hydratingPaths.TryGetValue(localPath, out var mark))
        {
            return false;
        }

        if (mark.Active > 0 || DateTimeOffset.UtcNow - mark.LastActivity <= HydrationGuard)
        {
            return true;
        }

        // Only drops the entry if no hydration began since it was read.
        _hydratingPaths.TryRemove(new KeyValuePair<string, (int, DateTimeOffset)>(localPath, mark));
        return false;
    }

    /// <summary>IsHydrating for the path itself or anything under it.</summary>
    private bool IsHydratingUnder(string localPath)
    {
        var prefix = localPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return IsHydrating(localPath) || _hydratingPaths.Keys.Any(path =>
            path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && IsHydrating(path));
    }

    private string? ResolveRemotePath(CF_CALLBACK_INFO info)
    {
        if (info.FileIdentity != IntPtr.Zero && info.FileIdentityLength > 0)
        {
            var identity = Marshal.PtrToStringUTF8(info.FileIdentity, (int)info.FileIdentityLength);
            if (!string.IsNullOrEmpty(identity))
            {
                var byIdentity = _store.GetByFileIdentity(identity);
                if (byIdentity is not null)
                {
                    return byIdentity.RemotePath;
                }

                // The identity blob is itself the remote-relative path, so it
                // remains usable even if the DB row has not been written yet.
                return identity;
            }
        }

        var byFileId = _store.GetByFileId(info.FileId);
        if (byFileId is not null)
        {
            return byFileId.RemotePath;
        }

        var normalized = Marshal.PtrToStringUni(info.NormalizedPath);
        if (!string.IsNullOrEmpty(normalized))
        {
            var byPath = _store.GetByLocalPath(ToLocalPath(normalized));
            if (byPath is not null)
            {
                return byPath.RemotePath;
            }
        }

        return null;
    }

    /// <summary>
    /// NOTIFY_DELETE and NOTIFY_RENAME arrive *before* the operation, and the
    /// platform holds the delete/rename until the provider acknowledges it
    /// (ACK_DELETE / ACK_RENAME). Without the acknowledgement the user's
    /// delete or rename simply hangs. Both are always allowed; the remote
    /// side is updated afterwards, off the callback thread.
    /// </summary>
    private static void Acknowledge(CF_CALLBACK_INFO info, CF_OPERATION_TYPE type)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = type,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey,
            CorrelationVector = IntPtr.Zero,
        };

        var opParams = new CF_OPERATION_PARAMETERS_ACK
        {
            // offsetof(member) + sizeof(member) = 8 + 8.
            ParamSize = 16,
            Flags = 0,
            CompletionStatus = StatusSuccess,
        };

        var hr = CfApi.CfExecute(ref opInfo, ref opParams);
        if (!CfApi.Succeeded(hr))
        {
            throw new InvalidOperationException($"CfExecute({type}) failed with {HResult.Describe(hr)}.");
        }
    }

    /// <summary>Full local path for a volume-relative path from a callback.</summary>
    private string ToLocalPath(string normalized)
    {
        if (normalized.Length == 0 || normalized.StartsWith(@"\\", StringComparison.Ordinal) || Path.IsPathFullyQualified(normalized))
        {
            return normalized;
        }

        var volume = Path.GetPathRoot(_syncRootPath)?.TrimEnd(Path.DirectorySeparatorChar) ?? string.Empty;
        return volume + normalized;
    }

    private void OnNotifyDelete(IntPtr callbackInfoPtr, IntPtr callbackParametersPtr)
    {
        CF_CALLBACK_INFO info = default;
        string? pending = null;
        try
        {
            info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(callbackInfoPtr);
            var parameters = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(callbackParametersPtr);
            var normalized = Marshal.PtrToStringUni(info.NormalizedPath) ?? string.Empty;
            var undelete = (parameters.DeleteFlags & CF_CALLBACK_DELETE_FLAGS.CF_CALLBACK_DELETE_FLAG_IS_UNDELETE) != 0;

            // Marked before the acknowledgement lets the delete happen: until
            // it reaches the server, a listing must not hand the item out again.
            if (!undelete)
            {
                pending = ToLocalPath(normalized);
                MarkLocalChange(pending);
            }

            Acknowledge(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE);

            if (undelete)
            {
                _log?.Invoke($"NOTIFY_DELETE (undelete ignored) '{normalized}'");
                return;
            }

            var isDirectory = (parameters.DeleteFlags & CF_CALLBACK_DELETE_FLAGS.CF_CALLBACK_DELETE_FLAG_IS_DIRECTORY) != 0;
            var remotePath = ResolveRemotePath(info) ?? RelativeRemotePath(normalized, isDirectory);
            if (remotePath is null)
            {
                _log?.Invoke($"NOTIFY_DELETE outside the sync root '{normalized}'; nothing to propagate.");
                return;
            }

            _log?.Invoke($"NOTIFY_DELETE '{remotePath}' -> deleting remote object once it is gone.");
            PropagateDelete(normalized, remotePath, pending!, info.FileId);
            pending = null; // PropagateDelete clears the mark when it is done
        }
        catch (Exception ex)
        {
            _log?.Invoke($"NOTIFY_DELETE handler failed: {ex.Message}");
        }
        finally
        {
            if (pending is not null)
            {
                UnmarkLocalChange(pending);
            }
        }
    }

    private void MarkLocalChange(string localPath) =>
        _localChangesPending.AddOrUpdate(localPath, 1, (_, count) => count + 1);

    private void UnmarkLocalChange(string localPath)
    {
        while (_localChangesPending.TryGetValue(localPath, out var count))
        {
            var done = count <= 1
                ? _localChangesPending.TryRemove(new KeyValuePair<string, int>(localPath, count))
                : _localChangesPending.TryUpdate(localPath, count - 1, count);
            if (done)
            {
                return;
            }
        }
    }

    /// <summary>
    /// True while a delete, move or rename the user made of the item at
    /// <paramref name="localPath"/>, or of a folder above it, has not reached
    /// the server yet. A listing must not hand such an item out again: the
    /// server still has it, but only because it has not been told yet.
    /// </summary>
    public bool IsLocalChangePending(string localPath)
    {
        foreach (var path in _localChangesPending.Keys)
        {
            if (string.Equals(path, localPath, StringComparison.OrdinalIgnoreCase) ||
                localPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deletes the server copy once the item has really gone. Both
    /// notifications arrive before the operation: a move out of the drive is
    /// announced before Windows downloads the item's data to take along, and
    /// a delete may be part of replacing the file (an editor saving through a
    /// temp file), or may not happen at all.
    /// </summary>
    /// <param name="fullPath">The item's full local path.</param>
    /// <param name="fileId">Its NTFS file id, to tell it from a replacement.</param>
    private void PropagateDelete(string localPath, string remotePath, string fullPath, long fileId)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                switch (await WaitUntilGoneAsync(fullPath, fileId).ConfigureAwait(false))
                {
                    case ItemFate.Replaced:
                        _log?.Invoke($"'{remotePath}' was replaced by a new item; its upload updates the server copy.");
                        return;
                    case ItemFate.Stayed:
                        _log?.Invoke($"'{remotePath}' never left the drive; the server copy is kept.");
                        return;
                }

                if (_deleteRemote is not null)
                {
                    await _deleteRemote(remotePath).ConfigureAwait(false);
                }

                Deleted?.Invoke(new RemoteDeleted(localPath, remotePath));
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Delete propagation failed for '{remotePath}': {ex.Message}");
            }
            finally
            {
                UnmarkLocalChange(fullPath);
            }
        });
    }

    private void OnNotifyRename(IntPtr callbackInfoPtr, IntPtr callbackParametersPtr)
    {
        CF_CALLBACK_INFO info = default;
        string? pending = null;
        try
        {
            info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(callbackInfoPtr);
            var parameters = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(callbackParametersPtr);
            var sourceLocal = Marshal.PtrToStringUni(info.NormalizedPath) ?? string.Empty;
            var sourceFull = ToLocalPath(sourceLocal);
            var targetLocal = parameters.RenameTargetPath == IntPtr.Zero
                ? string.Empty
                : ToLocalPath(Marshal.PtrToStringUni(parameters.RenameTargetPath) ?? string.Empty);

            // Both read or set before the acknowledgement lets the rename
            // happen: the item's state, and the mark that keeps listings from
            // handing it out again under its old name meanwhile.
            var source = SnapshotRenameSource(sourceFull);
            pending = sourceFull;
            MarkLocalChange(pending);
            Acknowledge(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME);

            var isDirectory = (parameters.RenameFlags & CF_CALLBACK_RENAME_FLAGS.CF_CALLBACK_RENAME_FLAG_IS_DIRECTORY) != 0;
            var remotePath = ResolveRemotePath(info) ?? RelativeRemotePath(sourceLocal, isDirectory);
            if (remotePath is null)
            {
                _log?.Invoke($"NOTIFY_RENAME from outside the sync root '{sourceLocal}'; nothing to propagate.");
                return;
            }

            // Moved out of the drive (the Recycle Bin, another folder): for
            // the server that is a delete, not a move to some outside path.
            if ((parameters.RenameFlags & CF_CALLBACK_RENAME_FLAGS.CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE) == 0 ||
                string.IsNullOrEmpty(targetLocal))
            {
                _log?.Invoke($"NOTIFY_RENAME '{remotePath}' moved out of the sync root -> deleting remote object once it has left.");
                PropagateDelete(sourceLocal, remotePath, sourceFull, info.FileId);
                pending = null; // PropagateDelete clears the mark when it is done
                return;
            }

            _log?.Invoke($"NOTIFY_RENAME '{remotePath}' -> '{targetLocal}'.");

            var renaming = pending;
            pending = null; // cleared below once the rename has reached the server
            _ = Task.Run(async () =>
            {
                try
                {
                    if (_renameRemote is not null)
                    {
                        await _renameRemote(remotePath, targetLocal, source).ConfigureAwait(false);
                    }

                    Renamed?.Invoke(new RemoteRenamed(sourceLocal, targetLocal, remotePath, remotePath));
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"NOTIFY_RENAME propagation failed for '{remotePath}': {ex.Message}");
                }
                finally
                {
                    UnmarkLocalChange(renaming);
                }
            });
        }
        catch (Exception ex)
        {
            _log?.Invoke($"NOTIFY_RENAME handler failed: {ex.Message}");
        }
        finally
        {
            if (pending is not null)
            {
                UnmarkLocalChange(pending);
            }
        }
    }

    private enum ItemFate { Gone, Replaced, Stayed }

    /// <summary>
    /// Waits until the item with <paramref name="fileId"/> is no longer at
    /// <paramref name="fullPath"/>: gone (the path stayed empty for
    /// ReplaceGrace), or replaced by a different item. Stayed means it is
    /// still there LeaveTimeout after its last download (the operation
    /// failed or was cancelled).
    /// </summary>
    private async Task<ItemFate> WaitUntilGoneAsync(string fullPath, long fileId)
    {
        var deadline = DateTimeOffset.UtcNow + LeaveTimeout;
        DateTimeOffset? emptySince = null;
        while (true)
        {
            var state = _placeholders.QueryState(fullPath);
            if (!state.Exists)
            {
                emptySince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - emptySince >= ReplaceGrace)
                {
                    return ItemFate.Gone;
                }

                await Task.Delay(100).ConfigureAwait(false);
                continue;
            }

            if (fileId != 0 && state.FileId != 0 && state.FileId != fileId)
            {
                return ItemFate.Replaced;
            }

            emptySince = null;
            if (IsHydratingUnder(fullPath))
            {
                deadline = DateTimeOffset.UtcNow + LeaveTimeout;
            }
            else if (DateTimeOffset.UtcNow > deadline)
            {
                return ItemFate.Stayed;
            }

            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    /// <summary>Metadata only: reading it never downloads the item.</summary>
    private RenameSource? SnapshotRenameSource(string localPath)
    {
        try
        {
            var state = _placeholders.QueryState(localPath);
            if (!state.IsPlaceholder)
            {
                return null;
            }

            return new RenameSource(
                state.InSync,
                File.GetLastWriteTimeUtc(localPath),
                File.Exists(localPath) ? new FileInfo(localPath).Length : 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The remote path for an item under the sync root that has no tracked
    /// identity, derived from where it is (directories end in '/'), or null
    /// for a path outside the sync root.
    /// </summary>
    private string? RelativeRemotePath(string normalized, bool isDirectory)
    {
        if (_syncRootVolumeRelative.Length == 0 ||
            !normalized.StartsWith(_syncRootVolumeRelative + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var relative = normalized[(_syncRootVolumeRelative.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
        return isDirectory ? relative + "/" : relative;
    }

    public void Disconnect()
    {
        if (!_connected)
        {
            return;
        }

        CfApi.CfDisconnectSyncRoot(_connectionKey);
        _connected = false;
        _connectionKey = default;
    }

    public void Dispose() => Disconnect();
}
