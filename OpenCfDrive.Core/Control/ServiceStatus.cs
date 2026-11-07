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
    RcloneStatus? Rclone = null,
    // How many sync problems the service has had since it started, and the
    // latest of them: the app tells the user about each new one.
    int ProblemsReported = 0,
    IReadOnlyList<ActivityEntry>? RecentProblems = null,
    // What the drive is doing now, and the number of the change it was last
    // told (see ServicePulse).
    SyncActivity? Activity = null,
    long Revision = 0)
{
    /// <summary>
    /// This status with what <paramref name="pulse"/> says, if the pulse is
    /// from the same service and no older: the app keeps whichever is newer.
    /// The problems are not taken from it; the app reads those with the
    /// full status.
    /// </summary>
    public ServiceStatus With(ServicePulse? pulse) =>
        pulse is not null && pulse.ProcessId == ProcessId && pulse.Revision >= Revision
            ? this with { State = pulse.State, Detail = pulse.Detail, Activity = pulse.Activity, Revision = pulse.Revision }
            : this;
}

/// <summary>
/// What the drive is doing at one moment, which the notification area icon
/// shows.
/// </summary>
/// <param name="Sending">Local changes (uploads, renames, deletes, new folders) the current sync run is sending.</param>
/// <param name="Downloading">Files being downloaded.</param>
/// <param name="Waiting">Local changes kept while the server cannot be reached.</param>
/// <param name="Failing">Files whose last sync failed, and that have not synced since (Explorer marks them).</param>
public sealed record SyncActivity(int Sending = 0, int Downloading = 0, int Waiting = 0, int Failing = 0)
{
    public static SyncActivity None { get; } = new();

    /// <summary>Sending or downloading.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Busy => Sending > 0 || Downloading > 0;

    /// <summary>One sentence on it, or "" when there is nothing to say.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Sending > 0)
        {
            parts.Add($"sending {Count(Sending, "change")}");
        }

        if (Downloading > 0)
        {
            parts.Add($"downloading {Count(Downloading, "file")}");
        }

        var sentences = new List<string>();
        if (parts.Count > 0)
        {
            var doing = string.Join(" and ", parts);
            sentences.Add(char.ToUpperInvariant(doing[0]) + doing[1..] + ".");
        }

        if (Waiting > 0)
        {
            sentences.Add($"{Count(Waiting, "change")} {(Waiting == 1 ? "waits" : "wait")} for the server.");
        }

        if (Failing > 0)
        {
            sentences.Add(
                $"{Count(Failing, "file")} could not be synced; Explorer marks {(Failing == 1 ? "it" : "them")}, " +
                "and the activity list says why.");
        }

        return string.Join(" ", sentences);
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count:N0} {noun}s";
}

/// <summary>
/// What the service sends an app that watches it, at once and whenever it
/// changes: the little the notification area icon shows. Numbered, so the
/// app keeps whichever of a pulse and a full status is newer.
/// </summary>
public sealed record ServicePulse(
    int ProcessId,
    long Revision,
    ServiceState State,
    string Detail,
    SyncActivity Activity,
    int ProblemsReported);

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
