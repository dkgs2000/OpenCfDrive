using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Control;

namespace OpenCfDrive.App;

internal enum Health
{
    Idle,
    Busy,
    Good,
    Problem,
}

/// <summary>Everything the app shows, read at one moment.</summary>
internal sealed record DriveSnapshot(
    DriveSettings Saved,
    string? SettingsProblem,
    EffectiveSettings Settings,
    bool Running,
    ServiceStatus? Status,
    RcloneProbeResult? Rclone,
    RemoteInfo? RemoteInfo)
{
    /// <summary>The remote the service syncs with, or would once started.</summary>
    public string Remote => Status?.Remote ?? Settings.Remote.Value;

    /// <summary>One word for the tray and the window's header, and a sentence on it.</summary>
    public (Health Health, string Headline, string Detail) Summary
    {
        get
        {
            if (SettingsProblem is not null)
            {
                return (Health.Problem, "Settings problem", SettingsProblem);
            }

            if (!Running)
            {
                if (!Settings.HasRemote)
                {
                    return (Health.Idle, "Not set up", "Choose your rclone remote in Settings to start.");
                }

                return Settings.ManageRclone && Rclone is { Answers: false }
                    ? (Health.Problem, "rclone not found", $"{Rclone.Problem} Choose rclone.exe or download it in Settings.")
                    : (Health.Idle, "Stopped", "The sync service is not running.");
            }

            if (Status is null)
            {
                return (Health.Busy, "Running", "The sync service runs, but has not reported its status.");
            }

            // The rclone the service runs itself reports through the service.
            var ownRclone = Status.Rclone is { Managed: true } own ? own : null;
            var (health, headline, detail) = Status.State switch
            {
                ServiceState.Starting => (Health.Busy, "Starting", Status.Detail),
                ServiceState.Offline => (Health.Busy, "Offline", Status.Detail),
                ServiceState.Unavailable => (Health.Problem, "Cloud Files unavailable", Status.Detail),
                ServiceState.Failed when ownRclone?.State == "NotFound" => (Health.Problem, "rclone not found", Status.Detail),
                ServiceState.Failed => (Health.Problem, "Problem", Status.Detail),
                _ when ownRclone is { State: not "Running" } => (Health.Problem, "rclone stopped", ownRclone.Detail),
                _ when ownRclone is null && Rclone is { Usable: false } => (Health.Problem, "rclone unreachable", Rclone.Problem ?? ""),
                _ => (Health.Good, "Running", Status.Detail),
            };

            return RunsWithOtherSettings
                ? (health, headline, $"{detail} Restart it to use the saved settings.")
                : (health, headline, detail);
        }
    }

    /// <summary>The service started before the settings in effect now were saved.</summary>
    public bool RunsWithOtherSettings =>
        Status is not null &&
        (!string.Equals(Status.SyncRoot, Settings.SyncRoot.Value, StringComparison.OrdinalIgnoreCase) ||
         !string.Equals(Status.Remote, Settings.Remote.Value, StringComparison.Ordinal) ||
         (Status.Rclone?.Managed ?? true) != Settings.ManageRclone ||
         (!Settings.ManageRclone && !string.Equals(Status.RcUrl, Settings.RcUrl.Value, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Reads the settings, the service's status and rclone's, whenever asked.</summary>
internal sealed class DriveMonitor
{
    private static readonly TimeSpan ProgramCheckInterval = TimeSpan.FromSeconds(30);

    private bool _refreshing;
    private bool _describing;
    private DateTime _rcloneCheckedAt;
    private DateTime _remoteDescribedAt;

    public DriveSnapshot? Latest { get; private set; }

    /// <summary>Raised on the thread that refreshed (the UI thread).</summary>
    public event Action<DriveSnapshot>? Updated;

    /// <param name="rcloneMaxAge">How old rclone's last answer may be before it is asked again.</param>
    /// <param name="remoteMaxAge">How old the remote's description (and space) may be.</param>
    public async Task RefreshAsync(TimeSpan rcloneMaxAge, TimeSpan remoteMaxAge)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            DriveSettings saved;
            string? problem = null;
            try
            {
                saved = DriveSettingsFile.Load();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                saved = new DriveSettings();
                problem = ex.Message;
            }

            var settings = DriveSettingsFile.Resolve(saved);
            var running = ServiceControl.IsRunning(settings.SyncRoot.Value);
            var status = running
                ? await ServiceControl.GetStatusAsync(settings.SyncRoot.Value, TimeSpan.FromSeconds(2))
                : null;

            // Asking the rclone program means running it a few times; the
            // service reports its own rclone's state live anyway.
            var access = RcloneAccess.From(settings);
            if (access.Managed && rcloneMaxAge > TimeSpan.Zero && rcloneMaxAge < ProgramCheckInterval)
            {
                rcloneMaxAge = ProgramCheckInterval;
            }

            var rclone = Latest?.Rclone;
            if (rclone is null || rclone.Key != access.Key || DateTime.UtcNow - _rcloneCheckedAt >= rcloneMaxAge)
            {
                rclone = await RcloneProbe.CheckAsync(access);
                _rcloneCheckedAt = DateTime.UtcNow;
            }

            var snapshot = new DriveSnapshot(saved, problem, settings, running, status, rclone, null);
            var described = Latest?.RemoteInfo;
            var known = (settings.HasRemote || running) && rclone.Usable;
            Latest = snapshot with { RemoteInfo = known && described?.Remote == snapshot.Remote ? described : null };
            Updated?.Invoke(Latest);

            if (known && (Latest.RemoteInfo is null || DateTime.UtcNow - _remoteDescribedAt >= remoteMaxAge))
            {
                _ = DescribeRemoteAsync(access, snapshot.Remote);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Asks rclone about the remote off to the side: the server may take a while to say its space.</summary>
    private async Task DescribeRemoteAsync(RcloneAccess access, string remote)
    {
        if (_describing)
        {
            return;
        }

        _describing = true;
        try
        {
            var info = await RcloneProbe.DescribeAsync(access, remote);
            _remoteDescribedAt = DateTime.UtcNow;
            if (Latest is { } latest && latest.Remote == remote)
            {
                Latest = latest with { RemoteInfo = info };
                Updated?.Invoke(Latest);
            }
        }
        finally
        {
            _describing = false;
        }
    }
}
