using System.Diagnostics;
using OpenCfDrive.Core.CloudFiles;

namespace OpenCfDrive.Tests.TestSupport;

public static class Prerequisites
{
    public const string WebDavImage = "bytemark/webdav";

    private static readonly Lazy<string> RclonePath = new(FindRclone, isThreadSafe: true);
    private static readonly Lazy<bool> DockerReady = new(ProbeDocker, isThreadSafe: true);
    private static readonly Lazy<bool> CfApiReady = new(
        () => CloudFilesCapability.Current.IsSupported,
        isThreadSafe: true);

    public static string RcloneExe => RclonePath.Value;

    public static bool DockerAvailable => DockerReady.Value;

    public static bool RcloneAvailable => !string.IsNullOrEmpty(RclonePath.Value);

    public static bool CloudFilesAvailable => CfApiReady.Value;

    public static string CloudFilesReason => CloudFilesCapability.Current.Reason;

    public static bool RcloneEnvironmentAvailable => DockerAvailable && RcloneAvailable;

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

    private static bool ProbeDocker()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                ArgumentList = { "version", "--format", "{{.Server.Version}}" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null)
            {
                return false;
            }

            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(15000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
