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

    /// <summary>Whether the server answers (cloud mode); see <see cref="ServerConnection"/>.</summary>
    private ServerConnection? _server;

    /// <summary>Files changed while offline, uploaded once the server answers.</summary>
    private readonly ConcurrentDictionary<string, byte> _deferredUploads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>New local folders made while offline, created on the server once it answers.</summary>
    private readonly ConcurrentDictionary<string, byte> _deferredFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Local paths (old and new) of the deletes and renames kept for the
    /// server while it could not be reached; the store keeps the changes
    /// themselves (<see cref="SqliteMetadataStore.AddPendingChange"/>).
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _offlinePaths = new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _pendingGate = new(1, 1);

    /// <summary>New local folders waiting to be created on the server.</summary>
    private readonly HashSet<string> _creatingFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The background sync with the server (cloud mode); see <see cref="SyncScheduler"/>.</summary>
    private SyncScheduler? _scheduler;

    /// <summary>
    /// False for remotes that have folders only as parts of file names (S3
    /// and other bucket stores): an empty folder made here cannot be on the
    /// server, so its absence there does not mean it was deleted.
    /// </summary>
    private bool _remoteKeepsEmptyFolders = true;

    /// <summary>False while the server cannot be reached; folders then show what is on this PC.</summary>
    public bool IsOnline => _server?.IsOnline ?? true;

    /// <summary>Why the server cannot be reached, while offline.</summary>
    public string? OfflineReason => _server?.OfflineReason;

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

                // Checked with a listing of the drive's top folder, given as
                // long as any listing.
                var rc = _rc;
                _server = new ServerConnection(
                    cancellationToken => rc.ListDirectoryAsync(_remote, string.Empty, cancellationToken),
                    SyncRootConnection.ListingTimeout,
                    _log);
                // Back online, everything kept for the server goes, and the
                // server is looked at again (Nextcloud runs a sync then too;
                // the run sends the deletes and renames kept first).
                _server.Changed += online =>
                {
                    if (online)
                    {
                        UploadDeferred();
                        _ = _scheduler?.SyncNowAsync();
                    }
                };
                _scheduler = new SyncScheduler(SyncRemoteAsync, SyncScheduler.ConfiguredInterval());

                _connection = new SyncRootConnection(
                    _store,
                    _serve,
                    _log,
                    deleteRemote: HandleRemoteDeleteAsync,
                    renameRemote: HandleRemoteRenameAsync,
                    listDirectory: ListNewRemoteEntriesAsync,
                    server: _server);

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

        if (_rc is not null)
        {
            try
            {
                _remoteKeepsEmptyFolders = await _rc.CanHaveEmptyDirectoriesAsync(_remote, cancellationToken).ConfigureAwait(false);
                if (!_remoteKeepsEmptyFolders)
                {
                    _log?.Invoke($"'{_remote}' keeps no empty folders (folders are parts of file names there): an empty folder made here stays only here until a file is put in it.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // Assume folders are kept, as on most remotes.
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
    /// nothing is listed up front: Windows asks for a folder's contents the
    /// first time it is opened (FETCH_PLACEHOLDERS), and that request lists
    /// the one folder. From then on the folder is fully populated and the
    /// background sync keeps it current (see <see cref="SyncRemoteAsync"/>).
    /// A folder that cannot be listed shows what is already local, which is
    /// nothing if it was never opened. Mock mode, a test stand-in, has no such
    /// callback and copies the remote into a plain folder.
    /// </summary>
    public async Task<int> PopulateAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSimulated)
        {
            _log?.Invoke($"{ListedDirectories().Count()} folder(s) listed on this PC; the background sync keeps them current.");
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

            var isKnown = known.TryGetValue(remotePath, out var record) || (record = _store.GetByRemotePath(remotePath)) is not null;

            // A file made here and not uploaded yet, where the server got one
            // of its own: never taken for the server's. Its upload finds the
            // server's and keeps both.
            if (!isKnown && !item.IsDirectory && File.Exists(localPath) && !_placeholders.QueryState(localPath).IsPlaceholder)
            {
                QueueUpload(localPath);
                continue;
            }

            if (isKnown)
            {
                var present = item.IsDirectory ? Directory.Exists(localPath) : File.Exists(localPath);
                if (present)
                {
                    if (!item.IsDirectory && !record!.IsDirectory && DiffersFromServer(record, item))
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
    /// Starts what runs beside the drive once it is connected and watched, as
    /// Nextcloud does on start: changes made here while the service was not
    /// running are sent, deletes and renames kept from an offline spell are
    /// sent, and the background sync starts (a run now, then every interval).
    /// </summary>
    public void StartBackgroundSync()
    {
        if (IsSimulated || _scheduler is null)
        {
            return;
        }

        foreach (var change in _store.GetPendingChanges())
        {
            _offlinePaths[change.LocalPath] = 0;
            if (change.NewLocalPath is not null)
            {
                _offlinePaths[change.NewLocalPath] = 0;
            }
        }

        // The first run sends what was kept from an offline spell before it
        // lists anything.
        _ = Task.Run(() =>
        {
            try
            {
                DiscoverLocalChanges();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Looking for changes made while OpenCfDrive was not running failed: {ex.Message}");
            }

            _scheduler.Start();
        });
    }

    /// <summary>
    /// Looks at the server now (the app's or a script's "sync now") and
    /// completes once that run has finished.
    /// </summary>
    public Task SyncNowAsync() => _scheduler?.SyncNowAsync() ?? Task.CompletedTask;

    /// <summary>
    /// One background sync run, the counterpart of a Nextcloud sync run's
    /// remote discovery for the folders this PC has listed: each is listed
    /// (parents first, one at a time, never recursively) and what changed on
    /// the server is applied: new items appear, changed files take the new
    /// version (keeping a local edit as a conflict copy), and deleted ones go.
    /// Folders never opened are left to be listed when they are. Offline,
    /// nothing is asked; the connection check runs instead.
    /// </summary>
    private async Task SyncRemoteAsync(CancellationToken cancellationToken)
    {
        if (_server is { IsOnline: false })
        {
            _server.CheckNow();
            return;
        }

        // Local changes kept from an offline spell go first, so no listing
        // takes what they change for the server's current state.
        await SendPendingChangesAsync().ConfigureAwait(false);

        foreach (var directory in ListedDirectories().ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await RefreshDirectoryAsync(directory, cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    /// <summary>
    /// The folders Windows has had listed (fully populated directory
    /// placeholders; the platform keeps that state on disk), parents first.
    /// </summary>
    private IEnumerable<string> ListedDirectories() =>
        _store.GetDirectories()
            .Select(row => row.RemotePath.TrimEnd('/'))
            .Prepend(string.Empty)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(directory => directory.Length == 0 ? 0 : directory.Count(c => c == '/') + 1)
            .Where(directory => IsListed(LocalPathOf(directory)));

    private bool IsListed(string localDirectory) =>
        Directory.Exists(localDirectory) &&
        _placeholders.QueryState(localDirectory) is { IsPlaceholder: true, Partial: false };

    private string LocalPathOf(string relative) =>
        relative.Length == 0
            ? _syncRootPath
            : Path.Combine(_syncRootPath, relative.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Lists one listed folder and applies what changed. False when the
    /// server could not be reached: the drive is offline and the run stops.
    /// </summary>
    private async Task<bool> RefreshDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(LocalPathOf(directory)))
        {
            return true; // removed by its parent's changes in this run
        }

        IReadOnlyList<PlaceholderEntry> entries;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(SyncRootConnection.ListingTimeout);
            try
            {
                entries = await ListNewRemoteEntriesAsync(directory, timeout.Token).ConfigureAwait(false);
                _server?.Succeeded();
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                var reason = timeout.IsCancellationRequested
                    ? $"no answer within {SyncRootConnection.ListingTimeout.TotalSeconds:0} s"
                    : ex.Message;
                if (timeout.IsCancellationRequested || ServerConnection.IsUnreachable(ex))
                {
                    _server?.Failed(reason);
                    return false;
                }

                // Typically a folder the server removed since its parent was
                // listed; the parent's next listing removes it here.
                _log?.Invoke($"Background sync could not list '{directory}' ({reason}); the next run tries again.");
                return true;
            }
        }

        if (entries.Count > 0)
        {
            var items = entries
                .Select(entry => new RcloneItemMetadata(
                    entry.FileIdentity,
                    directory.Length == 0 ? entry.RelativeName : $"{directory}/{entry.RelativeName}",
                    entry.Size,
                    entry.ModifiedUtc,
                    entry.IsDirectory))
                .ToList();
            var result = _placeholders.CreatePlaceholders(_syncRootPath, items);
            _log?.Invoke(
                $"Server added in '{directory}': {string.Join(", ", entries.Select(e => e.RelativeName))}" +
                (result.Failed > 0 ? $" ({result.Failed} could not be shown: {string.Join("; ", result.Errors)})." : "."));
        }

        await ApplyServerChangesAsync(directory).ConfigureAwait(false);
        return true;
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
    /// Applies what the last listing of a directory found, as Nextcloud does:
    /// files deleted on the server are deleted locally, files changed on the
    /// server get their new size/time and drop their local copy. A local
    /// change that has not reached the server is never lost: an edited file
    /// the server changed too is kept as a conflict copy, and one the server
    /// deleted is uploaded again. Only the client's own upload or rename still
    /// on its way is waited for (the listing may predate it); the upload then
    /// checks the server itself.
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
    /// The file is gone from the server, so it goes locally too; the local
    /// delete is not sent back. One with local changes the server never got
    /// stays and is uploaded again instead (Nextcloud restores it the same way).
    /// </summary>
    private void ApplyServerFileDelete(FileRecord record)
    {
        if (HasUnsyncedLocalChanges(record.LocalPath))
        {
            RestoreToServer(record, "deleted it");
            return;
        }

        _serverDeletes[record.RemotePath] = DateTimeOffset.UtcNow;
        try
        {
            if (File.Exists(record.LocalPath))
            {
                File.Delete(record.LocalPath);
            }

            _store.DeleteByRemotePath(record.RemotePath);
            _log?.Invoke($"Server deleted '{record.RemotePath}'; removed the local file.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Typically open in another program. The row stays, so the next
            // listing of this directory tries again.
            _serverDeletes.TryRemove(record.RemotePath, out _);
            _log?.Invoke($"Server deleted '{record.RemotePath}', but the local file could not be removed: {ex.Message}");
        }
    }

    /// <summary>
    /// The folder is gone from the server, so it goes locally too. Files in
    /// it with local changes the server never got stay, with the folders
    /// they are in, and are uploaded again (as Nextcloud keeps them); only
    /// the rest goes. The client's own upload or rename still on its way is
    /// waited for.
    /// </summary>
    private void ApplyServerDirectoryDelete(FileRecord record, DateTimeOffset listedAt)
    {
        var prefix = record.RemotePath.TrimEnd('/') + "/";

        // A remote without empty folders never lists one: absent is not deleted.
        if (!_remoteKeepsEmptyFolders && Directory.Exists(record.LocalPath) &&
            !Directory.EnumerateFileSystemEntries(record.LocalPath).Any())
        {
            return;
        }

        var edited = new List<string>();
        if (Directory.Exists(record.LocalPath))
        {
            // Only enumerations by other processes are forwarded to the
            // provider, so this sees exactly what is on disk.
            foreach (var file in Directory.EnumerateFiles(record.LocalPath, "*", SearchOption.AllDirectories))
            {
                if (IsOwnChangeInFlight(file, RelativeOf(file), listedAt))
                {
                    _log?.Invoke($"Server deleted folder '{prefix}', but '{RelativeOf(file)}' is still being sent; the next listing deletes it.");
                    return;
                }

                if (HasUnsyncedLocalChanges(file))
                {
                    edited.Add(file);
                }
            }
        }

        if (edited.Count > 0)
        {
            KeepEditedInDeletedFolder(record, prefix, edited);
            return;
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
    /// Deleted folder, edited files: those files and their folders stay and
    /// are uploaded again; every other file goes, and so do folders left
    /// empty. Rows go for what is deleted and for the kept files (which are
    /// new to the server now); the folders kept keep theirs.
    /// </summary>
    private void KeepEditedInDeletedFolder(FileRecord record, string prefix, IReadOnlyList<string> edited)
    {
        var keep = new HashSet<string>(edited, StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(record.LocalPath, "*", SearchOption.AllDirectories).ToList())
        {
            if (keep.Contains(file))
            {
                continue;
            }

            var remotePath = RelativeOf(file);
            _serverDeletes[remotePath] = DateTimeOffset.UtcNow;
            try
            {
                File.Delete(file);
                _store.DeleteByRemotePath(remotePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _serverDeletes.TryRemove(remotePath, out _);
                _log?.Invoke($"Server deleted '{remotePath}', but the local file could not be removed: {ex.Message}");
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(record.LocalPath, "*", SearchOption.AllDirectories)
                     .OrderByDescending(path => path.Length)
                     .ToList())
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                var remotePath = RelativeOf(directory) + "/";
                _serverDeletes[remotePath] = DateTimeOffset.UtcNow;
                try
                {
                    Directory.Delete(directory);
                    _store.DeleteByRemotePath(remotePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _serverDeletes.TryRemove(remotePath, out _);
                }
            }
        }

        // What stays is new to the server now: no rows, so no listing takes
        // it for deleted again; the uploads bring the folders back with them.
        _store.DeleteTree(prefix);
        _store.DeleteByRemotePath(record.RemotePath);
        foreach (var file in edited)
        {
            _log?.Invoke($"Server deleted folder '{prefix}', but '{RelativeOf(file)}' has local changes: it is kept and uploaded again.");
            QueueUpload(file);
        }
    }

    /// <summary>
    /// A file with local changes the server lost track of (it deleted the
    /// file, or its folder): the local version stays and is uploaded as new.
    /// </summary>
    private void RestoreToServer(FileRecord record, string what)
    {
        _store.DeleteByRemotePath(record.RemotePath);
        _log?.Invoke($"Server {what} '{record.RemotePath}', which has local changes: they are kept and uploaded again.");
        QueueUpload(record.LocalPath);
    }

    /// <summary>
    /// Keeps the local version of a file the server changed too: a copy of
    /// it next to it, named like Nextcloud's conflict copies, which is
    /// uploaded as a file of its own (OneDrive and Nextcloud keep both).
    /// Returns the copy's path, or null if it could not be made.
    /// </summary>
    private string? KeepConflictCopy(string localPath, string remotePath)
    {
        try
        {
            var copy = ConflictCopies.PathFor(localPath, File.GetLastWriteTime(localPath));
            File.Copy(localPath, copy);
            _log?.Invoke($"Conflict: '{remotePath}' changed here and on the server; the server's version keeps the name, this PC's is kept as '{RelativeOf(copy)}'.");
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"Conflict: '{remotePath}' changed here and on the server, and the local version could not be copied aside ({ex.Message}); it is kept as it is for now.");
            return null;
        }
    }

    /// <summary>
    /// The local copy is replaced by the server's version. A local change
    /// not yet sent is kept first as a conflict copy, and if it cannot be,
    /// nothing is replaced. In cloud mode the file becomes a dehydrated
    /// placeholder with the server's size and time (a plain local file is
    /// made a placeholder first); in mock mode the server's bytes are written
    /// over it.
    /// </summary>
    private bool ApplyServerVersion(string localPath, string remotePath, long size, DateTimeOffset? modifiedUtc)
    {
        if (!File.Exists(localPath))
        {
            return false;
        }

        if (HasUnsyncedLocalChanges(localPath) && KeepConflictCopy(localPath, remotePath) is null)
        {
            return false;
        }

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
        _log?.Invoke($"Server changed '{remotePath}' ({size} bytes); local copy will be re-downloaded on next open.");
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
        return !state.IsPlaceholder || !state.InSync || !MatchesLastSync(localPath, _store.GetByLocalPath(localPath));
    }

    /// <summary>
    /// Whether a file still has the size and write time it had when it was
    /// last in sync. In-sync alone does not prove a file unchanged: Windows
    /// does not clear it for every write (an append that starts on a page
    /// boundary leaves it set). So, like other sync clients (Nextcloud
    /// compares both with its journal), an edit is also told by these. True
    /// when the record cannot say, which leaves it to in-sync.
    /// </summary>
    private static bool MatchesLastSync(string localPath, FileRecord? record)
    {
        if (record is null || record.IsDirectory || record.SyncedLocalState is not { } synced)
        {
            return true;
        }

        var info = new FileInfo(localPath);
        return info.Exists && info.Length == synced.Size && info.LastWriteTimeUtc == synced.WriteUtc;
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

        _watcher.Created += (_, e) =>
        {
            if (Directory.Exists(e.FullPath))
            {
                QueueCreateFolder(e.FullPath);
            }
            else
            {
                QueueUpload(e.FullPath);
            }
        };
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

    /// <summary>
    /// One pin or unpin at a time per file: the second of two requests (the
    /// watcher's, and the one an upload makes once a file waiting to be freed
    /// is sent) then finds the work done. Two at once would open a file the
    /// other just freed with write access, which downloads it again.
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _pinLocks = new(StringComparer.OrdinalIgnoreCase);

    private void ApplyPinState(string localPath, bool keepLocal)
    {
        lock (_pinLocks.GetOrAdd(localPath, _ => new object()))
        {
            ApplyPinStateNow(localPath, keepLocal);
        }
    }

    private void ApplyPinStateNow(string localPath, bool keepLocal)
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
            // Never the only copy of a change: a file with local changes the
            // server does not have yet is uploaded first and freed after
            // (OneDrive does the same). The platform's own refusal covers
            // only what in-sync tells, which is not every edit.
            if (HasUnsyncedLocalChanges(localPath))
            {
                _log?.Invoke($"Free up space: '{relative}' has changes that are not on the server yet; it is freed once they are uploaded.");
                QueueUpload(localPath);
                return;
            }

            _providerWrites[localPath] = DateTimeOffset.UtcNow;
            if (_placeholders.Dehydrate(localPath, new FileInfo(localPath).Length))
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
        var isDirectory = remoteRelativePath.EndsWith('/') || record?.IsDirectory == true || _store.GetTree(relative).Count > 0;

        // Offline: kept, with the rows, until the server answers.
        if (_server is { IsOnline: false })
        {
            KeepForLater(PendingChangeKind.Delete, isDirectory ? relative + "/" : relative, LocalPathOf(relative));
            return;
        }

        try
        {
            if (isDirectory)
            {
                await DeleteRemoteDirectoryAsync(relative).ConfigureAwait(false);
            }
            else
            {
                await DeleteRemoteFileAsync(relative, record).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (_server is not null && ServerConnection.IsUnreachable(ex))
        {
            _server.Failed(ex.Message);
            KeepForLater(PendingChangeKind.Delete, isDirectory ? relative + "/" : relative, LocalPathOf(relative));
        }
    }

    /// <summary>
    /// The server version of a file deleted here changed since this client
    /// last saw it: it is not deleted, and comes back here with the next
    /// listing (Nextcloud restores it too, rather than lose the change).
    /// </summary>
    private async Task<bool> ChangedOnServerSinceAsync(string relative, FileRecord? record)
    {
        if (record is null || record.IsDirectory)
        {
            return false;
        }

        _rc ??= new RcloneRcClient(_rclone);
        var server = await _rc.StatAsync(_remote, relative).ConfigureAwait(false);
        if (server is { IsDirectory: false } && DiffersFromServer(record, server))
        {
            _log?.Invoke($"'{relative}' was deleted here, but it changed on the server since: the server's version is kept and comes back.");
            return true;
        }

        return false;
    }

    private async Task DeleteRemoteFileAsync(string relative, FileRecord? record)
    {
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

            if (await ChangedOnServerSinceAsync(relative, record).ConfigureAwait(false))
            {
                return;
            }

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
                // Changed on the server since: kept there, with its folder.
                if (!await ChangedOnServerSinceAsync(row.RemotePath, row).ConfigureAwait(false))
                {
                    await _rc.TryDeleteFileAsync(fs, remoteFile).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (!ServerConnection.IsUnreachable(ex))
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
        IsKeptOffline(localPath) ||
        _pendingDeletes.Keys.Any(path =>
            string.Equals(path, localPath, StringComparison.OrdinalIgnoreCase) ||
            localPath.StartsWith(path + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    /// <summary>A delete or rename of the item, or of a folder above it, is kept for the server.</summary>
    private bool IsKeptOffline(string localPath) =>
        _offlinePaths.Keys.Any(path =>
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

        if (Directory.Exists(newLocalPath) && !isTrackedDirectory)
        {
            // A folder the server does not have yet (Explorer's "New folder",
            // renamed before it was created there): made under the new name.
            QueueCreateFolder(newLocalPath);
            return;
        }

        // Offline: kept until the server answers. The item is already under
        // its new name here, and listings leave both names alone meanwhile.
        if (_server is { IsOnline: false } && (isTrackedDirectory || record is not null))
        {
            KeepForLater(PendingChangeKind.Rename, oldRemoteRelative, LocalPathOf(oldRelative), newLocalPath);
            return;
        }

        try
        {
            await RenameTrackedAsync(oldRelative, newRelative, newLocalPath, record, isTrackedDirectory, source).ConfigureAwait(false);
        }
        catch (Exception ex) when (_server is not null && ServerConnection.IsUnreachable(ex))
        {
            _server.Failed(ex.Message);
            KeepForLater(PendingChangeKind.Rename, oldRemoteRelative, LocalPathOf(oldRelative), newLocalPath);
        }
    }

    private async Task RenameTrackedAsync(
        string oldRelative,
        string newRelative,
        string newLocalPath,
        FileRecord? record,
        bool isTrackedDirectory,
        SyncRootConnection.RenameSource? source)
    {
        if (isTrackedDirectory)
        {
            await RenameRemoteDirectoryAsync(oldRelative, newRelative, newLocalPath, source).ConfigureAwait(false);
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
    /// identity, and marks it in sync again if it was unchanged just before
    /// the rename (in sync and as it was synced, see MatchesLastSync) and has
    /// not been written since (the platform clears in-sync on every rename,
    /// which would otherwise make it look edited and get it downloaded and
    /// uploaded again). An item that had local changes keeps waiting for its
    /// upload.
    /// </summary>
    private void UpdateRenamedPlaceholder(string localPath, string identityPath, SyncRootConnection.RenameSource? source)
    {
        var identity = CloudPlaceholderService.BuildFileIdentity(identityPath);
        var record = _store.GetByRemotePath(identityPath);

        // A rename sent later (it was kept while offline) has no state from
        // before it; the file's size and write time against its last sync
        // tell instead. A folder has no content to compare.
        var unchanged = source is null
            ? Directory.Exists(localPath) || (record?.SyncedLocalState is not null && MatchesLastSync(localPath, record))
            : source.InSync &&
              File.GetLastWriteTimeUtc(localPath) == source.LastWriteUtc &&
              (!File.Exists(localPath) || new FileInfo(localPath).Length == source.Length) &&
              (!File.Exists(localPath) || MatchesLastSync(localPath, record));

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
            var deferred = false;
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
                // free up space / keep on device) leave the file as it was
                // synced; a user edit does not. So a recent provider write
                // only suppresses the upload while the file is unchanged, and
                // an edit made right after one is still uploaded.
                if (IsProviderWrite(localPath) && !HasUnsyncedLocalChanges(localPath))
                {
                    return;
                }

                // A rename or delete of it is kept for the server: sending that
                // decides whether there is anything to upload (see
                // SendPendingChangesAsync); a rename alone looks like an edit.
                if (IsKeptOffline(localPath))
                {
                    return;
                }

                // Offline, nothing is sent: the file is uploaded once the
                // server answers, as Nextcloud's next sync run would.
                if (_server is { IsOnline: false })
                {
                    deferred = true;
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
                if (_server is not null && ServerConnection.IsUnreachable(ex))
                {
                    _server.Failed(ex.Message);
                    deferred = true;
                }
            }
            finally
            {
                lock (_uploading)
                {
                    _uploading.Remove(localPath);
                }

                // Only now, so the upload once online is not turned away as
                // one still running.
                if (deferred)
                {
                    DeferUpload(localPath);
                }
            }
        });
    }

    /// <summary>Uploads a file once the server answers again.</summary>
    private void DeferUpload(string localPath)
    {
        if (_deferredUploads.TryAdd(localPath, 0))
        {
            _log?.Invoke($"Offline: '{RelativeOf(localPath)}' is uploaded once the server answers.");
        }

        // Back online meanwhile: Changed has already fired.
        if (_server is { IsOnline: true })
        {
            UploadDeferred();
        }
        else
        {
            _server?.CheckNow();
        }
    }

    private void UploadDeferred()
    {
        foreach (var localPath in _deferredFolders.Keys)
        {
            if (_deferredFolders.TryRemove(localPath, out _))
            {
                QueueCreateFolder(localPath);
            }
        }

        foreach (var localPath in _deferredUploads.Keys)
        {
            if (_deferredUploads.TryRemove(localPath, out _))
            {
                QueueUpload(localPath);
            }
        }
    }

    /// <summary>
    /// A folder made here, empty or not, is made on the server too, as
    /// Nextcloud and OneDrive do (uploading a file creates the folders above
    /// it, but an empty folder has nothing to upload). Held for a moment like
    /// an upload: Explorer's "New folder" is usually renamed right away, and
    /// the rename then brings the name that stays.
    /// </summary>
    private void QueueCreateFolder(string localPath)
    {
        if (_populating || IsStateFile(localPath) || IsProviderWrite(localPath))
        {
            return;
        }

        lock (_creatingFolders)
        {
            if (!_creatingFolders.Add(localPath))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(750).ConfigureAwait(false);
                await CreateRemoteFolderAsync(localPath).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (_server is not null && ServerConnection.IsUnreachable(ex))
                {
                    _server.Failed(ex.Message);
                    DeferFolder(localPath);
                }
                else
                {
                    _log?.Invoke($"Upload failed for folder '{RelativeOf(localPath)}': {ex.Message}");
                }
            }
            finally
            {
                lock (_creatingFolders)
                {
                    _creatingFolders.Remove(localPath);
                }
            }
        });
    }

    private async Task CreateRemoteFolderAsync(string localPath)
    {
        if (!Directory.Exists(localPath))
        {
            return; // renamed or deleted meanwhile
        }

        var relative = RelativeOf(localPath);
        var remotePath = relative + "/";
        if (_store.GetByRemotePath(remotePath) is not null && (IsSimulated || _placeholders.QueryState(localPath).IsPlaceholder))
        {
            return; // a folder the server already has
        }

        if (_server is { IsOnline: false })
        {
            DeferFolder(localPath);
            return;
        }

        var (fs, remoteDirectory) = ResolveRemoteFile(relative);
        _rc ??= new RcloneRcClient(_rclone);
        await _rc.MakeDirectoryAsync(fs, remoteDirectory).ConfigureAwait(false);
        _store.UpsertFile(
            remotePath: remotePath,
            localPath: localPath,
            fileIdentity: remotePath,
            fileId: 0,
            isPlaceholder: !IsSimulated,
            isDirectory: true,
            size: 0,
            modifiedUtc: null,
            inSync: true);
        _localChangeAt[remotePath] = DateTimeOffset.UtcNow;

        // Everything in it is here, so it is fully populated: listed, and
        // kept current by the background sync from now on.
        if (!IsSimulated && !_placeholders.QueryState(localPath).IsPlaceholder)
        {
            _placeholders.ConvertToPlaceholder(localPath, CloudPlaceholderService.BuildFileIdentity(remotePath), markInSync: true);
        }

        _log?.Invoke($"Created folder '{relative}' on {fs}.");

        // A folder moved or copied in is reported alone, without what it holds.
        foreach (var file in Directory.EnumerateFiles(localPath))
        {
            QueueUpload(file);
        }

        foreach (var folder in Directory.EnumerateDirectories(localPath))
        {
            QueueCreateFolder(folder);
        }
    }

    private void DeferFolder(string localPath)
    {
        if (_deferredFolders.TryAdd(localPath, 0))
        {
            _log?.Invoke($"Offline: folder '{RelativeOf(localPath)}' is created on the server once it answers.");
        }

        if (_server is { IsOnline: true })
        {
            UploadDeferred();
        }
        else
        {
            _server?.CheckNow();
        }
    }

    /// <summary>
    /// Keeps a delete or rename for the server while it cannot be reached:
    /// in the store, so it survives a restart, and marked so that listings
    /// do not bring the item back meanwhile. Sent once the server answers.
    /// </summary>
    private void KeepForLater(PendingChangeKind kind, string remotePath, string localPath, string? newLocalPath = null)
    {
        _store.AddPendingChange(kind, remotePath, localPath, newLocalPath);
        _offlinePaths[localPath] = 0;
        if (newLocalPath is not null)
        {
            _offlinePaths[newLocalPath] = 0;
        }

        _log?.Invoke(kind == PendingChangeKind.Delete
            ? $"Offline: '{remotePath}' is deleted on the server once it answers."
            : $"Offline: '{remotePath}' is renamed to '{RelativeOf(newLocalPath!)}' on the server once it answers.");
        _server?.CheckNow();
    }

    /// <summary>
    /// Sends the deletes and renames kept while offline, in the order they
    /// were made; a caller that finds this running waits for it, so none of
    /// them is still on its way once this returns.
    /// </summary>
    private async Task SendPendingChangesAsync()
    {
        if (_server is { IsOnline: false })
        {
            return;
        }

        await _pendingGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var change in _store.GetPendingChanges())
            {
                if (_server is { IsOnline: false })
                {
                    return; // the rest waits for the next time it answers
                }

                // Taken first; a change that still cannot be sent is kept again.
                _store.RemovePendingChange(change.Id);
                try
                {
                    if (change.Kind == PendingChangeKind.Delete)
                    {
                        await HandleRemoteDeleteAsync(change.RemotePath).ConfigureAwait(false);
                    }
                    else
                    {
                        await HandleRemoteRenameAsync(change.RemotePath, change.NewLocalPath!).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _log?.Invoke($"Delete propagation failed for '{change.RemotePath}' (kept while offline): {ex.Message}");
                }
                finally
                {
                    // Listings leave the item alone until here, unless it was kept again.
                    var kept = _store.GetPendingChanges();
                    foreach (var path in new[] { change.LocalPath, change.NewLocalPath })
                    {
                        if (path is not null && !kept.Any(c => string.Equals(c.LocalPath, path, StringComparison.OrdinalIgnoreCase) ||
                                                               string.Equals(c.NewLocalPath, path, StringComparison.OrdinalIgnoreCase)))
                        {
                            _offlinePaths.TryRemove(path, out _);
                        }
                    }
                }

                // What was edited under the new name, offline too, goes now.
                if (change.NewLocalPath is { } renamed && !IsKeptOffline(renamed))
                {
                    var files = Directory.Exists(renamed)
                        ? Directory.EnumerateFiles(renamed, "*", SearchOption.AllDirectories)
                        : File.Exists(renamed) ? [renamed] : [];
                    foreach (var file in files.Where(HasUnsyncedLocalChanges))
                    {
                        QueueUpload(file);
                    }
                }
            }
        }
        finally
        {
            _pendingGate.Release();
        }
    }

    /// <summary>
    /// Changes made here while the service was not running, found when it
    /// starts (Nextcloud's local discovery does the same): edited or new
    /// files are uploaded, new folders created on the server. Deletes are
    /// not taken from it: unregistering the drive removes every online-only
    /// file, and that must never delete them on the server. What is missing
    /// comes back from the server instead.
    /// </summary>
    private void DiscoverLocalChanges()
    {
        int files = 0, folders = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var folder in Directory.EnumerateDirectories(_syncRootPath, "*", options))
        {
            if (_store.GetByRemotePath(RelativeOf(folder) + "/") is null && !_placeholders.QueryState(folder).IsPlaceholder)
            {
                QueueCreateFolder(folder);
                folders++;
            }
        }

        foreach (var file in Directory.EnumerateFiles(_syncRootPath, "*", options))
        {
            if (!IsStateFile(file) && HasUnsyncedLocalChanges(file))
            {
                QueueUpload(file);
                files++;
            }
        }

        if (files + folders > 0)
        {
            _log?.Invoke($"Changes made while OpenCfDrive was not running: {files} file(s) and {folders} folder(s) are sent to the server.");
        }
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
        var sent = (Length: info.Length, WriteUtc: info.LastWriteTimeUtc);
        var relative = Path.GetRelativePath(_syncRootPath, localPath).Replace('\\', '/');
        var record = _store.GetByLocalPath(localPath);

        // Nothing new to send? Size alone cannot tell (a same-size edit is
        // still an edit).
        var state = _placeholders.QueryState(localPath);
        if (!IsSimulated)
        {
            // Unchanged means in sync and still as it was synced (see
            // MatchesLastSync): downloading, freeing space and server updates
            // keep both, and a rename is marked in sync again once it reaches
            // the server. A dehydrated placeholder is unchanged, so its empty
            // local copy is never sent over good server data.
            if (state is { IsPlaceholder: true, InSync: true } && MatchesLastSync(localPath, record))
            {
                return false;
            }

            // A plain file still as it was last synced (unregistering leaves
            // downloaded files so): made a placeholder again, not sent again.
            if (!state.IsPlaceholder && record is { IsDirectory: false, SyncedLocalState: not null } && MatchesLastSync(localPath, record))
            {
                _placeholders.ConvertToPlaceholder(localPath, CloudPlaceholderService.BuildFileIdentity(record.RemotePath), markInSync: true);
                return false;
            }
        }
        else if (_lastSyncedState.TryGetValue(localPath, out var synced) &&
                 synced == (info.Length, info.LastWriteTimeUtc))
        {
            return false;
        }

        // A local change is only sent over the version this client last saw.
        // If the server has moved on since, nothing is lost, as in Nextcloud:
        // a file it changed, or one it got from elsewhere at this path, takes
        // the server's version and the local one is kept as a conflict copy;
        // a file it deleted is uploaded again.
        _rc ??= new RcloneRcClient(_rclone);
        var server = await _rc.StatAsync(_remote, relative, cancellationToken).ConfigureAwait(false);
        if (ServerMovedOn(record, server))
        {
            if (server is null)
            {
                _store.DeleteByRemotePath(record!.RemotePath);
                _log?.Invoke($"Server deleted '{relative}', which has local changes: they are kept and uploaded again.");
                record = null;
            }
            else if (server.IsDirectory)
            {
                // A folder took the name on the server: the file is kept
                // under its conflict name, and the folder comes with the
                // next listing.
                if (KeepConflictCopy(localPath, relative) is not null)
                {
                    File.Delete(localPath);
                    if (record is not null)
                    {
                        _store.DeleteByRemotePath(record.RemotePath);
                    }
                }

                return false;
            }
            else if (record is null && SameAsServer(info, server))
            {
                // Added on both sides, the same file (Nextcloud takes equal
                // size and time for equal): nothing to send.
                _store.UpsertFile(
                    remotePath: relative,
                    localPath: localPath,
                    fileIdentity: relative,
                    fileId: state.FileId,
                    isPlaceholder: true,
                    isDirectory: false,
                    size: server.Size,
                    modifiedUtc: server.ModifiedTime,
                    inSync: true,
                    localSize: sent.Length,
                    localWriteUtc: sent.WriteUtc);
                if (!IsSimulated)
                {
                    _placeholders.ConvertToPlaceholder(localPath, CloudPlaceholderService.BuildFileIdentity(relative), markInSync: true);
                }

                RememberSyncedState(localPath);
                return false;
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
                return false;
            }
        }

        var (fs, remoteFile) = ResolveRemoteFile(relative);
        var remoteDirectory = remoteFile.Contains('/')
            ? remoteFile[..remoteFile.LastIndexOf('/')]
            : string.Empty;

        await _rc.UploadFileAsync(fs, remoteDirectory, localPath, cancellationToken).ConfigureAwait(false);

        // The store holds the server's metadata, which is what later listings
        // are compared against. Most remotes stamp their own modification
        // time on upload, so the local one would read as a server-side change.
        // The file's own size and time as sent are kept beside it: what an
        // edit is told by from now on. Taken before the upload, so a write
        // during it still counts as an edit.
        var (serverSize, serverTime) = await GetServerMetadataAsync(relative, cancellationToken).ConfigureAwait(false);

        _store.UpsertFile(
            remotePath: relative,
            localPath: localPath,
            fileIdentity: relative,
            fileId: state.FileId != 0 ? state.FileId : record?.FileId ?? 0,
            isPlaceholder: true,
            isDirectory: false,
            size: serverSize ?? sent.Length,
            modifiedUtc: serverTime ?? new DateTimeOffset(sent.WriteUtc, TimeSpan.Zero),
            inSync: true,
            localSize: sent.Length,
            localWriteUtc: sent.WriteUtc);
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
                // With the file's size: the platform's own record of it is
                // what Free up space later downloads back, and an edit in
                // place does not change it (EditedPlaceholderContractTest).
                // The size it has now, not as sent: a write since then is
                // uploaded next, and must not be given a size it no longer has.
                _placeholders.MarkInSync(localPath, identity, new FileInfo(localPath).Length);
            }
            else
            {
                _placeholders.ConvertToPlaceholder(localPath, identity, markInSync: true);
            }

            // Free up space was asked for while this was still to be sent.
            if ((File.GetAttributes(localPath) & UnpinnedAttribute) != 0)
            {
                ApplyPinState(localPath, keepLocal: false);
            }
        }

        return true;
    }

    /// <summary>The same file on both sides, by Nextcloud's test: equal size and modification time.</summary>
    private static bool SameAsServer(FileInfo local, CloudItem server) =>
        local.Length == server.Size &&
        server.ModifiedTime is { } serverTime &&
        Math.Abs((serverTime.UtcDateTime - local.LastWriteTimeUtc).TotalSeconds) < 1;


    public async ValueTask DisposeAsync()
    {
        _scheduler?.Dispose();
        _scheduler = null;

        _watcher?.Dispose();
        _watcher = null;

        _connection?.Dispose();
        _connection = null;

        _server?.Dispose();
        _server = null;

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
