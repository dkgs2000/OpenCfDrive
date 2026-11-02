using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using OpenCfDrive.Core.Interop;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// Implements Pillar 1: register the local folder as a Windows sync root so
/// Explorer treats it as a cloud provider namespace.
/// </summary>
public sealed class SyncRootRegistrar
{
    public const string ProviderName = "OpenCfDrive";
    public const string ProviderVersion = "0.1.0";

    private const int HRESULT_ALREADY_EXISTS = unchecked((int)0x800700B7);

    /// <summary>
    /// Returned by CfRegisterSyncRoot when the root is already registered by a
    /// different process/session: a plain re-registration is not allowed, only
    /// an update of the existing registration is.
    /// </summary>
    private const int HRESULT_INVALID_ARG = unchecked((int)0x80070057);

    public sealed record RegistrationResult(bool Success, int HResult, string Message, bool ShellIntegrated = false);

    /// <summary>
    /// Registers the sync root through StorageProviderSyncRootManager, which
    /// does the Cloud Files driver registration (what CfRegisterSyncRoot does)
    /// and also registers the root with Explorer: a navigation pane entry with
    /// name and icon, the "Always keep on this device" / "Free up space" menu,
    /// sync status, and Storage Sense. If the shell registration is not
    /// available, it falls back to CfRegisterSyncRoot so syncing still works.
    /// </summary>
    public RegistrationResult Register(
        string syncRootPath,
        Guid syncRootId,
        Guid providerId)
    {
        Directory.CreateDirectory(syncRootPath);

        var support = CloudFilesCapability.Current;
        if (!support.IsSupported)
        {
            return new RegistrationResult(false, -1, support.Reason);
        }

        var shellId = ShellSyncRootId(syncRootPath);
        try
        {
            RegisterWithShell(syncRootPath, shellId, BuildIdentity(syncRootId, providerId));
            return new RegistrationResult(
                true,
                0,
                $"Registered sync root '{syncRootPath}' with Explorer as '{shellId}'.",
                ShellIntegrated: true);
        }
        catch (Exception ex)
        {
            var fallback = RegisterWithDriverOnly(syncRootPath, syncRootId, providerId);
            return fallback with
            {
                Message = $"{fallback.Message} Explorer integration unavailable " +
                          $"(StorageProviderSyncRootManager.Register: {ex.Message.Trim()}).",
            };
        }
    }

    /// <summary>
    /// The shell's id for a sync root: "provider!user SID!account". The
    /// account part is a hash of the folder path, so every sync root (the
    /// user's drive, a test root under %TEMP%) has its own registration and
    /// one can never replace another.
    /// </summary>
    public static string ShellSyncRootId(string syncRootPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(syncRootPath)).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16].ToLowerInvariant();
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? "S-0";
        return $"{ProviderName}!{sid}!{hash}";
    }

    private static void RegisterWithShell(string syncRootPath, string shellId, byte[] context)
    {
        var folder = StorageFolder.GetFolderFromPathAsync(Path.GetFullPath(syncRootPath)).AsTask().GetAwaiter().GetResult();

        var info = new StorageProviderSyncRootInfo
        {
            Id = shellId,
            Path = folder,
            DisplayNameResource = DisplayNameFor(syncRootPath),
            // The provider's own executable icon; set ApplicationIcon in the
            // service project to brand it.
            IconResource = $"{Environment.ProcessPath ?? "%SystemRoot%\\system32\\imageres.dll"},0",
            // Same policies as the CfRegisterSyncRoot fallback below; see the
            // comments there for why each one is set this way.
            HydrationPolicy = StorageProviderHydrationPolicy.Partial,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.None,
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = StorageProviderInSyncPolicy.FileLastWriteTime,
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            ShowSiblingsAsGroup = false,
            Version = ProviderVersion,
            // Enables "Always keep on this device" / "Free up space"; the
            // engine turns those attribute changes into hydrate/dehydrate.
            AllowPinning = true,
            Context = CryptographicBuffer.CreateFromByteArray(context),
        };

        // Registering an id that already exists updates it in place, which
        // also re-applies changed policies after an upgrade.
        StorageProviderSyncRootManager.Register(info);
    }

    /// <summary>Where Explorer keeps registered sync roots (HKLM), one key per id.</summary>
    public const string ShellRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\SyncRootManager";

    public static bool IsShellRegistered(string shellId)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"{ShellRegistryKey}\{shellId}");
        return key is not null;
    }

    private static string DisplayNameFor(string syncRootPath)
    {
        // The default folder (%USERPROFILE%\OpenCfDrive) shows as just the
        // provider name; any other folder adds its own name.
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(syncRootPath)));
        return string.Equals(name, ProviderName, StringComparison.OrdinalIgnoreCase)
            ? ProviderName
            : $"{ProviderName} - {name}";
    }

    internal RegistrationResult RegisterWithDriverOnly(
        string syncRootPath,
        Guid syncRootId,
        Guid providerId)
    {
        var identity = BuildIdentity(syncRootId, providerId);
        var identityPtr = Marshal.AllocHGlobal(identity.Length);
        var fileIdentity = BuildFileIdentity(providerId);
        var fileIdentityPtr = Marshal.AllocHGlobal(fileIdentity.Length);

        try
        {
            Marshal.Copy(identity, 0, identityPtr, identity.Length);
            Marshal.Copy(fileIdentity, 0, fileIdentityPtr, fileIdentity.Length);

            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)Marshal.SizeOf<CF_SYNC_REGISTRATION>(),
                ProviderName = ProviderName,
                ProviderVersion = ProviderVersion,
                SyncRootIdentity = identityPtr,
                SyncRootIdentityLength = (uint)identity.Length,
                FileIdentity = fileIdentityPtr,
                FileIdentityLength = (uint)fileIdentity.Length,
            };

            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)Marshal.SizeOf<CF_SYNC_POLICIES>(),
                // Partial hydration hydrates on demand (including byte ranges),
                // and the platform persists what the provider transfers. The
                // STREAMING_ALLOWED modifier must NOT be set here: it makes the
                // platform treat hydrated bytes as streamed, discard them when
                // the handle closes, and keep RECALL_ON_DATA_ACCESS set - which
                // breaks subsequent reads and every write with
                // ERROR_CLOUD_FILE_REQUEST_TIMEOUT.
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = (ushort)CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_PARTIAL,
                    Modifier = (ushort)CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_NONE,
                },
                // FULL: before a directory enumeration completes, the platform
                // asks the provider (FETCH_PLACEHOLDERS) for the directory's
                // contents. SyncRootConnection answers from a live rclone
                // listing and never marks the directory as fully populated, so
                // every Explorer listing picks up files added on the server.
                // (ALWAYS_FULL would never ask, and the namespace would stay
                // frozen at whatever PopulateAsync saw at startup.)
                Population = new CF_POPULATION_POLICY
                {
                    Primary = (ushort)CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL,
                    Modifier = (ushort)CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE,
                },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_TRACK_FILE_LAST_WRITE_TIME,
                HardLink = CF_HARDLINK_POLICY.CF_HARDLINK_POLICY_NONE,
            };

            // On-demand population stays enabled on the root too, so listing
            // the root itself also refreshes from the remote. This requires
            // the FETCH_PLACEHOLDERS handler to be connected: without one the
            // shell would block for the 60 s cfapi timeout on every listing.
            var flags = CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE;
            var hr = CfApi.CfRegisterSyncRoot(
                syncRootPath,
                ref registration,
                ref policies,
                flags);

            // A root that is already registered must be updated instead: a
            // plain CfRegisterSyncRoot returns ERROR_ALREADY_EXISTS when the
            // same process registered it, and E_INVALIDARG when a previous
            // process/session did. CF_REGISTER_FLAG_UPDATE re-applies the
            // current identities, policies and root flags, which is required
            // after an upgrade changes them. (Unregistering is not an option:
            // CfUnregisterSyncRoot permanently deletes every dehydrated
            // placeholder in the tree.)
            var updated = false;
            if (hr == HRESULT_ALREADY_EXISTS || hr == HRESULT_INVALID_ARG)
            {
                hr = CfApi.CfRegisterSyncRoot(
                    syncRootPath,
                    ref registration,
                    ref policies,
                    flags | CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE);
                updated = CfApi.Succeeded(hr);
            }

            var success = CfApi.Succeeded(hr);
            var message = success
                ? updated
                    ? $"Updated sync root policies for '{syncRootPath}'."
                    : $"Registered sync root '{syncRootPath}'."
                : $"CfRegisterSyncRoot failed with {HResult.Describe(hr)}.";

            return new RegistrationResult(success, hr, message);
        }
        finally
        {
            Marshal.FreeHGlobal(identityPtr);
            Marshal.FreeHGlobal(fileIdentityPtr);
        }
    }

    public RegistrationResult Unregister(string syncRootPath)
    {
        if (!CloudFilesCapability.Current.IsSupported)
        {
            return new RegistrationResult(false, -1, CloudFilesCapability.Current.Reason);
        }

        // Unregistering must leave a folder the user can use and delete.
        //  1. CfUnregisterSyncRoot removes the driver registration and turns
        //     downloaded placeholders into normal files.
        //  2. The Explorer entry is removed by its path-derived id
        //     (GetSyncRootInformationForFolder throws for these roots and
        //     GetCurrentSyncRoots does not list them).
        //  3. Neither step reliably removes placeholders that only exist on the
        //     server (no local data): left behind, they can no longer be
        //     opened or even deleted ("The cloud file provider exited
        //     unexpectedly"). Earlier versions left such orphans too. They can
        //     only be deleted while the folder is a sync root *and* a provider
        //     acknowledges the delete, so the folder is registered again for a
        //     moment with a throwaway connection that only acknowledges. It
        //     is not the sync engine, so nothing reaches a server.
        var shellId = ShellSyncRootId(syncRootPath);
        var shellRegistered = IsShellRegistered(shellId);

        var hr = CfApi.CfUnregisterSyncRoot(syncRootPath);
        var wasRegistered = CfApi.Succeeded(hr) || shellRegistered;

        var shellRemoved = false;
        if (shellRegistered)
        {
            try
            {
                StorageProviderSyncRootManager.Unregister(shellId);
                shellRemoved = true;
            }
            catch (Exception)
            {
                shellRemoved = !IsShellRegistered(shellId);
            }
        }

        var cleaned = 0;
        if (Directory.Exists(syncRootPath) && HasPlaceholdersWithoutLocalData(syncRootPath) &&
            RegisterWithDriverOnly(syncRootPath, Guid.NewGuid(), Guid.NewGuid()).Success)
        {
            cleaned = DeletePlaceholdersWithoutLocalData(syncRootPath);
            CfApi.CfUnregisterSyncRoot(syncRootPath);
        }

        var success = wasRegistered && (CfApi.Succeeded(hr) || shellRemoved);
        var message = wasRegistered
            ? $"Unregistered sync root '{syncRootPath}'" + (shellRemoved ? $" ('{shellId}')" : string.Empty) + "."
            : $"'{syncRootPath}' was not registered.";
        if (cleaned > 0)
        {
            message += $" Removed {cleaned} online-only placeholder(s) left in the folder.";
        }

        return new RegistrationResult(success, hr, message, ShellIntegrated: shellRemoved);
    }

    private const System.IO.FileAttributes RecallOnDataAccessAttribute = (System.IO.FileAttributes)0x00400000;

    private static bool IsWithoutLocalData(string file) =>
        (File.GetAttributes(file) & (RecallOnDataAccessAttribute | System.IO.FileAttributes.Offline)) != 0;

    private static bool HasPlaceholdersWithoutLocalData(string syncRootPath)
    {
        try
        {
            return Directory.EnumerateFiles(syncRootPath, "*", SearchOption.AllDirectories).Any(IsWithoutLocalData);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static int DeletePlaceholdersWithoutLocalData(string syncRootPath)
    {
        var deleted = 0;
        CF_CALLBACK acknowledgeDelete = (infoPtr, _) =>
        {
            try
            {
                var info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(infoPtr);
                var opInfo = new CF_OPERATION_INFO
                {
                    StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                    Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
                    ConnectionKey = info.ConnectionKey,
                    TransferKey = info.TransferKey,
                };
                var opParams = new CF_OPERATION_PARAMETERS_ACK { ParamSize = 16 };
                CfApi.CfExecute(ref opInfo, ref opParams);
            }
            catch
            {
                // never throw across the native boundary
            }
        };

        var table = new[]
        {
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE,
                Callback = Marshal.GetFunctionPointerForDelegate(acknowledgeDelete),
            },
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE },
        };

        var connected = CfApi.Succeeded(CfApi.CfConnectSyncRoot(
            syncRootPath, table, IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out var key));
        try
        {
            foreach (var file in Directory.EnumerateFiles(syncRootPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    if (IsWithoutLocalData(file))
                    {
                        File.Delete(file);
                        deleted++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort; CfUnregisterSyncRoot gets the next chance.
                }
            }
        }
        finally
        {
            if (connected)
            {
                CfApi.CfDisconnectSyncRoot(key);
            }

            GC.KeepAlive(acknowledgeDelete);
        }

        return deleted;
    }


    private static byte[] BuildIdentity(Guid syncRootId, Guid providerId) =>
        Encoding.UTF8.GetBytes($"opencfdrive:{providerId:D}:{syncRootId:D}");

    private static byte[] BuildFileIdentity(Guid providerId) =>
        Encoding.UTF8.GetBytes($"opencfdrive-provider:{providerId:D}");
}
