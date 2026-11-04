namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// When the drive syncs, the way Nextcloud's desktop client schedules its sync
/// runs. A full run (local changes, then a look at the server) when it starts,
/// then every remote poll interval (Nextcloud's remotePollInterval, 30 s by
/// default), and whenever it is asked to (back online, or "sync now"). A local
/// run (local changes only) a moment after the last local change, as
/// Nextcloud's folder watcher schedules a sync soon after a change.
/// One run at a time: asking while one runs queues a single further run, since
/// the one running may have looked before the change that prompted the request;
/// a full request covers a local one waiting with it.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    /// <summary>Nextcloud's ConfigFile::remotePollInterval default.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a local run waits after the last local change. At least a
    /// second, so a save that deletes the file and puts a new one in its place
    /// is seen as one replacement, not a delete.
    /// </summary>
    public static readonly TimeSpan LocalChangeDelay = TimeSpan.FromSeconds(1);

    /// <summary>Overrides the interval in seconds (tests and scripts).</summary>
    public const string IntervalVariable = "OPENCFDRIVE_REMOTE_POLL_SECONDS";

    /// <summary>
    /// Overrides, in seconds, how long after its last listing a folder holding
    /// nothing local is marked to be listed on its next opening (tests). 0
    /// marks it at every full run.
    /// </summary>
    public const string RelistVariable = "OPENCFDRIVE_RELIST_SECONDS";

    private readonly Func<bool, CancellationToken, Task> _run;
    private readonly ITimer _timer;
    private readonly ITimer _localTimer;
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private Task? _worker;
    private bool _wantFull;
    private bool _wantLocal;
    private TaskCompletionSource? _next;

    /// <param name="run">One sync run, full when its first argument is true.</param>
    /// <param name="time">Its two timers are made in order: the interval, then the local delay.</param>
    public SyncScheduler(Func<bool, CancellationToken, Task> run, TimeSpan interval, TimeProvider? time = null)
    {
        _run = run;
        Interval = interval;
        var clock = time ?? TimeProvider.System;
        _timer = clock.CreateTimer(_ => _ = SyncNowAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _localTimer = clock.CreateTimer(_ => _ = RequestAsync(full: false), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public TimeSpan Interval { get; }

    /// <summary>The interval from <see cref="IntervalVariable"/>, else the default.</summary>
    public static TimeSpan ConfiguredInterval() =>
        double.TryParse(Environment.GetEnvironmentVariable(IntervalVariable), System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultInterval;

    /// <summary>The relist age from <see cref="RelistVariable"/>, else the interval.</summary>
    public static TimeSpan ConfiguredRelistAge() =>
        double.TryParse(Environment.GetEnvironmentVariable(RelistVariable), System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0
            ? TimeSpan.FromSeconds(seconds)
            : ConfiguredInterval();

    /// <summary>The first full run, at once.</summary>
    public void Start() => _ = SyncNowAsync();

    /// <summary>A full run that starts after this call; completes when it has finished.</summary>
    public Task SyncNowAsync() => RequestAsync(full: true);

    /// <summary>
    /// A local change was made: a local run once <see cref="LocalChangeDelay"/>
    /// has passed without another. Under constant changes the interval's full
    /// runs still send them.
    /// </summary>
    public void RequestLocalRun()
    {
        lock (_gate)
        {
            if (!_stopping.IsCancellationRequested)
            {
                _localTimer.Change(LocalChangeDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private Task RequestAsync(bool full)
    {
        lock (_gate)
        {
            if (_stopping.IsCancellationRequested)
            {
                return Task.CompletedTask;
            }

            _wantFull |= full;
            _wantLocal |= !full;

            // Shared by everyone who asks before the next run starts.
            var next = _next ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _worker ??= WorkAsync();
            return next.Task;
        }
    }

    private async Task WorkAsync()
    {
        // Off the caller's thread: a timer callback or a request handler.
        await Task.Yield();
        while (true)
        {
            bool full;
            TaskCompletionSource done;
            lock (_gate)
            {
                if (_stopping.IsCancellationRequested || (!_wantFull && !_wantLocal) || _next is null)
                {
                    _worker = null;
                    return;
                }

                full = _wantFull;
                _wantFull = _wantLocal = false;
                done = _next;
                _next = null;
                if (full)
                {
                    _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }

            try
            {
                await _run(full, _stopping.Token).ConfigureAwait(false);
                done.TrySetResult();
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                done.TrySetResult();
            }
            catch (Exception ex)
            {
                done.TrySetException(ex);
            }
            finally
            {
                lock (_gate)
                {
                    if (full && !_stopping.IsCancellationRequested)
                    {
                        _timer.Change(Interval, Timeout.InfiniteTimeSpan);
                    }
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
        _localTimer.Dispose();
    }
}
