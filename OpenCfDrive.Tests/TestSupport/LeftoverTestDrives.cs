using System.Runtime.CompilerServices;
using System.Security.Principal;
using Microsoft.Win32;
using OpenCfDrive.Core.CloudFiles;

namespace OpenCfDrive.Tests.TestSupport;

/// <summary>
/// Removes the test drives that earlier runs left registered with Windows,
/// before any test runs. A test host that crashed never reaches its cleanup,
/// and Windows now and then keeps a drive's Explorer entry after it was
/// removed. Left alone, they pile up as "OpenCfDrive - drive" entries.
/// Only the folders the contract tests make count
/// (%TEMP%\opencfdrive-&lt;test&gt;\&lt;guid&gt;), and only once they are gone or
/// untouched for an hour: another test run on this PC may be using newer
/// ones. The end-to-end suite's drives and the user's own are never touched.
/// </summary>
internal static class LeftoverTestDrives
{
    private static readonly TimeSpan MinimumAge = TimeSpan.FromHours(1);

    [ModuleInitializer]
#pragma warning disable CA2255 // A test assembly, run once before its tests: the point is to run early.
    internal static void RemoveBeforeTheTests()
#pragma warning restore CA2255
    {
        try
        {
            Remove(DateTime.UtcNow);
        }
        catch (Exception)
        {
            // Best effort: a cleanup problem must not stop the tests.
        }
    }

    internal static int Remove(DateTime nowUtc)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return 0;
        }

        var removed = 0;
        var temp = Path.GetTempPath();
        foreach (var (shellId, path) in RegisteredDrives())
        {
            var folder = TestFolderOf(path, temp);
            if (folder is null ||
                (Directory.Exists(folder) && nowUtc - Directory.GetLastWriteTimeUtc(folder) < MinimumAge))
            {
                continue;
            }

            new SyncRootRegistrar().Unregister(path);
            // Unregister finds the entry by an id made from the path, which
            // a path given another way (\\?\ for instance) changes.
            if (SyncRootRegistrar.IsShellRegistered(shellId))
            {
                SyncRootRegistrar.UnregisterFromShell(shellId);
            }

            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
                // Still in use: it goes on a later run.
            }

            removed += SyncRootRegistrar.IsShellRegistered(shellId) ? 0 : 1;
        }

        return removed;
    }

    /// <summary>
    /// The test folder (%TEMP%\opencfdrive-&lt;test&gt;\&lt;guid&gt;) a registered
    /// drive is in, or null for any other drive.
    /// </summary>
    internal static string? TestFolderOf(string syncRootPath, string tempPath)
    {
        var path = syncRootPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? syncRootPath[4..] : syncRootPath;
        var temp = Path.TrimEndingDirectorySeparator(tempPath) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(temp, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = path[temp.Length..].Split(Path.DirectorySeparatorChar);
        return parts.Length >= 2 &&
               parts[0].StartsWith("opencfdrive-", StringComparison.OrdinalIgnoreCase) &&
               !parts[0].StartsWith("opencfdrive-e2e", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParseExact(parts[1], "N", out _)
            ? Path.Combine(temp, parts[0], parts[1])
            : null;
    }

    /// <summary>This user's OpenCfDrive drives, as Explorer has them registered.</summary>
    private static IEnumerable<(string ShellId, string Path)> RegisteredDrives()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value;
        using var key = Registry.LocalMachine.OpenSubKey(SyncRootRegistrar.ShellRegistryKey);
        if (sid is null || key is null)
        {
            yield break;
        }

        var prefix = $"{SyncRootRegistrar.ProviderName}!{sid}!";
        foreach (var shellId in key.GetSubKeyNames().Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            using var roots = key.OpenSubKey($@"{shellId}\UserSyncRoots");
            if (roots?.GetValue(sid) is string path)
            {
                yield return (shellId, path);
            }
        }
    }
}
