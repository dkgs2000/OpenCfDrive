using System.Reflection;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Service;

public sealed class SyncWorker(
    ILogger<SyncWorker> logger,
    ActivityLog activity,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private CloudSyncEngine? _engine;
    private SqliteMetadataStore? _store;
    private ServiceControlServer? _control;
    private EffectiveSettings? _settings;
    private SyncRootStatus? _initialized;
    private volatile ServiceState _state = ServiceState.Starting;
    private volatile string _detail = "Registering and connecting the drive.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        DriveSettings saved;
        try
        {
            saved = DriveSettingsFile.Load();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Ignoring the settings file: {Message}", ex.Message);
            saved = new DriveSettings();
        }

        // Explicit environment values win, which keeps scripted runs deterministic.
        var settings = DriveSettingsFile.Resolve(saved);
        _settings = settings;
        var syncRoot = settings.SyncRoot.Value;
        var remote = settings.Remote.Value;
        var rcUrl = settings.RcUrl.Value;
        logger.LogInformation(
            "Drive '{SyncRoot}' for {Remote} through rclone at {RcUrl} (settings: {SettingsPath})",
            syncRoot, remote, rcUrl, DriveSettingsFile.Path);
        if (!settings.HasRemote)
        {
            logger.LogWarning("No rclone remote is configured, so '{Remote}' is used. Choose one in the OpenCfDrive app.", remote);
        }

        // Engine state never lives in the sync root: everything under that path
        // is user data and is watched/uploaded.
        Directory.CreateDirectory(SyncStatePaths.StateRoot);
        var dbPath = SyncStatePaths.DatabasePath(syncRoot);

        SqliteMetadataStore store;
        try
        {
            store = new SqliteMetadataStore(dbPath);
            store.Initialize();
            logger.LogInformation("Using SQLite state at '{DbPath}'", dbPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            dbPath = SyncStatePaths.FallbackDatabasePath(syncRoot);
            logger.LogWarning(ex, "LocalAppData state is not writable; falling back to '{DbPath}'", dbPath);
            store = new SqliteMetadataStore(dbPath);
            store.Initialize();
        }

        // Which remote this folder's state belongs to (the app checks it before
        // pointing the folder at another remote).
        store.SetConfig("OpenCfDriveRoot", syncRoot);
        store.SetConfig("RcloneFs", remote);
        store.SetConfig("RcloneRcUrl", rcUrl);
        _store = store;

        _control = new ServiceControlServer(
            ServiceControl.PipeName(syncRoot),
            BuildStatus,
            lifetime.StopApplication,
            message => logger.LogInformation("{Message}", message));
        _control.Start();

        _engine = new CloudSyncEngine(syncRoot, remote, rcUrl, dbPath, msg => logger.LogInformation("{Message}", msg));

        try
        {
            var status = await _engine.InitializeAsync(stoppingToken).ConfigureAwait(false);
            _initialized = status;

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
                (_state, _detail) = status.Registered && status.Connected
                    ? (ServiceState.Syncing, "The drive is registered with Explorer and connected.")
                    : (ServiceState.Failed, "The drive could not be registered or connected; see the log.");
            }
            else
            {
                logger.LogError("Cloud Files is not available ({Reason}); nothing is synced.", status.CapabilityReason);
                (_state, _detail) = (ServiceState.Unavailable, status.CapabilityReason);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sync engine initialisation failed");
            (_state, _detail) = (ServiceState.Failed, ex.Message);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    private ServiceStatus BuildStatus()
    {
        var settings = _settings!;
        var items = 0;
        try
        {
            items = _store?.Count() ?? 0;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // Busy for a moment; the next request counts again.
        }

        var initialized = _initialized;
        return new ServiceStatus(
            Environment.ProcessId,
            _startedAt,
            Version,
            settings.SyncRoot.Value,
            settings.Remote.Value,
            settings.RcUrl.Value,
            _state,
            _detail,
            initialized?.Registered ?? false,
            initialized?.Connected ?? false,
            items,
            _engine?.BytesFetched ?? 0,
            ConsoleOutput.ActiveLogPath ?? "",
            activity.Recent(100));
    }

    private static string Version =>
        (typeof(SyncWorker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_control is not null)
        {
            await _control.DisposeAsync().ConfigureAwait(false);
        }

        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
