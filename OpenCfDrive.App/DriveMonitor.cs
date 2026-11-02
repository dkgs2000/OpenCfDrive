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
    RcloneProbeResult? Rclone)
{
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
                return Settings.HasRemote
                    ? (Health.Idle, "Stopped", "The sync service is not running.")
                    : (Health.Idle, "Not set up", "Choose your rclone remote in Settings to start.");
            }

            if (Status is null)
            {
                return (Health.Busy, "Running", "The sync service runs, but has not reported its status.");
            }

            var (health, headline, detail) = Status.State switch
            {
                ServiceState.Starting => (Health.Busy, "Starting", Status.Detail),
                ServiceState.Unavailable => (Health.Problem, "Cloud Files unavailable", Status.Detail),
                ServiceState.Failed => (Health.Problem, "Problem", Status.Detail),
                _ => Rclone is { Usable: false }
                    ? (Health.Problem, "rclone unreachable", Rclone.Problem ?? "")
                    : (Health.Good, "Running", Status.Detail),
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
         !string.Equals(Status.RcUrl, Settings.RcUrl.Value, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the settings, the service's status and rclone's, whenever asked.</summary>
internal sealed class DriveMonitor
{
    private bool _refreshing;
    private DateTime _rcloneCheckedAt;

    public DriveSnapshot? Latest { get; private set; }

    /// <summary>Raised on the thread that refreshed (the UI thread).</summary>
    public event Action<DriveSnapshot>? Updated;

    /// <param name="rcloneMaxAge">How old rclone's last answer may be before it is asked again.</param>
    public async Task RefreshAsync(TimeSpan rcloneMaxAge)
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

            var rclone = Latest?.Rclone;
            if (rclone is null || rclone.RcUrl != settings.RcUrl.Value || DateTime.UtcNow - _rcloneCheckedAt >= rcloneMaxAge)
            {
                rclone = await RcloneProbe.CheckAsync(settings.RcUrl.Value);
                _rcloneCheckedAt = DateTime.UtcNow;
            }

            Latest = new DriveSnapshot(saved, problem, settings, running, status, rclone);
            Updated?.Invoke(Latest);
        }
        finally
        {
            _refreshing = false;
        }
    }
}
