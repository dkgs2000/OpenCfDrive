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
    public static string PathFor(string localPath, DateTime modifiedLocal)
    {
        var directory = Path.GetDirectoryName(localPath)!;
        var name = Name(Path.GetFileName(localPath), modifiedLocal);
        var candidate = Path.Combine(directory, name);
        for (var n = 2; File.Exists(candidate) || Directory.Exists(candidate); n++)
        {
            var dot = name.LastIndexOf(')') + 1;
            candidate = Path.Combine(directory, name.Insert(dot, $" ({n})"));
        }

        return candidate;
    }

    /// <summary>Whether a name is a conflict copy's.</summary>
    public static bool IsConflictCopy(string fileName) => fileName.Contains(Marker, StringComparison.Ordinal);
}
