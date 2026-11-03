namespace OpenCfDrive.Core.Control;

/// <summary>The running service's own account of itself, as the app shows it.</summary>
public sealed record ServiceStatus(
    int ProcessId,
    DateTimeOffset StartedAt,
    string Version,
    string SyncRoot,
    string Remote,
    string RcUrl,
    ServiceState State,
    string Detail,
    bool Registered,
    bool Connected,
    int ItemsTracked,
    long BytesDownloaded,
    string LogPath,
    IReadOnlyList<ActivityEntry> RecentActivity,
    RcloneStatus? Rclone = null);

/// <summary>The rclone the service talks to.</summary>
/// <param name="Managed">The service runs it itself; otherwise the user runs it at <paramref name="Url"/>.</param>
/// <param name="Program">The rclone program it runs, and <paramref name="Source"/> says how it was found.</param>
/// <param name="State">Running, Starting, Restarting, NotFound or Failed; for one the user runs, External.</param>
public sealed record RcloneStatus(
    bool Managed,
    string Url,
    string? Program,
    string? Source,
    string? Version,
    int? ProcessId,
    string State,
    string Detail,
    string? LogPath);

public enum ServiceState
{
    /// <summary>Registering and connecting the drive.</summary>
    Starting,

    /// <summary>The drive is registered and connected.</summary>
    Syncing,

    /// <summary>The server cannot be reached: folders show what is on this PC, and changes wait.</summary>
    Offline,

    /// <summary>The Cloud Files API is missing; nothing syncs.</summary>
    Unavailable,

    /// <summary>Startup failed; Detail says why.</summary>
    Failed,
}

/// <summary>One line of what the service did recently.</summary>
public sealed record ActivityEntry(DateTimeOffset Time, string Level, string Message);
