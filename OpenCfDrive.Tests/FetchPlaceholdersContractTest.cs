using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Tests.TestSupport;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// Pins the population contract against the real platform, the model
/// Nextcloud's and OneDrive's folders follow: the first enumeration of a
/// folder is answered by FETCH_PLACEHOLDERS, which leaves the folder fully
/// populated for good. Windows then lists it from disk (and shows it as
/// available, not online-only); what the server adds later is handed out by
/// the background sync as placeholders of its own.
/// </summary>
public class FetchPlaceholdersContractTest(ITestOutputHelper output)
{
    [SkippableFact]
    public void FirstEnumeration_ListsTheFolder_WhichThenStaysPopulated()
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
        var placeholders = new CloudPlaceholderService();

        try
        {
            Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed.");
            Assert.True(placeholders.EnableOnDemandPopulation(syncRoot), "Could not enable on-demand population on the root.");
            Assert.True(placeholders.QueryState(syncRoot).Partial, "A root never listed must be partially populated.");

            var now = DateTimeOffset.UtcNow;
            Add("", new PlaceholderEntry("first.txt", false, 5, now, "first.txt"));
            Add("", new PlaceholderEntry("docs", true, 0, now, "docs/"));

            var first = Enumerate(syncRoot);
            Assert.Contains("first.txt", first);
            Assert.Contains("docs", first);
            Assert.False(placeholders.QueryState(syncRoot).Partial, "The listed root must stay fully populated.");

            // Listed once: from now on Windows lists it from disk, so a server
            // addition only shows once the provider hands it out itself.
            Add("", new PlaceholderEntry("late.txt", false, 4, now, "late.txt"));
            var listings = calls.Count;
            Assert.DoesNotContain("late.txt", Enumerate(syncRoot));
            Assert.Equal(listings, calls.Count);

            // ...as the background sync does: a placeholder made in the folder,
            // which stays fully populated.
            var created = placeholders.CreatePlaceholders(syncRoot, [new RcloneItemMetadata("late.txt", "late.txt", 4, now, false)]);
            Assert.Equal(1, created.Created);
            Assert.Contains("late.txt", Enumerate(syncRoot));
            Assert.False(placeholders.QueryState(syncRoot).Partial);

            // A folder handed out is listed on its own first enumeration.
            var docs = Path.Combine(syncRoot, "docs");
            Assert.True(placeholders.QueryState(docs).Partial, "A folder never opened must be partially populated.");
            Add("docs", new PlaceholderEntry("nested.txt", false, 6, now, "docs/nested.txt"));
            Assert.Contains("nested.txt", Enumerate(docs));
            Assert.False(placeholders.QueryState(docs).Partial);

            // Creating a new name needs a populated folder; in a partially
            // populated one the create is retried until it fails with "The
            // name of the file cannot be resolved by the system".
            CreateFileFromAnotherProcess(Path.Combine(syncRoot, "created-locally.txt"));
            CreateFileFromAnotherProcess(Path.Combine(docs, "created-locally.txt"));

            // One enumeration raises many FETCH_PLACEHOLDERS callbacks; the
            // remote must see one listing per folder, once.
            output.WriteLine($"remote listings: {string.Join(", ", calls.Select(c => $"'{c}'"))}");
            Assert.Equal(1, calls.Count(c => c.Length == 0));
            Assert.Equal(1, calls.Count(c => c == "docs"));
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

    /// <summary>
    /// Offline (the server could not be reached), a folder never listed shows
    /// what is on this PC at once and the server is not asked, as in Nextcloud
    /// and OneDrive. It is left unlisted, so once a check reaches the server
    /// opening it lists it.
    /// </summary>
    [SkippableFact]
    public void Offline_AFolderShowsWhatIsLocal_AndIsListedOnceTheServerAnswers()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-fetchph", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        Directory.CreateDirectory(syncRoot);
        var now = DateTimeOffset.UtcNow;
        var calls = new ConcurrentQueue<string>();

        Task<IReadOnlyList<PlaceholderEntry>> ListDirectory(string directory, CancellationToken cancellationToken)
        {
            calls.Enqueue(directory);
            return Task.FromResult<IReadOnlyList<PlaceholderEntry>>([new PlaceholderEntry("first.txt", false, 5, now, "first.txt")]);
        }

        // The server answers checks only once the test says it is back.
        var serverBack = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new ServerConnection(cancellationToken => serverBack.Task.WaitAsync(cancellationToken), TimeSpan.FromSeconds(30));
        var online = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Changed += isOnline =>
        {
            if (isOnline)
            {
                online.TrySetResult();
            }
        };

        var store = new SqliteMetadataStore(Path.Combine(root, "state.db"));
        store.Initialize();
        using var serve = new RcloneServeClient("http://127.0.0.1:1", "unused:");
        var registrar = new SyncRootRegistrar();
        Assert.True(registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid()).Success);
        using var connection = new SyncRootConnection(store, serve, output.WriteLine, listDirectory: ListDirectory, server: server);
        var placeholders = new CloudPlaceholderService();

        try
        {
            Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed.");
            Assert.True(placeholders.EnableOnDemandPopulation(syncRoot));

            server.Failed("the test took it offline");
            Assert.Empty(Enumerate(syncRoot));
            Assert.Empty(calls);
            Assert.True(placeholders.QueryState(syncRoot).Partial, "An offline answer must leave the folder unlisted.");

            // Using the drive started a check; the server is back.
            serverBack.SetResult();
            Assert.True(online.Task.Wait(TimeSpan.FromSeconds(30)), "The drive did not go back online.");
            Assert.Contains("first.txt", Enumerate(syncRoot));
            Assert.Single(calls);
            Assert.False(placeholders.QueryState(syncRoot).Partial);
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
