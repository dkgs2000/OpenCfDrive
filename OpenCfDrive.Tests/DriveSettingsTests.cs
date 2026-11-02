using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Tests;

/// <summary>
/// The settings file the app writes and the service reads, and the order in
/// which a value is chosen: environment, then the file, then the default.
/// </summary>
public sealed class DriveSettingsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opencfdrive-settings", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_folder, "settings.json");

    [Fact]
    public void Load_WithoutAFile_IsEmpty()
    {
        var settings = DriveSettingsFile.Load(SettingsPath);

        Assert.Equal(new DriveSettings(), settings);
    }

    [Fact]
    public void Save_ThenLoad_RoundTrips()
    {
        var saved = new DriveSettings { SyncRoot = @"D:\Drive", Remote = "box:backup", RcUrl = "http://127.0.0.1:6000", StartAtSignIn = true };

        DriveSettingsFile.Save(saved, SettingsPath);

        Assert.Equal(saved, DriveSettingsFile.Load(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Load_ABrokenFile_SaysWhichFile()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(SettingsPath, "{ not json");

        var error = Assert.Throws<InvalidDataException>(() => DriveSettingsFile.Load(SettingsPath));
        Assert.Contains(SettingsPath, error.Message);
    }

    [Fact]
    public void Resolve_PrefersTheEnvironment_ThenTheFile_ThenTheDefault()
    {
        var saved = new DriveSettings { SyncRoot = @"D:\FromFile", Remote = "file:" };
        var environment = new Dictionary<string, string> { [DriveSettingsFile.RemoteVariable] = "env:" };

        var effective = DriveSettingsFile.Resolve(saved, name => environment.GetValueOrDefault(name));

        Assert.Equal(new EffectiveSetting("env:", SettingSource.Environment, "RCLONE_FS"), effective.Remote);
        Assert.Equal(new EffectiveSetting(@"D:\FromFile", SettingSource.SettingsFile, "OPENCFDRIVE_ROOT"), effective.SyncRoot);
        Assert.Equal(new EffectiveSetting(DriveSettingsFile.DefaultRcUrl, SettingSource.Default, "RCLONE_RC_URL"), effective.RcUrl);
        Assert.True(effective.HasRemote);
    }

    [Fact]
    public void Resolve_WithNothingSet_HasNoRemote_AndNormalisesTheDefault()
    {
        var effective = DriveSettingsFile.Resolve(new DriveSettings(), _ => null);

        Assert.False(effective.HasRemote);
        Assert.Equal("e2e:", effective.Remote.Value);
        Assert.Equal(SyncStatePaths.DefaultSyncRoot, effective.SyncRoot.Value);
    }

    [Fact]
    public void Resolve_NormalisesTheRemote()
    {
        var effective = DriveSettingsFile.Resolve(new DriveSettings { Remote = " box:backup/ " }, _ => null);

        Assert.Equal("box:backup", effective.Remote.Value);
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
