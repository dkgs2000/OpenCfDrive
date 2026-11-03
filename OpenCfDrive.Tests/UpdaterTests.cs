using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCfDrive.Core.Updates;
using OpenCfDrive.Tests.TestSupport;

namespace OpenCfDrive.Tests;

/// <summary>
/// Updating from GitHub releases the way Nextcloud's desktop client updates
/// itself: check after start and every 10 hours, download a newer MSI and
/// check it, offer to install it, and tell on the next start whether that
/// worked. GitHub is a fake; time is driven by hand.
/// </summary>
public sealed class UpdaterTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static readonly Version Installed = new(0, 1, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opencfdrive-updater-" + Guid.NewGuid().ToString("N"));
    private readonly FakeGitHub _github = new();
    private readonly ManualTime _time = new();
    private bool _automatic = true;

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private Updater NewUpdater(Version? current = null, string? repository = "owner/repo") =>
        new(current ?? Installed, repository, _folder, () => _automatic, handler: _github, time: _time);

    [Fact]
    public void Parse_ReadsTheVersion_TheInstaller_AndItsChecksum()
    {
        var release = GitHubReleases.Parse(FakeGitHub.ReleaseJson("v1.2.3", [1, 2, 3]));

        Assert.Equal(new Version(1, 2, 3), release.Version);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v1.2.3", release.Page);
        Assert.NotNull(release.Installer);
        Assert.Equal("OpenCfDrive-1.2.3-x64-setup.exe", release.Installer.Name);
        Assert.Equal(3, release.Installer.Size);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3 })), release.Installer.Sha256, ignoreCase: true);
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("v0.1.0-beta", null)]
    [InlineData("v0.1.2.3", null)]
    [InlineData("latest", null)]
    public void ParseTag_TakesOnlyThreeNumbers(string tag, string? expected)
    {
        Assert.Equal(expected is null ? null : Version.Parse(expected), GitHubReleases.ParseTag(tag));
    }

    [Fact]
    public async Task ANewerRelease_IsDownloadedAndChecked_ThenReadyToInstall()
    {
        _github.Publish("v0.2.0", Encoding.UTF8.GetBytes("the 0.2.0 installer"));
        using var updater = NewUpdater();

        await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Ready, updater.Status.Stage);
        Assert.Equal(new Version(0, 2, 0), updater.Status.Available);
        Assert.Equal("https://github.com/owner/repo/releases/tag/v0.2.0", updater.Status.Page);
        var installer = updater.BeginInstall();
        Assert.Equal(Path.Combine(_folder, "OpenCfDrive-0.2.0-x64-setup.exe"), installer);
        Assert.Equal("the 0.2.0 installer", File.ReadAllText(installer!));
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchItsChecksum_IsThrownAway()
    {
        _github.Publish("v0.2.0", Encoding.UTF8.GetBytes("the 0.2.0 installer"), sha256: new string('0', 64));
        using var updater = NewUpdater();

        await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Failed, updater.Status.Stage);
        Assert.Contains("checksum", updater.Status.Detail);
        Assert.Empty(Directory.Exists(_folder) ? Directory.GetFiles(_folder) : []);
        Assert.Null(updater.BeginInstall());
    }

    [Fact]
    public async Task AReleaseWithoutAChecksum_IsNotDownloaded()
    {
        _github.Publish("v0.2.0", [1], sha256: null);
        using var updater = NewUpdater();

        await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Failed, updater.Status.Stage);
        Assert.Equal(1, _github.Requests); // the release, not its installer
    }

    [Theory]
    [InlineData("v0.1.0")]
    [InlineData("v0.0.9")]
    public async Task TheSameOrAnOlderRelease_IsUpToDate(string tag)
    {
        _github.Publish(tag, [1]);
        using var updater = NewUpdater();

        await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.UpToDate, updater.Status.Stage);
        Assert.Equal(1, _github.Requests);
    }

    [Fact]
    public async Task NoReleaseYet_IsUpToDate()
    {
        using var updater = NewUpdater();

        await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.UpToDate, updater.Status.Stage);
    }

    [Fact]
    public async Task WhenGitHubCannotBeReached_ADownloadedUpdateStaysReady()
    {
        _github.Publish("v0.2.0", [1, 2]);
        using var updater = NewUpdater();
        await updater.CheckNowAsync();

        _github.Unreachable = true;
        await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Ready, updater.Status.Stage);
        Assert.NotNull(updater.BeginInstall());
    }

    [Fact]
    public async Task WhenGitHubCannotBeReached_TheCheckFails()
    {
        _github.Unreachable = true;
        using var updater = NewUpdater();

        await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Failed, updater.Status.Stage);
        Assert.Contains("No such host is known", updater.Status.Detail);
    }

    [Fact]
    public async Task NextStart_AfterTheUpdateInstalled_RemovesTheInstaller()
    {
        _github.Publish("v0.2.0", [1, 2]);
        using (var before = NewUpdater())
        {
            await before.CheckNowAsync();
            Assert.NotNull(before.BeginInstall());
        }

        using var after = NewUpdater(current: new Version(0, 2, 0));
        after.Start();

        Assert.Equal(UpdateStage.UpToDate, after.Status.Stage);
        Assert.Equal("Updated to OpenCfDrive 0.2.0.", after.Status.Detail);
        Assert.Empty(Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task NextStart_AfterAFailedInstall_OffersItAgain()
    {
        _github.Publish("v0.2.0", [1, 2]);
        using (var before = NewUpdater())
        {
            await before.CheckNowAsync();
            Assert.NotNull(before.BeginInstall());
        }

        using var after = NewUpdater();
        after.Start();

        Assert.Equal(UpdateStage.Ready, after.Status.Stage);
        Assert.Contains("did not install", after.Status.Detail);
        Assert.Contains(after.InstallLog, after.Status.Detail);
        Assert.NotNull(after.BeginInstall());
    }

    [Fact]
    public async Task NextStart_WithADownloadNotInstalledYet_IsReady()
    {
        _github.Publish("v0.2.0", [1, 2]);
        using (var before = NewUpdater())
        {
            await before.CheckNowAsync();
        }

        using var after = NewUpdater();
        after.Start();

        Assert.Equal(UpdateStage.Ready, after.Status.Stage);
        Assert.Equal(new Version(0, 2, 0), after.Status.Available);
    }

    [Fact]
    public async Task Schedule_ChecksSoonAfterStart_ThenEveryTenHours()
    {
        _github.Publish("v0.1.0", [1]);
        using var updater = NewUpdater();

        updater.Start();
        Assert.Equal(Updater.FirstCheckAfter, _time.DueIn);

        await FireAndWaitAsync();
        Assert.Equal(1, _github.Requests);
        Assert.Equal(UpdateStage.UpToDate, updater.Status.Stage);
        Assert.Equal(Updater.CheckInterval, _time.DueIn);

        await FireAndWaitAsync();
        Assert.Equal(2, _github.Requests);
        Assert.Equal(Updater.CheckInterval, _time.DueIn);
    }

    [Fact]
    public async Task Schedule_AsksNothing_WhenAutomaticChecksAreOff()
    {
        _automatic = false;
        _github.Publish("v0.2.0", [1]);
        using var updater = NewUpdater();
        updater.Start();

        await FireAndWaitAsync();

        Assert.Equal(0, _github.Requests);
        Assert.Equal(UpdateStage.NotChecked, updater.Status.Stage);
        Assert.Equal(Updater.CheckInterval, _time.DueIn);

        // Asking by hand still works.
        await updater.CheckNowAsync();
        Assert.Equal(UpdateStage.Ready, updater.Status.Stage);
    }

    [Fact]
    public async Task Schedule_AsksNothing_WhileADownloadWaitsToBeInstalled()
    {
        _github.Publish("v0.2.0", [1]);
        using var updater = NewUpdater();
        updater.Start();
        await FireAndWaitAsync();
        Assert.Equal(UpdateStage.Ready, updater.Status.Stage);
        var requests = _github.Requests;

        await FireAndWaitAsync();

        Assert.Equal(requests, _github.Requests);
        Assert.Equal(UpdateStage.Ready, updater.Status.Stage);
    }

    [Fact]
    public async Task ABuildWithoutARepository_DoesNotUpdate()
    {
        using var updater = NewUpdater(repository: null);
        updater.Start();

        await updater.CheckNowAsync();

        Assert.Equal(UpdateStage.Unavailable, updater.Status.Stage);
        Assert.Null(_time.DueIn);
        Assert.Equal(0, _github.Requests);
    }

    [Fact]
    public void InstallCommand_RunsTheSetupWithAProgressBar_ThenOpensTheAppAgain()
    {
        var start = Updater.InstallCommand(@"C:\Users\o'brien\updates\OpenCfDrive-0.2.0-x64-setup.exe", @"C:\u\install.log", @"C:\Apps\OpenCfDrive.exe");

        Assert.Equal("powershell.exe", start.FileName);
        Assert.Equal(
            @"& { & 'C:\Users\o''brien\updates\OpenCfDrive-0.2.0-x64-setup.exe' /passive /norestart /log 'C:\u\install.log' | Out-Null; & 'C:\Apps\OpenCfDrive.exe' }",
            start.ArgumentList[^1]);
    }

    /// <summary>Fires the timer and waits until the check it started has scheduled the next one.</summary>
    private async Task FireAndWaitAsync()
    {
        var scheduled = _time.NextSchedule();
        _time.Fire();
        await scheduled.WaitAsync(Timeout);
    }

    private sealed class FakeGitHub : HttpMessageHandler
    {
        private static readonly Uri InstallerUrl = new("https://github.com/owner/repo/releases/download/installer.msi");
        private string? _release;
        private byte[] _installer = [];
        private int _requests;

        public bool Unreachable { get; set; }

        public int Requests => Volatile.Read(ref _requests);

        public void Publish(string tag, byte[] installer, string? sha256 = "computed")
        {
            _installer = installer;
            _release = ReleaseJson(tag, installer, sha256);
        }

        public static string ReleaseJson(string tag, byte[] installer, string? sha256 = "computed")
        {
            var version = GitHubReleases.ParseTag(tag);
            var asset = new Dictionary<string, object?>
            {
                ["name"] = version is null ? "unknown.msi" : GitHubReleases.InstallerName(version),
                ["browser_download_url"] = InstallerUrl.ToString(),
                ["size"] = installer.Length,
            };
            if (sha256 is not null)
            {
                asset["digest"] = "sha256:" + (sha256 == "computed" ? Convert.ToHexString(SHA256.HashData(installer)).ToLowerInvariant() : sha256);
            }

            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["tag_name"] = tag,
                ["html_url"] = $"https://github.com/owner/repo/releases/tag/{tag}",
                // The bare MSI is attached too; the setup is the one an update runs.
                ["assets"] = new object[]
                {
                    new { name = $"OpenCfDrive-{version}-x64.msi", browser_download_url = "https://example.invalid/msi", size = 1 },
                    asset,
                },
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            if (Unreachable)
            {
                throw new HttpRequestException("No such host is known. (api.github.com:443)");
            }

            if (request.RequestUri!.AbsoluteUri == "https://api.github.com/repos/owner/repo/releases/latest")
            {
                Assert.Contains(request.Headers.UserAgent, product => product.Product?.Name == "OpenCfDrive");
                return Task.FromResult(_release is null
                    ? new HttpResponseMessage(HttpStatusCode.NotFound)
                    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_release) });
            }

            return Task.FromResult(request.RequestUri == InstallerUrl
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(_installer) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
