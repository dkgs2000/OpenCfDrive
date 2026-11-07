using System.Reflection;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Service;

public sealed class SyncWorker(
    ILogger<SyncWorker> logger,
    ActivityLog activity,
    IHostApplicationLifetime lifetime) : BackgroundService
{
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private readonly ProblemLog _problems = new();
    private CloudSyncEngine? _engine;
    private SqliteMetadataStore? _store;
    private ServiceControlServer? _control;
    private EffectiveSettings? _settings;
    private SyncRootStatus? _initialized;
    private RcloneDaemon? _daemon;
    private RcloneStatus? _rcloneStatus;
    private volatile ServiceState _state = ServiceState.Starting;
    private volatile string _detail = "Registering and connecting the drive.";

    // The number of the last change a pulse shows; a watching app keeps
    // whichever of a pulse and a status is newer.
    private long _revision = 1;

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
            "Drive '{SyncRoot}' for {Remote} through {Rclone} (settings: {SettingsPath})",
            syncRoot, remote, settings.ManageRclone ? "an rclone it runs itself" : $"the rclone at {rcUrl}", DriveSettingsFile.Path);
        foreach (var (retired, replacement) in DriveSettingsFile.RetiredVariables)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(retired)))
            {
                logger.LogWarning("{Retired} is set but no longer read; {Replacement} replaced it.", retired, replacement);
            }
        }

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
            message => logger.LogInformation("{Message}", message),
            syncNow: () => _engine?.SyncNowAsync() ?? Task.CompletedTask,
            pulse: BuildPulse);
        _control.Start();

        var endpoint = await ConnectRcloneAsync(settings, stoppingToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            // Nothing to sync with; stay up so the app can say why.
            await WaitUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
            return;
        }

        _engine = new CloudSyncEngine(syncRoot, remote, endpoint, dbPath, msg => logger.LogInformation("{Message}", msg));

        // Every failed sync step, for the app to tell the user about.
        _engine.ProblemReported += message =>
        {
            logger.LogWarning("{Message}", message);
            _problems.Report(message);
            Changed();
        };

        // Sending, downloading, offline: the app's icon shows it as it happens.
        _engine.ActivityChanged += Changed;

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
                _engine.StartBackgroundSync();
                SetState(status.Registered && status.Connected
                    ? (ServiceState.Syncing, "The drive is registered with Explorer and connected.")
                    : (ServiceState.Failed, "The drive could not be registered or connected; see the log."));
            }
            else
            {
                logger.LogError("Cloud Files is not available ({Reason}); nothing is synced.", status.CapabilityReason);
                SetState((ServiceState.Unavailable, status.CapabilityReason));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Sync engine initialisation failed");
            SetState((ServiceState.Failed, ex.Message));
        }

        await WaitUntilStoppedAsync(stoppingToken).ConfigureAwait(false);
    }

    private static async Task WaitUntilStoppedAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>rclone's own log, next to the service's: %LOCALAPPDATA%\OpenCfDrive\logs\rclone.log.</summary>
    private static string RcloneLogPath => Path.Combine(Path.GetDirectoryName(ConsoleOutput.LogPath)!, "rclone.log");

    /// <summary>
    /// The rclone remote control to sync through: one the service starts and
    /// owns (the default), or the one the user runs at the configured
    /// address. Null when there is none; the status then says why.
    /// </summary>
    private async Task<RcloneEndpoint?> ConnectRcloneAsync(EffectiveSettings settings, CancellationToken stoppingToken)
    {
        if (!settings.ManageRclone)
        {
            _rcloneStatus = new RcloneStatus(
                false, settings.RcUrl.Value, null, null, null, null, "External", "rclone runs outside OpenCfDrive.", null);
            return settings.RcEndpoint;
        }

        var program = RcloneLocator.Find(settings.RclonePath);
        if (program is null)
        {
            var why = settings.RclonePath is null
                ? "rclone was not found: it is not on PATH and was not downloaded. Choose rclone.exe, or download it, in OpenCfDrive's Settings."
                : $"rclone was not found at '{settings.RclonePath}'.";
            logger.LogError("{Why}", why);
            _rcloneStatus = new RcloneStatus(true, "", settings.RclonePath, null, null, null, "NotFound", why, null);
            SetState((ServiceState.Failed, why));
            return null;
        }

        _rcloneStatus = new RcloneStatus(
            true, "", program.Path, program.Source.ToString(), null, null, "Starting", "Starting rclone.", RcloneLogPath);
        SetState((_state, "Starting rclone."));
        try
        {
            _daemon = await RcloneDaemon.StartAsync(
                program,
                settings.RcloneConfig,
                RcloneLogPath,
                message => logger.LogInformation("{Message}", message),
                stoppingToken).ConfigureAwait(false);
            SetState((_state, "Registering and connecting the drive."));
            return _daemon.Endpoint;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            var why = $"rclone could not be started: {ex.Message}";
            logger.LogError("{Why}", why);
            _rcloneStatus = _rcloneStatus with { State = "Failed", Detail = why };
            SetState((ServiceState.Failed, why));
            return null;
        }
    }

    private RcloneStatus? RcloneNow() =>
        _daemon is { } daemon
            ? new RcloneStatus(
                true,
                daemon.Endpoint.BaseUrl,
                daemon.Program.Path,
                daemon.Program.Source.ToString(),
                daemon.Version,
                daemon.ProcessId,
                daemon.State.ToString(),
                daemon.Detail,
                daemon.LogPath)
            : _rcloneStatus;

    private void SetState((ServiceState State, string Detail) state)
    {
        (_state, _detail) = state;
        Changed();
    }

    /// <summary>Something a pulse shows has changed: it is numbered, and sent to a watching app.</summary>
    private void Changed()
    {
        Interlocked.Increment(ref _revision);
        _control?.NotifyChanged();
    }

    /// <summary>The state to report: a connected drive whose server cannot be reached is offline.</summary>
    private (ServiceState State, string Detail) StateNow() =>
        _state == ServiceState.Syncing && _engine is { IsOnline: false } engine
            ? (ServiceState.Offline,
               $"The server cannot be reached ({engine.OfflineReason}). Folders show what is on this PC, and changes " +
               "are sent once it answers; it is checked again when the network changes, when the drive is used, and every minute or so.")
            : (_state, _detail);

    private ServicePulse BuildPulse()
    {
        // The number first: what is read after it is at least that new.
        var revision = Interlocked.Read(ref _revision);
        var (state, detail) = StateNow();
        return new ServicePulse(Environment.ProcessId, revision, state, detail, _engine?.Activity ?? SyncActivity.None, _problems.Count);
    }

    private ServiceStatus BuildStatus()
    {
        var revision = Interlocked.Read(ref _revision);
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
        var (state, detail) = StateNow();
        return new ServiceStatus(
            Environment.ProcessId,
            _startedAt,
            Version,
            settings.SyncRoot.Value,
            settings.Remote.Value,
            settings.RcUrl.Value,
            state,
            detail,
            initialized?.Registered ?? false,
            initialized?.Connected ?? false,
            items,
            _engine?.BytesFetched ?? 0,
            ConsoleOutput.ActiveLogPath ?? "",
            activity.Recent(100),
            RcloneNow(),
            _problems.Count,
            _problems.Recent(),
            _engine?.Activity ?? SyncActivity.None,
            revision);
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

        // After the engine: it may still be finishing a request through it.
        if (_daemon is not null)
        {
            await _daemon.DisposeAsync().ConfigureAwait(false);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }
}
