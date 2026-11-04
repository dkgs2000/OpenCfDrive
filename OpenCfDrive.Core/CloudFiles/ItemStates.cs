using Windows.Storage;
using Windows.Storage.Provider;

namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// One state Explorer shows for a file: the text of the custom state column and
/// the stock icon shown in the Status column.
/// </summary>
internal sealed record ItemState(string Value, string IconResource)
{
    public static ItemState WaitingToUpload { get; } =
        new("Waiting to upload", StockIcon("shell32.dll", 238));

    public static ItemState ConflictCopy { get; } =
        new("Conflict copy", StockIcon("imageres.dll", 79));

    public static ItemState CouldNotSync(string reason) =>
        new(reason, StockIcon("imageres.dll", 89));

    /// <summary>A Windows icon, in the "path,index" form the shell takes.</summary>
    private static string StockIcon(string module, int index) =>
        Path.Combine(Environment.SystemDirectory, module) + "," + index;
}

/// <summary>How a state is written to the shell; replaced in tests.</summary>
internal interface IItemStateWriter
{
    /// <summary>Sets <paramref name="state"/>, or clears it when null.</summary>
    Task ApplyAsync(string localPath, ItemState? state, CancellationToken cancellationToken);
}

/// <summary>
/// The per-file states Explorer shows in its Status column. Requests are
/// applied on a background task, so a sync step (which must not wait on the
/// shell) only records what the file's state should be; requests made while
/// it writes are merged per path, so a burst for one file is written once.
/// A conflict copy is told by its name (<see cref="ConflictCopies"/>) and is
/// shown as one until it is renamed or deleted, across restarts too. A state
/// that cannot be applied is logged once per path; it never fails a sync step.
/// </summary>
public sealed class ItemStates : IDisposable
{
    private readonly IItemStateWriter _writer;
    private readonly Action<string>? _log;
    private readonly bool _enabled;

    private readonly object _gate = new();
    private readonly Dictionary<string, ItemState?> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _reportedFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _worker;
    private TaskCompletionSource _idle = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public ItemStates(Action<string>? log = null, bool enabled = true)
        : this(new ShellItemStateWriter(), log, enabled)
    {
    }

    internal ItemStates(IItemStateWriter writer, Action<string>? log = null, bool enabled = true)
    {
        _writer = writer;
        _log = log;
        _enabled = enabled;
        _worker = Task.Run(RunAsync);
    }

    /// <summary>The file has a local change that the server does not have yet.</summary>
    public void WaitingToUpload(string localPath)
    {
        // A conflict copy says more; an edit to it does not make it an
        // ordinary upload.
        Set(localPath, IsConflictCopy(localPath) ? ItemState.ConflictCopy : ItemState.WaitingToUpload);
    }

    /// <summary>The file was kept as a conflict copy of a server version.</summary>
    public void ConflictCopy(string localPath) => Set(localPath, ItemState.ConflictCopy);

    /// <summary>A sync step failed for the file; <paramref name="reason"/> is shown as the state.</summary>
    public void CouldNotSync(string localPath, string reason) => Set(localPath, ItemState.CouldNotSync(reason));

    /// <summary>
    /// The file reached the server, or has nothing to send: no state is shown
    /// for it, but a conflict copy stays one until it is renamed or deleted
    /// (its rename clears it under the new name).
    /// </summary>
    public void Clear(string localPath) => Set(localPath, IsConflictCopy(localPath) ? ItemState.ConflictCopy : null);

    private static bool IsConflictCopy(string localPath) => ConflictCopies.IsConflictCopy(Path.GetFileName(localPath));

    private void Set(string localPath, ItemState? state)
    {
        if (!Wanted(localPath))
        {
            return;
        }

        lock (_gate)
        {
            Request(localPath, state);
        }

        Wake();
    }
    private bool Wanted(string localPath) =>
        _enabled && !_disposed && !string.IsNullOrEmpty(localPath);

    /// <summary>Must be called under <see cref="_gate"/>.</summary>
    private void Request(string localPath, ItemState? state)
    {
        // A new batch starts a new idle signal; one still being applied keeps
        // the old one until its batch is done.
        if (_pending.Count == 0)
        {
            _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _pending[localPath] = state;
    }

    private void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the request and the wake-up.
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await _signal.WaitAsync(_stop.Token).ConfigureAwait(false);

                Dictionary<string, ItemState?> batch;
                lock (_gate)
                {
                    batch = new Dictionary<string, ItemState?>(_pending, StringComparer.OrdinalIgnoreCase);
                    _pending.Clear();
                }

                foreach (var (localPath, state) in batch)
                {
                    try
                    {
                        await _writer.ApplyAsync(localPath, state, _stop.Token).ConfigureAwait(false);
                        _log?.Invoke(state is null
                            ? $"Cleared Explorer state for '{localPath}'."
                            : $"Explorer state for '{localPath}': {state.Value}.");
                        lock (_gate)
                        {
                            _reportedFailures.Remove(localPath);
                        }
                    }
                    catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        bool first;
                        lock (_gate)
                        {
                            first = _reportedFailures.Add(localPath);
                        }

                        if (first)
                        {
                            _log?.Invoke($"Could not show the sync state of '{localPath}': {ex.Message}");
                        }
                    }
                }

                // Under the lock: a request made meanwhile starts a new idle
                // signal, which this one must not complete.
                lock (_gate)
                {
                    if (_pending.Count == 0)
                    {
                        _idle.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Disposal.
        }
        finally
        {
            _idle.TrySetResult();
        }
    }

    /// <summary>The number of paths with a state still to apply, one entry per path.</summary>
    internal int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count;
            }
        }
    }

    /// <summary>Waits until everything recorded so far has been applied; for tests.</summary>
    internal async Task WhenIdleAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(5));
        while (true)
        {
            Task idle;
            lock (_gate)
            {
                idle = _idle.Task;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new TimeoutException("The item states were not applied in time.");
            }

            var finished = await Task.WhenAny(idle, Task.Delay(remaining)).ConfigureAwait(false);
            if (finished == idle)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stop.Cancel();
        Wake();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Best effort: a writer that ignores cancellation must not hang shutdown.
        }
    }
}

/// <summary>Writes a state through the Cloud Files item-property API.</summary>
internal sealed class ShellItemStateWriter : IItemStateWriter
{
    public async Task ApplyAsync(string localPath, ItemState? state, CancellationToken cancellationToken)
    {
        // Renamed, deleted or freed between recording and applying.
        if (!File.Exists(localPath) && !Directory.Exists(localPath))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        IStorageItem item = File.Exists(localPath)
            ? await StorageFile.GetFileFromPathAsync(localPath)
            : await StorageFolder.GetFolderFromPathAsync(localPath);

        IEnumerable<StorageProviderItemProperty> properties = state is null
            ? []
            :
            [
                new StorageProviderItemProperty
                {
                    Id = SyncRootRegistrar.StatePropertyId,
                    Value = state.Value,
                    IconResource = state.IconResource,
                },
            ];

        await StorageProviderItemProperties.SetAsync(item, properties);
    }
}
