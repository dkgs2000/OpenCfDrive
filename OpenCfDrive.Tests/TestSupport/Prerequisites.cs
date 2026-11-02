using OpenCfDrive.Core.CloudFiles;

namespace OpenCfDrive.Tests.TestSupport;

public static class Prerequisites
{
    private static readonly Lazy<string> RclonePath = new(FindRclone, isThreadSafe: true);
    private static readonly Lazy<bool> CfApiReady = new(
        () => CloudFilesCapability.Current.IsSupported,
        isThreadSafe: true);

    public static string RcloneExe => RclonePath.Value;

    public static bool RcloneAvailable => !string.IsNullOrEmpty(RclonePath.Value);

    public static bool CloudFilesAvailable => CfApiReady.Value;

    public static string CloudFilesReason => CloudFilesCapability.Current.Reason;

    /// <summary>
    /// The rclone contract tests need only rclone: it is both the client's
    /// rclone and, as a separate process, the WebDAV server.
    /// </summary>
    public static bool RcloneEnvironmentAvailable => RcloneAvailable;

    private static string FindRclone()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "rclone.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            candidate = Path.Combine(dir.FullName, "rclone.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        // Otherwise an rclone installed the usual way, on PATH.
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
        {
            try
            {
                candidate = Path.Combine(folder.Trim(), "rclone.exe");
                if (folder.Length > 0 && File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // malformed PATH entry
            }
        }

        return string.Empty;
    }
}
