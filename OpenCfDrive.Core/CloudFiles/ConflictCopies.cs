namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// Names for the copy a conflict keeps of the local version, as Nextcloud's
/// desktop client makes them (Utility::makeConflictFileName in
/// src/common/utility.cpp): "report (conflicted copy 2026-11-03 175500).txt".
/// </summary>
public static class ConflictCopies
{
    public const string Marker = " (conflicted copy ";

    /// <summary>
    /// The conflict copy's name for <paramref name="fileName"/>: the tag goes
    /// before the extension, or at the end when there is none (".hidden",
    /// "Makefile"). The time is the local copy's modification time, local.
    /// </summary>
    public static string Name(string fileName, DateTime modifiedLocal)
    {
        var dot = fileName.LastIndexOf('.');
        if (dot <= 0)
        {
            dot = fileName.Length;
        }

        return fileName.Insert(dot, $"{Marker}{modifiedLocal.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture)})");
    }

    /// <summary>
    /// A free path for the conflict copy of <paramref name="localPath"/>: its
    /// conflict name, or with " (2)", " (3)"... before the extension if two
    /// conflicts fall in the same second.
    /// </summary>
    public static string PathFor(string localPath, DateTime modifiedLocal) =>
        Candidates(localPath, modifiedLocal).First(candidate => !File.Exists(candidate) && !Directory.Exists(candidate));

    /// <summary>
    /// The conflict copy already kept of the file as it is now, if there is
    /// one: a copy under one of its conflict names with its size and write
    /// time (a copy keeps the write time). When the server's version could
    /// not be put in place (the file was in use), the next try finds it, so
    /// the file is not copied aside again and again.
    /// </summary>
    public static string? KeptCopyOf(string localPath)
    {
        var file = new FileInfo(localPath);
        if (!file.Exists)
        {
            return null;
        }

        foreach (var candidate in Candidates(localPath, file.LastWriteTime))
        {
            var copy = new FileInfo(candidate);
            if (!copy.Exists)
            {
                if (!Directory.Exists(candidate))
                {
                    return null;
                }

                continue;
            }

            if (copy.Length == file.Length && copy.LastWriteTimeUtc == file.LastWriteTimeUtc)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The conflict names in the order they are taken: the name, then " (2)", " (3)"...</summary>
    private static IEnumerable<string> Candidates(string localPath, DateTime modifiedLocal)
    {
        var directory = Path.GetDirectoryName(localPath)!;
        var name = Name(Path.GetFileName(localPath), modifiedLocal);
        yield return Path.Combine(directory, name);
        var dot = name.LastIndexOf(')') + 1;
        for (var n = 2; ; n++)
        {
            yield return Path.Combine(directory, name.Insert(dot, $" ({n})"));
        }
    }

    /// <summary>Whether a name is a conflict copy's.</summary>
    public static bool IsConflictCopy(string fileName) => fileName.Contains(Marker, StringComparison.Ordinal);
}
