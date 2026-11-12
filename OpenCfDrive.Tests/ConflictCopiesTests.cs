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

    /// <summary>
    /// A name too long to take the tag is cut at its end to make room: the
    /// copy's name is as long as Windows allows, with the tag and extension.
    /// </summary>
    [Fact]
    public void Name_CutsALongNameToFitTheTag()
    {
        var copy = ConflictCopies.Name(new string('a', 245) + ".docx", At);

        Assert.Equal(new string('a', 214) + " (conflicted copy 2026-11-03 175504).docx", copy);
        Assert.Equal(ConflictCopies.MaxNameLength, copy.Length);
    }

    /// <summary>An emoji is two UTF-16 units; a cut between them would leave half of one.</summary>
    [Fact]
    public void Name_NeverCutsACharacterInHalf()
    {
        var emoji = "\U0001F600";
        var copy = ConflictCopies.Name(string.Concat(Enumerable.Repeat(emoji, 120)) + ".txt", At);

        Assert.Equal(string.Concat(Enumerable.Repeat(emoji, 107)) + " (conflicted copy 2026-11-03 175504).txt", copy);
    }

    /// <summary>An "extension" too long to leave room for anything else is cut with the rest.</summary>
    [Fact]
    public void Name_AnExtensionThatLeavesNoRoomIsCutToo()
    {
        var copy = ConflictCopies.Name("a." + new string('x', 240), At);

        Assert.Equal("a." + new string('x', 217) + " (conflicted copy 2026-11-03 175504)", copy);
        Assert.Equal(ConflictCopies.MaxNameLength, copy.Length);
    }

    /// <summary>
    /// A file with a name as long as Windows allows still gets its copies:
    /// a second one in the same second too, and the next try finds each.
    /// </summary>
    [Fact]
    public void PathFor_AFileWithAVeryLongNameGetsItsCopies()
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, new string('r', 250) + ".txt");
        File.WriteAllText(file, "first edit");
        File.SetLastWriteTime(file, At);

        var first = ConflictCopies.PathFor(file, At);
        File.Copy(file, first);
        Assert.Equal(first, ConflictCopies.KeptCopyOf(file));

        File.WriteAllText(file, "later edit");   // the same size
        File.SetLastWriteTime(file, At.AddMilliseconds(300));
        var second = ConflictCopies.PathFor(file, At.AddMilliseconds(300));
        File.Copy(file, second);

        Assert.EndsWith(" (conflicted copy 2026-11-03 175504) (2).txt", second);
        Assert.Equal(ConflictCopies.MaxNameLength, Path.GetFileName(second).Length);
        Assert.Equal(second, ConflictCopies.KeptCopyOf(file));
    }

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
