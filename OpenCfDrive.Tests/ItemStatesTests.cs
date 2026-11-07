using System.Collections.Concurrent;
using OpenCfDrive.Core.CloudFiles;

namespace OpenCfDrive.Tests;

/// <summary>
/// The per-file states Explorer is given are applied off the sync thread, and
/// requests made while the shell is being written to are merged per path: a
/// burst of sync outcomes for one file results in one write of the state that
/// matters, and recording it never waits for the shell. A conflict copy, told
/// by its name, stays one until it is renamed or deleted.
/// </summary>
public class ItemStatesTests
{
    private const string Copy = @"C:\drive\a (conflicted copy 2026-11-04 120000).txt";

    private sealed class RecordingWriter(bool block = false) : IItemStateWriter
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<(string Path, string? Value)> Applied { get; } = new();

        /// <summary>The first write has begun (and, blocking, waits for <see cref="Release"/>).</summary>
        public Task Started => _started.Task;

        public void Release() => _gate.TrySetResult();

        public async Task ApplyAsync(string localPath, ItemState? state, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            if (block)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            Applied.Enqueue((localPath, state?.Value));
        }

        public IReadOnlyList<string?> ValuesFor(string localPath) =>
            Applied.Where(a => string.Equals(a.Path, localPath, StringComparison.OrdinalIgnoreCase)).Select(a => a.Value).ToList();
    }

    [Fact]
    public async Task ABurstForOneFile_WhileTheShellIsBusy_IsAppliedOnce()
    {
        var writer = new RecordingWriter(block: true);
        using var states = new ItemStates(writer);
        var busy = @"C:\drive\busy.txt";
        var file = @"C:\drive\a.txt";
        var other = @"C:\drive\b.txt";

        // The worker is writing busy.txt's state, and waits.
        states.WaitingToUpload(busy);
        await writer.Started.WaitAsync(TimeSpan.FromSeconds(5));

        states.WaitingToUpload(file);
        states.CouldNotSync(file, "refused");
        states.WaitingToUpload(file);
        states.WaitingToUpload(other);

        // One entry per path while the shell has not been asked yet.
        Assert.Equal(2, states.PendingCount);

        writer.Release();
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([ItemState.WaitingToUpload.Value], writer.ValuesFor(file));
        Assert.Equal([ItemState.WaitingToUpload.Value], writer.ValuesFor(other));
    }

    [Fact]
    public async Task AConflictCopy_StaysMarked_WhenUploadedOrSent_UntilItIsRenamed()
    {
        var writer = new RecordingWriter();
        using var states = new ItemStates(writer);
        var renamed = @"C:\drive\a.txt";

        states.ConflictCopy(Copy);
        states.WaitingToUpload(Copy);
        states.Clear(Copy);
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.NotEmpty(writer.ValuesFor(Copy));
        Assert.All(writer.ValuesFor(Copy), value => Assert.Equal(ItemState.ConflictCopy.Value, value));

        // Renamed (resolved): the run that sends the rename clears it under the new name.
        states.Clear(renamed);
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([null], writer.ValuesFor(renamed));
    }

    [Fact]
    public async Task AFailure_ReplacesAConflictCopy_AndTheNextSuccessBringsItBack()
    {
        var writer = new RecordingWriter();
        using var states = new ItemStates(writer);

        states.CouldNotSync(Copy, "the server refused the upload");
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("the server refused the upload", writer.ValuesFor(Copy)[^1]);

        states.Clear(Copy);
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ItemState.ConflictCopy.Value, writer.ValuesFor(Copy)[^1]);
    }

    [Fact]
    public async Task AFailure_IsShown_AndTheNextSuccessClearsIt()
    {
        var writer = new RecordingWriter();
        using var states = new ItemStates(writer);
        var file = @"C:\drive\a.txt";

        states.CouldNotSync(file, "the server refused the upload");
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("the server refused the upload", writer.ValuesFor(file)[^1]);

        states.Clear(file);
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));
        Assert.Null(writer.ValuesFor(file)[^1]);
    }

    [Fact]
    public async Task ARememberedFailure_IsNotWrittenAgain_AndANewFailureIs()
    {
        var writer = new RecordingWriter();
        using var states = new ItemStates(writer);
        var file = @"C:\drive\a.txt";

        states.CouldNotSync(file, "first");
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));
        states.CouldNotSync(file, "second");
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));

        // The state is written each time it is requested; what is remembered
        // is only that a write failed, so a broken shell is logged once.
        Assert.Equal(["first", "second"], writer.ValuesFor(file));
    }

    [Fact]
    public async Task AWriterFailure_IsLoggedOnceAndDoesNotStopLaterStates()
    {
        var writer = new FailingWriter();
        var log = new ConcurrentQueue<string>();
        using var states = new ItemStates(writer, log.Enqueue);
        var broken = @"C:\drive\broken.txt";
        var working = @"C:\drive\working.txt";

        states.WaitingToUpload(broken);
        states.WaitingToUpload(broken);
        states.WaitingToUpload(working);
        await states.WhenIdleAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, log.Count(l => l.Contains("broken.txt", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(working, writer.Applied);
    }

    private sealed class FailingWriter : IItemStateWriter
    {
        public ConcurrentQueue<string> Applied { get; } = new();

        public Task ApplyAsync(string localPath, ItemState? state, CancellationToken cancellationToken)
        {
            if (localPath.EndsWith("broken.txt", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("the shell refused");
            }

            Applied.Enqueue(localPath);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void FilesThatCouldNotSync_AreCounted_UntilTheySyncOrAreGone()
    {
        var folder = Directory.CreateTempSubdirectory("opencfdrive-failing").FullName;
        try
        {
            var a = Path.Combine(folder, "a.txt");
            var b = Path.Combine(folder, "b.txt");
            File.WriteAllText(a, "a");
            File.WriteAllText(b, "b");

            // Counted in mock mode too, where Explorer shows nothing.
            using var states = new ItemStates(new RecordingWriter(), enabled: false);
            var told = 0;
            states.FailingChanged += () => told++;

            states.CouldNotSync(a, "refused");
            states.CouldNotSync(b, "refused");
            states.CouldNotSync(b, "refused again");
            Assert.Equal(2, states.Failing);
            Assert.Equal(2, told);

            states.Clear(a); // it reached the server
            Assert.Equal(1, states.Failing);
            Assert.Equal(3, told);

            File.Delete(b);
            Assert.Equal(0, states.Failing);

            // Changed again, it is sent anew: not failing while it waits.
            states.CouldNotSync(a, "refused");
            states.WaitingToUpload(a);
            Assert.Equal(0, states.Failing);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
