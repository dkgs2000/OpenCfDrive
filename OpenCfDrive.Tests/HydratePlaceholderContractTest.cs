using System.Runtime.InteropServices;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Interop;
using OpenCfDrive.Tests.TestSupport;

namespace OpenCfDrive.Tests;

/// <summary>
/// "Always keep on this device" downloads a file through CfHydratePlaceholder,
/// with a handle that has no data access. A write handle there is refused
/// while a program has the file open sharing reads only (.NET's
/// File.ReadAllText, among many), and refuses such a program the other way
/// round, which it still did for a moment after the file showed as downloaded.
/// </summary>
public class HydratePlaceholderContractTest
{
    [SkippableFact]
    public void AlwaysKeepOnThisDevice_Downloads_WhileAProgramHasTheFileOpenForReading()
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var content = "kept-on-this-device"u8.ToArray();
        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-hydrate", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        var localPath = Path.Combine(syncRoot, "kept.txt");
        Directory.CreateDirectory(syncRoot);

        var registrar = new SyncRootRegistrar();
        var registration = registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(registration.Success, registration.Message);

        var callback = new CF_CALLBACK((infoPtr, parametersPtr) =>
        {
            var info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(infoPtr);
            var fetch = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(parametersPtr).FetchData;
            var buffer = Marshal.AllocHGlobal(content.Length);
            try
            {
                Marshal.Copy(content, 0, buffer, content.Length);
                var opInfo = new CF_OPERATION_INFO
                {
                    StructSize = 48,
                    Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                    ConnectionKey = info.ConnectionKey,
                    TransferKey = info.TransferKey,
                };
                var opParams = new CF_OPERATION_PARAMETERS
                {
                    ParamSize = 40,
                    TransferData = new CF_OPERATION_TRANSFER_DATA
                    {
                        Buffer = buffer,
                        Offset = fetch.RequiredFileOffset,
                        Length = fetch.RequiredLength,
                    },
                };
                CfApi.CfExecute(ref opInfo, ref opParams);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        });

        var table = new[]
        {
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = Marshal.GetFunctionPointerForDelegate(callback),
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE,
                Callback = IntPtr.Zero,
            },
        };

        var connect = CfApi.CfConnectSyncRoot(
            syncRoot, table, IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out var key);
        Assert.True(CfApi.Succeeded(connect), $"CfConnectSyncRoot failed: 0x{connect:X8}");

        try
        {
            var placeholders = new CloudPlaceholderService();
            var create = placeholders.CreatePlaceholders(
                syncRoot,
                [new RcloneItemMetadata("kept.txt", "kept.txt", content.Length, DateTimeOffset.UtcNow, IsDirectory: false)]);
            Assert.Equal(1, create.Created);

            // Opening reads nothing, so the file is still online-only here.
            using var reader = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var hr = placeholders.Hydrate(localPath);

            Assert.True(CfApi.Succeeded(hr), $"Hydrate failed: 0x{hr:X8}");
            var read = new byte[content.Length];
            reader.ReadExactly(read);
            Assert.Equal(content, read);
        }
        finally
        {
            GC.KeepAlive(callback);
            CfApi.CfDisconnectSyncRoot(key);
            registrar.Unregister(syncRoot);

            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // ignored
            }
        }
    }
}
