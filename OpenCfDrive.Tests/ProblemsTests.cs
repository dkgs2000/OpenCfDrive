using OpenCfDrive.Core.Control;

namespace OpenCfDrive.Tests;

/// <summary>
/// What the user is told about sync problems: one short line per problem
/// (the file and the server's answer, not rclone's raw reply), each new
/// problem once, and one that keeps failing not again for a while.
/// </summary>
public sealed class ProblemsTests
{
    // rclone's remote control wraps the server's own error page, JSON-escaped.
    private const string TooLarge =
        "Upload failed for 'C:\\Users\\someone\\OpenCfDrive\\Videos\\movie.mkv': operations/uploadfile failed (500): {\n" +
        "\t\"error\": \"unchunked simple update failed: \\u003chtml\\u003e\\r\\n\\u003chead\\u003e\\u003ctitle\\u003e413 Payload Too Large\\u003c/title\\u003e\\u003c/head\\u003e\",\n" +
        "\t\"status\": 500\n}";

    private const string NotFound =
        "Rename propagation failed for 'New folder/': sync/move failed (500): {\n" +
        "\t\"error\": \"DirMove MOVE call failed: \\u003c!DOCTYPE HTML PUBLIC \\\"-//IETF//DTD HTML 2.0//EN\\\"\\u003e\\n\\u003chtml\\u003e\\u003chead\\u003e\\n\\u003ctitle\\u003e404 Not Found\\u003c/title\\u003e\"\n}";

    [Theory]
    [InlineData(TooLarge, "Upload failed for 'movie.mkv': the server answered 413 Payload Too Large.")]
    [InlineData(NotFound, "Rename propagation failed for 'New folder/': the server answered 404 Not Found.")]
    [InlineData(
        "Server deleted 'a.txt', but the local file could not be removed: The process cannot access the file 'C:\\drive\\a.txt' because it is being used by another process.",
        "Server deleted 'a.txt', but the local file could not be removed: The process cannot access the file 'a.txt' because it is being used by another process.")]
    [InlineData(
        "Delete propagation failed for 'old.txt': operations/deletefile failed (500): {\n\t\"error\": \"object not found\"\n}",
        "Delete propagation failed for 'old.txt': the server answered object not found.")]
    public void Summarize_GivesTheFileAndTheServersAnswer(string message, string expected) =>
        Assert.Equal(expected, ProblemText.Summarize(message));

    [Fact]
    public void Summarize_KeepsANotificationShort()
    {
        var text = ProblemText.Summarize("Upload failed for 'a.txt': " + new string('x', 1000));
        Assert.True(text.Length <= 220, $"{text.Length} characters");
        Assert.StartsWith("Upload failed for 'a.txt': ", text);
    }

    [Fact]
    public void Notifier_TellsEachNewProblemOnce_NotWhatHappenedBeforeTheAppLooked()
    {
        var notifier = new ProblemNotifier();
        var problems = new ProblemLog();
        problems.Report("Upload failed for 'before.txt': refused.");

        // The app starts while the service runs: what came before is not news.
        Assert.Null(notifier.Next(Status(problems)));
        Assert.Null(notifier.Next(Status(problems)));

        problems.Report(TooLarge);
        var notice = notifier.Next(Status(problems));
        Assert.Equal("OpenCfDrive couldn't sync", notice?.Title);
        Assert.Equal("Upload failed for 'movie.mkv': the server answered 413 Payload Too Large.", notice?.Text);
        Assert.Null(notifier.Next(Status(problems)));
    }

    [Fact]
    public void Notifier_PutsSeveralNewProblemsInOneNotification()
    {
        var notifier = new ProblemNotifier();
        var problems = new ProblemLog();
        Assert.Null(notifier.Next(Status(problems)));

        problems.Report(TooLarge);
        problems.Report(NotFound);
        var notice = notifier.Next(Status(problems));
        Assert.Equal("OpenCfDrive couldn't sync 2 items", notice?.Title);
        Assert.Equal("Rename propagation failed for 'New folder/': the server answered 404 Not Found. (and 1 more; click for details)", notice?.Text);
    }

    [Fact]
    public void Notifier_TellsAProblemThatKeepsFailing_AgainOnlyAfterAWhile()
    {
        var clock = new Clock();
        var notifier = new ProblemNotifier(clock);
        var problems = new ProblemLog();
        Assert.Null(notifier.Next(Status(problems)));

        problems.Report(TooLarge);
        Assert.NotNull(notifier.Next(Status(problems)));

        // The next sync runs fail the same way.
        clock.Advance(TimeSpan.FromMinutes(1));
        problems.Report(TooLarge);
        Assert.Null(notifier.Next(Status(problems)));

        clock.Advance(ProblemNotifier.RepeatAfter);
        problems.Report(TooLarge);
        Assert.NotNull(notifier.Next(Status(problems)));
    }

    [Fact]
    public void Notifier_StartsAnewWithAServiceThatStartedAgain()
    {
        var notifier = new ProblemNotifier();
        var first = new ProblemLog();
        first.Report("Upload failed for 'a.txt': refused.");
        Assert.Null(notifier.Next(Status(first)));

        // Restarted while the app runs: everything it reports is new.
        var again = new ProblemLog();
        again.Report(NotFound);
        var notice = notifier.Next(Status(again, processId: 2, startedAt: Started.AddMinutes(5)));
        Assert.Equal("Rename propagation failed for 'New folder/': the server answered 404 Not Found.", notice?.Text);
        Assert.Null(notifier.Next(null));
    }

    private static readonly DateTimeOffset Started = new(2026, 11, 5, 17, 0, 0, TimeSpan.Zero);

    private static ServiceStatus Status(ProblemLog problems, int processId = 1, DateTimeOffset? startedAt = null) => new(
        ProcessId: processId,
        StartedAt: startedAt ?? Started,
        Version: "1.0.0",
        SyncRoot: @"C:\drive",
        Remote: "remote:",
        RcUrl: "",
        State: ServiceState.Syncing,
        Detail: "",
        Registered: true,
        Connected: true,
        ItemsTracked: 0,
        BytesDownloaded: 0,
        LogPath: "",
        RecentActivity: [],
        ProblemsReported: problems.Count,
        RecentProblems: problems.Recent());

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = Started;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
