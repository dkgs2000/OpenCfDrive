using Microsoft.Win32;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Interop;
using OpenCfDrive.Tests.TestSupport;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// Pins the Explorer (shell) registration done through
/// StorageProviderSyncRootManager against the real platform: the root shows
/// up as a storage provider with our id, name and pinning support, it can be
/// re-registered (upgrade) and unregistered, and a root registered by an
/// earlier version through CfRegisterSyncRoot alone can be upgraded in place.
/// </summary>
public class ShellRegistrationContractTest(ITestOutputHelper output)
{
    [SkippableFact]
    public void Register_AddsTheRootToExplorer_AndUnregisterRemovesIt()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-shell", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        var registrar = new SyncRootRegistrar();
        var id = SyncRootRegistrar.ShellSyncRootId(syncRoot);

        try
        {
            var first = registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid());
            output.WriteLine(first.Message);
            Assert.True(first.Success, first.Message);
            Assert.True(first.ShellIntegrated, first.Message);

            // What Explorer reads: name, icon, the navigation pane CLSID, and
            // which folder belongs to this user.
            using (var key = Registry.LocalMachine.OpenSubKey($@"{SyncRootRegistrar.ShellRegistryKey}\{id}"))
            {
                Assert.NotNull(key);
                Assert.Equal("OpenCfDrive - drive", key.GetValue("DisplayNameResource"));
                Assert.NotNull(key.GetValue("IconResource"));
                Assert.NotNull(key.GetValue("NamespaceCLSID"));
                output.WriteLine($"Flags=0x{key.GetValue("Flags"):X}");

                using var users = key.OpenSubKey("UserSyncRoots");
                Assert.NotNull(users);
                Assert.Contains(users.GetValueNames(), n => string.Equals(
                    users.GetValue(n) as string, Path.GetFullPath(syncRoot), StringComparison.OrdinalIgnoreCase));
            }

            Assert.True(IsSyncRoot(syncRoot), "The folder is not a Cloud Files sync root.");

            // A restart/upgrade registers the same id again.
            var again = registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid());
            Assert.True(again.ShellIntegrated, again.Message);

            var removed = registrar.Unregister(syncRoot);
            output.WriteLine(removed.Message);
            Assert.True(removed.Success, removed.Message);
            Assert.True(removed.ShellIntegrated, removed.Message);
            Assert.False(SyncRootRegistrar.IsShellRegistered(id), "The Explorer entry is still there after unregistering.");
            Assert.False(IsSyncRoot(syncRoot), "The driver registration is still there after unregistering.");
        }
        finally
        {
            registrar.Unregister(syncRoot);
            TryDelete(root);
        }
    }

    [SkippableFact]
    public void Register_UpgradesARootRegisteredByAnEarlierVersion()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-shell", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        Directory.CreateDirectory(syncRoot);
        var registrar = new SyncRootRegistrar();

        try
        {
            var old = registrar.RegisterWithDriverOnly(syncRoot, Guid.NewGuid(), Guid.NewGuid());
            Assert.True(old.Success, old.Message);
            Assert.False(SyncRootRegistrar.IsShellRegistered(SyncRootRegistrar.ShellSyncRootId(syncRoot)));

            var upgraded = registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid());
            output.WriteLine(upgraded.Message);
            Assert.True(upgraded.ShellIntegrated, upgraded.Message);
            Assert.True(SyncRootRegistrar.IsShellRegistered(SyncRootRegistrar.ShellSyncRootId(syncRoot)));
        }
        finally
        {
            registrar.Unregister(syncRoot);
            TryDelete(root);
        }
    }

    /// <summary>
    /// Asks the Cloud Files driver directly. Placeholder attributes are not
    /// usable here: Windows masks them for processes outside the system
    /// directories (see README, placeholder compatibility mode).
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unregister_RemovesPlaceholders_SoTheFolderCanBeDeleted(bool orphanedFirst)
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-shell", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        var registrar = new SyncRootRegistrar();

        try
        {
            Assert.True(registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid()).ShellIntegrated);
            var created = new CloudPlaceholderService().CreatePlaceholders(syncRoot,
            [
                new RcloneItemMetadata("d/", "d", 0, DateTimeOffset.UtcNow, IsDirectory: true),
                new RcloneItemMetadata("d/g.txt", "d/g.txt", 5, DateTimeOffset.UtcNow, IsDirectory: false),
                new RcloneItemMetadata("f.txt", "f.txt", 5, DateTimeOffset.UtcNow, IsDirectory: false),
            ]);
            Assert.Equal(3, created.Created);

            if (orphanedFirst)
            {
                // What an earlier version did: shell unregister only, which
                // leaves orphaned placeholders that cannot even be deleted.
                Windows.Storage.Provider.StorageProviderSyncRootManager.Unregister(SyncRootRegistrar.ShellSyncRootId(syncRoot));
                Assert.False(IsSyncRoot(syncRoot));
            }

            var removed = registrar.Unregister(syncRoot);
            output.WriteLine(removed.Message);

            Assert.False(IsSyncRoot(syncRoot));
            Assert.False(SyncRootRegistrar.IsShellRegistered(SyncRootRegistrar.ShellSyncRootId(syncRoot)));
            Directory.Delete(syncRoot, recursive: true);
            Assert.False(Directory.Exists(syncRoot));
        }
        finally
        {
            registrar.Unregister(syncRoot);
            TryDelete(root);
        }
    }

    private static bool IsSyncRoot(string path) =>
        CfApi.Succeeded(CfApi.CfGetSyncRootInfoByPath(path, 0, out _, sizeof(long), out _));

    private static void TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // ignored
        }
    }
}
