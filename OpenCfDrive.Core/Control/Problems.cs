using System.Text.RegularExpressions;

namespace OpenCfDrive.Core.Control;

/// <summary>
/// The sync problems a service has had since it started: a count that only
/// grows, and the latest few. The app compares the count with what it last
/// told the user about.
/// </summary>
public sealed class ProblemLog
{
    private const int Capacity = 50;

    private readonly object _gate = new();
    private readonly Queue<ActivityEntry> _recent = new();
    private int _count;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Report(string message, DateTimeOffset? at = null)
    {
        lock (_gate)
        {
            _count++;
            _recent.Enqueue(new ActivityEntry(at ?? DateTimeOffset.Now, "Warning", message));
            while (_recent.Count > Capacity)
            {
                _recent.Dequeue();
            }
        }
    }

    public IReadOnlyList<ActivityEntry> Recent()
    {
        lock (_gate)
        {
            return _recent.ToArray();
        }
    }
}

/// <summary>A notification for the user: a title and one or two short lines.</summary>
public sealed record ProblemNotice(string Title, string Text);

/// <summary>
/// Decides what the app tells the user about the service's sync problems,
/// from the status it reads every few seconds: each new problem once, several
/// at once in one notification, and the same problem again (a file that keeps
/// failing at every sync run) only after <see cref="RepeatAfter"/>. When the
/// app starts while the service runs, what happened before is not told again;
/// a service that started anew while the app runs starts from nothing.
/// </summary>
public sealed class ProblemNotifier(TimeProvider? time = null)
{
    public static readonly TimeSpan RepeatAfter = TimeSpan.FromMinutes(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Dictionary<string, DateTimeOffset> _told = new(StringComparer.Ordinal);
    private (int ProcessId, DateTimeOffset StartedAt)? _service;
    private int _seen;

    /// <summary>The notification to show for this status, or null.</summary>
    public ProblemNotice? Next(ServiceStatus? status)
    {
        if (status is null)
        {
            return null;
        }

        var service = (status.ProcessId, status.StartedAt);
        if (_service != service)
        {
            _seen = _service is null ? status.ProblemsReported : 0;
            _service = service;
        }

        var added = status.ProblemsReported - _seen;
        _seen = status.ProblemsReported;
        if (added <= 0)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var recent = status.RecentProblems ?? [];
        var fresh = recent
            .Skip(Math.Max(0, recent.Count - added))
            .Select(problem => ProblemText.Summarize(problem.Message))
            .Where(text => !_told.TryGetValue(text, out var told) || now - told >= RepeatAfter)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var text in fresh)
        {
            _told[text] = now;
        }

        return fresh.Count switch
        {
            0 => null,
            1 => new ProblemNotice("OpenCfDrive couldn't sync", fresh[0]),
            _ => new ProblemNotice($"OpenCfDrive couldn't sync {fresh.Count} items", $"{fresh[^1]} (and {fresh.Count - 1} more; click for details)"),
        };
    }
}

/// <summary>
/// A problem as one short line for a notification. The engine's messages
/// carry rclone's error, which for a web server is often a whole HTML page or
/// a JSON body; what the user needs is the file and the server's answer.
/// </summary>
public static partial class ProblemText
{
    private const int MaxLength = 220;

    public static string Summarize(string message)
    {
        var text = message.Replace("\\u003c", "<").Replace("\\u003e", ">").Replace("\\n", "\n").Replace("\\r", "\r");

        // The part before rclone's raw answer: what failed, and for which file.
        var cut = text.IndexOfAny(['{', '\n']);
        var head = (cut > 0 ? text[..cut] : text).Trim();
        head = Timestamp().Replace(head, "");
        head = RcWrapper().Replace(head, "").Trim().TrimEnd(':').Trim();
        head = WholePath().Replace(head, match => "'" + Path.GetFileName(match.Groups[1].Value.TrimEnd('\\', '/')) + "'");

        // What the server said: an HTTP status from its page or rclone's error.
        var status = HtmlTitle().Match(text) is { Success: true } title
            ? title.Groups[1].Value.Trim()
            : RcloneError().Match(text) is { Success: true } error ? error.Groups[1].Value.Trim() : null;

        var line = status is not null && !head.Contains(status, StringComparison.OrdinalIgnoreCase)
            ? $"{head}: the server answered {status}."
            : head.EndsWith('.') ? head : head + ".";
        line = Regex.Replace(line, @"\s+", " ");
        return line.Length <= MaxLength ? line : line[..(MaxLength - 1)] + "…";
    }

    // "[17:51:42.725] " at the start of a callback's line.
    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}(\.\d+)?\]\s*")]
    private static partial Regex Timestamp();

    // rclone's remote control wrapping the server's answer: ": operations/uploadfile failed (500)".
    [GeneratedRegex(@":\s*[a-z]+/[a-z]+ failed \(\d{3}\)")]
    private static partial Regex RcWrapper();

    // A quoted Windows path: shown by its last part, the file or folder name.
    [GeneratedRegex(@"'([A-Za-z]:\\[^']+)'")]
    private static partial Regex WholePath();

    [GeneratedRegex(@"<title>\s*([^<]+?)\s*</title>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTitle();

    [GeneratedRegex(@"""error""\s*:\s*""([^""<]{3,120})")]
    private static partial Regex RcloneError();
}
