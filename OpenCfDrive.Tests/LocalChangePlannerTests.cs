using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Tests;

/// <summary>
/// One plan per sync run, as Nextcloud's discovery makes: every item once,
/// read from the drive as it is now, whatever order the drive reported it in.
/// </summary>
public class LocalChangePlannerTests
{
    private const string Root = @"C:\drive";
    private static readonly DateTime Written = new(2026, 11, 4, 1, 2, 3, DateTimeKind.Utc);

    // Every change below is reported a second before the run plans it.
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 2, 0, 0, TimeSpan.Zero);

    private sealed class Drive : ILocalSide
    {
        private readonly Dictionary<string, LocalItem> _items = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Hydrating { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> OwnWrites { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Drive File(string relative, long fileId = 0, long length = 1, DateTime? written = null)
        {
            _items[Local(relative)] = new LocalItem(false, fileId, length, written ?? Written);
            return this;
        }

        public Drive Folder(string relative, long fileId = 0)
        {
            _items[Local(relative)] = new LocalItem(true, fileId, 0, default);
            return this;
        }

        public LocalItem? Find(string localPath) => _items.GetValueOrDefault(localPath);

        public bool IsHydrating(string localPath) => Hydrating.Contains(localPath);

        public bool IsOwnWrite(string localPath) => OwnWrites.Contains(localPath);
    }

    private static string Local(string relative) => Path.Combine(Root, relative.Replace('/', '\\'));

    private static LocalChange Changed(string relative) => new(0, LocalChangeKind.Changed, Local(relative));

    private static LocalChange Deleted(string remotePath, long fileId = 0, (long, DateTime)? synced = null) =>
        new(0, LocalChangeKind.Deleted, Local(remotePath.TrimEnd('/')), null, remotePath, fileId, remotePath.EndsWith('/'), synced);

    private static LocalChange Renamed(string remotePath, string newRelative) =>
        new(0, LocalChangeKind.Renamed, Local(remotePath.TrimEnd('/')), Local(newRelative), remotePath, 0, remotePath.EndsWith('/'));

    /// <summary>The changes as reported, in this order.</summary>
    private static List<LocalChange> InOrder(params LocalChange[] changes) =>
        changes.Select((change, index) => change with
        {
            Seq = index + 1,
            RecordedAt = change.RecordedAt == default ? Now - TimeSpan.FromSeconds(1) : change.RecordedAt,
        }).ToList();

    private static IEnumerable<T[]> Orders<T>(T[] items) =>
        items.Length <= 1
            ? [items]
            : items.SelectMany((item, index) => Orders(items.Where((_, i) => i != index).ToArray()).Select(rest => rest.Prepend(item).ToArray()));

    [Fact]
    public void AFolderDeletedWithWhatIsInIt_IsOneDelete_InEveryOrderReported()
    {
        // C10: the drive reports the folder, its subfolder and each file.
        LocalChange[] reported =
        [
            Deleted("f/sub/y.txt", fileId: 4),
            Deleted("f/sub/", fileId: 3),
            Deleted("f/x.txt", fileId: 2),
            Deleted("f/", fileId: 1),
        ];

        foreach (var order in Orders(reported))
        {
            var plan = LocalChangePlanner.Plan(InOrder(order), new Drive(), Now);

            var delete = Assert.Single(plan.Deletes);
            Assert.Equal(("f/", Local("f"), true), (delete.RemotePath, delete.LocalPath, delete.IsDirectory));
            Assert.Equal(4, delete.From.Count);
            Assert.Empty(plan.Moves);
            Assert.Empty(plan.Folders);
            Assert.Empty(plan.Uploads);
        }
    }

    [Fact]
    public void ADeletedFile_ThatIsBackAsAnotherFile_IsUploaded_NotDeleted()
    {
        // C13: a save that replaces the file.
        var plan = LocalChangePlanner.Plan(
            InOrder(Deleted("safe.txt", fileId: 10)),
            new Drive().File("safe.txt", fileId: 11), Now);

        Assert.Empty(plan.Deletes);
        Assert.Equal(Local("safe.txt"), Assert.Single(plan.Uploads).LocalPath);
        Assert.Contains("was replaced by a new item", Assert.Single(plan.Dropped).Reason);
    }

    [Fact]
    public void ADeletedFileStillThere_Waits_ThenIsKeptOnceTheDeleteHasFailed()
    {
        // C12: a move out of the drive downloads the file before it goes.
        var drive = new Drive().File("leaving.txt", fileId: 10);
        var plan = LocalChangePlanner.Plan(InOrder(Deleted("leaving.txt", fileId: 10)), drive, Now);
        Assert.Single(plan.Later);
        Assert.Empty(plan.Deletes);

        var failed = Deleted("leaving.txt", fileId: 10) with { RecordedAt = Now - LocalChangePlanner.LeaveTimeout };
        plan = LocalChangePlanner.Plan(InOrder(failed), drive, Now);
        Assert.Empty(plan.Later);
        Assert.Empty(plan.Deletes);
        Assert.Empty(plan.Uploads);
        Assert.Contains("never left the drive", Assert.Single(plan.Dropped).Reason);
    }

    [Fact]
    public void ARenameOfARename_IsOneMove()
    {
        // The second rename is reported under the item's server path still.
        var plan = LocalChangePlanner.Plan(
            InOrder(Renamed("a.txt", "b.txt"), Renamed("a.txt", "c.txt") with { LocalPath = Local("b.txt") }),
            new Drive().File("c.txt"), Now);

        var move = Assert.Single(plan.Moves);
        Assert.Equal(("a.txt", Local("a.txt"), Local("c.txt")), (move.RemotePath, move.OldLocalPath, move.NewLocalPath));
        Assert.Equal(2, move.From.Count);
    }

    [Fact]
    public void RenamedBack_IsNothing()
    {
        var plan = LocalChangePlanner.Plan(
            InOrder(Renamed("a.txt", "b.txt"), Renamed("a.txt", "a.txt") with { LocalPath = Local("b.txt") }),
            new Drive().File("a.txt"), Now);

        Assert.Empty(plan.Moves);
        Assert.Equal(2, plan.Dropped.Sum(d => d.Changes.Count));
    }

    [Fact]
    public void RenamedThenEdited_IsMovedBeforeItIsUploaded()
    {
        // C19 and C24: the edit goes up under the new name, after the move.
        var plan = LocalChangePlanner.Plan(
            InOrder(Renamed("a.txt", "b.txt"), Changed("b.txt")),
            new Drive().File("b.txt"), Now);

        Assert.Equal(Local("b.txt"), Assert.Single(plan.Moves).NewLocalPath);
        Assert.Equal(Local("b.txt"), Assert.Single(plan.Uploads).LocalPath);
    }

    [Fact]
    public void RenamedThenDeleted_DeletesItUnderItsOldName()
    {
        var plan = LocalChangePlanner.Plan(
            InOrder(Renamed("a.txt", "b.txt"), Deleted("b.txt")),
            new Drive(), Now);

        Assert.Empty(plan.Moves);
        var delete = Assert.Single(plan.Deletes);
        Assert.Equal("a.txt", delete.RemotePath);
        Assert.Equal(2, delete.From.Count);
    }

    [Fact]
    public void ARenameThatDidNotHappen_IsDropped()
    {
        var plan = LocalChangePlanner.Plan(InOrder(Renamed("a.txt", "b.txt")), new Drive().File("a.txt"), Now);

        Assert.Empty(plan.Moves);
        Assert.Contains("was not renamed after all", Assert.Single(plan.Dropped).Reason);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(7L)]
    public void AMoveTheWatcherReportsAsDeleteAndCreate_IsOneMove(long fileId)
    {
        // Mock mode's C09: told by the file id, or else by size and write time.
        var plan = LocalChangePlanner.Plan(
            InOrder(Deleted("client/move-me.txt", fileId, synced: (7, Written)), Changed("docs/moved.txt")),
            new Drive().File("docs/moved.txt", fileId, length: 7), Now);

        var move = Assert.Single(plan.Moves);
        Assert.Equal(("client/move-me.txt", Local("docs/moved.txt")), (move.RemotePath, move.NewLocalPath));
        Assert.Empty(plan.Deletes);
    }

    [Fact]
    public void ADeleteAndAnUnrelatedNewFile_StayADeleteAndAnUpload()
    {
        var plan = LocalChangePlanner.Plan(
            InOrder(Deleted("old.txt", synced: (7, Written)), Changed("new.txt")),
            new Drive().File("new.txt", length: 9), Now);

        Assert.Empty(plan.Moves);
        Assert.Equal("old.txt", Assert.Single(plan.Deletes).RemotePath);
        Assert.Equal(Local("new.txt"), Assert.Single(plan.Uploads).LocalPath);
    }

    [Fact]
    public void ANewFolderWithFiles_IsMadeParentsFirst_ThenItsFilesAreUploaded()
    {
        var plan = LocalChangePlanner.Plan(
            InOrder(Changed("n/sub/a.txt"), Changed("n/sub"), Changed("n")),
            new Drive().Folder("n").Folder("n/sub").File("n/sub/a.txt"), Now);

        Assert.Equal([Local("n"), Local("n/sub")], plan.Folders.Select(f => f.LocalPath));
        Assert.Equal(Local("n/sub/a.txt"), Assert.Single(plan.Uploads).LocalPath);
    }

    [Fact]
    public void AnEditInsideADeletedFolder_GoesWithIt()
    {
        var plan = LocalChangePlanner.Plan(InOrder(Changed("f/x.txt"), Deleted("f/")), new Drive(), Now);

        Assert.Equal("f/", Assert.Single(plan.Deletes).RemotePath);
        Assert.Empty(plan.Uploads);
    }

    [Fact]
    public void AFileStillDownloading_WaitsForTheNextRun()
    {
        var drive = new Drive().File("big.bin");
        drive.Hydrating.Add(Local("big.bin"));

        var plan = LocalChangePlanner.Plan(InOrder(Changed("big.bin")), drive, Now);

        Assert.Empty(plan.Uploads);
        Assert.Single(plan.Later);
    }

    [Fact]
    public void TheProvidersOwnWrite_IsNotUploaded()
    {
        var drive = new Drive().File("listed.txt");
        drive.OwnWrites.Add(Local("listed.txt"));

        var plan = LocalChangePlanner.Plan(InOrder(Changed("listed.txt")), drive, Now);

        Assert.Empty(plan.Uploads);
    }

    [Fact]
    public void TheJournal_KeepsDeletesAndRenamesUntilTheyAreDone_AcrossARestart()
    {
        var dir = Path.Combine(Path.GetTempPath(), "opencfdrive-changes", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SqliteMetadataStore(Path.Combine(dir, "state.db"));
        store.Initialize();

        var changes = new LocalChanges(store);
        var recorded = 0;
        changes.Recorded += () => recorded++;
        changes.Deleted(Local("gone.txt"), "gone.txt", 1, false);
        changes.Renamed(Local("old"), Local("new"), "old/", null);
        changes.Changed(Local("edited.txt"));
        var taken = changes.Pending();
        changes.Changed(Local("edited.txt"));
        Assert.Equal(4, recorded);
        Assert.Equal(3, changes.Pending().Count);

        // Edited again while a run sends it: that run's end leaves the new one.
        changes.Complete(taken.Where(c => c.Kind == LocalChangeKind.Changed));
        Assert.Contains(changes.Pending(), c => c.Kind == LocalChangeKind.Changed && LocalChanges.Same(c.LocalPath, Local("edited.txt")));
        Assert.Equal(3, changes.Pending().Count);

        // Listings leave a deleted or renamed item, and what is below it, alone.
        Assert.True(changes.IsPending(Local("gone.txt")));
        Assert.True(changes.IsPending(Local(@"new\inside.txt")));
        Assert.True(changes.IsPending(Local(@"old\inside.txt")));
        Assert.False(changes.IsPending(Local("edited.txt")));
        Assert.False(changes.IsPending(Local("newer")));

        // What the renamed folder is listed into counts only its own changes.
        Assert.True(changes.IsPendingAt(Local("old")));
        Assert.True(changes.IsPendingAt(Local("new")));
        Assert.True(changes.IsPendingAt(Local("GONE.txt")));
        Assert.False(changes.IsPendingAt(Local(@"old\inside.txt")));
        Assert.False(changes.IsPendingAt(Local(@"new\inside.txt")));
        Assert.False(changes.IsPendingAt(Local("edited.txt")));

        // A download asks by server path whether a rename above it waits.
        Assert.Equal(Local("new"), changes.PendingRenameOf("old/inside.txt")?.NewLocalPath);
        Assert.Equal(Local("new"), changes.PendingRenameOf("old/")?.NewLocalPath);
        Assert.Null(changes.PendingRenameOf("older/inside.txt"));
        Assert.Null(changes.PendingRenameOf("gone.txt"));

        // A restart: the delete and the rename are still there, the edit is
        // found again by the look at the drive.
        var restarted = new LocalChanges(store);
        restarted.LoadJournal();
        Assert.Equal([LocalChangeKind.Deleted, LocalChangeKind.Renamed], restarted.Pending().Select(c => c.Kind));

        restarted.Complete(restarted.Pending().Where(c => c.Kind == LocalChangeKind.Deleted));
        Assert.Equal("old/", Assert.Single(store.GetPendingChanges()).RemotePath);
    }
}
