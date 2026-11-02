using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Tests.TestSupport;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// Pins the on-demand population contract against the real platform: every
/// directory enumeration is answered by FETCH_PLACEHOLDERS, and because the
/// provider never marks a directory fully populated, the platform asks again
/// on the next enumeration. That is what makes files added on the server
/// visible in Explorer without restarting the service.
/// </summary>
public class FetchPlaceholdersContractTest(ITestOutputHelper output)
{
    /// <summary>A little longer than SyncRootConnection's default reuse window.</summary>
    private static readonly TimeSpan ReuseWindow = TimeSpan.FromSeconds(1.5);

    [SkippableFact]
    public void EveryEnumeration_PicksUpEntriesAddedOnTheRemote()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-fetchph", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        Directory.CreateDirectory(syncRoot);

        // The fake remote: directory -> entries the provider has not handed out yet.
        var pending = new ConcurrentDictionary<string, List<PlaceholderEntry>>(StringComparer.OrdinalIgnoreCase);
        var calls = new ConcurrentQueue<string>();

        void Add(string directory, PlaceholderEntry entry) =>
            pending.AddOrUpdate(directory, _ => [entry], (_, list) => { lock (list) { list.Add(entry); } return list; });

        Task<IReadOnlyList<PlaceholderEntry>> ListDirectory(string directory, CancellationToken cancellationToken)
        {
            calls.Enqueue(directory);
            if (!pending.TryRemove(directory, out var list))
            {
                return Task.FromResult<IReadOnlyList<PlaceholderEntry>>([]);
            }

            lock (list)
            {
                return Task.FromResult<IReadOnlyList<PlaceholderEntry>>(list.ToList());
            }
        }

        var store = new SqliteMetadataStore(Path.Combine(root, "state.db"));
        store.Initialize();
        using var serve = new RcloneServeClient("http://127.0.0.1:1", "unused:");

        var registrar = new SyncRootRegistrar();
        var registration = registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(registration.Success, registration.Message);

        using var connection = new SyncRootConnection(
            store, serve, output.WriteLine, listDirectory: ListDirectory);

        try
        {
            Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed.");

            // Same step the engine runs after its startup population.
            var placeholders = new CloudPlaceholderService();
            output.WriteLine($"root state before: 0x{placeholders.QueryState(syncRoot).RawState:X}");
            Assert.True(placeholders.EnableOnDemandPopulation(syncRoot), "Could not enable on-demand population on the root.");
            output.WriteLine($"root state after: 0x{placeholders.QueryState(syncRoot).RawState:X}");

            var now = DateTimeOffset.UtcNow;
            Add("", new PlaceholderEntry("first.txt", false, 5, now, "first.txt"));
            Add("", new PlaceholderEntry("docs", true, 0, now, "docs/"));

            var first = Enumerate(syncRoot);
            Assert.Contains("first.txt", first);
            Assert.Contains("docs", first);

            // Added "on the server" after the first listing. Within the reuse
            // window the shell is answered from the previous listing, so wait
            // it out the way a user's next refresh naturally would.
            Add("", new PlaceholderEntry("late.txt", false, 4, now, "late.txt"));
            Thread.Sleep(ReuseWindow);
            var second = Enumerate(syncRoot);
            Assert.Contains("first.txt", second);
            Assert.Contains("late.txt", second);

            // A nested directory placeholder is refreshed the same way.
            Add("docs", new PlaceholderEntry("nested.txt", false, 6, now, "docs/nested.txt"));
            var nested = Enumerate(Path.Combine(syncRoot, "docs"));
            Assert.Contains("nested.txt", nested);

            Add("docs", new PlaceholderEntry("nested-late.txt", false, 6, now, "docs/nested-late.txt"));
            Thread.Sleep(ReuseWindow);
            var nestedAgain = Enumerate(Path.Combine(syncRoot, "docs"));
            Assert.Contains("nested.txt", nestedAgain);
            Assert.Contains("nested-late.txt", nestedAgain);

            // Creating a new name makes the platform ask for the directory
            // first. If the answer leaves the directory "partially
            // populated", the create is retried until it fails with "The
            // name of the file cannot be resolved by the system".
            Thread.Sleep(ReuseWindow);
            CreateFileFromAnotherProcess(Path.Combine(syncRoot, "created-locally.txt"));
            Thread.Sleep(ReuseWindow);
            CreateFileFromAnotherProcess(Path.Combine(syncRoot, "docs", "created-locally.txt"));

            // ...and refreshes keep working after a local create.
            Add("", new PlaceholderEntry("after-create.txt", false, 3, now, "after-create.txt"));
            Thread.Sleep(ReuseWindow);
            Assert.Contains("after-create.txt", Enumerate(syncRoot));

            // One enumeration raises hundreds of FETCH_PLACEHOLDERS callbacks;
            // the remote must see roughly one listing per enumeration.
            output.WriteLine($"remote listings: {string.Join(", ", calls.Select(c => $"'{c}'"))}");
            Assert.InRange(calls.Count(c => c.Length == 0), 3, 5);
            Assert.InRange(calls.Count(c => c == "docs"), 2, 4);
        }
        finally
        {
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

    private void CreateFileFromAnotherProcess(string path)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /c \"echo created> \"{path}\"\"")
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(70)), $"Creating '{path}' did not finish.");
        output.WriteLine($"created '{path}': exit {process.ExitCode} {error.Trim()}");
        Assert.True(process.ExitCode == 0 && File.Exists(path), $"Creating '{path}' failed: {error.Trim()}");
    }

    /// <summary>
    /// Lists the directory from another process, the way Explorer does.
    /// </summary>
    private List<string> Enumerate(string directory)
    {
        var clock = Stopwatch.StartNew();
        var start = new ProcessStartInfo("cmd.exe", $"/d /c dir /b /a \"{directory}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(TimeSpan.FromSeconds(70)), $"Listing '{directory}' did not finish.");
        var names = stdout
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(n => n != "File Not Found")
            .ToList();
        clock.Stop();
        output.WriteLine($"enumerated '{directory}' in {clock.ElapsedMilliseconds} ms: {string.Join(", ", names)}");

        // Anything near the 60 s cfapi timeout means the callback was not answered.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"Enumerating '{directory}' took {clock.Elapsed}.");
        return names;
    }
}
