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
    IReadOnlyList<ActivityEntry> RecentActivity);

public enum ServiceState
{
    /// <summary>Registering and connecting the drive.</summary>
    Starting,

    /// <summary>The drive is registered and connected.</summary>
    Syncing,

    /// <summary>The Cloud Files API is missing; nothing syncs.</summary>
    Unavailable,

    /// <summary>Startup failed; Detail says why.</summary>
    Failed,
}

/// <summary>One line of what the service did recently.</summary>
public sealed record ActivityEntry(DateTimeOffset Time, string Level, string Message);
