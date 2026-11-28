using System.Security.Cryptography;
using System.Text;
using OpenCfDrive.Core.Models;

namespace OpenCfDrive.Core.CloudFiles;

/// <summary>What listing one folder for the background sync came to.</summary>
public enum FolderCheck
{
    /// <summary>Listed, and what changed was applied.</summary>
    Listed,

    /// <summary>No longer on this PC: a listing above it removed it.</summary>
    Gone,

    /// <summary>The server has no such folder: deleted or renamed there, or a folder above it was.</summary>
    NotFound,

    /// <summary>The server answered with an error; the next run tries again.</summary>
    Failed,

    /// <summary>The server could not be reached: the run stops.</summary>
    Unreachable,
}

/// <summary>
/// When the background sync next lists each folder it keeps current. A
/// server without folder ETags (plain WebDAV servers) cannot say that nothing
/// changed below a folder, so where Nextcloud asks for one ETag and stops,
/// this client has to list every such folder. Listing every one at every run
/// (30 s) is a constant stream of requests once many folders hold local
/// files. Instead a folder whose listing came back the same waits twice as
/// long as before, from the poll interval up to <see cref="MaxInterval"/>,
/// and goes back to the poll interval as soon as its listing changes or it is
/// used here (<see cref="Touch"/>). Each wait is spread by up to a tenth
/// either way, so folders that started together do not keep falling due in
/// the same run.
///
/// Kept in memory only: after a restart every folder is due once.
/// </summary>
public sealed class FolderPollSchedule
{
    /// <summary>The longest a folder whose listing does not change waits.</summary>
    public static readonly TimeSpan DefaultMaxInterval = TimeSpan.FromMinutes(30);

    /// <summary>Overrides <see cref="DefaultMaxInterval"/> in seconds (tests and scripts).</summary>
    public const string MaxIntervalVariable = "OPENCFDRIVE_REMOTE_POLL_MAX_SECONDS";

    /// <summary>How far a wait is spread either way, as a part of it.</summary>
    internal const double Jitter = 0.1;

    private readonly TimeProvider _time;
    private readonly Func<double> _random;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="interval">The poll interval: the wait of a folder that just changed.</param>
    /// <param name="maxInterval">The longest wait; never shorter than <paramref name="interval"/>.</param>
    /// <param name="random">A number in [0, 1) for the spread (tests pass a fixed one).</param>
    public FolderPollSchedule(TimeSpan interval, TimeSpan maxInterval, TimeProvider? time = null, Func<double>? random = null)
    {
        Interval = interval;
        MaxInterval = maxInterval < interval ? interval : maxInterval;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared.NextDouble;
    }

    public TimeSpan Interval { get; }

    public TimeSpan MaxInterval { get; }

    /// <summary>
    /// The longest wait from <see cref="MaxIntervalVariable"/> (at most
    /// <see cref="SyncScheduler.LongestConfigured"/>), else the default.
    /// </summary>
    public static TimeSpan ConfiguredMaxInterval() => SyncScheduler.SecondsFrom(MaxIntervalVariable) ?? DefaultMaxInterval;

    /// <summary>
    /// Whether a run starting now lists the folder: never listed, or its wait
    /// ends before the middle of the gap to the next run. Runs are an interval
    /// apart, so a wait ends at the run nearest to it, not always the one after.
    /// </summary>
    public bool IsDue(string directory)
    {
        lock (_gate)
        {
            return !_entries.TryGetValue(directory, out var entry) ||
                   entry.Next <= _time.GetUtcNow() + Interval / 2;
        }
    }

    /// <summary>
    /// One run's look at the server: each of <paramref name="folders"/>
    /// (parents first) that is due is listed with <paramref name="check"/>;
    /// the top folder ("") at every run, since it is one request and what
    /// Explorer opens first. A folder the server no longer has was deleted or
    /// renamed there, or a folder above it was; the folders above may not be
    /// due for a long time, so they are listed at once, nearest first, until
    /// one is found: its listing removes what is gone. False when the server
    /// could not be reached; the run stops there.
    /// </summary>
    public async Task<bool> RunAsync(
        IReadOnlyList<string> folders,
        Func<string, CancellationToken, Task<FolderCheck>> check,
        CancellationToken cancellationToken = default)
    {
        var checkedNow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in folders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (folder.Length > 0 && !IsDue(folder))
            {
                continue;
            }

            for (var current = folder; checkedNow.Add(current); current = ParentOf(current))
            {
                var result = await check(current, cancellationToken).ConfigureAwait(false);
                if (result == FolderCheck.Unreachable)
                {
                    return false;
                }

                if (result != FolderCheck.NotFound || current.Length == 0)
                {
                    break;
                }
            }
        }

        return true;
    }

    /// <summary>The folder a server path is in: "a/b" for "a/b/c" and for "a/b/c.txt", "" at the top.</summary>
    internal static string ParentOf(string path)
    {
        var trimmed = path.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash < 0 ? string.Empty : trimmed[..slash];
    }

    /// <summary>
    /// What a listing says the folder holds, as one short text: the same for
    /// two listings exactly when no item was added, removed or changed. The
    /// order the server lists in does not count. Nor does a folder's own time:
    /// rclone reports the time of the listing for folders on remotes that keep
    /// none, which would make every listing look new. File times are left out
    /// (<paramref name="withTimes"/> false) where listings come without them
    /// and the hash tells.
    /// </summary>
    public static string Fingerprint(IEnumerable<CloudItem> items, bool withTimes = true)
    {
        // No name holds either separator.
        const char Field = '\u0000';
        const char Item = '\u0001';
        var text = new StringBuilder();
        foreach (var item in items.OrderBy(i => i.RelativePath, StringComparer.Ordinal))
        {
            text.Append(item.RelativePath).Append(Field)
                .Append(item.IsDirectory ? 'd' : 'f').Append(Field);
            if (!item.IsDirectory)
            {
                text.Append(item.Size).Append(Field)
                    .Append(withTimes ? item.ModifiedTime?.UtcTicks : null).Append(Field)
                    .Append(item.Hash);
            }

            text.Append(Item);
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>
    /// The folder was listed: <paramref name="changed"/> when the listing
    /// differed from the last one (or left something still to apply), so it
    /// waits one interval; otherwise its wait doubles, up to
    /// <see cref="MaxInterval"/>. The first listing of a folder counts as
    /// changed.
    /// </summary>
    public void Listed(string directory, string fingerprint, bool changed = false)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            _entries.TryGetValue(directory, out var last);
            var wait = !changed && last is not null && last.Fingerprint == fingerprint ? Doubled(last.Wait) : Interval;
            _entries[directory] = new Entry(fingerprint, wait, now, now + Spread(wait));
        }
    }

    /// <summary>
    /// The folder is used here (a local change, a download): it goes back to
    /// the poll interval, counted from when it was last listed.
    /// </summary>
    public void Touch(string directory)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(directory, out var entry) && entry.Wait > Interval)
            {
                var next = entry.ListedAt + Interval;
                _entries[directory] = entry with { Wait = Interval, Next = next < entry.Next ? next : entry.Next };
            }
        }
    }

    /// <summary>Every folder is due ("sync now", back online). How long each had waited stays.</summary>
    public void MakeAllDue()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var directory in _entries.Keys.ToList())
            {
                _entries[directory] = _entries[directory] with { Next = now };
            }
        }
    }

    /// <summary>Forgets every folder not in <paramref name="directories"/>.</summary>
    public void Retain(IReadOnlySet<string> directories)
    {
        lock (_gate)
        {
            foreach (var directory in _entries.Keys.Where(d => !directories.Contains(d)).ToList())
            {
                _entries.Remove(directory);
            }
        }
    }

    /// <summary>How long the folder now waits between listings; null if never listed.</summary>
    public TimeSpan? WaitOf(string directory)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(directory, out var entry) ? entry.Wait : null;
        }
    }

    /// <summary>
    /// How many of <paramref name="directories"/> are listed within
    /// <paramref name="within"/> from now: those never listed, those whose
    /// wait ends by then, and the top folder (""), which every run lists
    /// whatever its wait (see <see cref="RunAsync"/>).
    /// </summary>
    public int CountDueWithin(IEnumerable<string> directories, TimeSpan within)
    {
        lock (_gate)
        {
            var until = _time.GetUtcNow() + within;
            return directories.Count(d => d.Length == 0 || !_entries.TryGetValue(d, out var entry) || entry.Next <= until);
        }
    }

    private TimeSpan Doubled(TimeSpan wait)
    {
        var doubled = wait * 2;
        return doubled > MaxInterval ? MaxInterval : doubled;
    }

    private TimeSpan Spread(TimeSpan wait) => wait * (1 + Jitter * (2 * _random() - 1));

    private sealed record Entry(string Fingerprint, TimeSpan Wait, DateTimeOffset ListedAt, DateTimeOffset Next);
}
