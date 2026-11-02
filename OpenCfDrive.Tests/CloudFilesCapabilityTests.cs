using OpenCfDrive.Core.CloudFiles;

namespace OpenCfDrive.Tests;

public class CloudFilesCapabilityTests
{
    [Fact]
    public void CapabilityProbe_NeverThrows_AndExplainsItself()
    {
        var support = CloudFilesCapability.Current;

        Assert.NotNull(support);
        Assert.False(string.IsNullOrWhiteSpace(support.Reason));

        if (support.IsSupported)
        {
            Assert.NotNull(support.BuildNumber);
            Assert.True(support.BuildNumber > 0);
        }
    }

    [Fact]
    public void CapabilityProbe_ReportsMissingDllPrecisely()
    {
        var support = CloudFilesCapability.Current;
        if (support.IsSupported)
        {
            // Host really has Cloud Files; nothing to assert about the failure path.
            return;
        }

        Assert.Contains("cldapi.dll", support.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CapabilityLibraryPath_PointsAtSystemDirectory()
    {
        var path = CloudFilesCapability.LibraryPath;

        Assert.EndsWith("cldapi.dll", path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("System32", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaceholderIdentity_IsUtf8RemotePath()
    {
        var identity = CloudPlaceholderService.BuildFileIdentity("folder/ünïcode.txt");

        Assert.Equal("folder/ünïcode.txt", System.Text.Encoding.UTF8.GetString(identity));
    }
}
