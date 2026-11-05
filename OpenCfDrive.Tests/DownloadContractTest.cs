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
/// What the connection does around a download (FETCH_DATA), against a real
/// sync root: where it reads from, and how a download is known while it runs.
/// </summary>
public class DownloadContractTest(ITestOutputHelper output)
{
    /// <summary>
    /// A download reads from where the engine says the server has the file
    /// now (a move of it may have just reached the server), lets the engine's
    /// hold go once the read has begun, and fails at once, asking the server
    /// nothing, when the engine says it cannot be read now.
    /// </summary>
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADownload_ReadsFromWhereTheEngineSays(bool readable)
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var asked = new ConcurrentQueue<string>();
        var released = 0;
        using var server = new OneByteServer(() => Task.CompletedTask);
        using var drive = new Drive(output, server, remotePath =>
        {
            asked.Enqueue(remotePath);
            return readable
                ? new SyncRootConnection.DownloadSource("new.txt", new Release(() => Interlocked.Increment(ref released)))
                : null;
        });

        var read = drive.Read("a.txt");

        Assert.Equal("old.txt", Assert.Single(asked.Distinct()));
        if (readable)
        {
            Assert.Equal("z", read.Trim());
            Assert.NotEmpty(server.Requests);
            Assert.All(server.Requests, request => Assert.Contains("/new.txt", request));
            Assert.Equal(asked.Count, released);
        }
        else
        {
            // cmd's type reports the failure but exits 0.
            Assert.Equal("", read.Trim());
            Assert.Empty(server.Requests);
        }
    }

    /// <summary>
    /// While a download runs, the file counts as downloading by its own local
    /// path (and so does its folder); once it ends, sent or failed, it no
    /// longer does, and the end is announced with that path. Downloads carry
    /// their path only with CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH: without
    /// it this never held, and changes to a file being downloaded did not wait.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADownload_IsKnownByItsLocalPath_UntilItEnds(bool fails)
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        Drive? drive = null;
        (bool File, bool Folder)? during = null;
        var ended = new ConcurrentQueue<(string Path, bool StillDownloading)>();
        using var endings = new SemaphoreSlim(0);
        using var server = new OneByteServer(
            () =>
            {
                during = (drive!.Connection.IsHydrating(drive.PathOf("a.txt")), drive.Connection.IsHydratingUnder(drive.SyncRoot));
                return Task.CompletedTask;
            },
            fail: fails);
        drive = new Drive(output, server, prepareDownload: null);
        using (drive)
        {
            drive.Connection.DownloadEnded += path =>
            {
                ended.Enqueue((path, drive.Connection.IsHydrating(path)));
                endings.Release();
            };

            var read = drive.Read("a.txt");

            // The reader has its bytes as soon as the last of them reach
            // Windows, which can be before the callback gets to end the
            // download: wait for each download's own end (one per request
            // to the server, all made before the reader finished).
            Assert.NotEmpty(server.Requests);
            foreach (var _ in server.Requests)
            {
                Assert.True(endings.Wait(TimeSpan.FromSeconds(30)), "A download's end was never announced.");
            }

            Assert.Equal((true, true), during);
            Assert.All(ended, end => Assert.Equal((drive.PathOf("a.txt"), false), end));
            Assert.False(drive.Connection.IsHydrating(drive.PathOf("a.txt")));
            Assert.Equal(fails ? "" : "z", read.Trim());
        }
    }

    /// <summary>
    /// A large download reports provider progress as bytes arrive (at least
    /// once before the first byte and then as it goes), so Windows can show a
    /// progress bar and keeps the callback alive. The read still completes and
    /// the bytes are whole.
    /// </summary>
    [SkippableFact]
    public void ADownload_ReportsProviderProgress_AsBytesArrive()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        const int length = 2 * 1024 * 1024;
        var body = Enumerable.Repeat((byte)'p', length).ToArray();
        using var server = new OneByteServer(
            () => Task.CompletedTask,
            body: body,
            chunkSize: 64 * 1024,
            pause: TimeSpan.FromMilliseconds(15));
        using var drive = new Drive(output, server, prepareDownload: null, name: "big.bin", identity: "big.bin", size: length);

        var read = drive.Read("big.bin");

        Assert.Equal(length, read.Length);

        // The whole file fits in one 4 MB transfer to Windows, so a report
        // only per transfer would make two. As bytes arrive it is at least one
        // per 256 KB.
        const int expected = length / (256 * 1024);
        Assert.True(drive.Connection.ProgressReports >= expected,
            $"Only {drive.Connection.ProgressReports} progress report(s) were made; expected at least {expected} as bytes arrived.");
    }

    /// <summary>A sync root with one online-only file, a.txt, whose placeholder names "old.txt".</summary>
    private sealed class Drive : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _root;
        private readonly SyncRootRegistrar _registrar = new();
        private readonly RcloneServeClient _serve;
        private readonly ConcurrentQueue<string> _log = new();

        public Drive(ITestOutputHelper output, OneByteServer server, SyncRootConnection.PrepareDownload? prepareDownload,
            string name = "a.txt", string identity = "old.txt", long size = 1)
        {
            _output = output;
            _root = Path.Combine(Path.GetTempPath(), "opencfdrive-download", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "drive"));

            // The long form, as callbacks give paths (the temp folder is an
            // 8.3 name on CI runners).
            SyncRoot = LongPath(Path.Combine(_root, "drive"));
            var now = DateTimeOffset.UtcNow;
            var store = new SqliteMetadataStore(Path.Combine(_root, "state.db"));
            store.Initialize();
            _serve = new RcloneServeClient(server.Url, "e2e:");
            Assert.True(_registrar.Register(SyncRoot, Guid.NewGuid(), Guid.NewGuid()).Success);
            Connection = new SyncRootConnection(
                store,
                _serve,
                _log.Enqueue,
                listDirectory: (directory, _, _) => Task.FromResult<SyncRootConnection.DirectoryListing?>(
                    new SyncRootConnection.DirectoryListing(
                        directory.Length == 0 ? [new PlaceholderEntry(name, false, size, now, identity)] : [],
                        Listed: true)),
                prepareDownload: prepareDownload);
            Assert.True(Connection.Connect(SyncRoot), "CfConnectSyncRoot failed.");
            Assert.True(new CloudPlaceholderService().EnableOnDemandPopulation(SyncRoot));
        }

        public string SyncRoot { get; }

        public SyncRootConnection Connection { get; }

        public string PathOf(string name) => Path.Combine(SyncRoot, name);

        /// <summary>Reads a file from another process (the provider's own reads raise no callbacks), as text.</summary>
        public string Read(string name)
        {
            var start = new ProcessStartInfo("cmd.exe", $"/d /c \"type \"{PathOf(name)}\"\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            var finished = process.WaitForExit(TimeSpan.FromSeconds(70));
            if (!finished)
            {
                process.Kill(entireProcessTree: true);
            }

            // The service's side first, so a read that hangs (on CI) still shows why.
            foreach (var line in _log)
            {
                _output.WriteLine(line);
            }

            Assert.True(finished, $"Reading {name} did not finish.");
            _output.WriteLine($"type {name}: exit {process.ExitCode} {stderr.Result.Trim()}");
            return stdout.Result;
        }

        public void Dispose()
        {
            Connection.Disconnect();
            Connection.Dispose();
            _serve.Dispose();
            _registrar.Unregister(SyncRoot);
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch
            {
                // ignored
            }
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

    private sealed class Release(Action release) : IDisposable
    {
        private int _done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                release();
            }
        }
    }
}
