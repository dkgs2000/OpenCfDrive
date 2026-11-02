using System.Collections.Concurrent;
using OpenCfDrive.Core.Control;

namespace OpenCfDrive.Service;

/// <summary>
/// Keeps the service's latest log lines for the app's status page: what the
/// drive did (uploads, deletes, renames), and every warning or error.
/// </summary>
public sealed class ActivityLog : ILoggerProvider
{
    private const int Capacity = 200;

    private readonly ConcurrentQueue<ActivityEntry> _entries = new();

    public IReadOnlyList<ActivityEntry> Recent(int count)
    {
        var all = _entries.ToArray();
        return all.Length <= count ? all : all[^count..];
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(ActivityEntry entry)
    {
        _entries.Enqueue(entry);
        while (_entries.Count > Capacity && _entries.TryDequeue(out _))
        {
        }
    }

    private sealed class Logger(ActivityLog owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        // The drive's own messages; from the host and libraries only problems.
        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= LogLevel.Warning ||
            (logLevel >= LogLevel.Information && category.StartsWith("OpenCfDrive", StringComparison.Ordinal));

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $": {exception.Message}";
            }

            owner.Add(new ActivityEntry(DateTimeOffset.Now, logLevel.ToString(), message));
        }
    }
}
