using System.Runtime.InteropServices;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Service;

/// <summary>
/// The service is a Windows app (no console window), so the installed copy
/// runs in the background. Its output still reaches a redirected stream (the
/// scripts and tests) or the console it was started from; with neither (the
/// Start Menu shortcut, the installer), it goes to a log file.
/// </summary>
internal static class ConsoleOutput
{
    private const int AttachParentProcess = -1;
    private const long MaxLogBytes = 5 * 1024 * 1024;

    /// <summary>%LOCALAPPDATA%\OpenCfDrive\logs\OpenCfDrive.log</summary>
    public static string LogPath =>
        Path.Combine(Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "logs", "OpenCfDrive.log");

    /// <summary>The log file the output goes to, or null when it goes to a console or stream.</summary>
    public static string? ActiveLogPath { get; private set; }

    public static void Connect()
    {
        // Not Console.IsOutputRedirected: a Windows app started without any
        // output handle reports that as redirected, and its output is lost.
        if (HasStandardOutput() || AttachConsole(AttachParentProcess))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
            {
                File.Delete(LogPath);
            }

            var writer = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                AutoFlush = true,
            };
            Console.SetOut(writer);
            Console.SetError(writer);
            ActiveLogPath = LogPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // No log then; the service itself still runs.
        }
    }

    private static bool HasStandardOutput()
    {
        var handle = GetStdHandle(StdOutputHandle);
        return handle != IntPtr.Zero && handle != new IntPtr(-1);
    }

    private const int StdOutputHandle = -11;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int stdHandle);
}
