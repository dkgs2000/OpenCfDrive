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

    /// <summary>
    /// When the server's version could not be put in place after the copy
    /// was made (the file was in use), the next try finds that copy and makes
    /// no second one, also when it is not the first of the conflict names.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void KeptCopyOf_FindsTheCopyOfTheFileAsItIsNow(bool nameTakenBefore)
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "report.txt");
        File.WriteAllText(file, "local edit");
        File.SetLastWriteTime(file, At);
        Assert.Null(ConflictCopies.KeptCopyOf(file));

        if (nameTakenBefore)
        {
            // An earlier conflict in the same second, of another version.
            File.WriteAllText(ConflictCopies.PathFor(file, At), "another version");
        }

        var copy = ConflictCopies.PathFor(file, At);
        File.Copy(file, copy);

        Assert.Equal(copy, ConflictCopies.KeptCopyOf(file));
    }

    /// <summary>A file edited again after its copy was made is another version: it gets a copy of its own.</summary>
    [Fact]
    public void KeptCopyOf_IgnoresACopyOfAnEarlierVersion()
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "report.txt");
        File.WriteAllText(file, "first edit");
        File.SetLastWriteTime(file, At);
        File.Copy(file, ConflictCopies.PathFor(file, At));

        File.WriteAllText(file, "later edit");   // the same size
        File.SetLastWriteTime(file, At.AddMilliseconds(300));

        Assert.Null(ConflictCopies.KeptCopyOf(file));
        Assert.Equal(Path.Combine(_folder, "report (conflicted copy 2026-11-03 175504) (2).txt"), ConflictCopies.PathFor(file, At.AddMilliseconds(300)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
