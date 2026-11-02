using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Service;

public sealed class SyncWorker(ILogger<SyncWorker> logger) : BackgroundService
{
    private CloudSyncEngine? _engine;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var envRoot = Environment.GetEnvironmentVariable("OPENCFDRIVE_ROOT");
        var defaultRoot = string.IsNullOrWhiteSpace(envRoot) ? SyncStatePaths.DefaultSyncRoot : envRoot;

        // Engine state never lives in the sync root: everything under that path
        // is user data and is watched/uploaded.
        var stateRoot = SyncStatePaths.StateRoot;
        Directory.CreateDirectory(stateRoot);
        var dbPath = SyncStatePaths.DatabasePath(defaultRoot);

        SqliteMetadataStore store;
        try
        {
            store = new SqliteMetadataStore(dbPath);
            store.Initialize();
            logger.LogInformation("Using SQLite state at '{DbPath}'", dbPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            dbPath = SyncStatePaths.FallbackDatabasePath(defaultRoot);
            logger.LogWarning(ex, "LocalAppData state is not writable; falling back to '{DbPath}'", dbPath);
            store = new SqliteMetadataStore(dbPath);
            store.Initialize();
        }

        // Explicit environment values win, which keeps scripted runs deterministic.
        var syncRoot = !string.IsNullOrWhiteSpace(envRoot)
            ? envRoot
            : store.GetConfig("OpenCfDriveRoot");
        if (string.IsNullOrWhiteSpace(syncRoot))
        {
            syncRoot = defaultRoot;
        }

        var remote = Environment.GetEnvironmentVariable("RCLONE_FS");
        if (string.IsNullOrWhiteSpace(remote))
        {
            remote = store.GetConfig("RcloneFs");
        }
        if (string.IsNullOrWhiteSpace(remote))
        {
            remote = "e2e:";
        }

        var rcUrl = Environment.GetEnvironmentVariable("RCLONE_RC_URL");
        if (string.IsNullOrWhiteSpace(rcUrl))
        {
            rcUrl = store.GetConfig("RcloneRcUrl");
        }
        if (string.IsNullOrWhiteSpace(rcUrl))
        {
            rcUrl = "http://127.0.0.1:5572";
        }

        remote = SyncStatePaths.NormalizeRemote(remote);

        store.SetConfig("OpenCfDriveRoot", syncRoot);
        store.SetConfig("RcloneFs", remote);
        store.SetConfig("RcloneRcUrl", rcUrl);

        _engine = new CloudSyncEngine(syncRoot, remote, rcUrl, dbPath, msg => logger.LogInformation("{Message}", msg));

        try
        {
            var status = await _engine.InitializeAsync(stoppingToken).ConfigureAwait(false);

            logger.LogInformation(
                "Mode: {Mode} | cloudFiles={Supported} ({Reason}) | registered={Registered} | connected={Connected} | items={Items}",
                status.CloudFilesSupported ? "cloud" : "unavailable",
                status.CloudFilesSupported,
                status.CapabilityReason,
                status.Registered,
                status.Connected,
                status.RegisteredItems);

            if (status.CloudFilesSupported)
            {
                _engine.StartWatcher();
            }
            else
            {
                logger.LogError("Cloud Files is not available ({Reason}); nothing is synced.", status.CapabilityReason);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Sync engine initialisation failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
