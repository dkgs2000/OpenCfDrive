using System.Threading.Channels;
using OpenCfDrive.Core.Control;

namespace OpenCfDrive.Tests;

/// <summary>
/// The pipe between the app and the running service: status comes back
/// whole, a stop request reaches the service, a watching app is told each
/// change, and nothing answering means "not running" rather than a hang.
/// </summary>
public sealed class ServiceControlTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static ServiceStatus SampleStatus(string syncRoot) => new(
        ProcessId: 4242,
        StartedAt: new DateTimeOffset(2026, 11, 2, 10, 0, 0, TimeSpan.Zero),
        Version: "1.2.3",
        SyncRoot: syncRoot,
        Remote: "box:",
        RcUrl: "http://127.0.0.1:5572",
        State: ServiceState.Syncing,
        Detail: "Connected.",
        Registered: true,
        Connected: true,
        ItemsTracked: 17,
        BytesDownloaded: 1234,
        LogPath: @"C:\logs\OpenCfDrive.log",
        RecentActivity: [new ActivityEntry(new DateTimeOffset(2026, 11, 2, 10, 1, 0, TimeSpan.Zero), "Information", "Uploaded 'a.txt'")],
        Activity: new SyncActivity(Sending: 2, Downloading: 1, Failing: 1),
        Revision: 7);

    private static ServicePulse Pulse(long revision, SyncActivity? activity = null) =>
        new(4242, revision, ServiceState.Syncing, "Connected.", activity ?? SyncActivity.None, ProblemsReported: 0);

    private static string UniqueRoot() => Path.Combine(Path.GetTempPath(), "opencfdrive-control", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetStatus_ReturnsWhatTheServiceReports()
    {
        var root = UniqueRoot();
        var expected = SampleStatus(root);
        await using var server = new ServiceControlServer(ServiceControl.PipeName(root), () => expected, () => { });
        server.Start();

        var status = await ServiceControl.GetStatusAsync(root, Timeout);

        Assert.NotNull(status);
        Assert.Equal(expected with { RecentActivity = [] }, status with { RecentActivity = [] });
        Assert.Equal(expected.RecentActivity, status.RecentActivity);
    }

    [Fact]
    public async Task Stop_ReachesTheService()
    {
        var root = UniqueRoot();
        var stopped = new TaskCompletionSource();
        await using var server = new ServiceControlServer(ServiceControl.PipeName(root), () => SampleStatus(root), () => stopped.TrySetResult());
        server.Start();

        Assert.True(await ServiceControl.RequestStopAsync(root, Timeout));
        await stopped.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task ManyRequests_AreAllAnswered()
    {
        var root = UniqueRoot();
        await using var server = new ServiceControlServer(ServiceControl.PipeName(root), () => SampleStatus(root), () => { });
        server.Start();

        var answers = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => ServiceControl.GetStatusAsync(root, Timeout)));

        Assert.All(answers, Assert.NotNull);
    }

    [Fact]
    public async Task WithoutAService_ThereIsNoStatus()
    {
        var root = UniqueRoot();

        Assert.False(ServiceControl.IsRunning(root));
        Assert.Null(await ServiceControl.GetStatusAsync(root, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void IsRunning_FollowsWhoOwnsTheMutex()
    {
        var root = UniqueRoot();
        using var mutex = new Mutex(initiallyOwned: false, ServiceControl.InstanceMutexName(root));

        // It exists but nobody owns it: the app's own look must not count.
        Assert.False(ServiceControl.IsRunning(root));

        var owned = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            mutex.WaitOne();
            owned.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        owner.Start();
        owned.Wait();
        try
        {
            Assert.True(ServiceControl.IsRunning(root));
        }
        finally
        {
            release.Set();
            owner.Join();
        }

        Assert.False(ServiceControl.IsRunning(root));
    }

    [Fact]
    public async Task Watch_GetsAPulseAtOnce_ThenOneAtEachChange()
    {
        var root = UniqueRoot();
        var current = Pulse(1);
        await using var server = new ServiceControlServer(
            ServiceControl.PipeName(root), () => SampleStatus(root), () => { }, pulse: () => Volatile.Read(ref current));
        server.Start();

        var received = Channel.CreateUnbounded<ServicePulse>();
        using var leave = new CancellationTokenSource();
        var watching = ServiceControl.WatchAsync(root, pulse => received.Writer.TryWrite(pulse), Timeout, leave.Token);

        Assert.Equal(Pulse(1), await received.Reader.ReadAsync().AsTask().WaitAsync(Timeout));

        Volatile.Write(ref current, Pulse(2, new SyncActivity(Sending: 3)));
        server.NotifyChanged();
        Assert.Equal(Pulse(2, new SyncActivity(Sending: 3)), await received.Reader.ReadAsync().AsTask().WaitAsync(Timeout));

        // The app leaving ends the watch on its side; it heard the service.
        leave.Cancel();
        Assert.True(await watching.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Watch_Ends_WhenTheServiceStops()
    {
        var root = UniqueRoot();
        var server = new ServiceControlServer(ServiceControl.PipeName(root), () => SampleStatus(root), () => { }, pulse: () => Pulse(1));
        server.Start();
        var heard = new TaskCompletionSource();
        var watching = ServiceControl.WatchAsync(root, _ => heard.TrySetResult(), Timeout);
        await heard.Task.WaitAsync(Timeout);

        await server.DisposeAsync();

        Assert.True(await watching.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Watch_OfAServiceThatDoesNotKnowIt_EndsAtOnce()
    {
        var root = UniqueRoot();
        await using var server = new ServiceControlServer(ServiceControl.PipeName(root), () => SampleStatus(root), () => { });
        server.Start();

        Assert.False(await ServiceControl.WatchAsync(root, _ => Assert.Fail("An error is no pulse."), Timeout).WaitAsync(Timeout));
    }

    [Fact]
    public async Task Watch_WithoutAService_ReturnsAtOnce()
    {
        Assert.False(await ServiceControl.WatchAsync(UniqueRoot(), _ => { }, TimeSpan.FromMilliseconds(300)).WaitAsync(Timeout));
    }
}
