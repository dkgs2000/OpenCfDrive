namespace OpenCfDrive.Tests.TestSupport;

/// <summary>
/// A clock whose one timer fires only when the test says so, and which
/// signals when the timer is next scheduled.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private ManualTimer? _timer;
    private TaskCompletionSource? _scheduled;

    /// <summary>When the timer is due, or null while it is off.</summary>
    public TimeSpan? DueIn
    {
        get
        {
            lock (_gate)
            {
                return _timer?.Due;
            }
        }
    }

    /// <summary>Completes when the timer is next scheduled.</summary>
    public Task NextSchedule()
    {
        lock (_gate)
        {
            _scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _scheduled.Task;
        }
    }

    public void Fire()
    {
        ManualTimer timer;
        lock (_gate)
        {
            timer = _timer ?? throw new InvalidOperationException("No timer.");
            Assert.NotNull(timer.Due);
        }

        timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            _timer = new ManualTimer(this, callback, state);
            return _timer;
        }
    }

    private void OnChanged(TimeSpan? due)
    {
        if (due is not null)
        {
            lock (_gate)
            {
                _scheduled?.TrySetResult();
            }
        }
    }

    private sealed class ManualTimer(ManualTime owner, TimerCallback callback, object? state) : ITimer
    {
        public TimeSpan? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == global::System.Threading.Timeout.InfiniteTimeSpan ? null : dueTime;
            owner.OnChanged(Due);
            return true;
        }

        public void Fire()
        {
            Due = null;
            callback(state);
        }

        public void Dispose() => Due = null;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
