using OpenCfDrive.Core.Updates;

namespace OpenCfDrive.Tests;

/// <summary>
/// When the app tells the user about an update: a download that becomes
/// ready (again at each start while it waits), and a version that is out but
/// could not be downloaded, once per version.
/// </summary>
public sealed class UpdateNotifierTests
{
    private static readonly Version Next = new(1, 4, 0);
    private const string Page = "https://github.com/owner/name/releases/tag/v1.4.0";

    private static UpdateStatus Ready(Version version) => new(UpdateStage.Ready, "Downloaded.", version, Page);

    private static UpdateStatus Checking(Version? available = null) => new(UpdateStage.Checking, "Asking.", available, Page);

    [Fact]
    public void ADownloadThatBecomesReady_IsToldOnce_AndClickingItInstalls()
    {
        var notifier = new UpdateNotifier(UpdateStage.NotChecked);

        Assert.Null(notifier.Next(Checking()));
        Assert.Null(notifier.Next(new UpdateStatus(UpdateStage.Downloading, "Downloading.", Next, Page)));
        var notice = notifier.Next(Ready(Next));

        Assert.NotNull(notice);
        Assert.Equal("OpenCfDrive 1.4.0 is ready", notice.Title);
        Assert.True(notice.Install);

        // A later check that finds the same download says nothing new.
        Assert.Null(notifier.Next(Checking(Next)));
        Assert.Null(notifier.Next(Ready(Next)));
    }

    [Fact]
    public void ADownloadLeftFromBefore_IsToldAtStart()
    {
        // What the updater reports at start for an installer it kept.
        Assert.NotNull(new UpdateNotifier(UpdateStage.NotChecked).Next(Ready(Next)));
    }

    [Fact]
    public void ANewerVersionReady_WhileAnotherWaited_IsToldToo()
    {
        var notifier = new UpdateNotifier(UpdateStage.Ready);

        Assert.Null(notifier.Next(Checking(Next)));
        Assert.Null(notifier.Next(new UpdateStatus(UpdateStage.Downloading, "Downloading.", new Version(1, 5, 0), Page)));

        Assert.Equal("OpenCfDrive 1.5.0 is ready", notifier.Next(Ready(new Version(1, 5, 0)))?.Title);
    }

    [Fact]
    public void AVersionThatCouldNotBeDownloaded_IsToldOncePerVersion_WithItsPage()
    {
        var notifier = new UpdateNotifier(UpdateStage.NotChecked);
        var failed = new UpdateStatus(UpdateStage.Failed, "Out, but not downloaded.", Next, Page);

        var notice = notifier.Next(failed);

        Assert.NotNull(notice);
        Assert.Equal("OpenCfDrive 1.4.0 is available", notice.Title);
        Assert.False(notice.Install);
        Assert.Equal(Page, notice.Page);

        // The next check failing the same way says nothing new; a newer one does.
        Assert.Null(notifier.Next(Checking()));
        Assert.Null(notifier.Next(failed));
        Assert.NotNull(notifier.Next(failed with { Available = new Version(1, 5, 0) }));
    }

    [Fact]
    public void NoNewerVersion_OrACheckThatFailed_SaysNothing()
    {
        var notifier = new UpdateNotifier(UpdateStage.NotChecked);

        Assert.Null(notifier.Next(new UpdateStatus(UpdateStage.UpToDate, "Latest.")));
        Assert.Null(notifier.Next(new UpdateStatus(UpdateStage.Failed, "Could not check for updates.")));
        Assert.Null(notifier.Next(new UpdateStatus(UpdateStage.Unavailable, "Not this copy.")));
    }
}
