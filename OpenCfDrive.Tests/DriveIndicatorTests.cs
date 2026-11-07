using OpenCfDrive.Core.Control;

namespace OpenCfDrive.Tests;

/// <summary>
/// What the notification area icon shows, from what the app and the service
/// say; what the service says about its activity, in words; and how the app
/// keeps the newer of a pulse and a full status.
/// </summary>
public sealed class DriveIndicatorTests
{
    private static ServiceStatus Status(ServiceState state = ServiceState.Syncing, SyncActivity? activity = null, long revision = 5) => new(
        ProcessId: 100,
        StartedAt: DateTimeOffset.UnixEpoch,
        Version: "1.0.0",
        SyncRoot: @"C:\drive",
        Remote: "box:",
        RcUrl: "",
        State: state,
        Detail: "Connected.",
        Registered: true,
        Connected: true,
        ItemsTracked: 0,
        BytesDownloaded: 0,
        LogPath: "",
        RecentActivity: [],
        ProblemsReported: 1,
        Activity: activity ?? SyncActivity.None,
        Revision: revision);

    private static DriveIndicator Of(ServiceStatus? status, bool problem = false, bool running = true, bool setUp = true) =>
        DriveIndicators.Of(problem, running, setUp, status);

    [Fact]
    public void AProblem_ShowsAsOne_WhateverElse()
    {
        Assert.Equal(DriveIndicator.Problem, Of(Status(activity: new SyncActivity(Sending: 1)), problem: true));
        Assert.Equal(DriveIndicator.Problem, Of(null, problem: true, running: false));
    }

    [Fact]
    public void AStoppedService_IsPaused_OrNotSetUp()
    {
        Assert.Equal(DriveIndicator.Paused, Of(null, running: false));
        Assert.Equal(DriveIndicator.NotSetUp, Of(null, running: false, setUp: false));
    }

    [Theory]
    [InlineData(ServiceState.Starting, DriveIndicator.Syncing)]
    [InlineData(ServiceState.Offline, DriveIndicator.Offline)]
    [InlineData(ServiceState.Failed, DriveIndicator.Problem)]
    [InlineData(ServiceState.Unavailable, DriveIndicator.Problem)]
    public void TheServiceState_Decides_BeforeItsActivity(ServiceState state, DriveIndicator expected)
    {
        Assert.Equal(expected, Of(Status(state, new SyncActivity(Downloading: 1, Failing: 1))));
    }

    [Fact]
    public void AConnectedDrive_SyncsWhileSendingOrDownloading_ThenWarnsOfWhatFailed()
    {
        Assert.Equal(DriveIndicator.Syncing, Of(null)); // running, not answering yet
        Assert.Equal(DriveIndicator.Syncing, Of(Status(activity: new SyncActivity(Sending: 1))));
        Assert.Equal(DriveIndicator.Syncing, Of(Status(activity: new SyncActivity(Downloading: 1, Failing: 2))));
        Assert.Equal(DriveIndicator.Warning, Of(Status(activity: new SyncActivity(Failing: 2))));
        Assert.Equal(DriveIndicator.UpToDate, Of(Status()));

        // Changes kept offline are no activity once the server answers again.
        Assert.Equal(DriveIndicator.UpToDate, Of(Status(activity: new SyncActivity(Waiting: 3))));
    }

    [Theory]
    [InlineData(0, 0, 0, 0, "")]
    [InlineData(1, 0, 0, 0, "Sending 1 change.")]
    [InlineData(3, 1, 0, 0, "Sending 3 changes and downloading 1 file.")]
    [InlineData(0, 2, 0, 0, "Downloading 2 files.")]
    [InlineData(0, 0, 1, 0, "1 change waits for the server.")]
    [InlineData(0, 0, 2, 0, "2 changes wait for the server.")]
    [InlineData(0, 0, 0, 1, "1 file could not be synced; Explorer marks it, and the activity list says why.")]
    [InlineData(1, 0, 0, 2, "Sending 1 change. 2 files could not be synced; Explorer marks them, and the activity list says why.")]
    public void Activity_IsSaidInWords(int sending, int downloading, int waiting, int failing, string expected)
    {
        Assert.Equal(expected, new SyncActivity(sending, downloading, waiting, failing).Describe());
    }

    [Fact]
    public void ANewerPulse_FromTheSameService_ReplacesTheState()
    {
        var status = Status(revision: 5);
        var pulse = new ServicePulse(100, 6, ServiceState.Offline, "Gone.", new SyncActivity(Waiting: 2), ProblemsReported: 4);

        var merged = status.With(pulse);

        Assert.Equal(ServiceState.Offline, merged.State);
        Assert.Equal("Gone.", merged.Detail);
        Assert.Equal(new SyncActivity(Waiting: 2), merged.Activity);
        Assert.Equal(6, merged.Revision);

        // The problems are read with the full status, which lists them.
        Assert.Equal(1, merged.ProblemsReported);
    }

    [Fact]
    public void AnOlderPulse_OrOneFromAnotherService_IsIgnored()
    {
        var status = Status(revision: 5);

        Assert.Same(status, status.With(new ServicePulse(100, 4, ServiceState.Offline, "Old.", SyncActivity.None, 0)));
        Assert.Same(status, status.With(new ServicePulse(200, 9, ServiceState.Offline, "Another.", SyncActivity.None, 0)));
        Assert.Same(status, status.With(null));
    }
}
