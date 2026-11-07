namespace OpenCfDrive.Core.Control;

/// <summary>
/// What the notification area icon shows, as OneDrive's and Nextcloud's do:
/// the plain icon when all is synced, and a small badge otherwise.
/// </summary>
public enum DriveIndicator
{
    /// <summary>Connected, with nothing to send or download: the plain icon.</summary>
    UpToDate,

    /// <summary>Starting, sending or downloading.</summary>
    Syncing,

    /// <summary>The server cannot be reached; changes wait.</summary>
    Offline,

    /// <summary>The sync service is not running.</summary>
    Paused,

    /// <summary>No remote is chosen yet.</summary>
    NotSetUp,

    /// <summary>Some files could not be synced; the rest syncs.</summary>
    Warning,

    /// <summary>Nothing syncs: a settings, rclone or service problem.</summary>
    Problem,
}

public static class DriveIndicators
{
    /// <param name="problem">The app sees a problem that stops the drive syncing.</param>
    /// <param name="running">The sync service runs.</param>
    /// <param name="setUp">A remote is chosen.</param>
    /// <param name="status">What the service last said, or null if it has not answered.</param>
    public static DriveIndicator Of(bool problem, bool running, bool setUp, ServiceStatus? status)
    {
        if (problem)
        {
            return DriveIndicator.Problem;
        }

        if (!running)
        {
            return setUp ? DriveIndicator.Paused : DriveIndicator.NotSetUp;
        }

        if (status is null)
        {
            return DriveIndicator.Syncing; // just started, not answering yet
        }

        var activity = status.Activity ?? SyncActivity.None;
        return status.State switch
        {
            ServiceState.Starting => DriveIndicator.Syncing,
            ServiceState.Offline => DriveIndicator.Offline,
            ServiceState.Unavailable or ServiceState.Failed => DriveIndicator.Problem,
            _ when activity.Busy => DriveIndicator.Syncing,
            _ when activity.Failing > 0 => DriveIndicator.Warning,
            _ => DriveIndicator.UpToDate,
        };
    }
}
