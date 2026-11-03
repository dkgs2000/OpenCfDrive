namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// When the drive looks at the server for changes, the way Nextcloud's desktop
/// client schedules its sync runs: one when it starts, then one every remote
/// poll interval (Nextcloud's remotePollInterval, 30 s by default), and one
/// whenever it is asked to (back online, or "sync now"). One run at a time:
/// asking while one runs queues a single further run, since the one running
/// may have looked at the server before the change that prompted the request.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    /// <summary>Nextcloud's ConfigFile::remotePollInterval default.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>Overrides the interval in seconds (tests and scripts).</summary>
    public const string IntervalVariable = "OPENCFDRIVE_REMOTE_POLL_SECONDS";

    private readonly Func<CancellationToken, Task> _run;
    private readonly ITimer _timer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private Task? _running;
    private Task? _queued;

    /// <param name="run">One sync run; it handles its own errors.</param>
    public SyncScheduler(Func<CancellationToken, Task> run, TimeSpan interval, TimeProvider? time = null)
    {
        _run = run;
        Interval = interval;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => _ = SyncNowAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Interval { get; }

    /// <summary>The interval from <see cref="IntervalVariable"/>, else the default.</summary>
    public static TimeSpan ConfiguredInterval() =>
        double.TryParse(Environment.GetEnvironmentVariable(IntervalVariable), System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultInterval;

    /// <summary>The first run, at once.</summary>
    public void Start() => _ = SyncNowAsync();

    /// <summary>A run that starts after this call; completes when it has finished.</summary>
    public Task SyncNowAsync()
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            if (_running is null)
            {
                _running = RunAsync();
                return _running;
            }

            // The one running may predate the request: one more after it,
            // shared by everyone who asks meanwhile.
            return _queued ??= ContinueAfterAsync(_running);
        }
    }

    private async Task ContinueAfterAsync(Task running)
    {
        await running.ConfigureAwait(false);
        Task next;
        lock (_gate)
        {
            _queued = null;
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            next = _running ??= RunAsync();
        }

        await next.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        // Off the caller's thread: a timer callback or a request handler.
        await Task.Yield();
        try
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            await _run(_stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        finally
        {
            lock (_gate)
            {
                _running = null;
                if (!_stopping.IsCancellationRequested)
                {
                    _timer.Change(Interval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            _stopping.Cancel();
        }

        _timer.Dispose();
    }
}
