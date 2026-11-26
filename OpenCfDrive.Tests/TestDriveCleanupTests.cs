using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Tests.TestSupport;

namespace OpenCfDrive.Tests;

/// <summary>
/// Removing a drive makes sure its Explorer entry is gone, and the test run
/// removes the test drives earlier runs left, and only those.
/// </summary>
public class TestDriveCleanupTests
{
    [Fact]
    public void UnregisterFromShell_AsksAgain_WhileTheEntryStays()
    {
        var registered = true;
        var calls = 0;
        var removed = SyncRootRegistrar.UnregisterFromShell(
            "id",
            _ =>
            {
                // The first call returns without removing it, the second
                // throws "Element not found" and removes it.
                if (++calls == 2)
                {
                    registered = false;
                    throw new System.Runtime.InteropServices.COMException("Element not found.", unchecked((int)0x80070490));
                }
            },
            _ => registered);

        Assert.True(removed);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void UnregisterFromShell_GivesUp_AfterThreeTries()
    {
        var calls = 0;
        Assert.False(SyncRootRegistrar.UnregisterFromShell("id", _ => calls++, _ => true));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(@"C:\Temp\opencfdrive-update\0924a224aeb14ac283e7a918f0d71b68\drive", @"C:\Temp\opencfdrive-update\0924a224aeb14ac283e7a918f0d71b68")]
    [InlineData(@"\\?\C:\Temp\opencfdrive-download\0924a224aeb14ac283e7a918f0d71b68\drive", @"C:\Temp\opencfdrive-download\0924a224aeb14ac283e7a918f0d71b68")]
    [InlineData(@"c:\temp\OpenCfDrive-Shell\0924A224AEB14AC283E7A918F0D71B68", @"C:\Temp\OpenCfDrive-Shell\0924A224AEB14AC283E7A918F0D71B68")]
    [InlineData(@"C:\Users\someone\OpenCfDrive", null)]
    [InlineData(@"C:\Temp\opencfdrive-e2e\0924a224aeb14ac283e7a918f0d71b68\drive", null)]
    [InlineData(@"C:\Temp\opencfdrive-e2e-20261126-101500\drive", null)]
    [InlineData(@"C:\Temp\opencfdrive-update\drive", null)]
    [InlineData(@"C:\Temp\other\0924a224aeb14ac283e7a918f0d71b68\drive", null)]
    [InlineData(@"D:\Temp\opencfdrive-update\0924a224aeb14ac283e7a918f0d71b68\drive", null)]
    public void OnlyTheContractTestsFolders_CountAsTestDrives(string syncRoot, string? expected)
    {
        Assert.Equal(expected, LeftoverTestDrives.TestFolderOf(syncRoot, @"C:\Temp\"), StringComparer.OrdinalIgnoreCase);
    }
}
