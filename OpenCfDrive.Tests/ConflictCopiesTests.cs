using OpenCfDrive.Core.CloudFiles;

namespace OpenCfDrive.Tests;

/// <summary>
/// Conflict copies are named as Nextcloud's desktop client names them
/// (Utility::makeConflictFileName): the tag before the extension, or at the
/// end of a name without one, with the local copy's modification time.
/// </summary>
public sealed class ConflictCopiesTests : IDisposable
{
    private static readonly DateTime At = new(2026, 11, 3, 17, 55, 4);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "opencfdrive-conflict-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("report.txt", "report (conflicted copy 2026-11-03 175504).txt")]
    [InlineData("archive.tar.gz", "archive.tar (conflicted copy 2026-11-03 175504).gz")]
    [InlineData("Makefile", "Makefile (conflicted copy 2026-11-03 175504)")]
    [InlineData(".hidden", ".hidden (conflicted copy 2026-11-03 175504)")]
    [InlineData("تقرير.txt", "تقرير (conflicted copy 2026-11-03 175504).txt")]
    public void Name_PutsTheTagBeforeTheExtension(string name, string expected)
    {
        Assert.Equal(expected, ConflictCopies.Name(name, At));
        Assert.True(ConflictCopies.IsConflictCopy(expected));
        Assert.False(ConflictCopies.IsConflictCopy(name));
    }

    [Fact]
    public void Name_UsesA24HourClock() =>
        Assert.Equal("a (conflicted copy 2026-11-03 090807).txt", ConflictCopies.Name("a.txt", new DateTime(2026, 11, 3, 9, 8, 7)));

    [Fact]
    public void PathFor_TwoConflictsInTheSameSecond_GetTheirOwnNames()
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "report.txt");
        var first = ConflictCopies.PathFor(file, At);
        Assert.Equal(Path.Combine(_folder, "report (conflicted copy 2026-11-03 175504).txt"), first);
        File.WriteAllText(first, "one");

        Assert.Equal(Path.Combine(_folder, "report (conflicted copy 2026-11-03 175504) (2).txt"), ConflictCopies.PathFor(file, At));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
