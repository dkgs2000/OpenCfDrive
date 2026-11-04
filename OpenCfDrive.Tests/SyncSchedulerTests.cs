using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Tests.TestSupport;

namespace OpenCfDrive.Tests;

/// <summary>
/// The sync's schedule, copied from Nextcloud's: a full run at start, then one
/// every remote poll interval, and one on request; a local run a moment after
/// the last local change. Time is driven by hand; each run finishes when the
/// test says so.
/// </summary>
public sealed class SyncSchedulerTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    // The scheduler's timers, in the order it makes them.
    private const int IntervalTimer = 0;
    private const int LocalTimer = 1;

    private readonly ManualTime _time = new();
    private readonly List<TaskCompletionSource> _runs = [];
    private readonly List<bool> _full = [];
    private readonly SemaphoreSlim _started = new(0);
    private readonly SyncScheduler _scheduler;

    public SyncSchedulerTests()
    {
        _scheduler = new SyncScheduler((full, _) =>
        {
            var run = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_runs)
            {
                _runs.Add(run);
                _full.Add(full);
            }

            _started.Release();
            return run.Task;
        }, Interval, _time);
    }

    private IReadOnlyList<bool> Kinds
    {
        get
        {
            lock (_runs)
            {
                return _full.ToList();
            }
        }
    }

    private int Runs
    {
        get
        {
            lock (_runs)
            {
                return _runs.Count;
            }
        }
    }

    private async Task<TaskCompletionSource> NextRunAsync()
    {
        Assert.True(await _started.WaitAsync(Timeout), "No sync run started.");
        lock (_runs)
        {
            return _runs[^1];
        }
    }

    [Fact]
    public void TheDefault_IsNextcloudsRemotePollInterval() =>
        Assert.Equal(TimeSpan.FromSeconds(30), SyncScheduler.DefaultInterval);

    [Fact]
    public async Task RunsAtStart_ThenEveryInterval()
    {
        var scheduled = _time.NextSchedule();
        _scheduler.Start();
        (await NextRunAsync()).SetResult();
        await scheduled.WaitAsync(Timeout);
        Assert.Equal(Interval, _time.DueIn);

        scheduled = _time.NextSchedule();
        _time.Fire();
        (await NextRunAsync()).SetResult();
        await scheduled.WaitAsync(Timeout);
        Assert.Equal(2, Runs);
        Assert.Equal(Interval, _time.DueIn);
    }

    [Fact]
    public async Task TheTimerIsOff_WhileARunRuns()
    {
        _scheduler.Start();
        var run = await NextRunAsync();
        Assert.Null(_time.DueIn);

        var scheduled = _time.NextSchedule();
        run.SetResult();
        await scheduled.WaitAsync(Timeout);
        Assert.Equal(Interval, _time.DueIn);
    }

    [Fact]
    public async Task SyncNow_WhileARunRuns_WaitsForAFreshRun_SharedByEveryoneAsking()
    {
        _scheduler.Start();
        var running = await NextRunAsync();

        // The run in progress may have looked at the server before the change.
        var first = _scheduler.SyncNowAsync();
        var second = _scheduler.SyncNowAsync();
        running.SetResult();
        Assert.False(first.IsCompleted);

        var fresh = await NextRunAsync();
        Assert.False(first.IsCompleted);
        fresh.SetResult();
        await Task.WhenAll(first, second).WaitAsync(Timeout);
        Assert.Equal(2, Runs);
    }

    [Fact]
    public async Task SyncNow_WhenIdle_RunsAtOnce()
    {
        var done = _scheduler.SyncNowAsync();
        (await NextRunAsync()).SetResult();
        await done.WaitAsync(Timeout);
        Assert.Equal(1, Runs);
    }

    [Fact]
    public async Task AFailedRun_StillSchedulesTheNext()
    {
        var scheduled = _time.NextSchedule();
        var done = _scheduler.SyncNowAsync();
        (await NextRunAsync()).SetException(new InvalidOperationException("listing failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => done.WaitAsync(Timeout));
        await scheduled.WaitAsync(Timeout);
        Assert.Equal(Interval, _time.DueIn);
    }

    [Fact]
    public async Task ALocalChange_RunsALocalRun_OnceTheDelayHasPassed()
    {
        _scheduler.RequestLocalRun();
        Assert.Equal(SyncScheduler.LocalChangeDelay, _time.DueInFor(LocalTimer));
        Assert.Equal(0, Runs);

        // Another change starts the wait again.
        _scheduler.RequestLocalRun();
        Assert.Equal(SyncScheduler.LocalChangeDelay, _time.DueInFor(LocalTimer));

        _time.Fire(LocalTimer);
        (await NextRunAsync()).SetResult();
        Assert.Equal([false], Kinds);
    }

    [Fact]
    public async Task ALocalRun_LeavesTheIntervalAlone()
    {
        var scheduled = _time.NextSchedule();
        _scheduler.Start();
        (await NextRunAsync()).SetResult();
        await scheduled.WaitAsync(Timeout);
        Assert.Equal(Interval, _time.DueInFor(IntervalTimer));

        _scheduler.RequestLocalRun();
        _time.Fire(LocalTimer);
        var local = await NextRunAsync();
        Assert.Equal(Interval, _time.DueInFor(IntervalTimer));
        local.SetResult();
        Assert.Equal([true, false], Kinds);
    }

    [Fact]
    public async Task AFullRequest_CoversALocalOneWaitingWithIt()
    {
        _scheduler.Start();
        var running = await NextRunAsync();

        _scheduler.RequestLocalRun();
        _time.Fire(LocalTimer);
        var full = _scheduler.SyncNowAsync();
        running.SetResult();

        (await NextRunAsync()).SetResult();
        await full.WaitAsync(Timeout);
        Assert.Equal([true, true], Kinds);
    }

    [Fact]
    public async Task ALocalChangeDuringARun_RunsAgainAfterIt()
    {
        _scheduler.Start();
        var running = await NextRunAsync();

        _scheduler.RequestLocalRun();
        _time.Fire(LocalTimer);
        running.SetResult();

        (await NextRunAsync()).SetResult();
        Assert.Equal([true, false], Kinds);
    }

    [Fact]
    public void TheInterval_CanBeSetForScripts()
    {
        var before = Environment.GetEnvironmentVariable(SyncScheduler.IntervalVariable);
        try
        {
            Environment.SetEnvironmentVariable(SyncScheduler.IntervalVariable, "2.5");
            Assert.Equal(TimeSpan.FromSeconds(2.5), SyncScheduler.ConfiguredInterval());
            Environment.SetEnvironmentVariable(SyncScheduler.IntervalVariable, "not a number");
            Assert.Equal(SyncScheduler.DefaultInterval, SyncScheduler.ConfiguredInterval());
        }
        finally
        {
            Environment.SetEnvironmentVariable(SyncScheduler.IntervalVariable, before);
        }
    }

    public void Dispose()
    {
        _scheduler.Dispose();
        lock (_runs)
        {
            foreach (var run in _runs)
            {
                run.TrySetResult();
            }
        }

        _started.Dispose();
    }
}
