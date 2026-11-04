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

    private readonly SqliteMetadataStore _store;
    private readonly RcloneServeClient _serve;
    private readonly Action<string>? _log;
    private readonly LocalDelete? _localDelete;
    private readonly LocalRename? _localRename;
    private readonly ListDirectory? _listDirectory;
    private readonly PrepareDownload? _prepareDownload;
    private readonly ServerConnection? _server;

    /// <summary>
    /// How long a folder's remote listing may take before the shell is
    /// answered from what is already local. Offline usually means rclone is
    /// up but cannot reach the server, and it keeps retrying; without a limit
    /// the shell would wait for the platform's 60 s timeout and show an error.
    /// </summary>
    internal static readonly TimeSpan ListingTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A listing answered without the server this slowly is logged.</summary>
    private static readonly TimeSpan SlowAnswer = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _listingReuseWindow;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastListed = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _listedOk = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _listingGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly CloudPlaceholderService _placeholders = new();
    private readonly List<CF_CALLBACK> _callbackRoots = [];
    // Full local path -> downloads of it under way; gone at none.
    private readonly ConcurrentDictionary<string, int> _hydratingPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;
    private string _syncRootPath = string.Empty;
    private string _syncRootVolumeRelative = string.Empty;

    /// <summary>A local delete or move out of the drive, reported on the callback thread.</summary>
    /// <param name="remotePath">The item's server path; folders end in '/'.</param>
    /// <param name="fileId">Its NTFS file id, to tell it from what may replace it.</param>
    public delegate void LocalDelete(string localPath, string remotePath, long fileId, bool isDirectory);

    /// <summary>A local rename or move within the drive, reported on the callback thread.</summary>
    public delegate void LocalRename(string oldLocalPath, string newLocalPath, string remotePath, RenameSource? source);

    /// <summary>
    /// Answers FETCH_PLACEHOLDERS: the server entries of one folder that have
    /// no placeholder yet, or null when it is not listed now (it is then
    /// listed when it is next opened).
    /// </summary>
    /// <param name="remoteDirectory">The server path its placeholder names ('/' separated, empty for the root).</param>
    /// <param name="localDirectory">
    /// Where it is on disk now: under another name than its server path while
    /// a rename made here has not reached the server.
    /// </param>
    public delegate Task<IReadOnlyList<PlaceholderEntry>?> ListDirectory(
        string remoteDirectory, string localDirectory, CancellationToken cancellationToken);

    /// <summary>
    /// Asked before a file is downloaded (FETCH_DATA): where the server has it
    /// now, or null when it cannot be read now (the download then fails, and
    /// opening the file again tries again).
    /// </summary>
    /// <param name="remotePath">The server path its placeholder names.</param>
    public delegate DownloadSource? PrepareDownload(string remotePath);

    /// <summary>
    /// Where a download reads from. <paramref name="Hold"/>, when set, keeps the
    /// file from being moved on the server until the read has begun, and is
    /// disposed then.
    /// </summary>
    public sealed record DownloadSource(string RemotePath, IDisposable? Hold);

    /// <param name="localDelete">Records a delete for the next sync run; called before it is let happen.</param>
    /// <param name="localRename">Records a rename for the next sync run; called before it is let happen.</param>
    /// <param name="listDirectory">Answers FETCH_PLACEHOLDERS (see <see cref="ListDirectory"/>).</param>
    /// <param name="prepareDownload">Asked before each download (see <see cref="PrepareDownload"/>); without it, the placeholder's path is read.</param>
    /// <param name="listingReuseWindow">
    /// How long one remote listing answers further FETCH_PLACEHOLDERS for the
    /// same directory (default 1 s): requests that were already waiting when
    /// it was answered get nothing new.
    /// </param>
    /// <param name="server">
    /// Whether the server answers. While it does not, listings show what is
    /// local and downloads fail at once instead of waiting on the network.
    /// </param>
    public SyncRootConnection(
        SqliteMetadataStore store,
        RcloneServeClient serve,
        Action<string>? log = null,
        LocalDelete? localDelete = null,
        LocalRename? localRename = null,
        ListDirectory? listDirectory = null,
        TimeSpan? listingReuseWindow = null,
        ServerConnection? server = null,
        PrepareDownload? prepareDownload = null)
    {
        _store = store;
        _serve = serve;
        _log = log;
        _localDelete = localDelete;
        _localRename = localRename;
        _listDirectory = listDirectory;
        _prepareDownload = prepareDownload;
        _listingReuseWindow = listingReuseWindow ?? TimeSpan.FromSeconds(1);
        _server = server;
    }

    public bool IsConnected => _connected;

    /// <summary>Raised after a successful hydration so callers/tests can observe progress.</summary>
    public event Action<HydrationCompleted>? Hydrated;

    /// <summary>
    /// Raised with the file's full local path once a download of it has
    /// ended, sent or failed, and no longer counts in <see cref="IsHydrating"/>.
    /// What waited for it can go now.
    /// </summary>
    public event Action<string>? DownloadEnded;

    /// <summary>
    /// Raised on the callback thread after a directory was listed from the
    /// remote and the platform received its new entries. Handlers must not
    /// block: the shell is still waiting for the enumeration to finish.
    /// </summary>
    public event Action<string>? DirectoryListed;

    public sealed record HydrationCompleted(string LocalPath, string RemotePath, long Offset, long Length);

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

        // Process info: the log says which program asked for a listing. The
        // full path: without it a download (FETCH_DATA) comes with none, and
        // could not be told by the file it is for (IsHydrating).
        var hr = CfApi.CfConnectSyncRoot(
            syncRootPath,
            table,
            IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
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
        DownloadSource? download = null;
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

            // Offline, an online-only file cannot be opened, and fails at once
            // instead of waiting on the network (as in Nextcloud).
            if (_server is { IsOnline: false })
            {
                _log?.Invoke($"[{DateTimeOffset.Now:HH:mm:ss.fff}] Offline: '{remotePath}' is not on this PC and cannot be downloaded until the server answers.");
                _server.CheckNow();
                CompleteWithFailure(info, fetch.RequiredFileOffset, fetch.RequiredLength);
                return;
            }

            var normalizedPath = Marshal.PtrToStringUni(info.NormalizedPath) ?? string.Empty;

            // Renamed or moved here, or inside a folder that was: where the
            // server has it depends on whether that move has reached it.
            if (_prepareDownload is not null)
            {
                download = _prepareDownload(remotePath);
                if (download is null)
                {
                    CompleteWithFailure(info, fetch.RequiredFileOffset, fetch.RequiredLength);
                    return;
                }

                remotePath = download.RemotePath;
            }

            _log?.Invoke(
                $"[{DateTimeOffset.Now:HH:mm:ss.fff}] FETCH_DATA '{remotePath}' for {RequesterOf(info)} offset={fetch.RequiredFileOffset} length={fetch.RequiredLength} " +
                $"optional={fetch.OptionalFileOffset}+{fetch.OptionalLength}");

            // Keyed by the full path, which is what IsHydrating is asked about
            // (NormalizedPath has no drive letter).
            var guardKey = string.IsNullOrEmpty(normalizedPath) ? remotePath : ToLocalPath(normalizedPath);
            _hydratingPaths.AddOrUpdate(guardKey, 1, (_, active) => active + 1);

            try
            {
                // Only the required range is transferred; the platform issues
                // its own callback when it needs the optional bytes.
                if (fetch.OptionalLength > 0)
                {
                    _log?.Invoke($"  optional range {fetch.OptionalFileOffset}+{fetch.OptionalLength} deferred.");
                }

                TransferRange(info, remotePath, fetch.RequiredFileOffset, fetch.RequiredLength, ref delivered, download?.Hold);
            }
            finally
            {
                EndDownload(guardKey);
            }

            _server?.Succeeded();
            _store.MarkHydrated(remotePath);
            Hydrated?.Invoke(new HydrationCompleted(
                normalizedPath,
                remotePath,
                fetch.RequiredFileOffset,
                fetch.RequiredLength));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"[{DateTimeOffset.Now:HH:mm:ss.fff}] FETCH_DATA failed ({DescribeFile(info)}): {ex}");
            if (ServerConnection.IsUnreachable(ex))
            {
                _server?.Failed(ex.Message);
            }

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
        finally
        {
            download?.Hold?.Dispose();
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
    /// <paramref name="hold"/> is let go once the server is sending.
    /// </summary>
    private void TransferRange(
        CF_CALLBACK_INFO info,
        string remotePath,
        long offset,
        long length,
        ref long delivered,
        IDisposable? hold = null)
    {
        if (length <= 0)
        {
            return;
        }

        using var source = _serve
            .OpenRangeAsync(remotePath, offset, length)
            .GetAwaiter()
            .GetResult();
        hold?.Dispose();

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
    /// Answers the first enumeration of a directory (or a name lookup in it,
    /// such as creating a new file) with a live remote listing. The answer
    /// marks the directory fully populated, and it stays so, as Nextcloud's
    /// and OneDrive's folders do: Windows lists it from disk from then on and
    /// shows it as available rather than online-only, and the background sync
    /// (see <see cref="SyncScheduler"/>) keeps it up to date with the server.
    /// The platform also needs a populated directory before it lets a new
    /// name be created there: left "partially populated", it re-issues the
    /// create after every answer until it gives up with
    /// ERROR_CANT_RESOLVE_FILENAME.
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
                TransferPlaceholders(info, [], StatusUnsuccessful, populated: false);
                return;
            }

            // Who asked, and for which names ("*" for a listing, a single name
            // for a lookup).
            var pattern = Marshal.PtrToStringUni(
                Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(callbackParametersPtr).FetchPlaceholdersPattern);
            var requester = $"{RequesterOf(info)}, pattern '{pattern}'";

            var clock = Stopwatch.StartNew();
            var localDirectory = normalized.Length == 0 ? _syncRootPath : ToLocalPath(normalized);
            var (entries, listed, notListed) = ListCoalesced(directory, localDirectory, requester);

            // Offline, a folder never listed shows what is local and stays
            // unlisted, so opening it once the server answers lists it.
            var populated = listed || IsListedRecently(directory);
            TransferPlaceholders(info, entries, StatusSuccess, populated);
            // Only a slow one: Windows asks again at once for a folder left
            // unlisted, so quick ones come by the hundred.
            if (notListed is not null && clock.Elapsed >= SlowAnswer)
            {
                _log?.Invoke(
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] FETCH_PLACEHOLDERS '{directory}' for {requester}: not listed ({notListed}), " +
                    $"answered in {clock.ElapsedMilliseconds} ms{(populated ? "" : ", still to be listed")}.");
            }

            if (listed)
            {
                _log?.Invoke(
                    $"[{DateTimeOffset.Now:HH:mm:ss.fff}] FETCH_PLACEHOLDERS '{directory}' for {requester} -> " +
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
                TransferPlaceholders(info, [], StatusUnsuccessful, populated: false);
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
    /// ListingTimeout also returns nothing, so the shell shows the local
    /// entries (empty if there are none) instead of an error; if the server
    /// could not be reached, the drive goes offline, and until it answers
    /// again every listing shows what is local at once (see ServerConnection).
    /// One the lister declines also returns nothing, and leaves the next
    /// request free to list.
    /// </summary>
    /// <returns>Also why nothing was listed, when that was not a failure (failures are logged here).</returns>
    private (IReadOnlyList<PlaceholderEntry> Entries, bool Listed, string? NotListed) ListCoalesced(string directory, string localDirectory, string requester)
    {
        if (_server is { IsOnline: false })
        {
            _server.CheckNow();
            return ([], false, "offline");
        }

        var gate = _listingGates.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (_lastListed.TryGetValue(directory, out var at) &&
                DateTimeOffset.UtcNow - at < _listingReuseWindow)
            {
                return ([], false, $"tried {(DateTimeOffset.UtcNow - at).TotalMilliseconds:0} ms ago");
            }

            // Gone offline while this one waited for the gate.
            if (_server is { IsOnline: false })
            {
                return ([], false, "offline");
            }

            using var timeout = new CancellationTokenSource(ListingTimeout);
            var declined = false;
            try
            {
                var entries = _listDirectory!(directory, localDirectory, timeout.Token).GetAwaiter().GetResult();
                if (entries is null)
                {
                    declined = true;
                    return ([], false, "declined");
                }

                _server?.Succeeded();
                _listedOk[directory] = DateTimeOffset.UtcNow;
                return (entries, true, null);
            }
            catch (Exception ex)
            {
                var reason = timeout.IsCancellationRequested ? $"no answer within {ListingTimeout.TotalSeconds:0} s" : ex.Message;
                _log?.Invoke($"[{DateTimeOffset.Now:HH:mm:ss.fff}] FETCH_PLACEHOLDERS '{directory}' for {requester}: remote listing failed, showing local entries: {reason}");
                if (timeout.IsCancellationRequested || ServerConnection.IsUnreachable(ex))
                {
                    _server?.Failed(reason);
                }

                return ([], false, null);
            }
            finally
            {
                if (!declined)
                {
                    _lastListed[directory] = DateTimeOffset.UtcNow;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Whether the directory's last listing succeeded within the reuse window:
    /// a request that waited for it is answered from it, so the directory
    /// stays marked fully populated.
    /// </summary>
    private bool IsListedRecently(string directory) =>
        _listedOk.TryGetValue(directory, out var at) && DateTimeOffset.UtcNow - at < _listingReuseWindow;

    /// <summary>The program whose I/O raised a callback, as "name (pid)".</summary>
    /// <summary>The file a failed callback was for, as it is on disk now, read without opening it.</summary>
    private string DescribeFile(CF_CALLBACK_INFO info)
    {
        try
        {
            var normalized = Marshal.PtrToStringUni(info.NormalizedPath);
            if (string.IsNullOrEmpty(normalized))
            {
                return "no path";
            }

            var localPath = ToLocalPath(normalized);
            if (!File.Exists(localPath))
            {
                return $"'{localPath}' is not on disk";
            }

            var state = _placeholders.QueryState(localPath);
            return $"file id {info.FileId}, on disk id {state.FileId}, placeholder={state.IsPlaceholder}, " +
                   $"in sync={state.InSync}, partial={state.Partial}, attributes=0x{(int)File.GetAttributes(localPath):X}";
        }
        catch (Exception ex)
        {
            return $"state unknown: {ex.Message}";
        }
    }

    private static string RequesterOf(CF_CALLBACK_INFO info)
    {
        if (info.ProcessInfo == IntPtr.Zero)
        {
            return "an unknown program";
        }

        var process = Marshal.PtrToStructure<CF_PROCESS_INFO>(info.ProcessInfo);
        var image = process.ImagePath == IntPtr.Zero ? null : Marshal.PtrToStringUni(process.ImagePath);
        return $"{(string.IsNullOrEmpty(image) ? "pid" : Path.GetFileName(image))} ({process.ProcessId})";
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

    /// <param name="populated">Marks the directory fully populated (see OnFetchPlaceholders).</param>
    private void TransferPlaceholders(CF_CALLBACK_INFO info, IReadOnlyList<PlaceholderEntry> entries, int status, bool populated)
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
                    // A listed directory is marked fully populated for good
                    // (see OnFetchPlaceholders).
                    Flags = populated && status == StatusSuccess
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
    /// True while a download of the file at this full local path is under
    /// way, however long it takes: a change to it waits until the download
    /// ends (<see cref="DownloadEnded"/>). What the download writes is no
    /// edit either way: it leaves the file in sync, which is what tells an
    /// edit.
    /// </summary>
    public bool IsHydrating(string localPath) =>
        _hydratingPaths.TryGetValue(localPath, out var active) && active > 0;

    /// <summary>One download of the file has ended, sent or failed.</summary>
    private void EndDownload(string guardKey)
    {
        while (_hydratingPaths.TryGetValue(guardKey, out var active))
        {
            var done = active <= 1
                ? _hydratingPaths.TryRemove(new KeyValuePair<string, int>(guardKey, active))
                : _hydratingPaths.TryUpdate(guardKey, active - 1, active);
            if (done)
            {
                break;
            }
        }

        DownloadEnded?.Invoke(guardKey);
    }

    /// <summary>A download is under way for the item or anything below it.</summary>
    public bool IsHydratingUnder(string localPath)
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
    /// delete or rename simply hangs. Both are always allowed; the server is
    /// told by the next sync run.
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

    /// <summary>
    /// A delete, recorded for the next sync run before the acknowledgement
    /// lets it happen: from then until it reaches the server, listings leave
    /// the item alone. Whether it really went (a delete may be part of
    /// replacing the file, or fail) is the run's to read from the drive.
    /// </summary>
    private void OnNotifyDelete(IntPtr callbackInfoPtr, IntPtr callbackParametersPtr)
    {
        CF_CALLBACK_INFO info = default;
        try
        {
            info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(callbackInfoPtr);
            var parameters = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(callbackParametersPtr);
            var normalized = Marshal.PtrToStringUni(info.NormalizedPath) ?? string.Empty;
            if ((parameters.DeleteFlags & CF_CALLBACK_DELETE_FLAGS.CF_CALLBACK_DELETE_FLAG_IS_UNDELETE) != 0)
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

            _log?.Invoke($"NOTIFY_DELETE '{remotePath}' -> sent to the server with the next sync run.");
            _localDelete?.Invoke(ToLocalPath(normalized), remotePath, info.FileId, isDirectory);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"NOTIFY_DELETE handler failed: {ex.Message}");
        }
        finally
        {
            AcknowledgeAlways(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE);
        }
    }

    /// <summary>
    /// A rename or move, recorded for the next sync run before the
    /// acknowledgement lets it happen, with the item's state from before it.
    /// A move out of the drive (the Recycle Bin, another folder) is a delete
    /// for the server, not a move to some outside path; Windows downloads the
    /// item's data to take along first, which the run waits for.
    /// </summary>
    private void OnNotifyRename(IntPtr callbackInfoPtr, IntPtr callbackParametersPtr)
    {
        CF_CALLBACK_INFO info = default;
        try
        {
            info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(callbackInfoPtr);
            var parameters = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(callbackParametersPtr);
            var sourceLocal = Marshal.PtrToStringUni(info.NormalizedPath) ?? string.Empty;
            var sourceFull = ToLocalPath(sourceLocal);
            var targetLocal = parameters.RenameTargetPath == IntPtr.Zero
                ? string.Empty
                : ToLocalPath(Marshal.PtrToStringUni(parameters.RenameTargetPath) ?? string.Empty);

            var isDirectory = (parameters.RenameFlags & CF_CALLBACK_RENAME_FLAGS.CF_CALLBACK_RENAME_FLAG_IS_DIRECTORY) != 0;
            var remotePath = ResolveRemotePath(info) ?? RelativeRemotePath(sourceLocal, isDirectory);
            if (remotePath is null)
            {
                _log?.Invoke($"NOTIFY_RENAME from outside the sync root '{sourceLocal}'; nothing to propagate.");
                return;
            }

            if ((parameters.RenameFlags & CF_CALLBACK_RENAME_FLAGS.CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE) == 0 ||
                string.IsNullOrEmpty(targetLocal))
            {
                _log?.Invoke($"NOTIFY_RENAME '{remotePath}' moved out of the sync root -> deleted on the server with the next sync run.");
                _localDelete?.Invoke(sourceFull, remotePath, info.FileId, isDirectory);
                return;
            }

            _log?.Invoke($"NOTIFY_RENAME '{remotePath}' -> '{targetLocal}'.");
            _localRename?.Invoke(sourceFull, targetLocal, remotePath, SnapshotRenameSource(sourceFull));
        }
        catch (Exception ex)
        {
            _log?.Invoke($"NOTIFY_RENAME handler failed: {ex.Message}");
        }
        finally
        {
            AcknowledgeAlways(info, CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME);
        }
    }

    /// <summary>Unacknowledged, the user's delete or rename would hang.</summary>
    private void AcknowledgeAlways(CF_CALLBACK_INFO info, CF_OPERATION_TYPE type)
    {
        try
        {
            Acknowledge(info, type);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"{type} failed: {ex.Message}");
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
