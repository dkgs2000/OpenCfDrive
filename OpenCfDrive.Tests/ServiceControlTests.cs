using OpenCfDrive.Core.Control;

namespace OpenCfDrive.Tests;

/// <summary>
/// The pipe between the app and the running service: status comes back
/// whole, a stop request reaches the service, and nothing answering means
/// "not running" rather than a hang.
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
        RecentActivity: [new ActivityEntry(new DateTimeOffset(2026, 11, 2, 10, 1, 0, TimeSpan.Zero), "Information", "Uploaded 'a.txt'")]);

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
}
