using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.CloudFiles;

public enum LocalChangeKind
{
    /// <summary>A file written or created, or a folder created.</summary>
    Changed,
    Deleted,
    Renamed,
}

/// <summary>
/// A local change reported and not sent yet. <paramref name="RemotePath"/> is
/// the item's server path when it was reported (folders end in '/'); null for
/// <see cref="LocalChangeKind.Changed"/>. <paramref name="Synced"/> is the size
/// and write time a deleted file had when last in sync, which a move reported
/// as delete + create is told by. <paramref name="JournalId"/> is its row in
/// the store's journal (deletes and renames), 0 when it has none.
/// </summary>
public sealed record LocalChange(
    long Seq,
    LocalChangeKind Kind,
    string LocalPath,
    string? NewLocalPath = null,
    string? RemotePath = null,
    long FileId = 0,
    bool IsDirectory = false,
    (long Length, DateTime LastWriteUtc)? Synced = null,
    SyncRootConnection.RenameSource? Source = null,
    long JournalId = 0,
    DateTimeOffset RecordedAt = default);

/// <summary>
/// The local changes waiting for the next sync run, in the order they were
/// reported, as Nextcloud's folder watcher marks paths for its next run.
/// Deletes and renames are written to the store's journal when they are
/// reported and leave it once they reached the server, so one made just
/// before the service stops still reaches the server when it starts again.
/// Uploads and new folders need no journal: the look at the drive at start
/// finds them again.
/// </summary>
public sealed class LocalChanges(SqliteMetadataStore store)
{
    private readonly object _gate = new();
    private readonly List<LocalChange> _changes = [];
    private long _seq;

    /// <summary>After every change recorded: the cue for a local run.</summary>
    public event Action? Recorded;

    public bool IsEmpty
    {
        get
        {
            lock (_gate)
            {
                return _changes.Count == 0;
            }
        }
    }

    public void Changed(string localPath)
    {
        lock (_gate)
        {
            if (!_changes.Any(c => c.Kind == LocalChangeKind.Changed && Same(c.LocalPath, localPath)))
            {
                _changes.Add(new LocalChange(++_seq, LocalChangeKind.Changed, localPath, RecordedAt: DateTimeOffset.UtcNow));
            }
        }

        Recorded?.Invoke();
    }

    public void Deleted(string localPath, string remotePath, long fileId, bool isDirectory, (long Length, DateTime LastWriteUtc)? synced = null)
    {
        var journal = store.AddPendingChange(PendingChangeKind.Delete, remotePath, localPath);
        Add(new LocalChange(0, LocalChangeKind.Deleted, localPath, null, remotePath, fileId, isDirectory, synced, null, journal));
    }

    public void Renamed(string oldLocalPath, string newLocalPath, string remotePath, SyncRootConnection.RenameSource? source)
    {
        var journal = store.AddPendingChange(PendingChangeKind.Rename, remotePath, oldLocalPath, newLocalPath);
        Add(new LocalChange(0, LocalChangeKind.Renamed, oldLocalPath, newLocalPath, remotePath, 0, remotePath.EndsWith('/'), null, source, journal));
    }

    /// <summary>The deletes and renames the journal kept from before the service started.</summary>
    public void LoadJournal()
    {
        foreach (var row in store.GetPendingChanges())
        {
            lock (_gate)
            {
                if (_changes.Any(c => c.JournalId == row.Id))
                {
                    continue;
                }

                _changes.Add(row.Kind == PendingChangeKind.Delete
                    ? new LocalChange(++_seq, LocalChangeKind.Deleted, row.LocalPath, null, row.RemotePath, 0, row.RemotePath.EndsWith('/'), null, null, row.Id, DateTimeOffset.UtcNow)
                    : new LocalChange(++_seq, LocalChangeKind.Renamed, row.LocalPath, row.NewLocalPath, row.RemotePath, 0, row.RemotePath.EndsWith('/'), null, null, row.Id, DateTimeOffset.UtcNow));
            }
        }
    }

    /// <summary>What is waiting, oldest first.</summary>
    public IReadOnlyList<LocalChange> Pending()
    {
        lock (_gate)
        {
            return _changes.ToList();
        }
    }

    /// <summary>Done with: sent, or found to need nothing. Their journal rows go with them.</summary>
    public void Complete(IEnumerable<LocalChange> done)
    {
        var journal = new List<long>();
        lock (_gate)
        {
            var seqs = done.Select(c => c.Seq).ToHashSet();
            var finished = _changes.Where(c => seqs.Contains(c.Seq)).ToList();
            _changes.RemoveAll(c => seqs.Contains(c.Seq));
            journal.AddRange(finished
                .Where(c => c.JournalId != 0 && !_changes.Any(other => other.JournalId == c.JournalId))
                .Select(c => c.JournalId)
                .Distinct());
        }

        foreach (var id in journal)
        {
            store.RemovePendingChange(id);
        }
    }

    /// <summary>
    /// A delete or rename of the item, or of a folder above it, is waiting for
    /// the server: listings leave both its names alone meanwhile.
    /// </summary>
    public bool IsPending(string localPath)
    {
        lock (_gate)
        {
            return _changes.Any(c => c.Kind != LocalChangeKind.Changed &&
                                     (IsAtOrUnder(localPath, c.LocalPath) ||
                                      (c.NewLocalPath is not null && IsAtOrUnder(localPath, c.NewLocalPath))));
        }
    }

    /// <summary>Any change of this item is waiting.</summary>
    public bool HasChangeFor(string localPath)
    {
        lock (_gate)
        {
            return _changes.Any(c => Same(c.LocalPath, localPath) || (c.NewLocalPath is not null && Same(c.NewLocalPath, localPath)));
        }
    }

    private void Add(LocalChange change)
    {
        lock (_gate)
        {
            _changes.Add(change with { Seq = ++_seq, RecordedAt = DateTimeOffset.UtcNow });
        }

        Recorded?.Invoke();
    }

    internal static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static bool IsAtOrUnder(string path, string root) =>
        Same(path, root) || IsUnder(path, root);

    internal static bool IsUnder(string path, string root) =>
        path.Length > root.Length + 1 &&
        path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
        path[root.Length] == Path.DirectorySeparatorChar;
}

/// <summary>What the planner needs to know about the drive as it is now.</summary>
public interface ILocalSide
{
    /// <summary>The item at the path, or null when there is none.</summary>
    LocalItem? Find(string localPath);

    /// <summary>A download is on its way at or below the path.</summary>
    bool IsHydrating(string localPath);

    /// <summary>The file was last written by the provider and not edited since.</summary>
    bool IsOwnWrite(string localPath);
}

/// <param name="FileId">The NTFS file id, 0 when unknown.</param>
public sealed record LocalItem(bool IsDirectory, long FileId, long Length, DateTime LastWriteUtc);

public sealed record PlannedMove(string RemotePath, string OldLocalPath, string NewLocalPath, SyncRootConnection.RenameSource? Source, IReadOnlyList<LocalChange> From);

public sealed record PlannedDelete(string RemotePath, string LocalPath, bool IsDirectory, IReadOnlyList<LocalChange> From);

public sealed record PlannedItem(string LocalPath, IReadOnlyList<LocalChange> From);

/// <summary>Changes that need nothing sent; <paramref name="Reason"/> is logged when there is one.</summary>
public sealed record DroppedChange(IReadOnlyList<LocalChange> Changes, string? Reason);

/// <summary>
/// One run's plan, carried out in this order: moves (in the order they were
/// made), deletes, new folders (parents first), uploads.
/// </summary>
public sealed record LocalChangePlan(
    IReadOnlyList<PlannedMove> Moves,
    IReadOnlyList<PlannedDelete> Deletes,
    IReadOnlyList<PlannedItem> Folders,
    IReadOnlyList<PlannedItem> Uploads,
    IReadOnlyList<LocalChange> Later,
    IReadOnlyList<DroppedChange> Dropped);

/// <summary>
/// Turns the changes reported since the last run into one plan, as a Nextcloud
/// sync run's discovery does: each item once, with what happened to it since
/// it was reported read from the drive as it is now, so nothing waits for an
/// item to go and nothing runs beside anything else. A folder deleted is one
/// delete, whatever was reported inside it and in whichever order.
/// </summary>
public static class LocalChangePlanner
{
    /// <summary>
    /// How long a deleted or moved-out item may stay in place before the
    /// delete counts as failed and the server copy is kept. A move out of the
    /// drive downloads the item first, which can take a while.
    /// </summary>
    public static readonly TimeSpan LeaveTimeout = TimeSpan.FromSeconds(30);

    private sealed class MoveDraft(string remotePath, string oldLocal, string newLocal, SyncRootConnection.RenameSource? source, LocalChange first)
    {
        public string RemotePath { get; } = remotePath;
        public string OldLocal { get; } = oldLocal;
        public string NewLocal { get; set; } = newLocal;
        public SyncRootConnection.RenameSource? Source { get; } = source;
        public List<LocalChange> From { get; } = [first];
    }

    private sealed class DeleteDraft(LocalChange first)
    {
        public string RemotePath { get; set; } = first.RemotePath ?? string.Empty;
        public string LocalPath { get; } = first.LocalPath;
        public bool IsDirectory { get; set; } = first.IsDirectory;
        public long FileId { get; set; } = first.FileId;
        public (long Length, DateTime LastWriteUtc)? Synced { get; set; } = first.Synced;
        public List<LocalChange> From { get; } = [first];
    }

    private sealed class ItemDraft(string localPath)
    {
        public string LocalPath { get; } = localPath;
        public List<LocalChange> From { get; } = [];
    }

    public static LocalChangePlan Plan(IReadOnlyList<LocalChange> changes, ILocalSide side, DateTimeOffset now)
    {
        var moves = new List<MoveDraft>();
        var deletes = new List<DeleteDraft>();
        var changed = new List<ItemDraft>();
        var dropped = new List<DroppedChange>();
        var later = new List<LocalChange>();

        ItemDraft ChangedAt(string localPath)
        {
            var item = changed.FirstOrDefault(i => LocalChanges.Same(i.LocalPath, localPath));
            if (item is null)
            {
                item = new ItemDraft(localPath);
                changed.Add(item);
            }

            return item;
        }

        // Each item once; a rename of a rename is one rename (A -> B -> C is A -> C).
        foreach (var change in changes.OrderBy(c => c.Seq))
        {
            switch (change.Kind)
            {
                case LocalChangeKind.Renamed:
                    var chain = moves.LastOrDefault(m => LocalChanges.Same(m.NewLocal, change.LocalPath));
                    var repeat = moves.FirstOrDefault(m => LocalChanges.Same(m.OldLocal, change.LocalPath) && LocalChanges.Same(m.NewLocal, change.NewLocalPath!));
                    if (repeat is not null)
                    {
                        repeat.From.Add(change); // both of the drive's signals reported it
                    }
                    else if (chain is not null)
                    {
                        chain.NewLocal = change.NewLocalPath!;
                        chain.From.Add(change);
                    }
                    else
                    {
                        moves.Add(new MoveDraft(change.RemotePath!, change.LocalPath, change.NewLocalPath!, change.Source, change));
                    }

                    break;

                case LocalChangeKind.Deleted:
                    var known = deletes.FirstOrDefault(d => LocalChanges.Same(d.LocalPath, change.LocalPath));
                    if (known is null)
                    {
                        deletes.Add(new DeleteDraft(change));
                    }
                    else
                    {
                        known.From.Add(change);
                        known.IsDirectory |= change.IsDirectory;
                        known.FileId = known.FileId != 0 ? known.FileId : change.FileId;
                        known.Synced ??= change.Synced;
                    }

                    break;

                default:
                    ChangedAt(change.LocalPath).From.Add(change);
                    break;
            }
        }

        // A move the watcher reports as a delete and a new file (mock mode,
        // plain files): the same file id, else the size and write time it had
        // when last in sync (a move keeps both), preferring the same name.
        foreach (var delete in deletes.ToList())
        {
            if (delete.IsDirectory || side.Find(delete.LocalPath) is not null || (delete.Synced is null && delete.FileId == 0))
            {
                continue;
            }

            var target = changed
                .Where(i => !LocalChanges.Same(i.LocalPath, delete.LocalPath))
                .Select(i => (Item: i, Found: side.Find(i.LocalPath)))
                .Where(x => x.Found is { IsDirectory: false } found &&
                            ((delete.FileId != 0 && found.FileId == delete.FileId) ||
                             (delete.Synced is { } synced && (found.Length, found.LastWriteUtc) == synced)))
                .OrderByDescending(x => string.Equals(Path.GetFileName(x.Item.LocalPath), Path.GetFileName(delete.LocalPath), StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Item)
                .FirstOrDefault();
            if (target is not null)
            {
                var move = new MoveDraft(delete.RemotePath, delete.LocalPath, target.LocalPath, null, delete.From[0]);
                move.From.AddRange(delete.From.Skip(1));
                moves.Add(move);
                deletes.Remove(delete);
            }
        }

        // A delete is read from the drive now instead of waited for.
        var gone = new List<DeleteDraft>();
        foreach (var delete in deletes)
        {
            var item = side.Find(delete.LocalPath);
            if (item is null)
            {
                gone.Add(delete);
            }
            else if (delete.FileId != 0 && item.FileId == delete.FileId)
            {
                // Still the same item: on its way out (downloaded first for a
                // move out of the drive), or the delete failed.
                if (side.IsHydrating(delete.LocalPath) || now - delete.From[0].RecordedAt < LeaveTimeout)
                {
                    later.AddRange(delete.From);
                }
                else
                {
                    dropped.Add(new DroppedChange(delete.From, $"'{delete.RemotePath}' never left the drive; the server copy is kept."));
                }
            }
            else
            {
                // Something else is there now: it is what the server gets.
                dropped.Add(new DroppedChange(
                    delete.From,
                    delete.FileId != 0 && item.FileId != 0 ? $"'{delete.RemotePath}' was replaced by a new item; its upload updates the server copy." : null));
                ChangedAt(delete.LocalPath);
            }
        }

        // What was inside a deleted folder goes with it: one delete.
        var planned = new List<DeleteDraft>();
        foreach (var delete in gone.OrderBy(d => d.LocalPath.Length))
        {
            var folder = planned.FirstOrDefault(p => LocalChanges.IsUnder(delete.LocalPath, p.LocalPath));
            if (folder is null)
            {
                planned.Add(delete);
            }
            else
            {
                folder.IsDirectory = true;
                folder.From.AddRange(delete.From);
            }
        }

        foreach (var move in moves.ToList())
        {
            if (LocalChanges.Same(move.OldLocal, move.NewLocal))
            {
                moves.Remove(move);
                dropped.Add(new DroppedChange(move.From, null)); // renamed back
                continue;
            }

            // Renamed, then deleted: the server still has it under its old name.
            var deletedAfter = planned.FirstOrDefault(p => LocalChanges.Same(p.LocalPath, move.NewLocal));
            if (deletedAfter is not null)
            {
                moves.Remove(move);
                deletedAfter.RemotePath = move.RemotePath;
                deletedAfter.IsDirectory |= move.RemotePath.EndsWith('/');
                deletedAfter.From.AddRange(move.From);
                continue;
            }

            if (side.Find(move.NewLocal) is null && side.Find(move.OldLocal) is not null)
            {
                moves.Remove(move);
                dropped.Add(new DroppedChange(move.From, $"'{move.RemotePath}' was not renamed after all; the server copy keeps its name."));
            }
        }

        var folders = new List<ItemDraft>();
        var uploads = new List<ItemDraft>();
        foreach (var item in changed)
        {
            var found = side.Find(item.LocalPath);
            if (found is null || planned.Any(p => LocalChanges.IsAtOrUnder(item.LocalPath, p.LocalPath)))
            {
                dropped.Add(new DroppedChange(item.From, null)); // gone again, or with its folder
            }
            else if (found.IsDirectory)
            {
                folders.Add(item);
            }
            else if (side.IsHydrating(item.LocalPath))
            {
                later.AddRange(item.From);
            }
            else if (side.IsOwnWrite(item.LocalPath))
            {
                dropped.Add(new DroppedChange(item.From, null));
            }
            else
            {
                uploads.Add(item);
            }
        }

        return new LocalChangePlan(
            moves.Select(m => new PlannedMove(m.RemotePath, m.OldLocal, m.NewLocal, m.Source, m.From)).ToList(),
            planned.Select(d => new PlannedDelete(d.RemotePath, d.LocalPath, d.IsDirectory, d.From)).ToList(),
            folders
                .OrderBy(f => f.LocalPath.Count(c => c == Path.DirectorySeparatorChar))
                .Select(f => new PlannedItem(f.LocalPath, f.From))
                .ToList(),
            uploads.Select(u => new PlannedItem(u.LocalPath, u.From)).ToList(),
            later,
            dropped.Where(d => d.Changes.Count > 0 || d.Reason is not null).ToList());
    }
}
