namespace OpenCfDrive.Tests.TestSupport;

/// <summary>
/// A clock whose timers fire only when the test says so, and which signals
/// when a timer is next scheduled. Timers are numbered in the order they were
/// created; <see cref="DueIn"/> and <see cref="Fire()"/> are for the first.
/// </summary>
internal sealed class ManualTime : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private TaskCompletionSource? _scheduled;

    /// <summary>When the first timer is due, or null while it is off.</summary>
    public TimeSpan? DueIn => DueInFor(0);

    /// <summary>When timer <paramref name="index"/> is due, or null while it is off.</summary>
    public TimeSpan? DueInFor(int index)
    {
        lock (_gate)
        {
            return index < _timers.Count ? _timers[index].Due : null;
        }
    }

    /// <summary>Completes when any timer is next scheduled.</summary>
    public Task NextSchedule()
    {
        lock (_gate)
        {
            _scheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _scheduled.Task;
        }
    }

    public void Fire() => Fire(0);

    public void Fire(int index)
    {
        ManualTimer timer;
        lock (_gate)
        {
            if (index >= _timers.Count)
            {
                throw new InvalidOperationException("No timer.");
            }

            timer = _timers[index];
            Assert.NotNull(timer.Due);
        }

        timer.Fire();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            return timer;
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
