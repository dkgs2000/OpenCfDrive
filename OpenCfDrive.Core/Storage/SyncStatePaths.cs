using System.Security.Cryptography;
using System.Text;

namespace OpenCfDrive.Core.Storage;

/// <summary>
/// Single source of truth for where engine state (SQLite metadata/config)
/// lives. State is never written inside the sync root: that path is user data
/// and is watched for uploads. Each sync root gets its own state database,
/// keyed by a stable hash of its full path.
/// </summary>
public static class SyncStatePaths
{
    /// <summary>The sync root when none is configured: %USERPROFILE%\OpenCfDrive.</summary>
    public static string DefaultSyncRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "OpenCfDrive");

    /// <summary>Per-user state directory, e.g. %LOCALAPPDATA%\OpenCfDrive\state.</summary>
    public static string StateRoot
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                return FallbackRoot;
            }

            return Path.Combine(localAppData, "OpenCfDrive", "state");
        }
    }

    /// <summary>Used only when the per-user state directory cannot be written.</summary>
    public static string FallbackRoot => Path.Combine(Path.GetTempPath(), "OpenCfDrive", "state");

    public static string DatabasePath(string syncRoot) =>
        Path.Combine(StateRoot, FileNameFor(syncRoot));

    public static string FallbackDatabasePath(string syncRoot) =>
        Path.Combine(FallbackRoot, FileNameFor(syncRoot));

    /// <summary>Accepts "e2e" or "e2e:" and always returns a valid rclone spec.</summary>
    public static string NormalizeRemote(string? remote)
    {
        var value = string.IsNullOrWhiteSpace(remote) ? "e2e:" : remote.Trim();
        var colon = value.IndexOf(':');
        if (colon < 0)
        {
            return value + ":";
        }

        // "e2e:sub/dir" keeps its sub path; only trailing slashes are removed.
        return value[..(colon + 1)] + value[(colon + 1)..].Trim('/');
    }

    private static string FileNameFor(string syncRoot)
    {
        var full = Path.GetFullPath(syncRoot);
        full = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Windows paths are case-insensitive; hash the normalized lower-case form
        // so "C:\Foo" and "c:\foo" map to the same state database.
        var bytes = Encoding.UTF8.GetBytes(full.ToLowerInvariant());
        var hash = Convert.ToHexString(SHA256.HashData(bytes))[..16].ToLowerInvariant();
        return $"opencfdrive-{hash}.db";
    }
}
