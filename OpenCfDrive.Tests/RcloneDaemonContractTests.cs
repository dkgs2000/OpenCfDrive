using System.Diagnostics;
using System.Net;
using System.Text;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Tests.TestSupport;

namespace OpenCfDrive.Tests;

/// <summary>
/// The rclone remote control the service runs for itself, against real
/// rclone: it wants its login, it serves listings and byte ranges to the
/// engine's clients, it comes back when it dies, and it ends with its owner.
/// </summary>
public sealed class RcloneDaemonContractTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opencfdrive-daemon", Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_folder, "rclone.conf");

    private string DataFolder => Path.Combine(_folder, "data");

    private string LogPath => Path.Combine(_folder, "rclone.log");

    /// <summary>A config with one remote, "files", an alias over a local folder.</summary>
    private RcloneProgram Prepare()
    {
        Directory.CreateDirectory(DataFolder);
        File.WriteAllText(Path.Combine(DataFolder, "hello.txt"), "hello from the managed rclone");
        File.WriteAllText(ConfigPath, $"[files]\ntype = alias\nremote = {DataFolder}\n");
        return new RcloneProgram(Prerequisites.RcloneExe, RcloneSource.Chosen);
    }

    private Task<RcloneDaemon> StartAsync(RcloneProgram program) =>
        RcloneDaemon.StartAsync(program, ConfigPath, LogPath, null, CancellationToken.None);

    [SkippableFact]
    public async Task ItWantsItsLogin_AndServesTheEngineWithIt()
    {
        Skip.IfNot(Prerequisites.RcloneAvailable, "rclone is not on PATH.");
        await using var daemon = await StartAsync(Prepare());

        Assert.Equal(RcloneDaemonState.Running, daemon.State);
        Assert.StartsWith("http://127.0.0.1:", daemon.Endpoint.Url);
        Assert.StartsWith("v", daemon.Version);

        // Without the login: refused.
        using (var anonymous = new HttpClient())
        {
            using var response = await anonymous.PostAsync(
                $"{daemon.Endpoint.BaseUrl}/operations/list",
                new StringContent("""{"fs":"files:","remote":""}""", Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        // With it: the engine's clients list and read.
        using var rc = new RcloneRcClient(daemon.Endpoint);
        var items = await rc.ListDirectoryAsync("files:", "");
        Assert.Contains(items, item => item.Name == "hello.txt");

        using var serve = new RcloneServeClient(daemon.Endpoint, "files:");
        await using var range = await serve.OpenRangeAsync("hello.txt", 6, 4);
        using var reader = new StreamReader(range);
        Assert.Equal("from", await reader.ReadToEndAsync());

        // The password never shows in its command line.
        Assert.DoesNotContain(daemon.Endpoint.Password!, CommandLineOf(daemon.ProcessId!.Value));
    }

    [SkippableFact]
    public async Task WhenRcloneDies_ItComesBackOnTheSamePortAndLogin()
    {
        Skip.IfNot(Prerequisites.RcloneAvailable, "rclone is not on PATH.");
        await using var daemon = await StartAsync(Prepare());
        var first = daemon.ProcessId!.Value;

        using (var process = Process.GetProcessById(first))
        {
            process.Kill();
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline && (daemon.State != RcloneDaemonState.Running || daemon.ProcessId is null || daemon.ProcessId == first))
        {
            await Task.Delay(200);
        }

        Assert.Equal(RcloneDaemonState.Running, daemon.State);
        Assert.NotEqual(first, daemon.ProcessId);
        using var rc = new RcloneRcClient(daemon.Endpoint);
        Assert.Contains(await rc.ListDirectoryAsync("files:", ""), item => item.Name == "hello.txt");
    }

    [SkippableFact]
    public async Task Dispose_EndsRclone()
    {
        Skip.IfNot(Prerequisites.RcloneAvailable, "rclone is not on PATH.");
        var daemon = await StartAsync(Prepare());
        using var process = Process.GetProcessById(daemon.ProcessId!.Value);

        await daemon.DisposeAsync();

        Assert.True(process.WaitForExit(10_000));
    }

    [SkippableFact]
    public async Task AProgramThatIsNoRclone_FailsWithAReason()
    {
        Skip.IfNot(Prerequisites.RcloneAvailable, "rclone is not on PATH.");
        Prepare();
        // whoami.exe rejects rclone's arguments and exits at once.
        var notRclone = new RcloneProgram(Path.Combine(Environment.SystemDirectory, "whoami.exe"), RcloneSource.Chosen);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(notRclone));
        Assert.Contains("stopped right away", error.Message);
    }

    [SkippableFact]
    public async Task TheCli_ReadsTheConfig_ForTheApp()
    {
        Skip.IfNot(Prerequisites.RcloneAvailable, "rclone is not on PATH.");
        var program = Prepare();
        var cli = new RcloneCli(program.Path, ConfigPath);

        Assert.StartsWith("v", await cli.VersionAsync());
        Assert.Equal(ConfigPath, await cli.ConfigFileAsync(), ignoreCase: true);
        Assert.Equal(["files"], await cli.ListRemotesAsync());

        var config = await cli.RemoteConfigAsync("files");
        Assert.Equal("alias", config!.Value.GetProperty("type").GetString());
        Assert.Null(await cli.RemoteConfigAsync("missing"));

        var (about, error) = await cli.AboutAsync("files:");
        Assert.Null(error);
        Assert.True(about!.Value.GetProperty("total").GetInt64() > 0);
    }

    private static string CommandLineOf(int processId)
    {
        using var searcher = Process.Start(new ProcessStartInfo(
            "powershell.exe",
            $"-NoProfile -Command \"(Get-CimInstance Win32_Process -Filter 'ProcessId = {processId}').CommandLine\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        var output = searcher.StandardOutput.ReadToEnd();
        searcher.WaitForExit();
        return output;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
