using System.Runtime.InteropServices;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Interop;
using OpenCfDrive.Tests.TestSupport;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// A placeholder edited in place to a new size, uploaded and freed must
/// download the whole new file when it is opened again. The platform keeps
/// its own record of a placeholder's size, which a write does not change:
/// marking the file in sync without the new size leaves the old one, and the
/// next download of a file the edit made bigger fills only that much (the
/// rest reads as zeros).
/// </summary>
public class EditedPlaceholderContractTest(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData("123", "123456")]
    [InlineData("123456", "123")]
    public void AnEditThatChangesTheSize_IsDownloadedWhole_WhenTheUploadGivesTheSize(string original, string edited)
    {
        var back = EditUploadFreeAndOpen(original, edited, sizeOnUpload: true, sizeOnFree: false);
        Assert.Equal(edited, System.Text.Encoding.UTF8.GetString(back));
    }

    [SkippableFact]
    public void FreeUpSpace_GivingTheSize_MendsAPlaceholderUploadedWithout()
    {
        var back = EditUploadFreeAndOpen("123", "123456", sizeOnUpload: false, sizeOnFree: true);
        Assert.Equal("123456", System.Text.Encoding.UTF8.GetString(back));
    }

    [SkippableFact]
    public void WithoutTheNewSize_TheOldSizeIsDownloaded()
    {
        // What the drive did before: "123" edited to "123456" came back as
        // "123" and three zero bytes, which Notepad shows as another encoding.
        var back = EditUploadFreeAndOpen("123", "123456", sizeOnUpload: false, sizeOnFree: false);
        Assert.Equal("313233000000", Convert.ToHexString(back));
    }

    private byte[] EditUploadFreeAndOpen(string original, string edited, bool sizeOnUpload, bool sizeOnFree)
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var server = System.Text.Encoding.UTF8.GetBytes(original);
        var requests = new List<string>();

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-edited", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        Directory.CreateDirectory(syncRoot);

        var registrar = new SyncRootRegistrar();
        var registration = registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(registration.Success, registration.Message);

        // Serves what the server holds, as the service does: the range asked for.
        var callback = new CF_CALLBACK((infoPtr, parametersPtr) =>
        {
            var info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(infoPtr);
            var fetch = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(parametersPtr).FetchData;
            var content = server;
            lock (requests)
            {
                requests.Add($"FETCH_DATA {fetch.RequiredFileOffset}+{fetch.RequiredLength} (server has {content.Length})");
            }

            var available = Math.Max(0, Math.Min(fetch.RequiredLength, content.Length - fetch.RequiredFileOffset));
            var buffer = Marshal.AllocHGlobal((int)Math.Max(1, available));
            try
            {
                Marshal.Copy(content, (int)fetch.RequiredFileOffset, buffer, (int)available);
                Execute(info, available == fetch.RequiredLength ? buffer : IntPtr.Zero, fetch.RequiredFileOffset, fetch.RequiredLength,
                    available == fetch.RequiredLength ? 0 : StatusUnsuccessful);
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
            var localPath = Path.Combine(syncRoot, "test.txt");
            var identity = CloudPlaceholderService.BuildFileIdentity("test.txt");
            var create = placeholders.CreatePlaceholders(
                syncRoot,
                [new RcloneItemMetadata("test.txt", "test.txt", server.Length, DateTimeOffset.UtcNow, IsDirectory: false)]);
            Assert.Equal(1, create.Created);

            Assert.Equal(server, File.ReadAllBytes(localPath));

            // Notepad's save: the same file, truncated and written. Not
            // File.WriteAllBytes, which replaces the file with a plain one.
            var edit = System.Text.Encoding.UTF8.GetBytes(edited);
            using (var stream = new FileStream(localPath, FileMode.Open, FileAccess.Write))
            {
                stream.SetLength(0);
                stream.Write(edit);
            }

            Assert.True(placeholders.QueryState(localPath).IsPlaceholder, "The edit replaced the placeholder.");

            // The upload: the server now has the edit, and the file is in sync.
            server = edit;
            Assert.True(placeholders.MarkInSync(localPath, identity, sizeOnUpload ? edit.Length : null), "MarkInSync failed.");
            Assert.True(placeholders.Dehydrate(localPath, sizeOnFree ? edit.Length : null), "Dehydrate failed.");

            var back = File.ReadAllBytes(localPath);
            foreach (var request in requests)
            {
                output.WriteLine(request);
            }

            output.WriteLine($"read back: {Convert.ToHexString(back)}");
            return back;
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

    private const int StatusUnsuccessful = unchecked((int)0xC0000001);

    private static int Execute(CF_CALLBACK_INFO info, IntPtr buffer, long offset, long length, int status)
    {
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
                CompletionStatus = status,
                Buffer = buffer,
                Offset = offset,
                Length = length,
            },
        };

        return CfApi.CfExecute(ref opInfo, ref opParams);
    }
}
