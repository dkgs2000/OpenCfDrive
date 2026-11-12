namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// Names for the copy a conflict keeps of the local version, as Nextcloud's
/// desktop client makes them (Utility::makeConflictFileName in
/// src/common/utility.cpp): "report (conflicted copy 2026-11-03 175500).txt".
/// </summary>
public static class ConflictCopies
{
    public const string Marker = " (conflicted copy ";

    /// <summary>The longest file name Windows allows, in characters.</summary>
    public const int MaxNameLength = 255;

    /// <summary>
    /// The conflict copy's name for <paramref name="fileName"/>: the tag goes
    /// before the extension, or at the end when there is none (".hidden",
    /// "Makefile"). The time is the local copy's modification time, local.
    /// A name too long to take the tag is cut at its end to make room
    /// (Nextcloud doesn't, and its copy then can't be made at all).
    /// </summary>
    public static string Name(string fileName, DateTime modifiedLocal) => Name(fileName, modifiedLocal, 1);

    /// <summary>The <paramref name="n"/>th conflict name: " (2)", " (3)"... after the tag.</summary>
    private static string Name(string fileName, DateTime modifiedLocal, int n)
    {
        var tag = $"{Marker}{modifiedLocal.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture)})";
        if (n > 1)
        {
            tag += $" ({n})";
        }

        var dot = fileName.LastIndexOf('.');
        if (dot <= 0)
        {
            dot = fileName.Length;
        }

        var stem = fileName[..dot];
        var extension = fileName[dot..];
        var room = MaxNameLength - tag.Length - extension.Length;
        if (room < 1)
        {
            // An "extension" that leaves no room is cut like the rest.
            (stem, extension, room) = (fileName, "", MaxNameLength - tag.Length);
        }

        if (stem.Length > room)
        {
            // Never half a character: a cut inside a surrogate pair takes it whole.
            var cut = char.IsHighSurrogate(stem[room - 1]) ? room - 1 : room;
            stem = stem[..cut];
        }

        return stem + tag + extension;
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
        var fileName = Path.GetFileName(localPath);
        for (var n = 1; ; n++)
        {
            yield return Path.Combine(directory, Name(fileName, modifiedLocal, n));
        }
    }

    /// <summary>Whether a name is a conflict copy's.</summary>
    public static bool IsConflictCopy(string fileName) => fileName.Contains(Marker, StringComparison.Ordinal);
}
