using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Core.Services;

public enum RcloneSource
{
    /// <summary>The user chose this program.</summary>
    Chosen,

    /// <summary>Found on PATH.</summary>
    Path,

    /// <summary>The copy OpenCfDrive downloaded from rclone.org.</summary>
    Downloaded,
}

/// <summary>An rclone program OpenCfDrive can run, and where it came from.</summary>
public sealed record RcloneProgram(string Path, RcloneSource Source);

/// <summary>
/// OpenCfDrive does not ship rclone. It runs the one the user chose, else one
/// on PATH, else the copy it downloaded (see RcloneDownloader).
/// </summary>
public static class RcloneLocator
{
    private const string ExeName = "rclone.exe";

    /// <summary>%LOCALAPPDATA%\OpenCfDrive\rclone, where a downloaded rclone goes.</summary>
    public static string DownloadFolder =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "rclone");

    public static string DownloadedPath => System.IO.Path.Combine(DownloadFolder, ExeName);

    /// <summary>
    /// The program to run, or null when there is none. A chosen program that
    /// is missing is not replaced by another: the user meant that one.
    /// </summary>
    public static RcloneProgram? Find(string? chosen)
    {
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            var full = System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(chosen.Trim()));
            return File.Exists(full) ? new RcloneProgram(full, RcloneSource.Chosen) : null;
        }

        foreach (var folder in PathFolders())
        {
            try
            {
                var candidate = System.IO.Path.Combine(Environment.ExpandEnvironmentVariables(folder), ExeName);
                if (File.Exists(candidate))
                {
                    return new RcloneProgram(System.IO.Path.GetFullPath(candidate), RcloneSource.Path);
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry.
            }
        }

        return File.Exists(DownloadedPath) ? new RcloneProgram(DownloadedPath, RcloneSource.Downloaded) : null;
    }

    /// <summary>
    /// This process's PATH, then the user's and the machine's as saved now:
    /// rclone installed while the app runs (winget, scoop) is found without
    /// signing out first.
    /// </summary>
    private static IEnumerable<string> PathFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in new[]
                 {
                     Environment.GetEnvironmentVariable("PATH"),
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
                     Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
                 })
        {
            foreach (var folder in (path ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(folder))
                {
                    yield return folder.Trim('"');
                }
            }
        }
    }
}
