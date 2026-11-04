using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Tests.TestSupport;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// A folder renamed here is acknowledged at once; its placeholder gets its new
/// identity once the next sync run has sent the rename, which can be while a
/// program is already opening a file in it (E2E scenario C11).
/// </summary>
public class RenamedFolderContractTest(ITestOutputHelper output)
{
    public enum Window
    {
        Listing,
        Download,
    }

    [SkippableTheory]
    [InlineData(Window.Listing, true)]
    [InlineData(Window.Listing, false)]
    [InlineData(Window.Download, true)]
    [InlineData(Window.Download, false)]
    public void AFileInARenamedFolder_Opens_WhileTheFolderGetsItsNewIdentity(Window window, bool markInSync)
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-renamed", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        Directory.CreateDirectory(syncRoot);
        var now = DateTimeOffset.UtcNow;
        var log = new ConcurrentQueue<string>();

        // The moment the update lands: inside the listing, or the download.
        var renaming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inWindow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<IReadOnlyList<PlaceholderEntry>> ListDirectory(string directory, CancellationToken cancellationToken)
        {
            if (directory.Length == 0)
            {
                return [new PlaceholderEntry("old", true, 0, now, "old/")];
            }

            if (window == Window.Listing)
            {
                await renaming.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                inWindow.TrySetResult();
                await updated.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }

            return [new PlaceholderEntry("z.txt", false, 1, now, "new/z.txt")];
        }

        using var server = new OneByteServer(async () =>
        {
            if (window == Window.Download)
            {
                inWindow.TrySetResult();
                await updated.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        });

        var placeholders = new CloudPlaceholderService();

        // The run's part once the rename is on the server (RenameRemoteDirectoryAsync).
        async Task RenameRemote(string targetLocal)
        {
            renaming.TrySetResult();
            await inWindow.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var identity = CloudPlaceholderService.BuildFileIdentity("new/");
            var ok = markInSync
                ? placeholders.MarkInSync(targetLocal, identity)
                : placeholders.UpdateFileIdentity(targetLocal, identity);
            log.Enqueue($"folder update ({(markInSync ? "in sync" : "identity")}) -> {ok}");
            updated.TrySetResult();
        }

        var store = new SqliteMetadataStore(Path.Combine(root, "state.db"));
        store.Initialize();
        using var serve = new RcloneServeClient(server.Url, "e2e:");
        var registrar = new SyncRootRegistrar();
        Assert.True(registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid()).Success);
        using var connection = new SyncRootConnection(
            store,
            serve,
            log.Enqueue,
            localRename: (_, newLocalPath, _, _) => _ = Task.Run(() => RenameRemote(newLocalPath)),
            listDirectory: ListDirectory);

        try
        {
            Assert.True(connection.Connect(syncRoot), "CfConnectSyncRoot failed.");
            Assert.True(placeholders.EnableOnDemandPopulation(syncRoot));

            Assert.Equal("", Run($"dir /b \"{syncRoot}\"").Output.Replace("old", "").Trim());

            // From another process (the provider's own rename raises no
            // NOTIFY_RENAME), and one that does not list the folder first, as
            // cmd's ren does. The folder was never opened.
            Assert.Equal(0, Run($"powershell -NoProfile -Command \"Rename-Item -LiteralPath '{Path.Combine(syncRoot, "old")}' -NewName new\"").ExitCode);
            Assert.True(placeholders.QueryState(Path.Combine(syncRoot, "new")).Partial, "The renamed folder was listed already.");

            var read = Run($"type \"{Path.Combine(syncRoot, "new", "z.txt")}\"");

            foreach (var line in log)
            {
                output.WriteLine(line);
            }

            output.WriteLine($"read: exit {read.ExitCode}, '{read.Output.Trim()}'");
            Assert.True(updated.Task.IsCompleted, "The folder update never ran.");
            Assert.DoesNotContain(log, line => line.Contains("failed", StringComparison.OrdinalIgnoreCase));
            Assert.Equal("z", read.Output.Trim());
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

    /// <summary>From another process: the provider's own I/O raises no listing callbacks.</summary>
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

    /// <summary>Answers every GET with the one byte "z", once <c>beforeAnswer</c> is done.</summary>
    private sealed class OneByteServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();

        public OneByteServer(Func<Task> beforeAnswer)
        {
            _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var buffer = new byte[4096];
                    while (!request.ToString().Contains("\r\n\r\n"))
                    {
                        var read = await stream.ReadAsync(buffer, _stop.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    await beforeAnswer();
                    var answer = "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nz"u8.ToArray();
                    await stream.WriteAsync(answer, _stop.Token);
                }
            });
        }

        public string Url { get; }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
        }
    }
}
