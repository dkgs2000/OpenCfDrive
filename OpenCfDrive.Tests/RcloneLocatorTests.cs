using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Services;

namespace OpenCfDrive.Tests;

/// <summary>
/// Which rclone OpenCfDrive runs, and whether it runs one at all: the chosen
/// program, else PATH, else its download; its own unless the environment
/// points at another remote control.
/// </summary>
public sealed class RcloneLocatorTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opencfdrive-locator", Guid.NewGuid().ToString("N"));

    [Fact]
    public void AChosenProgram_IsUsed()
    {
        Directory.CreateDirectory(_folder);
        var exe = Path.Combine(_folder, "my-rclone.exe");
        File.WriteAllText(exe, "");

        Assert.Equal(new RcloneProgram(exe, RcloneSource.Chosen), RcloneLocator.Find(exe));
    }

    [Fact]
    public void AChosenProgramThatIsMissing_IsNotReplacedByAnother()
    {
        Assert.Null(RcloneLocator.Find(Path.Combine(_folder, "missing", "rclone.exe")));
    }

    [Fact]
    public void ADownload_GoesUnderLocalAppData()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenCfDrive", "rclone", "rclone.exe"),
            RcloneLocator.DownloadedPath);
    }

    [Fact]
    public void ByDefault_OpenCfDriveRunsItsOwnRclone()
    {
        var effective = DriveSettingsFile.Resolve(new DriveSettings(), _ => null);

        Assert.True(effective.ManageRclone);
        Assert.Null(effective.RclonePath);
        Assert.Null(effective.RcloneConfig);
    }

    [Fact]
    public void ARemoteControlInTheEnvironment_IsUsedInstead()
    {
        var effective = DriveSettingsFile.Resolve(
            new DriveSettings { ManageRclone = true },
            name => name == DriveSettingsFile.RcUrlVariable ? "http://127.0.0.1:6000" : null);

        Assert.False(effective.ManageRclone);
        Assert.Equal("http://127.0.0.1:6000", effective.RcUrl.Value);
    }

    [Fact]
    public void TheUsersOwnRemoteControl_WhenChosen()
    {
        var effective = DriveSettingsFile.Resolve(new DriveSettings { ManageRclone = false, RcUrl = "http://127.0.0.1:6000" }, _ => null);

        Assert.False(effective.ManageRclone);
    }

    [Fact]
    public void TheProgramInTheEnvironment_WinsOverTheSettingsFile()
    {
        var effective = DriveSettingsFile.Resolve(
            new DriveSettings { RclonePath = @"C:\from\settings\rclone.exe", RcloneConfig = @" C:\my\rclone.conf " },
            name => name == DriveSettingsFile.RclonePathVariable ? @"C:\from\env\rclone.exe" : null);

        Assert.Equal(@"C:\from\env\rclone.exe", effective.RclonePath);
        Assert.Equal(@"C:\my\rclone.conf", effective.RcloneConfig);
    }

    [Fact]
    public void TheChecksum_IsReadFromTheSignedList()
    {
        const string sums = """
            -----BEGIN PGP SIGNED MESSAGE-----
            Hash: SHA1

            ad5ca6619b30c08887db92eecc5fae42f2502ef2711cd0418e2ccc5835149d9b  rclone-v1.75.2-aix-ppc64.zip
            8a9db2bad1dc79a65b3348fd857069598e2aab7cf4d118b77b1df23055aff818  rclone-v1.75.2-windows-amd64.zip
            7eb9844c9e5b723b9ef28821891caa17f6d9d1a375c49365cbe04114cec8dcd9  rclone-v1.75.2-windows-arm64.zip
            -----BEGIN PGP SIGNATURE-----
            =ojpc
            -----END PGP SIGNATURE-----
            """;

        Assert.Equal(
            "8a9db2bad1dc79a65b3348fd857069598e2aab7cf4d118b77b1df23055aff818",
            RcloneDownloader.ExpectedHash(sums, "rclone-v1.75.2-windows-amd64.zip"));
        Assert.Null(RcloneDownloader.ExpectedHash(sums, "rclone-v1.75.2-windows-386.zip"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
