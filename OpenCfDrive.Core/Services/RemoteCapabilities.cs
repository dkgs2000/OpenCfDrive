using System.Text.Json;

namespace OpenCfDrive.Core.Services;

/// <summary>
/// What a remote can tell about its files, from rclone's <c>operations/fsinfo</c>:
/// the content hashes it keeps, how exact its file times are, and what
/// costs extra to read. rclone works the same way with every backend, but
/// backends differ here, so the sync engine asks once and uses the most
/// each one offers.
/// </summary>
/// <param name="Hashes">The hash types the remote keeps (rclone's names: md5, sha1, quickxor...).</param>
/// <param name="PrecisionNanoseconds">How exact its file times are.</param>
/// <param name="SlowHash">rclone computes the hashes by reading the whole file (a local disk, SFTP).</param>
/// <param name="SlowModTime">Each file's time costs a request of its own (S3).</param>
public sealed record RemoteCapabilities(
    IReadOnlyList<string> Hashes,
    long PrecisionNanoseconds,
    bool SlowHash,
    bool SlowModTime,
    bool CanHaveEmptyDirectories)
{
    /// <summary>rclone's fs.ModTimeNotSupported: the remote cannot set a file's time.</summary>
    public const long ModTimeNotSupported = 3153600000000000000;

    /// <summary>Until the remote is asked: no hashes, exact times, folders kept.</summary>
    public static RemoteCapabilities Unknown { get; } = new([], 1, false, false, true);

    // Several kept (Google Drive): the one most backends with more than one share.
    private static readonly string[] Preferred = ["sha1", "md5", "sha256"];

    /// <summary>
    /// The hash files are told by: the remote's own, read with the listing.
    /// None where there is none, or where rclone would read every file to
    /// make one.
    /// </summary>
    public string? HashType =>
        SlowHash || Hashes.Count == 0
            ? null
            : Preferred.FirstOrDefault(type => Hashes.Contains(type, StringComparer.OrdinalIgnoreCase)) ?? Hashes[0].ToLowerInvariant();

    /// <summary>
    /// False where the remote cannot set a file's time: the times it lists
    /// are its own, of the last upload, never the file's.
    /// </summary>
    public bool KeepsFileTimes => PrecisionNanoseconds < ModTimeNotSupported;

    /// <summary>How far apart two times of the same file may be: the remote's precision, at least a second.</summary>
    public TimeSpan TimeTolerance =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, KeepsFileTimes ? PrecisionNanoseconds / 100 : 0));

    /// <summary>
    /// Whether a folder listed before is listed without file times: where
    /// each time costs a request of its own and the hash tells what changed.
    /// </summary>
    public bool ListsWithoutTimes => SlowModTime && HashType is not null;

    public static RemoteCapabilities FromFsInfo(JsonElement root)
    {
        var hashes = root.TryGetProperty("Hashes", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(h => h.GetString()).OfType<string>().Where(h => h.Length > 0).ToList()
            : [];
        var precision = root.TryGetProperty("Precision", out var p) && p.TryGetInt64(out var ns) ? ns : 1;

        bool Feature(string name, bool fallback) =>
            root.TryGetProperty("Features", out var features) && features.TryGetProperty(name, out var value)
                ? value.ValueKind == JsonValueKind.True
                : fallback;

        return new RemoteCapabilities(
            hashes,
            precision,
            Feature("SlowHash", false),
            Feature("SlowModTime", false),
            Feature("CanHaveEmptyDirectories", true));
    }

    /// <summary>One line for the log: what tells a change on this remote.</summary>
    public string Describe(string remote) =>
        (HashType, KeepsFileTimes) switch
        {
            (null, true) => $"'{remote}' keeps no content hashes this client can use: changes are told by size and time.",
            (null, false) => $"'{remote}' keeps no content hashes and no file times: changes are told by size and the server's upload time.",
            (var hash, _) when ListsWithoutTimes =>
                $"'{remote}' keeps {hash} hashes: changes are told by size and {hash}, and a folder listed before is listed without each file's time, which costs a request of its own there.",
            (var hash, _) => $"'{remote}' keeps {hash} hashes: changes are told by size and {hash}, else by time.",
        };
}
