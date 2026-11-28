using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Models;

namespace OpenCfDrive.Tests;

/// <summary>
/// How often the background sync lists a folder it keeps current: every
/// interval while its listing changes, twice as long each time it comes back
/// the same, up to the cap; back to the interval when it changes or is used
/// here. The clock is set by hand.
/// </summary>
public sealed class FolderPollScheduleTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Max = TimeSpan.FromMinutes(30);

    private readonly SetClock _clock = new();
    private double _random = 0.5; // no spread

    private FolderPollSchedule NewSchedule() => new(Interval, Max, _clock, () => _random);

    [Fact]
    public void AFolderNeverListed_IsDue()
    {
        Assert.True(NewSchedule().IsDue("a"));
    }

    [Fact]
    public void AFirstListing_WaitsOneInterval()
    {
        var schedule = NewSchedule();
        schedule.Listed("a", "x");

        Assert.Equal(Interval, schedule.WaitOf("a"));
        Assert.False(schedule.IsDue("a"));
        _clock.Advance(Interval);
        Assert.True(schedule.IsDue("a"));
    }

    [Fact]
    public void AListingThatComesBackTheSame_DoublesTheWait_UpToTheCap()
    {
        var schedule = NewSchedule();
        schedule.Listed("a", "x");

        var waits = new List<TimeSpan>();
        for (var i = 0; i < 8; i++)
        {
            schedule.Listed("a", "x");
            waits.Add(schedule.WaitOf("a")!.Value);
        }

        Assert.Equal(
            new[] { 1.0, 2, 4, 8, 16, 30, 30, 30 }.Select(TimeSpan.FromMinutes),
            waits);
    }

    [Fact]
    public void AListingThatChanged_GoesBackToTheInterval()
    {
        var schedule = NewSchedule();
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");
        Assert.Equal(TimeSpan.FromMinutes(2), schedule.WaitOf("a"));

        schedule.Listed("a", "y");

        Assert.Equal(Interval, schedule.WaitOf("a"));
    }

    [Fact]
    public void AListingThatFoundSomethingToDo_CountsAsChanged_EvenIfItLooksTheSame()
    {
        var schedule = NewSchedule();
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");

        schedule.Listed("a", "x", changed: true);

        Assert.Equal(Interval, schedule.WaitOf("a"));
    }

    [Fact]
    public void AFolderIsDue_AtTheRunNearestItsWait_NotOnlyTheOneAfter()
    {
        var schedule = NewSchedule();
        _random = 0.99; // the longest spread: 33 s
        schedule.Listed("a", "x");

        _clock.Advance(Interval - TimeSpan.FromSeconds(1));
        Assert.True(schedule.IsDue("a"));
    }

    [Fact]
    public void AFolderIsNotDue_AtTheRunBeforeTheNearestOne()
    {
        var schedule = NewSchedule();
        schedule.Listed("a", "x");
        schedule.Listed("a", "x"); // waits 1 min

        _clock.Advance(Interval);
        Assert.False(schedule.IsDue("a"));
        _clock.Advance(Interval);
        Assert.True(schedule.IsDue("a"));
    }

    [Theory]
    [InlineData(0.0, 0.9)]
    [InlineData(0.5, 1.0)]
    [InlineData(0.9999, 1.1)]
    public void EachWaitIsSpread_ByUpToATenth(double random, double factor)
    {
        _random = random;
        var schedule = NewSchedule();
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");
        schedule.Listed("a", "x"); // waits 8 min, spread

        var listedAt = _clock.Now;
        var due = FirstDue(schedule, "a");

        // Due at the run nearest the end of the wait, half an interval early at most.
        var expected = TimeSpan.FromMinutes(8) * factor;
        Assert.InRange(due - listedAt, expected - Interval / 2 - TimeSpan.FromSeconds(1), expected - Interval / 2 + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void AFolderUsedHere_GoesBackToTheInterval_FromWhenItWasLastListed()
    {
        var schedule = NewSchedule();
        for (var i = 0; i < 6; i++)
        {
            schedule.Listed("a", "x");
        }

        Assert.Equal(TimeSpan.FromMinutes(16), schedule.WaitOf("a"));
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False(schedule.IsDue("a"));

        schedule.Touch("a");

        Assert.Equal(Interval, schedule.WaitOf("a"));
        Assert.True(schedule.IsDue("a"));
    }

    [Fact]
    public void AFolderUsedRightAfterItWasListed_IsDueOneIntervalLater()
    {
        var schedule = NewSchedule();
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");

        schedule.Touch("a");

        Assert.False(schedule.IsDue("a"));
        _clock.Advance(Interval);
        Assert.True(schedule.IsDue("a"));
    }

    [Fact]
    public void TouchingAFolderNeverListed_ChangesNothing()
    {
        var schedule = NewSchedule();
        schedule.Touch("a");

        Assert.True(schedule.IsDue("a"));
        Assert.Null(schedule.WaitOf("a"));
    }

    [Fact]
    public void MakeAllDue_MakesEveryFolderDue_AndKeepsHowLongEachWaits()
    {
        var schedule = NewSchedule();
        for (var i = 0; i < 6; i++)
        {
            schedule.Listed("a", "x");
        }

        schedule.Listed("b", "x");

        schedule.MakeAllDue();

        Assert.True(schedule.IsDue("a"));
        Assert.True(schedule.IsDue("b"));

        // Listed again the same, the wait goes on from where it was.
        schedule.Listed("a", "x");
        Assert.Equal(Max, schedule.WaitOf("a"));
    }

    [Fact]
    public void FolderNamesDifferingOnlyInCase_AreOneFolder()
    {
        var schedule = NewSchedule();
        schedule.Listed("Photos/2024", "x");

        Assert.False(schedule.IsDue("photos/2024"));
    }

    [Fact]
    public void Retain_ForgetsTheOthers()
    {
        var schedule = NewSchedule();
        schedule.Listed("a", "x");
        schedule.Listed("b", "x");

        schedule.Retain(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a" });

        Assert.False(schedule.IsDue("a"));
        Assert.True(schedule.IsDue("b"));
        Assert.Null(schedule.WaitOf("b"));
    }

    [Fact]
    public void CountDueWithin_CountsFoldersNeverListed_AndThoseWhoseWaitEndsInTime()
    {
        var schedule = NewSchedule();
        schedule.Listed("soon", "x"); // 30 s
        schedule.Listed("later", "x");
        schedule.Listed("later", "x"); // 1 min

        var count = schedule.CountDueWithin(["soon", "later", "never"], TimeSpan.FromSeconds(45));

        Assert.Equal(2, count);
    }

    [Fact]
    public void CountDueWithin_CountsTheTopFolder_WhichEveryRunLists()
    {
        var schedule = NewSchedule();
        for (var i = 0; i < 6; i++)
        {
            schedule.Listed("", "x"); // its wait grows like any folder's: 16 min by now
            schedule.Listed("quiet", "x");
        }

        Assert.Equal(TimeSpan.FromMinutes(16), schedule.WaitOf(""));
        Assert.Equal(1, schedule.CountDueWithin(["", "quiet"], TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ACapBelowTheInterval_IsTheInterval()
    {
        var schedule = new FolderPollSchedule(Interval, TimeSpan.FromSeconds(5), _clock, () => 0.5);
        schedule.Listed("a", "x");
        schedule.Listed("a", "x");

        Assert.Equal(Interval, schedule.WaitOf("a"));
    }

    [Fact]
    public void TheCapComesFromItsVariable()
    {
        var before = Environment.GetEnvironmentVariable(FolderPollSchedule.MaxIntervalVariable);
        try
        {
            Environment.SetEnvironmentVariable(FolderPollSchedule.MaxIntervalVariable, "120");
            Assert.Equal(TimeSpan.FromMinutes(2), FolderPollSchedule.ConfiguredMaxInterval());

            Environment.SetEnvironmentVariable(FolderPollSchedule.MaxIntervalVariable, "nonsense");
            Assert.Equal(FolderPollSchedule.DefaultMaxInterval, FolderPollSchedule.ConfiguredMaxInterval());

            Environment.SetEnvironmentVariable(FolderPollSchedule.MaxIntervalVariable, null);
            Assert.Equal(TimeSpan.FromMinutes(30), FolderPollSchedule.ConfiguredMaxInterval());
        }
        finally
        {
            Environment.SetEnvironmentVariable(FolderPollSchedule.MaxIntervalVariable, before);
        }
    }

    /// <summary>Such a number stopped the service as it started: no TimeSpan holds it.</summary>
    [Theory]
    [InlineData("1e12")]
    [InlineData("Infinity")]
    public void ACapNoTimeSpanHolds_IsTheLongestASettingGives(string value)
    {
        var before = Environment.GetEnvironmentVariable(FolderPollSchedule.MaxIntervalVariable);
        try
        {
            Environment.SetEnvironmentVariable(FolderPollSchedule.MaxIntervalVariable, value);
            var cap = FolderPollSchedule.ConfiguredMaxInterval();
            Assert.Equal(SyncScheduler.LongestConfigured, cap);

            // And a schedule works with it: the wait doubles and the next listing has a time.
            var schedule = new FolderPollSchedule(Interval, cap, _clock, () => 0.999);
            for (var i = 0; i < 40; i++)
            {
                schedule.Listed("a", "x");
            }

            Assert.Equal(cap, schedule.WaitOf("a"));
            Assert.False(schedule.IsDue("a"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(FolderPollSchedule.MaxIntervalVariable, before);
        }
    }

    // --- a run's look at the server, against a server that counts what it is asked ---

    [Fact]
    public async Task TheFirstRun_ListsEveryFolder_ParentsFirst()
    {
        var server = new CountingServer(NewSchedule(), "", "a", "a/b", "c");

        Assert.True(await server.RunAsync());

        Assert.Equal(["", "a", "a/b", "c"], server.Asked);
    }

    [Fact]
    public async Task AFolderThatDidNotChange_IsNotListedAtTheNextRun_ButTheTopFolderAlwaysIs()
    {
        var server = new CountingServer(NewSchedule(), "", "a", "a/b");
        await server.RunAsync();
        await RunAfterAsync(server, Interval); // same again: each now waits a minute
        server.Asked.Clear();

        await RunAfterAsync(server, Interval);

        Assert.Equal([""], server.Asked);
    }

    [Fact]
    public async Task OverAnHourOfRuns_AQuietFolderIsListedAFewTimes_NotAtEveryRun()
    {
        var server = new CountingServer(NewSchedule(), "", "quiet");
        await server.RunAsync();
        for (var run = 1; run < 120; run++)
        {
            await RunAfterAsync(server, Interval);
        }

        // At the start, then after waits of 30 s, 1, 2, 4, 8 and 16 min; the
        // next, 30 min on, is past the hour.
        Assert.Equal(7, server.Asked.Count(folder => folder == "quiet"));
        Assert.Equal(120, server.Asked.Count(folder => folder.Length == 0));
    }

    [Fact]
    public async Task AChangeOnTheServer_IsSeenWhenTheFolderIsNextDue_AndTheFolderIsThenListedEveryRunAgain()
    {
        var server = new CountingServer(NewSchedule(), "", "a");
        await server.RunAsync();
        await RunAfterAsync(server, Interval);
        await RunAfterAsync(server, Interval); // not due: waits a minute
        server.Change("a");
        server.Asked.Clear();

        await RunAfterAsync(server, Interval); // due, and finds the change
        await RunAfterAsync(server, Interval); // so due again

        Assert.Equal(2, server.Asked.Count(folder => folder == "a"));
    }

    [Fact]
    public async Task AFolderUsedHere_IsListedAtTheNextRun()
    {
        var schedule = NewSchedule();
        var server = new CountingServer(schedule, "", "a");
        await server.RunAsync();
        for (var run = 0; run < 20; run++)
        {
            await RunAfterAsync(server, Interval);
        }

        server.Asked.Clear();
        await RunAfterAsync(server, Interval);
        Assert.DoesNotContain("a", server.Asked);

        schedule.Touch("a");
        await RunAfterAsync(server, Interval);

        Assert.Contains("a", server.Asked);
    }

    [Fact]
    public async Task SyncNow_ListsEveryFolder()
    {
        var schedule = NewSchedule();
        var server = new CountingServer(schedule, "", "a", "a/b");
        await server.RunAsync();
        await RunAfterAsync(server, Interval);
        server.Asked.Clear();

        schedule.MakeAllDue();
        await server.RunAsync();

        Assert.Equal(["", "a", "a/b"], server.Asked);
    }

    [Fact]
    public async Task AFolderTheServerNoLongerHas_HasTheFolderAboveListedAtOnce_ThoughThatOneWasNotDue()
    {
        var schedule = NewSchedule();
        var server = new CountingServer(schedule, "", "a", "a/b", "a/b/c");
        await server.RunAsync();
        for (var run = 0; run < 3; run++)
        {
            await RunAfterAsync(server, Interval); // all wait 4 min now
        }

        // "a/b" was renamed on the server; "a/b/c" is used here, so it is due.
        server.Remove("a/b");
        server.Remove("a/b/c");
        schedule.Touch("a/b/c");
        server.Asked.Clear();

        await RunAfterAsync(server, Interval);

        // The top folder as always; then "a/b/c", not found; the folder above
        // it, not found either; and the one above that, which is there.
        Assert.Equal(["", "a/b/c", "a/b", "a"], server.Asked);
    }

    [Fact]
    public async Task AFolderAboveAlreadyListedInThisRun_IsNotListedAgainForAFolderNotFound()
    {
        var server = new CountingServer(NewSchedule(), "", "a", "a/b");
        server.Remove("a/b");

        await server.RunAsync();

        Assert.Equal(["", "a", "a/b"], server.Asked);
    }

    [Fact]
    public async Task AServerThatCannotBeReached_StopsTheRun_AndWhatWasNotListedIsStillDue()
    {
        var schedule = NewSchedule();
        var server = new CountingServer(schedule, "", "a", "b");
        server.Answer("a", FolderCheck.Unreachable);

        Assert.False(await server.RunAsync());

        Assert.Equal(["", "a"], server.Asked);
        Assert.True(schedule.IsDue("a"));
        Assert.True(schedule.IsDue("b"));
    }

    [Fact]
    public async Task AFolderTheServerAnswersWithAnError_IsTriedAgainAtTheNextRun_AndTheRunGoesOn()
    {
        var server = new CountingServer(NewSchedule(), "", "a", "b");
        server.Answer("a", FolderCheck.Failed);

        Assert.True(await server.RunAsync());
        await RunAfterAsync(server, Interval);

        Assert.Equal(["", "a", "b", "", "a", "b"], server.Asked);
    }

    [Fact]
    public async Task AFolderRemovedByAListingAboveIt_HasNothingAboveItListed()
    {
        var server = new CountingServer(NewSchedule(), "", "a", "a/b");
        server.Answer("a/b", FolderCheck.Gone);

        await server.RunAsync();

        Assert.Equal(["", "a", "a/b"], server.Asked);
    }

    // --- what counts as the same listing ---

    [Fact]
    public void TheFingerprintIsTheSame_WhateverOrderTheServerListsIn()
    {
        var a = File("a.txt", 1, hash: "h1");
        var b = File("b.txt", 2, hash: "h2");

        Assert.Equal(FolderPollSchedule.Fingerprint([a, b]), FolderPollSchedule.Fingerprint([b, a]));
    }

    [Fact]
    public void TheFingerprintChanges_WhenAnItemIsAddedRemovedOrChanged()
    {
        var before = FolderPollSchedule.Fingerprint([File("a.txt", 1), Folder("sub")]);

        Assert.NotEqual(before, FolderPollSchedule.Fingerprint([File("a.txt", 1), Folder("sub"), File("new.txt", 1)]));
        Assert.NotEqual(before, FolderPollSchedule.Fingerprint([File("a.txt", 1)]));
        Assert.NotEqual(before, FolderPollSchedule.Fingerprint([File("a.txt", 2), Folder("sub")]));
        Assert.NotEqual(before, FolderPollSchedule.Fingerprint([File("a.txt", 1, minutes: 5), Folder("sub")]));
        Assert.NotEqual(before, FolderPollSchedule.Fingerprint([File("a.txt", 1, hash: "other"), Folder("sub")]));
        Assert.NotEqual(before, FolderPollSchedule.Fingerprint([File("b.txt", 1), Folder("sub")]));
        Assert.NotEqual(before, FolderPollSchedule.Fingerprint([File("a.txt", 1), File("sub", 0)]));
    }

    [Fact]
    public void TheFingerprintLeavesOutAFoldersOwnTime_WhichSomeRemotesReportAsTheTimeOfTheListing()
    {
        Assert.Equal(
            FolderPollSchedule.Fingerprint([Folder("sub", minutes: 0)]),
            FolderPollSchedule.Fingerprint([Folder("sub", minutes: 7)]));
    }

    [Fact]
    public void WithoutTimes_TheFingerprintLeavesOutFileTimes()
    {
        Assert.Equal(
            FolderPollSchedule.Fingerprint([File("a.txt", 1, minutes: 0)], withTimes: false),
            FolderPollSchedule.Fingerprint([File("a.txt", 1, minutes: 7)], withTimes: false));
    }

    private static readonly DateTimeOffset Listed = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static CloudItem File(string path, long size, int minutes = 0, string? hash = null) =>
        new() { Path = path, Size = size, ModifiedTime = Listed.AddMinutes(minutes), Hash = hash };

    private static CloudItem Folder(string path, int minutes = 0) =>
        new() { Path = path, IsDirectory = true, ModifiedTime = Listed.AddMinutes(minutes) };

    private Task<bool> RunAfterAsync(CountingServer server, TimeSpan wait)
    {
        _clock.Advance(wait);
        return server.RunAsync();
    }

    /// <summary>
    /// A server that notes each folder it is asked to list, and answers as
    /// the engine's listing does: a folder it has is listed (the schedule is
    /// told what the listing looked like), one it does not have is not found.
    /// </summary>
    private sealed class CountingServer(FolderPollSchedule schedule, params string[] folders)
    {
        private readonly List<string> _folders = [.. folders];
        private readonly Dictionary<string, int> _versions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, FolderCheck> _answers = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _removed = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Asked { get; } = [];

        public void Change(string folder) => _versions[folder] = _versions.GetValueOrDefault(folder) + 1;

        /// <summary>Gone from the server; this PC still has it until a listing above it says so.</summary>
        public void Remove(string folder) => _removed.Add(folder);

        public void Answer(string folder, FolderCheck answer) => _answers[folder] = answer;

        public Task<bool> RunAsync() => schedule.RunAsync(_folders, Check);

        private Task<FolderCheck> Check(string folder, CancellationToken cancellationToken)
        {
            Asked.Add(folder);
            if (_answers.TryGetValue(folder, out var answer))
            {
                return Task.FromResult(answer);
            }

            if (_removed.Contains(folder))
            {
                return Task.FromResult(FolderCheck.NotFound);
            }

            schedule.Listed(folder, _versions.GetValueOrDefault(folder).ToString());
            return Task.FromResult(FolderCheck.Listed);
        }
    }

    /// <summary>The first moment, in one-second steps, at which the folder is due.</summary>
    private DateTimeOffset FirstDue(FolderPollSchedule schedule, string directory)
    {
        for (var i = 0; i < 24 * 3600; i++)
        {
            if (schedule.IsDue(directory))
            {
                return _clock.Now;
            }

            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        throw new InvalidOperationException("Never due.");
    }

    private sealed class SetClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 28, 12, 0, 0, TimeSpan.Zero);

        public void Advance(TimeSpan by) => Now += by;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
