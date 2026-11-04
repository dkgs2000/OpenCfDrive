using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Tests.TestSupport;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// Whether a folder already listed can be listed again when it is next
/// opened, instead of every listed folder being listed on a timer: Windows
/// asks for a folder's contents (FETCH_PLACEHOLDERS) only while it is not
/// fully populated, so the folder is given on-demand population again
/// (CF_UPDATE_FLAG_ENABLE_ON_DEMAND_POPULATION) some time after it was listed.
/// What this pins: the next opening asks again; an answer with nothing new
/// keeps what is there (downloaded files included) and marks it populated, so
/// Windows does not ask again in a loop; a new file can be created in such a
/// folder. And what Explorer is told about the folder meanwhile.
/// </summary>
public class RelistFolderContractTest(ITestOutputHelper output)
{
    [SkippableFact]
    public void AListedFolder_GivenOnDemandPopulationAgain_IsListedAgainWhenNextOpened_AndKeepsItsFiles()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-relist", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "drive"));
        var syncRoot = LongPath(Path.Combine(root, "drive"));
        var folder = Path.Combine(syncRoot, "dir");
        var now = DateTimeOffset.UtcNow;
        var log = new ConcurrentQueue<string>();
        var listings = new ConcurrentQueue<(string Directory, string Pattern)>();

        var failNext = false;

        Task<SyncRootConnection.DirectoryListing?> ListDirectory(string directory, string localDirectory, CancellationToken cancellationToken)
        {
            // Only the first listing of a folder has entries; later ones, as
            // a refresh with nothing new would, have none.
            var first = !listings.Any(l => l.Directory == directory);
            listings.Enqueue((directory, ""));
            if (failNext)
            {
                failNext = false;
                throw new IOException("the server could not be reached");
            }

            IReadOnlyList<PlaceholderEntry> entries = directory switch
            {
                "" when first => [new PlaceholderEntry("dir", true, 0, now, "dir/")],
                "dir" when first =>
                [
                    new PlaceholderEntry("a.txt", false, 1, now, "dir/a.txt"),
                    new PlaceholderEntry("b.txt", false, 1, now, "dir/b.txt"),
                ],
                _ => [],
            };
            return Task.FromResult<SyncRootConnection.DirectoryListing?>(
                new SyncRootConnection.DirectoryListing(entries, Listed: true));
        }

        using var server = new OneByteServer(() => Task.CompletedTask);
        var store = new SqliteMetadataStore(Path.Combine(root, "state.db"));
        store.Initialize();
        using var serve = new RcloneServeClient(server.Url, "e2e:");
        var registrar = new SyncRootRegistrar();
        var placeholders = new CloudPlaceholderService();
        Assert.True(registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid()).Success);
        using var connection = new SyncRootConnection(
            store,
            serve,
            log.Enqueue,
            listDirectory: ListDirectory,
            // Every request lists: what is pinned here is Windows' asking.
            listingReuseWindow: TimeSpan.Zero);

        int Listings(string directory) => listings.Count(l => l.Directory == directory);
        void Show(string when)
        {
            var state = placeholders.QueryState(folder);
            output.WriteLine(
                $"{when}: folder partial={state.Partial} placeholder={state.IsPlaceholder} in sync={state.InSync} " +
                $"attributes=0x{state.RawAttributes:X} ({Describe(state.RawAttributes)}); shell: {ShellStatus(folder)}");
        }

        try
        {
            Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed.");
            Assert.True(placeholders.EnableOnDemandPopulation(syncRoot));

            // First opening: listed, then a file downloaded.
            Assert.Equal(["a.txt", "b.txt"], Names(Run($"dir /b \"{folder}\"").Output));
            Assert.Equal(1, Listings("dir"));
            Assert.Equal("z", Run($"type \"{Path.Combine(folder, "a.txt")}\"").Output.Trim());
            var downloads = server.Requests.Count;
            Show("listed");

            // Fully populated: Windows lists it from disk and asks nothing.
            Assert.Equal(["a.txt", "b.txt"], Names(Run($"dir /b \"{folder}\"").Output));
            Assert.Equal(1, Listings("dir"));

            // Marked to be listed again: the next opening asks.
            Assert.True(placeholders.EnableOnDemandPopulation(folder), "Could not give the folder on-demand population again.");
            Show("marked to be listed again");
            Assert.Equal(1, Listings("dir"));
            Assert.Equal(["a.txt", "b.txt"], Names(Run($"dir /b \"{folder}\"").Output));
            Assert.Equal(2, Listings("dir"));
            Show("listed again");

            // Nothing new in that answer: both files are still there, and the
            // downloaded one still is, without being downloaded again.
            Assert.False(placeholders.QueryState(Path.Combine(folder, "a.txt")).Partial, "a.txt is no longer downloaded.");
            Assert.Equal("z", Run($"type \"{Path.Combine(folder, "a.txt")}\"").Output.Trim());
            Assert.Equal(downloads, server.Requests.Count);

            // Populated again: no more asking, no loop.
            Assert.Equal(["a.txt", "b.txt"], Names(Run($"dir /b \"{folder}\"").Output));
            Assert.Equal(2, Listings("dir"));
            Assert.False(placeholders.QueryState(folder).Partial, "The folder was left to be listed again.");

            // A new file in a folder marked to be listed again: Windows asks
            // first, and the create goes through once it is answered.
            Assert.True(placeholders.EnableOnDemandPopulation(folder));
            var created = Run($"echo new> \"{Path.Combine(folder, "new.txt")}\"");
            Assert.Equal(0, created.ExitCode);
            Assert.True(File.Exists(Path.Combine(folder, "new.txt")), "The new file was not created.");
            Assert.Equal(3, Listings("dir"));
            Show("after a file was created");
        }
        finally
        {
            foreach (var line in log)
            {
                output.WriteLine(line);
            }

            connection.Disconnect();
            registrar.Unregister(syncRoot);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static string Describe(uint attributes)
    {
        var names = new List<string>();
        if ((attributes & 0x400) != 0) names.Add("REPARSE_POINT");
        if ((attributes & 0x40000) != 0) names.Add("RECALL_ON_OPEN");
        if ((attributes & 0x400000) != 0) names.Add("RECALL_ON_DATA_ACCESS");
        if ((attributes & 0x80000) != 0) names.Add("PINNED");
        if ((attributes & 0x100000) != 0) names.Add("UNPINNED");
        if ((attributes & 0x1000) != 0) names.Add("OFFLINE");
        return string.Join(" ", names);
    }

    /// <summary>What the Windows shell tells Explorer about the item: its placeholder status and availability.</summary>
    private string ShellStatus(string path)
    {
        var parent = Path.GetDirectoryName(path)!.Replace("'", "''");
        var name = Path.GetFileName(path).Replace("'", "''");
        var script =
            "$i = (New-Object -ComObject Shell.Application).Namespace('" + parent + "').ParseName('" + name + "'); " +
            "'status=' + $i.ExtendedProperty('System.FilePlaceholderStatus') + ' state=' + $i.ExtendedProperty('System.StorageProviderState') + " +
            "' availability=' + $i.ExtendedProperty('System.StorageProviderFileFlags')";
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-Command", script })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(60)), "The shell query did not finish.");
        return (stdout.Result.Trim() + " " + stderr.Result.Trim()).Trim();
    }

    /// <summary>
    /// A folder listed before whose next listing fails (offline, or no answer)
    /// is answered from what is on disk and marked populated again: its files,
    /// downloaded ones included, stay, and a new file can still be created.
    /// A folder never listed keeps the old behaviour (it stays unlisted).
    /// </summary>
    [SkippableFact]
    public void AListedFolder_WhenTheNextListingFails_StaysPopulated_KeepsItsFiles_AndAllowsANewFile()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-relist-fail", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "drive"));
        var syncRoot = LongPath(Path.Combine(root, "drive"));
        var folder = Path.Combine(syncRoot, "dir");
        var now = DateTimeOffset.UtcNow;
        var log = new ConcurrentQueue<string>();
        var listings = new ConcurrentQueue<(string Directory, string Pattern)>();
        var failNext = false;

        Task<SyncRootConnection.DirectoryListing?> ListDirectory(string directory, string localDirectory, CancellationToken cancellationToken)
        {
            var first = !listings.Any(l => l.Directory == directory);
            listings.Enqueue((directory, ""));
            if (failNext)
            {
                failNext = false;
                throw new IOException("the server could not be reached");
            }

            IReadOnlyList<PlaceholderEntry> entries = directory switch
            {
                "" when first => [new PlaceholderEntry("dir", true, 0, now, "dir/")],
                "dir" when first =>
                [
                    new PlaceholderEntry("a.txt", false, 1, now, "dir/a.txt"),
                    new PlaceholderEntry("b.txt", false, 1, now, "dir/b.txt"),
                ],
                _ => [],
            };
            return Task.FromResult<SyncRootConnection.DirectoryListing?>(
                new SyncRootConnection.DirectoryListing(entries, Listed: true));
        }

        using var server = new OneByteServer(() => Task.CompletedTask);
        var store = new SqliteMetadataStore(Path.Combine(root, "state.db"));
        store.Initialize();
        using var serve = new RcloneServeClient(server.Url, "e2e:");
        var registrar = new SyncRootRegistrar();
        var placeholders = new CloudPlaceholderService();
        Assert.True(registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid()).Success);
        using var connection = new SyncRootConnection(
            store,
            serve,
            log.Enqueue,
            listDirectory: ListDirectory,
            listingReuseWindow: TimeSpan.Zero);

        try
        {
            Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed.");
            Assert.True(placeholders.EnableOnDemandPopulation(syncRoot));

            // Listed once, and a file downloaded.
            Assert.Equal(["a.txt", "b.txt"], Names(Run($"dir /b \"{folder}\"").Output));
            Assert.Equal("z", Run($"type \"{Path.Combine(folder, "a.txt")}\"").Output.Trim());
            var downloads = server.Requests.Count;

            // Marked to be listed again, but the next listing fails.
            Assert.True(placeholders.EnableOnDemandPopulation(folder));
            failNext = true;
            Assert.Equal(["a.txt", "b.txt"], Names(Run($"dir /b \"{folder}\"").Output));

            // Answered from disk and marked populated again: no loop, the
            // files stay, and the downloaded one stays downloaded.
            Assert.False(placeholders.QueryState(folder).Partial, "A listed folder was left to be listed again after a failed listing.");
            Assert.Equal("z", Run($"type \"{Path.Combine(folder, "a.txt")}\"").Output.Trim());
            Assert.Equal(downloads, server.Requests.Count);

            // A new file can still be created in it.
            var created = Run($"echo new> \"{Path.Combine(folder, "new.txt")}\"");
            Assert.Equal(0, created.ExitCode);
            Assert.True(File.Exists(Path.Combine(folder, "new.txt")), "The new file was not created after a failed listing.");
        }
        finally
        {
            foreach (var line in log)
            {
                output.WriteLine(line);
            }

            connection.Disconnect();
            registrar.Unregister(syncRoot);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    /// <summary>
    /// An empty folder listed in an earlier run (so known only by the mark its
    /// listing left in the store, with no rows below it) whose next listing
    /// fails after a restart is still answered as listed before: populated,
    /// so Windows does not ask in a loop and a new file can be created.
    /// </summary>
    [SkippableFact]
    public void AnEmptyFolderListedInAnEarlierRun_WhenTheNextListingFails_StaysPopulated_AndAllowsANewFile()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-relist-empty", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "drive"));
        var syncRoot = LongPath(Path.Combine(root, "drive"));
        var folder = Path.Combine(syncRoot, "empty");
        var now = DateTimeOffset.UtcNow;
        var log = new ConcurrentQueue<string>();
        var fail = false;

        Task<SyncRootConnection.DirectoryListing?> ListDirectory(string directory, string localDirectory, CancellationToken cancellationToken)
        {
            if (fail)
            {
                throw new IOException("the server could not be reached");
            }

            IReadOnlyList<PlaceholderEntry> entries = directory.Length == 0 ? [new PlaceholderEntry("empty", true, 0, now, "empty/")] : [];
            return Task.FromResult<SyncRootConnection.DirectoryListing?>(new SyncRootConnection.DirectoryListing(entries, Listed: true));
        }

        using var server = new OneByteServer(() => Task.CompletedTask);
        var store = new SqliteMetadataStore(Path.Combine(root, "state.db"));
        store.Initialize();
        using var serve = new RcloneServeClient(server.Url, "e2e:");
        var registrar = new SyncRootRegistrar();
        var placeholders = new CloudPlaceholderService();
        Assert.True(registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid()).Success);

        try
        {
            // The earlier run: the folder is listed (empty), and the engine
            // leaves its row and the listed mark in the store.
            using (var earlier = new SyncRootConnection(store, serve, log.Enqueue, listDirectory: ListDirectory, listingReuseWindow: TimeSpan.Zero))
            {
                Assert.True(earlier.Connect(syncRoot), "CfConnectSyncRoot failed.");
                Assert.True(placeholders.EnableOnDemandPopulation(syncRoot));
                Assert.Empty(Names(Run($"dir /b \"{folder}\"").Output));
                earlier.Disconnect();
            }

            store.UpsertFile("empty/", folder, "empty/", 0, isPlaceholder: true, isDirectory: true, size: 0, modifiedUtc: now, inSync: true);
            store.MarkListed("empty");
            Assert.Empty(store.GetChildren("empty"));

            // After a restart, marked to be listed again, and the listing fails.
            using var connection = new SyncRootConnection(store, serve, log.Enqueue, listDirectory: ListDirectory, listingReuseWindow: TimeSpan.Zero);
            try
            {
                Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed.");
                Assert.True(placeholders.EnableOnDemandPopulation(folder));
                fail = true;
                Assert.Empty(Names(Run($"dir /b \"{folder}\"").Output));

                Assert.False(placeholders.QueryState(folder).Partial, "An empty folder listed before was left to be listed again after a failed listing.");
                var created = Run($"echo new> \"{Path.Combine(folder, "new.txt")}\"");
                Assert.Equal(0, created.ExitCode);
                Assert.True(File.Exists(Path.Combine(folder, "new.txt")), "No new file could be made in the empty folder after a failed listing.");
            }
            finally
            {
                connection.Disconnect();
            }
        }
        finally
        {
            foreach (var line in log)
            {
                output.WriteLine(line);
            }

            registrar.Unregister(syncRoot);
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }

    private static string[] Names(string listing) =>
        listing.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(n => n.Trim()).Order(StringComparer.Ordinal).ToArray();

    /// <summary>From another process: the provider's own I/O raises no callbacks.</summary>
    private (int ExitCode, string Output) Run(string command)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"{command}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(70)), $"'{command}' did not finish.");
        output.WriteLine($"{command}: exit {process.ExitCode} {stderr.Result.Trim()}");
        return (process.ExitCode, stdout.Result);
    }

    private static string LongPath(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetLongPathName(path, buffer, (uint)buffer.Capacity);
        return length > 0 && length < buffer.Capacity ? buffer.ToString() : path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);
}
