using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.App;

/// <summary>Unexpected errors go to %LOCALAPPDATA%\OpenCfDrive\logs\OpenCfDrive.App.log.</summary>
internal static class CrashLog
{
    public static string Path =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(SyncStatePaths.StateRoot)!, "logs", "OpenCfDrive.App.log");

    public static void Write(Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.AppendAllText(Path, $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nowhere to report it.
        }
    }
}
