using System.Collections.Concurrent;
using System.Globalization;
using OpenCfDrive.Core.Interop;
using OpenCfDrive.Core.Models;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.CloudFiles;

public sealed record SyncRootStatus(
    bool CloudFilesSupported,
    string CapabilityReason,
    bool Registered,
    bool Connected,
    string SyncRootPath,
    string Remote,
    int RegisteredItems);

/// <summary>
/// End-to-end orchestrator: registers the sync root, connects cfapi callbacks,
/// populates placeholders from rclone and uploads local edits back.
/// When the Cloud Files API is unavailable the engine falls back to an explicit
/// simulation mode (plain empty files) and never claims to be a mount.
/// </summary>
public sealed class CloudSyncEngine : IAsyncDisposable
{
    private readonly string _syncRootPath;
    private readonly string _remote;
    private readonly RcloneEndpoint _rclone;
    private readonly SqliteMetadataStore _store;
    private readonly Action<string>? _log;

    private readonly SyncRootRegistrar _registrar = new();
    private readonly CloudPlaceholderService _placeholders = new();

    private RcloneRcClient? _rc;
    private RcloneServeClient? _serve;
    private SyncRootConnection? _connection;
    private FileSystemWatcher? _watcher;

    private readonly Guid _syncRootId = new("6f2a1f1e-6a4b-4f5b-9c2a-0d5d5c1b9e10");
    private static readonly Guid ProviderId = new("d022a580-8ff9-4d6f-9d87-2c0951d08246");

    private readonly HashSet<string> _uploading = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _forceSimulation;
    private readonly HashSet<string> _stateFiles = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _populating;

    /// <summary>
    /// Local paths the provider itself just wrote: placeholders handed out by
    /// FETCH_PLACEHOLDERS and placeholders updated for a server-side change.
    /// Those writes raise watcher events that are not user edits, so they
    /// must not be queued for upload.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _providerWrites =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ProviderWriteGuard = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Remote paths (files) and directory prefixes (ending in '/') that were
    /// deleted on the server and are now being deleted locally. The local
    /// delete raises NOTIFY_DELETE and watcher events; they must not be
    /// propagated back as remote deletes.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _serverDeletes =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan ServerDeleteGuard = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Watcher deletes held for WatcherDeleteHold before they reach the
    /// server, by old local path (see QueueRemoteDelete).
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingDelete> _pendingDeletes =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record PendingDelete(string RemotePath, string Name, (long Length, DateTime LastWriteUtc)? Synced);

    private static readonly TimeSpan WatcherDeleteHold = TimeSpan.FromSeconds(1);

    /// <summary>File deletes being carried to the server, by remote path.</summary>
    private readonly ConcurrentDictionary<string, Task> _remoteDeletes =
        new(StringComparer.Ordinal);

    /// <summary>New local paths of renames being carried to the server.</summary>
    private readonly ConcurrentDictionary<string, byte> _renaming =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When a local change (upload, rename) last reached the server, by remote
    /// path. A listing that started before that moment can predate the change,
    /// so it must not be used to delete or overwrite that file.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _localChangeAt =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mock mode only (plain files, no in-sync state): length and write time
    /// of each file when it last matched the server, so a change event with
    /// nothing new is not uploaded again.
    /// </summary>
    private readonly ConcurrentDictionary<string, (long Length, DateTime LastWriteUtc)> _lastSyncedState =
        new(StringComparer.OrdinalIgnoreCase);

    private void RememberSyncedState(string localPath)
    {
        try
        {
            var info = new FileInfo(localPath);
            _lastSyncedState[localPath] = (info.Length, info.LastWriteTimeUtc);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Server-side changes found by a listing, applied after the shell got its reply.</summary>
    private readonly ConcurrentDictionary<string, DirectoryDiff> _pendingDiffs =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _applyGate = new(1, 1);

    private sealed record ChangedFile(FileRecord Record, long Size, DateTimeOffset? ModifiedUtc);

    private sealed record DirectoryDiff(
        DateTimeOffset ListedAt,
        IReadOnlyList<FileRecord> Deleted,
        IReadOnlyList<ChangedFile> Changed);

    /// <param name="forceSimulation">
    /// Runs the full remote pipeline (list, materialise, hydrate, watch, upload)
    /// against a plain directory instead of a cfapi sync root. Used by the
    /// mock E2E so the client-side flow can be validated without Cloud Files
    /// registration.
    /// </param>
    public CloudSyncEngine(
        string syncRootPath,
        string remote,
        string rcUrl,
        string dbPath,
        Action<string>? log = null,
        bool forceSimulation = false)
        : this(syncRootPath, remote, new RcloneEndpoint(rcUrl), dbPath, log, forceSimulation)
    {
    }

    public CloudSyncEngine(
        string syncRootPath,
        string remote,
        RcloneEndpoint rclone,
        string dbPath,
        Action<string>? log = null,
        bool forceSimulation = false)
    {
        _syncRootPath = syncRootPath;
        _remote = remote;
        _rclone = rclone;
        _store = new SqliteMetadataStore(dbPath);
        _log = log;
        _forceSimulation = forceSimulation;

        // The metadata DB and its SQLite side files are engine state, never
        // user data. If they live inside the sync root they must not be
        // uploaded, deleted or renamed through watcher events.
        foreach (var path in new[] { dbPath, dbPath + "-journal", dbPath + "-wal", dbPath + "-shm" })
        {
            try
            {
                _stateFiles.Add(Path.GetFullPath(path));
            }
            catch (ArgumentException)
            {
                // Ignore unparsable paths; they cannot be watcher events either.
            }
        }
    }

    private bool IsStateFile(string localPath)
    {
        try
        {
            return _stateFiles.Contains(Path.GetFullPath(localPath));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public SqliteMetadataStore Store => _store;

    /// <summary>Exposed so hosts/tests can observe cfapi callback activity.</summary>
    public SyncRootConnection? Connection => _connection;

    /// <summary>Payload bytes pulled from the remote so far (hydration telemetry).</summary>
    public long BytesFetched => _serve?.BytesFetched ?? 0;

    /// <summary>Zeroes the hydration byte counter; used by range-hydration tests.</summary>
    public void ResetBytesFetched() => _serve?.ResetBytesFetched();

    public bool IsSimulated { get; private set; }

    private static bool IsRcStartUpTransient(Exception ex)
    {
        if (ex is HttpRequestException httpEx)
        {
            var msg = httpEx.Message ?? string.Empty;
            return httpEx.InnerException is System.Net.Sockets.SocketException
                || msg.Contains("actively refused", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("404", StringComparison.Ordinal)
                || msg.Contains("directory not found", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    public async Task<SyncRootStatus> InitializeAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_syncRootPath);
        _store.Initialize();

        var support = CloudFilesCapability.Current;
        IsSimulated = _forceSimulation || !support.IsSupported;

        var registered = false;
        var connected = false;

        if (_forceSimulation)
        {
            // Mock mode still needs the rclone clients so hydration + upload
            // exercise the same code paths as the real mount.
            _rc = new RcloneRcClient(_rclone);
            _serve = new RcloneServeClient(_rclone, _remote);
            _log?.Invoke("MOCK MODE: no sync root registration; using a plain directory.");
        }
        else if (support.IsSupported)
        {
            var registration = _registrar.Register(_syncRootPath, _syncRootId, ProviderId);
            registered = registration.Success;
            _log?.Invoke(registration.Message);

            if (registered)
            {
                _rc = new RcloneRcClient(_rclone);
                _serve = new RcloneServeClient(_rclone, _remote);
                _connection = new SyncRootConnection(
                    _store,
                    _serve,
                    _log,
                    deleteRemote: HandleRemoteDeleteAsync,
                    renameRemote: HandleRemoteRenameAsync,
                    listDirectory: ListNewRemoteEntriesAsync);

                // Off the callback thread: the shell is still waiting on it.
                _connection.DirectoryListed += directory => _ = Task.Run(() => ApplyServerChangesAsync(directory));
                connected = _connection.Connect(_syncRootPath);
            }
        }
        else
        {
            // Without Cloud Files there are no placeholders, and showing the
            // remote would mean downloading all of it into a plain folder.
            // That is never done: nothing is synced.
            _log?.Invoke($"Cloud Files is not available: {support.Reason} Nothing is synced.");
            return new SyncRootStatus(false, support.Reason, false, false, _syncRootPath, _remote, 0);
        }

        var items = 0;
        for (var attempt = 1; attempt <= 12; attempt++)
        {
            try
            {
                items = await PopulateAsync(cancellationToken).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (IsRcStartUpTransient(ex) && attempt < 12)
            {
                _log?.Invoke($"Rclone RC not ready yet (attempt {attempt}/12): {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }

        return new SyncRootStatus(
            support.IsSupported,
            support.Reason,
            registered,
            connected,
            _syncRootPath,
            _remote,
            items);
    }

    /// <summary>
    /// Pillar 2: the namespace. In cloud mode the drive is never crawled and
    /// nothing is listed up front: Windows asks for a folder's contents when
    /// it is opened (FETCH_PLACEHOLDERS), and each request lists that one
    /// folder. A folder that cannot be listed shows what is already local,
    /// which is nothing if it was never opened. Startup only switches
    /// on-demand population back on for the folders on disk, with no server
    /// calls. Mock mode, a test stand-in, has no such callback and copies the
    /// remote into a plain folder.
    /// </summary>
    public async Task<int> PopulateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSimulated)
        {
            // A folder can be left marked fully populated (by an earlier
            // version, or a stop just after answering it); switched back on,
            // opening it lists the server again.
            var reenabled = _store.GetAll()
                .Where(row => row.IsDirectory)
                .Select(row => row.LocalPath)
                .Prepend(_syncRootPath)
                .Count(_placeholders.EnableOnDemandPopulation);
            _log?.Invoke($"On-demand population enabled on {reenabled} directories; each is listed when opened.");
            return 0;
        }

        _rc ??= new RcloneRcClient(_rclone);

        var items = await _rc.ListFilesAsync(_remote, cancellationToken).ConfigureAwait(false);
        if (items.Count == 0)
        {
            _log?.Invoke($"Remote '{_remote}' returned no items.");
            return 0;
        }

        // rclone's recursive listing only reports files, so the directory
        // entries that make up the namespace are synthesised from the file
        // paths.
        var all = BuildNamespace(items);
        var files = all.Where(i => !i.IsDirectory).ToList();

        // Plain-directory mode: materialise the real folder tree and file
        // contents. Directories have to be created explicitly because
        // MaterializeFilesAsync only creates the parents of real files.
        foreach (var directory in all.Where(i => i.IsDirectory))
        {
            Directory.CreateDirectory(Path.Combine(
                _syncRootPath,
                directory.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        }

        // The watcher must not see the files we are writing while the
        // namespace is being populated, or it would echo them back.
        _populating = true;
        try
        {
            return await MaterializeFilesAsync(files, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _populating = false;
        }
    }

    /// <summary>
    /// FETCH_PLACEHOLDERS source: lists one remote directory live and returns
    /// the entries that have no placeholder yet. Their rows are written before
    /// returning, because the platform can issue FETCH_DATA as soon as the
    /// placeholders exist.
    ///
    /// The remote is the source of truth: a known item whose placeholder is
    /// gone (the folder was unregistered and registered again, or handing it
    /// out failed) is handed out again. The one exception is an item the user
    /// deleted, moved or renamed whose change has not reached the server yet:
    /// the server still lists it only because it has not been told.
    ///
    /// The same listing also reveals files changed or deleted on the server.
    /// The reply to the platform can only add entries, so those are recorded
    /// here and applied by <see cref="ApplyServerChangesAsync"/> once the
    /// shell has its reply (see <see cref="SyncRootConnection.DirectoryListed"/>).
    /// </summary>
    private async Task<IReadOnlyList<PlaceholderEntry>> ListNewRemoteEntriesAsync(
        string relativeDirectory,
        CancellationToken cancellationToken)
    {
        _rc ??= new RcloneRcClient(_rclone);
        var listedAt = DateTimeOffset.UtcNow;
        var items = await _rc.ListDirectoryAsync(_remote, relativeDirectory, cancellationToken).ConfigureAwait(false);

        var known = _store.GetChildren(relativeDirectory)
            .ToDictionary(r => r.RemotePath, StringComparer.Ordinal);
        var listedPaths = new HashSet<string>(StringComparer.Ordinal);
        var changed = new List<ChangedFile>();

        var entries = new List<PlaceholderEntry>();
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            var relative = item.RelativePath.Replace('\\', '/').Trim('/');
            if (relative.Length == 0)
            {
                continue;
            }

            // Same identity convention as BuildNamespace: directories end in '/'.
            var remotePath = item.IsDirectory ? relative + "/" : relative;
            listedPaths.Add(remotePath);

            var localPath = Path.Combine(_syncRootPath, relative.Replace('/', Path.DirectorySeparatorChar));
            if (IsLocalChangePending(localPath))
            {
                continue;
            }

            // A server folder that is on disk as a plain folder (unregistering
            // leaves them so) would never be asked for its contents. Made a
            // placeholder again, it is listed when it is opened.
            if (item.IsDirectory && Directory.Exists(localPath) && !_placeholders.QueryState(localPath).IsPlaceholder)
            {
                var converted = _placeholders.ConvertToPlaceholder(
                    localPath,
                    CloudPlaceholderService.BuildFileIdentity(remotePath),
                    markInSync: true,
                    enableOnDemandPopulation: true);
                _log?.Invoke(converted
                    ? $"'{remotePath}' was a plain folder; now a placeholder, listed when opened."
                    : $"'{remotePath}' is a plain folder and could not be made a placeholder.");
                _store.UpsertFile(
                    remotePath,
                    localPath,
                    fileIdentity: remotePath,
                    fileId: 0,
                    isPlaceholder: converted,
                    isDirectory: true,
                    size: 0,
                    modifiedUtc: item.ModifiedTime,
                    inSync: converted);
                continue;
            }

            if (known.TryGetValue(remotePath, out var record) || (record = _store.GetByRemotePath(remotePath)) is not null)
            {
                var present = item.IsDirectory ? Directory.Exists(localPath) : File.Exists(localPath);
                if (present)
                {
                    if (!item.IsDirectory && !record.IsDirectory && DiffersFromServer(record, item))
                    {
                        changed.Add(new ChangedFile(record, item.Size, item.ModifiedTime));
                    }

                    continue;
                }

                // Known, but its placeholder is gone: handed out again below.
            }

            var size = item.IsDirectory ? 0 : item.Size;
            _store.UpsertFile(
                remotePath,
                localPath,
                fileIdentity: remotePath,
                fileId: 0,
                isPlaceholder: true,
                isDirectory: item.IsDirectory,
                size: size,
                modifiedUtc: item.ModifiedTime,
                inSync: true);

            _providerWrites[localPath] = now;
            entries.Add(new PlaceholderEntry(
                RelativeName: Path.GetFileName(relative),
                IsDirectory: item.IsDirectory,
                Size: size,
                ModifiedUtc: item.ModifiedTime,
                FileIdentity: remotePath));
        }

        var deleted = known.Values.Where(r => !listedPaths.Contains(r.RemotePath)).ToList();
        if (deleted.Count > 0 || changed.Count > 0)
        {
            _pendingDiffs[relativeDirectory] = new DirectoryDiff(listedAt, deleted, changed);
        }

        return entries;
    }

    /// <summary>
    /// True when the server's copy is not the one this client last saw: a
    /// known file changed or gone, or a file at a path this client never saw.
    /// </summary>
    private static bool ServerMovedOn(FileRecord? record, CloudItem? server) =>
        record is null
            ? server is { IsDirectory: false }
            : server is null || server.IsDirectory || DiffersFromServer(record, server);

    private static bool DiffersFromServer(FileRecord record, CloudItem item)
    {
        if (record.Size != item.Size)
        {
            return true;
        }

        // Times survive the round trip through the store as ISO 8601; allow
        // for sub-second rounding on either side.
        return item.ModifiedTime is { } serverTime &&
               DateTimeOffset.TryParse(record.ModifiedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var knownTime) &&
               Math.Abs((serverTime - knownTime).TotalSeconds) >= 1;
    }

    /// <summary>
    /// Applies what the last listing of a directory found: files deleted on
    /// the server are deleted locally, files changed on the server get their
    /// new size/time and drop their local copy. The server wins: local
    /// changes that have not reached the server are discarded. Only the
    /// client's own upload or rename still on its way is waited for (the
    /// listing may predate it); the upload then checks the server itself.
    /// </summary>
    private async Task ApplyServerChangesAsync(string relativeDirectory)
    {
        if (!_pendingDiffs.TryRemove(relativeDirectory, out var diff))
        {
            return;
        }

        await _applyGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var record in diff.Deleted)
            {
                if (record.IsDirectory)
                {
                    ApplyServerDirectoryDelete(record, diff.ListedAt);
                }
                else if (!IsOwnChangeInFlight(record.LocalPath, record.RemotePath, diff.ListedAt))
                {
                    ApplyServerFileDelete(record);
                }
            }

            foreach (var change in diff.Changed)
            {
                if (!IsOwnChangeInFlight(change.Record.LocalPath, change.Record.RemotePath, diff.ListedAt))
                {
                    ApplyServerVersion(change.Record.LocalPath, change.Record.RemotePath, change.Size, change.ModifiedUtc);
                }
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Applying server changes in '{relativeDirectory}' failed: {ex.Message}");
        }
        finally
        {
            _applyGate.Release();
        }
    }

    /// <summary>
    /// Server wins: the file is gone from the server, so it goes locally too,
    /// local changes included. The local delete is not sent back.
    /// </summary>
    private void ApplyServerFileDelete(FileRecord record)
    {
        var discarded = HasUnsyncedLocalChanges(record.LocalPath);
        _serverDeletes[record.RemotePath] = DateTimeOffset.UtcNow;
        try
        {
            if (File.Exists(record.LocalPath))
            {
                File.Delete(record.LocalPath);
            }

            _store.DeleteByRemotePath(record.RemotePath);
            _log?.Invoke(discarded
                ? $"Server deleted '{record.RemotePath}'; the server wins: removed the local file and its local changes."
                : $"Server deleted '{record.RemotePath}'; removed the local file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically open in another program. The row stays, so the next
            // listing of this directory tries again.
            _serverDeletes.TryRemove(record.RemotePath, out _);
            _log?.Invoke($"Server deleted '{record.RemotePath}', but the local file could not be removed: {ex.Message}");
        }
    }

    private void ApplyServerDirectoryDelete(FileRecord record, DateTimeOffset listedAt)
    {
        var prefix = record.RemotePath.TrimEnd('/') + "/";
        if (Directory.Exists(record.LocalPath))
        {
            // Server wins over local changes inside, but the client's own
            // upload or rename still on its way is waited for. Only
            // enumerations by other processes are forwarded to the provider,
            // so this sees exactly what is on disk.
            foreach (var file in Directory.EnumerateFiles(record.LocalPath, "*", SearchOption.AllDirectories))
            {
                if (IsOwnChangeInFlight(file, RelativeOf(file), listedAt))
                {
                    _log?.Invoke($"Server deleted folder '{prefix}', but '{RelativeOf(file)}' is still being sent; the next listing deletes it.");
                    return;
                }
            }
        }

        _serverDeletes[prefix] = DateTimeOffset.UtcNow;
        try
        {
            if (Directory.Exists(record.LocalPath))
            {
                Directory.Delete(record.LocalPath, recursive: true);
            }

            _store.DeleteTree(prefix);
            _store.DeleteByRemotePath(record.RemotePath);
            _log?.Invoke($"Server deleted folder '{prefix}'; removed the local folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _serverDeletes.TryRemove(prefix, out _);
            _log?.Invoke($"Server deleted folder '{prefix}', but the local folder could not be removed: {ex.Message}");
        }
    }

    /// <summary>
    /// Server wins: the local copy is replaced by the server's version, and
    /// local changes not yet sent are discarded. In cloud mode the file
    /// becomes a dehydrated placeholder with the server's size and time (a
    /// plain local file is made a placeholder first); in mock mode the
    /// server's bytes are written over it.
    /// </summary>
    private bool ApplyServerVersion(string localPath, string remotePath, long size, DateTimeOffset? modifiedUtc)
    {
        if (!File.Exists(localPath))
        {
            return false;
        }

        var discarded = HasUnsyncedLocalChanges(localPath);
        _providerWrites[localPath] = DateTimeOffset.UtcNow;
        bool replaced;
        if (IsSimulated)
        {
            replaced = DownloadOver(localPath, remotePath);
        }
        else
        {
            if (!_placeholders.QueryState(localPath).IsPlaceholder &&
                !_placeholders.ConvertToPlaceholder(localPath, CloudPlaceholderService.BuildFileIdentity(remotePath), markInSync: false))
            {
                _log?.Invoke($"Server changed '{remotePath}', but the local file could not be made a placeholder to take it.");
                return false;
            }

            var hr = _placeholders.UpdateFromRemote(localPath, size, modifiedUtc, discardLocalChanges: true);
            replaced = CfApi.Succeeded(hr);
            if (!replaced)
            {
                // Typically open in another program. The row keeps the old
                // metadata, so the next listing retries.
                _log?.Invoke($"Server changed '{remotePath}', but the local copy could not be updated: {HResult.Describe(hr)}.");
                return false;
            }
        }

        if (!replaced)
        {
            return false;
        }

        _store.UpdateRemoteMetadata(remotePath, size, modifiedUtc, dehydrated: !IsSimulated);
        _log?.Invoke(discarded
            ? $"Server changed '{remotePath}' ({size} bytes); the server wins: its version replaced the local changes."
            : $"Server changed '{remotePath}' ({size} bytes); local copy will be re-downloaded on next open.");
        return true;
    }

    /// <summary>Mock mode: writes the server's current bytes over the local file.</summary>
    private bool DownloadOver(string localPath, string remotePath)
    {
        if (_serve is null)
        {
            return false;
        }

        try
        {
            using (var source = _serve.OpenRangeAsync(remotePath, 0, -1).GetAwaiter().GetResult())
            using (var target = File.Create(localPath))
            {
                source.CopyTo(target);
            }

            RememberSyncedState(localPath);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HttpRequestException)
        {
            _log?.Invoke($"Server changed '{remotePath}', but the local file could not be replaced: {ex.Message}");
            return false;
        }
    }

    private async Task<(long? Size, DateTimeOffset? ModifiedUtc)> GetServerMetadataAsync(
        string relative,
        CancellationToken cancellationToken)
    {
        try
        {
            // One lookup of the file, not a listing of its whole folder.
            _rc ??= new RcloneRcClient(_rclone);
            var item = await _rc.StatAsync(_remote, relative, cancellationToken).ConfigureAwait(false);
            return item is { IsDirectory: false } ? (item.Size, item.ModifiedTime) : (null, null);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not read server metadata for '{relative}': {ex.Message}");
            return (null, null);
        }
    }

    /// <summary>
    /// True while the client's own change of the file may not show in a
    /// listing yet: an upload queued or running (it checks the server itself
    /// before sending, and the server wins there too), or an upload or
    /// rename that landed around when the listing was taken. That is not a
    /// conflict, just a listing that may predate the change.
    /// </summary>
    private bool IsOwnChangeInFlight(string localPath, string remotePath, DateTimeOffset listedAt)
    {
        lock (_uploading)
        {
            if (_uploading.Contains(localPath))
            {
                return true;
            }
        }

        return _localChangeAt.TryGetValue(remotePath, out var changedAt) &&
               changedAt >= listedAt - TimeSpan.FromSeconds(2);
    }

    /// <summary>
    /// True when the local file holds something the server does not have: a
    /// placeholder edited since it was last in sync, or a plain local file
    /// (mock mode: a file that differs from when it last matched the server).
    /// </summary>
    private bool HasUnsyncedLocalChanges(string localPath)
    {
        if (!File.Exists(localPath))
        {
            return false;
        }

        if (IsSimulated)
        {
            var info = new FileInfo(localPath);
            return !(_lastSyncedState.TryGetValue(localPath, out var synced) &&
                     synced == (info.Length, info.LastWriteTimeUtc));
        }

        var state = _placeholders.QueryState(localPath);
        return !state.IsPlaceholder || !state.InSync;
    }

    private bool IsProviderWrite(string localPath)
    {
        if (!_providerWrites.TryGetValue(localPath, out var at))
        {
            return false;
        }

        if (DateTimeOffset.UtcNow - at <= ProviderWriteGuard)
        {
            return true;
        }

        _providerWrites.TryRemove(localPath, out _);
        return false;
    }

    private bool IsServerDelete(string remotePath)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, at) in _serverDeletes)
        {
            if (now - at > ServerDeleteGuard)
            {
                _serverDeletes.TryRemove(key, out _);
                continue;
            }

            // A folder is guarded as "dir/". Its own local delete may come as
            // "dir" too: once its row is gone, nothing says it was a folder.
            if (string.Equals(key.TrimEnd('/'), remotePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                (key.EndsWith('/') && remotePath.StartsWith(key, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Turns a flat remote listing into the full namespace: every file plus
    /// every ancestor directory, parents before children. Directories reported
    /// by the listing are kept as-is; missing ones are synthesised because
    /// rclone's recursive listing omits them.
    /// </summary>
    public static IReadOnlyList<RcloneItemMetadata> BuildNamespace(IReadOnlyList<CloudItem> items)
    {
        var directories = new Dictionary<string, RcloneItemMetadata>(StringComparer.OrdinalIgnoreCase);
        var files = new List<RcloneItemMetadata>();

        foreach (var item in items)
        {
            var relative = item.RelativePath.Replace('\\', '/').Trim('/');
            if (relative.Length == 0)
            {
                continue;
            }

            if (item.IsDirectory)
            {
                directories[relative] = new RcloneItemMetadata(
                    item.Path.TrimEnd('/') + "/",
                    relative,
                    0,
                    item.ModifiedTime,
                    IsDirectory: true);
                continue;
            }

            files.Add(new RcloneItemMetadata(
                item.Path,
                relative,
                item.Size,
                item.ModifiedTime,
                IsDirectory: false));

            // Add every ancestor so CfCreatePlaceholders can create the file
            // inside a directory placeholder instead of a plain directory.
            var separator = relative.LastIndexOf('/');
            while (separator > 0)
            {
                var parent = relative[..separator];
                directories.TryAdd(parent, new RcloneItemMetadata(
                    parent + "/",
                    parent,
                    0,
                    ModifiedUtc: null,
                    IsDirectory: true));
                separator = parent.LastIndexOf('/');
            }
        }

        return directories.Values
            .OrderBy(d => d.RelativePath.Count(c => c == '/'))
            .ThenBy(d => d.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Concat(files)
            .ToList();
    }

    /// <summary>
    /// Mock-mode materialisation: writes the remote bytes to a real file so the
    /// E2E can read them back, instead of creating dehydrated placeholders.
    /// </summary>
    private async Task<int> MaterializeFilesAsync(
        IReadOnlyList<RcloneItemMetadata> files,
        CancellationToken cancellationToken)
    {
        var created = 0;
        foreach (var file in files)
        {
            var localPath = Path.Combine(
                _syncRootPath,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

            var parent = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            var downloaded = false;
            if (_serve is not null && file.Size > 0)
            {
                try
                {
                    await using var source = await _serve
                        .OpenRangeAsync(file.RelativePath, 0, -1, cancellationToken)
                        .ConfigureAwait(false);
                    await using var target = File.Create(localPath);
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    downloaded = true;
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Mock download failed for '{file.RelativePath}': {ex.Message}");
                }
            }

            if (!downloaded && !File.Exists(localPath))
            {
                using var stream = File.Create(localPath);
                stream.SetLength(0);
            }

            _store.UpsertFile(
                file.RemotePath,
                localPath,
                file.RemotePath,
                fileId: 0,
                isPlaceholder: false,
                isDirectory: false,
                size: file.Size,
                modifiedUtc: file.ModifiedUtc);

            _store.MarkHydrated(file.RemotePath);
            RememberSyncedState(localPath);
            created++;
        }

        _log?.Invoke($"MOCK: materialised {created} file(s).");
        return created;
    }

    /// <summary>Pillar 4: watch the sync root and push local edits back to rclone.</summary>
    public void StartWatcher()
    {
        if (_watcher is not null)
        {
            return;
        }

        _watcher = new FileSystemWatcher(_syncRootPath)
        {
            IncludeSubdirectories = true,
            // Attributes: "Always keep on this device" / "Free up space" only
            // set the pinned / unpinned attribute; acting on it is up to us.
            // DirectoryName: folder deletes and renames (in mock mode the
            // watcher is the only source of them).
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                           NotifyFilters.Size | NotifyFilters.Attributes,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = true,
        };

        _watcher.Created += (_, e) => QueueUpload(e.FullPath);
        _watcher.Changed += (_, e) =>
        {
            QueuePinChange(e.FullPath);
            QueueUpload(e.FullPath);
        };

        // Deletes are propagated from either signal; both are idempotent.
        _watcher.Deleted += (_, e) => QueueRemoteDelete(e.FullPath);

        // cfapi delivers NOTIFY_RENAME for placeholders; the watcher covers
        // simulation mode and anything the platform does not report.
        _watcher.Renamed += (_, e) => QueueRemoteRename(e.OldFullPath, e.FullPath);

        _log?.Invoke($"Watching '{_syncRootPath}' for local changes.");
    }

    private const FileAttributes PinnedAttribute = (FileAttributes)0x00080000;
    private const FileAttributes UnpinnedAttribute = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnDataAccessAttribute = (FileAttributes)0x00400000;

    /// <summary>
    /// Explorer's "Always keep on this device" sets the pinned attribute and
    /// "Free up space" sets unpinned (on a folder, also on what is inside);
    /// Storage Sense frees space the same way. The platform only records the
    /// request: the provider downloads (hydrates) or drops (dehydrates) the
    /// local copy. Both are idempotent, so the watcher events our own
    /// hydrate/dehydrate raise end here as no-ops.
    /// </summary>
    private void QueuePinChange(string localPath)
    {
        if (IsSimulated || _populating || IsStateFile(localPath))
        {
            return;
        }

        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(localPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var pinned = (attributes & PinnedAttribute) != 0;
        var unpinned = (attributes & UnpinnedAttribute) != 0;
        if (pinned == unpinned)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var files = (attributes & FileAttributes.Directory) != 0
                    ? Directory.EnumerateFiles(localPath, "*", SearchOption.AllDirectories)
                    : [localPath];

                foreach (var file in files)
                {
                    ApplyPinState(file, keepLocal: pinned);
                }
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Pin/unpin handling failed for '{localPath}': {ex.Message}");
            }
        });
    }

    private void ApplyPinState(string localPath, bool keepLocal)
    {
        var attributes = File.GetAttributes(localPath);
        var isLocal = (attributes & RecallOnDataAccessAttribute) == 0;
        var relative = RelativeOf(localPath);

        // A file inside a pinned folder can be unpinned on its own, and the
        // other way round; the file's own attribute wins.
        if ((attributes & (keepLocal ? UnpinnedAttribute : PinnedAttribute)) != 0)
        {
            return;
        }

        if (keepLocal && !isLocal)
        {
            _providerWrites[localPath] = DateTimeOffset.UtcNow;
            var hr = _placeholders.Hydrate(localPath);
            _log?.Invoke(CfApi.Succeeded(hr)
                ? $"Always keep on this device: downloaded '{relative}'."
                : $"Always keep on this device: could not download '{relative}': {HResult.Describe(hr)}.");
        }
        else if (!keepLocal && isLocal)
        {
            // Refused by the platform if the file has local edits that have
            // not been uploaded (it is not in sync), so nothing is lost.
            _providerWrites[localPath] = DateTimeOffset.UtcNow;
            if (_placeholders.Dehydrate(localPath))
            {
                _store.MarkDehydrated(relative);
                _log?.Invoke($"Free up space: removed the local copy of '{relative}'.");
            }
            else
            {
                _log?.Invoke($"Free up space: kept '{relative}' (in use, or local changes not uploaded yet).");
            }
        }
    }

    private void QueueRemoteDelete(string localPath)
    {
        if (IsStateFile(localPath))
        {
            return;
        }

        // Directory rows carry a trailing '/', so a folder is handled as one.
        var record = _store.GetByLocalPath(localPath);

        // Cloud Files reports every delete of a placeholder (NOTIFY_DELETE),
        // and reports a move to another folder as a rename. The watcher sees
        // that same move as delete-old + create-new, so acting on it would
        // delete the server file before the move. It only matters for files
        // that are not placeholders yet, and in mock mode.
        if (!IsSimulated && record is { IsPlaceholder: true })
        {
            return;
        }

        var relative = record?.RemotePath ?? RelativeOf(localPath);

        // Held for a moment before it reaches the server: the watcher reports
        // a move between folders as delete + create (QueueUpload then claims
        // it as a move), and a file replaced by delete-then-rename comes back.
        var synced = _lastSyncedState.TryGetValue(localPath, out var state) ? state : ((long, DateTime)?)null;
        var pending = new PendingDelete(relative, Path.GetFileName(localPath), synced);
        _pendingDeletes[localPath] = pending;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(WatcherDeleteHold).ConfigureAwait(false);
                if (!_pendingDeletes.TryRemove(new KeyValuePair<string, PendingDelete>(localPath, pending)))
                {
                    return; // claimed as a move
                }

                if (File.Exists(localPath) || Directory.Exists(localPath))
                {
                    _log?.Invoke($"'{relative}' was replaced by a new item; its upload updates the server copy.");
                    return;
                }

                await HandleRemoteDeleteAsync(relative).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Delete propagation failed for '{relative}': {ex.Message}");
            }
        });
    }

    /// <summary>
    /// A pending watcher delete whose file just appeared at
    /// <paramref name="localPath"/>: the size and write time it had when last
    /// in sync (a move keeps both; the time is exact to 100 ns), preferring
    /// one with the same name, since a move can rename too. Claimed, so it is
    /// carried out as one server move, not a delete and an upload.
    /// </summary>
    private PendingDelete? TryTakeMovedFrom(string localPath)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(localPath);
            if (!info.Exists)
            {
                return null;
            }
        }
        catch (IOException)
        {
            return null;
        }

        var candidates = _pendingDeletes
            .Where(entry => !string.Equals(entry.Key, localPath, StringComparison.OrdinalIgnoreCase) &&
                            entry.Value.Synced == (info.Length, info.LastWriteTimeUtc))
            .OrderByDescending(entry => string.Equals(entry.Value.Name, info.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var candidate in candidates)
        {
            if (_pendingDeletes.TryRemove(candidate))
            {
                return candidate.Value;
            }
        }

        return null;
    }

    private void QueueRemoteRename(string oldLocalPath, string newLocalPath)
    {
        if (IsStateFile(oldLocalPath) || IsStateFile(newLocalPath))
        {
            return;
        }

        // Cloud Files reports every rename of a placeholder (NOTIFY_RENAME,
        // with its state from before the rename); handling the watcher's copy
        // as well would move it on the server twice. Files that are not
        // placeholders yet, and mock mode, are the watcher's.
        if (!IsSimulated && _placeholders.QueryState(newLocalPath).IsPlaceholder)
        {
            return;
        }

        var record = _store.GetByLocalPath(oldLocalPath);
        var oldRelative = record?.RemotePath ?? RelativeOf(oldLocalPath);
        _ = Task.Run(async () =>
        {
            try
            {
                await HandleRemoteRenameAsync(oldRelative, newLocalPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Rename propagation failed for '{oldRelative}': {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Deletes a remote file, or a remote folder (path ending in '/', or one
    /// the store knows files under), and forgets it locally. Idempotent: the
    /// Cloud Files notification and the watcher both report every delete.
    /// </summary>
    public async Task HandleRemoteDeleteAsync(string remoteRelativePath)
    {
        // The local delete came from the server in the first place.
        if (IsServerDelete(remoteRelativePath))
        {
            return;
        }

        var relative = remoteRelativePath.TrimEnd('/');
        var record = _store.GetByRemotePath(remoteRelativePath) ?? _store.GetByRemotePath(relative);
        if (remoteRelativePath.EndsWith('/') || record?.IsDirectory == true || _store.GetTree(relative).Count > 0)
        {
            await DeleteRemoteDirectoryAsync(relative).ConfigureAwait(false);
            return;
        }

        // Registered before the claim below, so a folder delete that finds
        // the file claimed can wait for it.
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _remoteDeletes[relative] = done.Task;
        try
        {
            // Each file is deleted once: its own notification and its
            // folder's delete both reach here, whichever claims the row first.
            if (record is not null && !_store.DeleteByRemotePath(relative))
            {
                return;
            }

            var (fs, remoteFile) = ResolveRemoteFile(relative);
            _rc ??= new RcloneRcClient(_rclone);

            if (record is null && IsSimulated)
            {
                // Mock mode tracks files only, so an unknown path is a folder
                // or a file that never reached the server. A folder inside one
                // that is going too is removed with it; otherwise let the
                // deletes below it land, then remove it if it is empty.
                var slash = relative.LastIndexOf('/');
                if (slash > 0 && !Directory.Exists(Path.Combine(_syncRootPath, relative[..slash].Replace('/', Path.DirectorySeparatorChar))))
                {
                    return;
                }

                await WaitForRemoteDeletesUnderAsync(relative).ConfigureAwait(false);
                await _rc.RemoveEmptyDirectoriesAsync(fs, remoteFile).ConfigureAwait(false);
                _log?.Invoke($"Removed remote folder '{fs}{remoteFile}' if it was one and is empty.");
                return;
            }

            var deleted = false;
            try
            {
                deleted = await _rc.TryDeleteFileAsync(fs, remoteFile).ConfigureAwait(false);
            }
            catch (Exception) when (record is null)
            {
                // Unknown path: it may have been a folder that was never
                // tracked (mock mode keeps no folder rows); handled below.
            }

            if (record is null && !deleted)
            {
                // Possibly a folder whose files are being deleted one by one
                // right now: let those finish, then remove it if empty.
                await WaitForRemoteDeletesUnderAsync(relative).ConfigureAwait(false);
                await _rc.RemoveEmptyDirectoriesAsync(fs, remoteFile).ConfigureAwait(false);
            }

            _log?.Invoke(deleted
                ? $"Deleted remote '{fs}{remoteFile}'."
                : $"Remote delete reported failure for '{fs}{remoteFile}'.");
        }
        finally
        {
            done.SetResult();
            _remoteDeletes.TryRemove(new KeyValuePair<string, Task>(relative, done.Task));
        }
    }

    /// <summary>Waits for file deletes in flight below a folder.</summary>
    private async Task WaitForRemoteDeletesUnderAsync(string relativeDirectory)
    {
        var prefix = relativeDirectory.TrimEnd('/') + "/";
        var inFlight = _remoteDeletes
            .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => entry.Value)
            .ToList();
        await Task.WhenAll(inFlight).ConfigureAwait(false);
    }

    /// <summary>
    /// A folder deleted locally: deletes the files this client knows under
    /// it, then removes the remote folder only if it is empty. A file added
    /// on the server that this client never listed survives, together with
    /// its folder; the next listing brings the folder back with it.
    /// </summary>
    private async Task DeleteRemoteDirectoryAsync(string relativeDirectory)
    {
        var (fs, remoteDirectory) = ResolveRemoteFile(relativeDirectory);
        _rc ??= new RcloneRcClient(_rclone);

        // A subfolder of a folder that is being deleted too: the parent's
        // delete covers everything below it, including this folder (this
        // folder's own rows are left for the parent to find). Mock mode keeps
        // no folder rows, but the watcher reports the parent's delete too, so
        // a parent that is gone is enough; its rows are no sign, since the
        // files' own deletes may have claimed them all by now.
        var slash = relativeDirectory.LastIndexOf('/');
        if (slash > 0)
        {
            var parent = relativeDirectory[..slash];
            var parentLocal = Path.Combine(_syncRootPath, parent.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(parentLocal) &&
                (IsSimulated || _store.GetByRemotePath(parent + "/") is { IsDirectory: true } || _store.GetTree(parent).Count > 0))
            {
                return;
            }
        }

        foreach (var row in _store.GetTree(relativeDirectory).Where(r => !r.IsDirectory))
        {
            // Claimed here unless the file's own delete got to it first.
            if (!_store.DeleteByRemotePath(row.RemotePath))
            {
                continue;
            }

            var (_, remoteFile) = ResolveRemoteFile(row.RemotePath);
            try
            {
                await _rc.TryDeleteFileAsync(fs, remoteFile).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // One file must not stop the rest; the folder then stays.
                _log?.Invoke($"Could not delete remote '{fs}{remoteFile}': {ex.Message}");
            }
        }

        _store.DeleteTree(relativeDirectory);
        _store.DeleteByRemotePath(relativeDirectory + "/");

        // Files claimed by their own delete may still be on their way; the
        // folder can only go once they have.
        await WaitForRemoteDeletesUnderAsync(relativeDirectory).ConfigureAwait(false);
        await _rc.RemoveEmptyDirectoriesAsync(fs, remoteDirectory).ConfigureAwait(false);

        _log?.Invoke($"Deleted remote folder '{fs}{remoteDirectory}' (only if it held nothing this client has not seen).");
    }

    /// <summary>
    /// Mirrors a local rename/move on the server. Skipped when the old path is
    /// no longer tracked by a previous signal (the Cloud Files notification
    /// and the watcher both report renames). Moving out of the drive is a
    /// delete; renaming a file that only existed locally (an editor's temp
    /// file replacing the original) uploads it under its new name.
    /// </summary>
    public async Task HandleRemoteRenameAsync(
        string oldRemoteRelative,
        string newLocalPath,
        SyncRootConnection.RenameSource? source = null)
    {
        _renaming[newLocalPath] = 0;
        try
        {
            await RenameRemoteAsync(oldRemoteRelative, newLocalPath, source).ConfigureAwait(false);
        }
        finally
        {
            _renaming.TryRemove(newLocalPath, out _);
        }
    }

    /// <summary>
    /// True while the user's own delete, move or rename of the item at
    /// <paramref name="localPath"/> (or of a folder above it) is on its way to
    /// the server: either its old path or, for a rename, its new one.
    /// </summary>
    private bool IsLocalChangePending(string localPath) =>
        _connection?.IsLocalChangePending(localPath) == true ||
        IsRenaming(localPath) ||
        _pendingDeletes.Keys.Any(path =>
            string.Equals(path, localPath, StringComparison.OrdinalIgnoreCase) ||
            localPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True while a rename to the path, or to a folder above it, is being
    /// carried to the server: the platform has cleared the item's in-sync
    /// state and it is not marked again yet, so it would look edited.
    /// </summary>
    private bool IsRenaming(string localPath) =>
        _renaming.Keys.Any(path =>
            string.Equals(path, localPath, StringComparison.OrdinalIgnoreCase) ||
            localPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private async Task RenameRemoteAsync(
        string oldRemoteRelative,
        string newLocalPath,
        SyncRootConnection.RenameSource? source)
    {
        var oldRelative = oldRemoteRelative.TrimEnd('/');
        var newRelative = RelativeOf(newLocalPath);
        var record = _store.GetByRemotePath(oldRemoteRelative) ?? _store.GetByRemotePath(oldRelative);
        var isTrackedDirectory = oldRemoteRelative.EndsWith('/') ||
                                 record?.IsDirectory == true ||
                                 _store.GetTree(oldRelative).Count > 0;

        if (newRelative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(newRelative))
        {
            await HandleRemoteDeleteAsync(isTrackedDirectory ? oldRelative + "/" : oldRelative).ConfigureAwait(false);
            return;
        }

        if (string.Equals(newRelative, oldRelative, StringComparison.Ordinal))
        {
            return;
        }

        if (isTrackedDirectory)
        {
            await RenameRemoteDirectoryAsync(oldRelative, newRelative, newLocalPath, source).ConfigureAwait(false);
            return;
        }

        if (Directory.Exists(newLocalPath))
        {
            // A folder nothing is tracked under: already handled by the other
            // signal, or empty and never on the server.
            return;
        }

        if (record is null)
        {
            // Whether there is anything to send is UploadAsync's call.
            if (File.Exists(newLocalPath))
            {
                QueueUpload(newLocalPath);
            }

            return;
        }

        var (fs, oldRemote) = ResolveRemoteFile(oldRelative);
        var (_, newRemote) = ResolveRemoteFile(newRelative);

        _rc ??= new RcloneRcClient(_rclone);
        await _rc.MoveFileAsync(fs, oldRemote, newRemote).ConfigureAwait(false);

        _store.RenameRemotePath(oldRelative, newRelative, newLocalPath, newRelative);
        _localChangeAt[newRelative] = DateTimeOffset.UtcNow;
        if (_lastSyncedState.TryRemove(Path.Combine(_syncRootPath, oldRelative.Replace('/', Path.DirectorySeparatorChar)), out var state))
        {
            _lastSyncedState[newLocalPath] = state;
        }

        _log?.Invoke($"Renamed remote '{fs}{oldRemote}' -> '{newRemote}'.");

        if (!IsSimulated)
        {
            UpdateRenamedPlaceholder(newLocalPath, newRelative, source);
        }
    }

    /// <summary>
    /// After a rename has reached the server: gives the placeholder its new
    /// identity, and marks it in sync again if it was in sync just before the
    /// rename and has not been written since (the platform clears in-sync on
    /// every rename, which would otherwise make it look edited and get it
    /// downloaded and uploaded again). An item that had local changes keeps
    /// waiting for its upload.
    /// </summary>
    private void UpdateRenamedPlaceholder(string localPath, string identityPath, SyncRootConnection.RenameSource? source)
    {
        var identity = CloudPlaceholderService.BuildFileIdentity(identityPath);
        var unchanged = source is { InSync: true } &&
                        File.GetLastWriteTimeUtc(localPath) == source.LastWriteUtc &&
                        (!File.Exists(localPath) || new FileInfo(localPath).Length == source.Length);

        if (unchanged)
        {
            _placeholders.MarkInSync(localPath, identity);
        }
        else
        {
            _placeholders.UpdateFileIdentity(localPath, identity);
        }
    }

    /// <summary>
    /// A folder renamed/moved locally: moves it on the server, then re-keys
    /// every row below it and gives every placeholder below it its new
    /// identity, so opening a file there still downloads from the right path.
    /// </summary>
    private async Task RenameRemoteDirectoryAsync(
        string oldRelative,
        string newRelative,
        string newLocalPath,
        SyncRootConnection.RenameSource? source)
    {
        // Cloud Files reports the rename before it happens.
        for (var attempt = 0; attempt < 50 && !Directory.Exists(newLocalPath); attempt++)
        {
            await Task.Delay(100).ConfigureAwait(false);
        }

        var (fs, oldRemote) = ResolveRemoteFile(oldRelative);
        var (_, newRemote) = ResolveRemoteFile(newRelative);
        _rc ??= new RcloneRcClient(_rclone);
        await _rc.MoveDirectoryAsync(fs + oldRemote, fs + newRemote).ConfigureAwait(false);
        await _rc.RemoveEmptyDirectoriesAsync(fs, oldRemote).ConfigureAwait(false);

        var oldLocalPath = Path.Combine(_syncRootPath, oldRelative.Replace('/', Path.DirectorySeparatorChar));
        _store.RenameTree(oldRelative, newRelative, oldLocalPath, newLocalPath);

        var now = DateTimeOffset.UtcNow;
        foreach (var row in _store.GetTree(newRelative))
        {
            _localChangeAt[row.RemotePath] = now;
            if (IsSimulated || !(File.Exists(row.LocalPath) || Directory.Exists(row.LocalPath)))
            {
                continue;
            }

            // Only the folder itself was renamed; what is inside keeps its
            // in-sync state and just needs the new identity.
            if (string.Equals(row.RemotePath, newRelative + "/", StringComparison.Ordinal))
            {
                UpdateRenamedPlaceholder(row.LocalPath, row.RemotePath, source);
            }
            else
            {
                _placeholders.UpdateFileIdentity(row.LocalPath, CloudPlaceholderService.BuildFileIdentity(row.RemotePath));
            }
        }

        foreach (var key in _lastSyncedState.Keys.Where(k => k.StartsWith(oldLocalPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (_lastSyncedState.TryRemove(key, out var state))
            {
                _lastSyncedState[newLocalPath + key[oldLocalPath.Length..]] = state;
            }
        }

        _log?.Invoke($"Renamed remote folder '{fs}{oldRemote}' -> '{newRemote}'.");
    }

    private string RelativeOf(string localPath) =>
        Path.GetRelativePath(_syncRootPath, localPath).Replace('\\', '/');

    /// <summary>
    /// Maps a sync-root-relative path to the real rclone target. The DB stores
    /// paths relative to the configured remote, which is not a valid rclone
    /// spec on its own: uploadfile/deletefile would treat it as a local path.
    /// </summary>
    private (string Fs, string RemoteFile) ResolveRemoteFile(string relative)
    {
        var (fs, baseRemote) = RcloneRcClient.SplitRemote(_remote);
        var remoteFile = string.IsNullOrEmpty(baseRemote) ? relative : $"{baseRemote}/{relative}";
        return (fs, remoteFile);
    }

    private void QueueUpload(string localPath)
    {
        if (Directory.Exists(localPath) || IsStateFile(localPath))
        {
            return;
        }

        // Namespace population writes local files; never echo those back.
        if (_populating)
        {
            return;
        }

        lock (_uploading)
        {
            if (!_uploading.Add(localPath))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // Debounce: editors write several times per save.
                await Task.Delay(750).ConfigureAwait(false);

                // The other half of a move between folders, as the watcher
                // reports it (delete + create): one server move, no upload.
                if (TryTakeMovedFrom(localPath) is { } moved)
                {
                    await HandleRemoteRenameAsync(moved.RemotePath, localPath).ConfigureAwait(false);
                    return;
                }

                // Our own placeholder writes (new entries, server changes,
                // free up space / keep on device) leave the file in sync; a
                // user edit clears in-sync. So a recent provider write only
                // suppresses the upload while the file is still in sync, and
                // an edit made right after one is still uploaded.
                if (IsProviderWrite(localPath) && _placeholders.QueryState(localPath) is { IsPlaceholder: true, InSync: true })
                {
                    return;
                }

                // Never upload a file this process is still hydrating: the
                // watcher fires on our own writes and a partial file would
                // overwrite a good remote copy. Nor one whose rename is still
                // on its way to the server: it is not marked in sync yet.
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    if (_connection?.IsHydrating(localPath) == true || IsRenaming(localPath))
                    {
                        await Task.Delay(1000).ConfigureAwait(false);
                        continue;
                    }

                    await UploadAsync(localPath).ConfigureAwait(false);
                    return;
                }

                _log?.Invoke($"Skipped upload for '{localPath}': still hydrating or renaming.");
            }
            catch (Exception ex)
            {
                _log?.Invoke($"Upload failed for '{localPath}': {ex.Message}");
            }
            finally
            {
                lock (_uploading)
                {
                    _uploading.Remove(localPath);
                }
            }
        });
    }

    public async Task<bool> UploadAsync(string localPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(localPath))
        {
            return false;
        }

        if (_connection?.IsHydrating(localPath) == true)
        {
            return false;
        }

        var info = new FileInfo(localPath);
        var relative = Path.GetRelativePath(_syncRootPath, localPath).Replace('\\', '/');
        var record = _store.GetByLocalPath(localPath);

        // Nothing new to send? Size alone cannot tell (a same-size edit is
        // still an edit).
        var state = _placeholders.QueryState(localPath);
        if (!IsSimulated)
        {
            // Cloud Files tracks it: any local write clears in-sync, while
            // downloading, freeing space and server updates keep it (and a
            // rename is marked in sync again once it reaches the server). A
            // dehydrated placeholder is in sync, so its empty local copy is
            // never sent over good server data.
            if (state is { IsPlaceholder: true, InSync: true })
            {
                return false;
            }
        }
        else if (_lastSyncedState.TryGetValue(localPath, out var synced) &&
                 synced == (info.Length, info.LastWriteTimeUtc))
        {
            return false;
        }

        // Server wins: a local change is only sent over the version this
        // client last saw. If the server has moved on since (changed,
        // deleted, or a file this client never saw at that path), its
        // version replaces the local one and the local change is discarded.
        _rc ??= new RcloneRcClient(_rclone);
        var server = await _rc.StatAsync(_remote, relative, cancellationToken).ConfigureAwait(false);
        if (ServerMovedOn(record, server))
        {
            if (server is null || server.IsDirectory)
            {
                ApplyServerFileDelete(record!);
            }
            else
            {
                // A file this client never saw needs a row to take its version.
                if (record is null)
                {
                    _store.UpsertFile(
                        remotePath: relative,
                        localPath: localPath,
                        fileIdentity: relative,
                        fileId: state.FileId,
                        isPlaceholder: state.IsPlaceholder,
                        isDirectory: false,
                        size: server.Size,
                        modifiedUtc: server.ModifiedTime,
                        inSync: false);
                }

                ApplyServerVersion(localPath, relative, server.Size, server.ModifiedTime);
            }

            return false;
        }

        var (fs, remoteFile) = ResolveRemoteFile(relative);
        var remoteDirectory = remoteFile.Contains('/')
            ? remoteFile[..remoteFile.LastIndexOf('/')]
            : string.Empty;

        await _rc.UploadFileAsync(fs, remoteDirectory, localPath, cancellationToken).ConfigureAwait(false);

        // The store holds the server's metadata, which is what later listings
        // are compared against. Most remotes stamp their own modification
        // time on upload, so the local one would read as a server-side change.
        var (serverSize, serverTime) = await GetServerMetadataAsync(relative, cancellationToken).ConfigureAwait(false);

        _store.UpsertFile(
            remotePath: relative,
            localPath: localPath,
            fileIdentity: relative,
            fileId: state.FileId != 0 ? state.FileId : record?.FileId ?? 0,
            isPlaceholder: true,
            isDirectory: false,
            size: serverSize ?? info.Length,
            modifiedUtc: serverTime ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            inSync: true);
        _localChangeAt[relative] = DateTimeOffset.UtcNow;
        RememberSyncedState(localPath);

        _log?.Invoke($"Uploaded '{relative}' ({info.Length} bytes) to {fs}{remoteDirectory}.");

        if (!IsSimulated)
        {
            // Decided by the file, not the store: a plain file can take a
            // known placeholder's place (an editor saving through a temp file).
            var identity = CloudPlaceholderService.BuildFileIdentity(relative);
            if (state.IsPlaceholder)
            {
                _placeholders.MarkInSync(localPath, identity);
            }
            else
            {
                _placeholders.ConvertToPlaceholder(localPath, identity, markInSync: true);
            }
        }

        return true;
    }


    public async ValueTask DisposeAsync()
    {
        _watcher?.Dispose();
        _watcher = null;

        _connection?.Dispose();
        _connection = null;

        _serve?.Dispose();
        _serve = null;

        _rc?.Dispose();
        _rc = null;

        // The drive stays registered: stopping the service (the app's Stop,
        // a restart to apply settings) is not removing the drive. Until the
        // service is back, Explorer still lists it and downloaded files open;
        // online-only ones wait for it. Unregistering is the uninstaller's
        // and --unregister's job, and the app's when the drive moves.
        await Task.CompletedTask;
    }
}
