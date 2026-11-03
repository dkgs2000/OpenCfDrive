using System.Runtime.InteropServices;
using System.Text;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Interop;
using OpenCfDrive.Tests.TestSupport;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// Pins how the engine's placeholder updates behave on the real platform:
/// a server-side change (CloudPlaceholderService.UpdateFromRemote) drops the
/// local copy and the next read fetches the new content; a rename and the
/// identity update after it neither download the file nor clear in-sync.
/// </summary>
public class UpdatePlaceholderContractTest(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpdateFromRemote_DropsTheLocalCopy_AndTheNextReadGetsNewContent(bool hydrateFirst)
    {
        var content = Encoding.UTF8.GetBytes("old-content");

        RunInSyncRoot(() => content, (syncRoot, placeholders, fetches) =>
        {
            var path = Path.Combine(syncRoot, "file.txt");
            if (hydrateFirst)
            {
                Assert.Equal("old-content", File.ReadAllText(path));
                Assert.Equal(1, fetches());

                // An edited file, uploaded and marked in sync again by the
                // engine. Whether the edit cleared in-sync is not relied on:
                // Windows does not do it for every write.
                File.AppendAllText(path, "-edited");
                Assert.True(placeholders.MarkInSync(path, CloudPlaceholderService.BuildFileIdentity("file.txt")), "MarkInSync failed.");
                Assert.True(placeholders.QueryState(path).InSync, "MarkInSync did not set in-sync.");
                Assert.Equal("old-content-edited", File.ReadAllText(path));
            }

            content = Encoding.UTF8.GetBytes("new-content-from-server");
            var before = fetches();
            var hr = placeholders.UpdateFromRemote(path, content.Length, DateTimeOffset.UtcNow);
            output.WriteLine($"UpdateFromRemote -> {HResult.Describe(hr)}, fetches {fetches() - before}");
            Assert.True(CfApi.Succeeded(hr), $"UpdateFromRemote failed with {HResult.Describe(hr)}");
            Assert.Equal(before, fetches());
            Assert.True(placeholders.QueryState(path).InSync, "UpdateFromRemote did not leave the file in sync.");

            Assert.Equal(content.Length, new FileInfo(path).Length);
            Assert.Equal(0, CloudPlaceholderService.GetOnDiskSize(path));
            Assert.Equal("new-content-from-server", File.ReadAllText(path));
        });
    }

    [SkippableFact]
    public void UpdateFromRemote_DiscardingLocalChanges_ReplacesAnEditedFile()
    {
        var content = Encoding.UTF8.GetBytes("old-content");

        RunInSyncRoot(() => content, (syncRoot, placeholders, fetches) =>
        {
            var path = Path.Combine(syncRoot, "file.txt");
            Assert.Equal("old-content", File.ReadAllText(path));
            File.AppendAllText(path, "-edited");

            // Server wins: the edit is dropped (in sync or not, see
            // SizeAndWriteTime_StayAsSynced_UntilTheFileIsEdited) and the
            // server's version is what the next read gets.
            content = Encoding.UTF8.GetBytes("new-content-from-server");
            var hr = placeholders.UpdateFromRemote(path, content.Length, DateTimeOffset.UtcNow, discardLocalChanges: true);
            output.WriteLine($"UpdateFromRemote(discard) on an edited file -> {HResult.Describe(hr)}");
            Assert.True(CfApi.Succeeded(hr), $"UpdateFromRemote failed with {HResult.Describe(hr)}");
            Assert.True(placeholders.QueryState(path).InSync, "The replaced file is not in sync.");
            Assert.Equal(0, CloudPlaceholderService.GetOnDiskSize(path));
            Assert.Equal("new-content-from-server", File.ReadAllText(path));
        });
    }

    /// <summary>
    /// What the engine tells an edit by besides in-sync, which Windows does
    /// not clear for every write (an append starting on a page boundary
    /// leaves it set): the file's size and write time. Downloading the file
    /// and freeing up its space keep both, and a server update sets them to
    /// exactly the server's, which is what the store records.
    /// </summary>
    [SkippableFact]
    public void SizeAndWriteTime_StayAsSynced_UntilTheFileIsEdited()
    {
        var content = Encoding.UTF8.GetBytes("synced-content");

        RunInSyncRoot(() => content, (syncRoot, placeholders, fetches) =>
        {
            var path = Path.Combine(syncRoot, "file.txt");
            var synced = SizeAndWriteTime(path);

            Assert.Equal("synced-content", File.ReadAllText(path));
            Assert.Equal(synced, SizeAndWriteTime(path));

            Assert.True(placeholders.Dehydrate(path), "Freeing up space failed.");
            Assert.Equal(synced, SizeAndWriteTime(path));

            var serverTime = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero).AddTicks(1234567);
            content = Encoding.UTF8.GetBytes("server-version");
            var hr = placeholders.UpdateFromRemote(path, content.Length, serverTime);
            Assert.True(CfApi.Succeeded(hr), $"UpdateFromRemote failed with {HResult.Describe(hr)}");
            Assert.Equal((content.Length, serverTime.UtcDateTime), SizeAndWriteTime(path));

            Assert.Equal("server-version", File.ReadAllText(path));
            Assert.Equal((content.Length, serverTime.UtcDateTime), SizeAndWriteTime(path));

            File.AppendAllText(path, "-edited");
            Assert.NotEqual((content.Length, serverTime.UtcDateTime), SizeAndWriteTime(path));
        });
    }

    private static (long Length, DateTime WriteUtc) SizeAndWriteTime(string path)
    {
        var info = new FileInfo(path);
        return (info.Length, info.LastWriteTimeUtc);
    }

    [SkippableFact]
    public void UpdateFromRemote_OnAPartlyDownloadedFile_DownloadsNothingMore()
    {
        var content = new byte[1024 * 1024];
        Random.Shared.NextBytes(content);
        var limit = long.MaxValue;

        RunInSyncRoot(() => content, deliverAtMost: () => limit, test: (syncRoot, placeholders, fetches) =>
        {
            var path = Path.Combine(syncRoot, "file.txt");

            // The platform asks for whole files, so a partly downloaded one
            // is what a download that broke off leaves: 64 KB, then a failure.
            limit = 64 * 1024;
            Assert.ThrowsAny<IOException>(() => File.ReadAllBytes(path));
            limit = long.MaxValue;

            var onDisk = CloudPlaceholderService.GetOnDiskSize(path);
            output.WriteLine($"after a broken-off download: partial={placeholders.QueryState(path).Partial} on disk={onDisk}");
            Assert.True(placeholders.QueryState(path).Partial, "The whole file was downloaded.");
            Assert.True(onDisk > 0, "Nothing was downloaded.");

            var before = fetches();
            var hr = placeholders.UpdateFromRemote(path, 123, DateTimeOffset.UtcNow);
            output.WriteLine($"UpdateFromRemote -> {HResult.Describe(hr)}, fetches {fetches() - before}");
            Assert.True(CfApi.Succeeded(hr), $"UpdateFromRemote failed with {HResult.Describe(hr)}");
            Assert.Equal(before, fetches());
            Assert.Equal(123, new FileInfo(path).Length);
            Assert.Equal(0, CloudPlaceholderService.GetOnDiskSize(path));
            Assert.True(placeholders.QueryState(path).InSync, "UpdateFromRemote did not leave the file in sync.");
        });
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Rename_ClearsInSync_AndMarkInSyncRestoresItWithoutDownloading(bool hydrateFirst)
    {
        var content = Encoding.UTF8.GetBytes("rename-me");

        RunInSyncRoot(() => content, (syncRoot, placeholders, fetches) =>
        {
            var path = Path.Combine(syncRoot, "file.txt");
            if (hydrateFirst)
            {
                Assert.Equal("rename-me", File.ReadAllText(path));
            }

            Assert.True(placeholders.QueryState(path).InSync, "A new placeholder should be in sync.");
            var before = fetches();

            // The platform clears in-sync on a rename: the provider is meant
            // to carry the rename to the server, then mark the file again.
            var renamed = Path.Combine(syncRoot, "renamed.txt");
            File.Move(path, renamed);
            Assert.False(placeholders.QueryState(renamed).InSync, "A rename no longer clears in-sync.");

            var marked = placeholders.MarkInSync(renamed, CloudPlaceholderService.BuildFileIdentity("renamed.txt"));
            output.WriteLine($"MarkInSync after rename: ok={marked} fetches={fetches() - before}");
            Assert.True(marked, "MarkInSync failed.");
            Assert.True(placeholders.QueryState(renamed).InSync, "MarkInSync did not set in-sync.");

            // A new identity alone (the files inside a renamed folder) keeps
            // in-sync as it is.
            Assert.True(
                placeholders.UpdateFileIdentity(renamed, CloudPlaceholderService.BuildFileIdentity("other.txt")),
                "UpdateFileIdentity failed.");
            Assert.True(placeholders.QueryState(renamed).InSync, "UpdateFileIdentity cleared in-sync.");

            Assert.Equal(before, fetches());
            Assert.Equal(hydrateFirst ? content.Length : 0, CloudPlaceholderService.GetOnDiskSize(renamed));
        });
    }

    /// <summary>
    /// Registers a throwaway sync root with one dehydrated placeholder,
    /// file.txt, whose FETCH_DATA answers with <paramref name="content"/>'s
    /// current value, then runs <paramref name="test"/> with a fetch counter.
    /// With <paramref name="deliverAtMost"/>, a request sends that many bytes
    /// and fails the rest, as a download that broke off.
    /// </summary>
    private static void RunInSyncRoot(
        Func<byte[]> content,
        Action<string, CloudPlaceholderService, Func<int>> test,
        Func<long>? deliverAtMost = null)
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-update", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        Directory.CreateDirectory(syncRoot);

        var fetches = 0;

        var registrar = new SyncRootRegistrar();
        Assert.True(registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid()).Success);

        // Sends exactly the range Windows asks for, so partial reads stay partial.
        var callback = new CF_CALLBACK((infoPtr, parametersPtr) =>
        {
            Interlocked.Increment(ref fetches);
            var info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(infoPtr);
            var fetch = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(parametersPtr).FetchData;
            var current = content();
            var offset = (int)fetch.RequiredFileOffset;
            var length = (int)Math.Min(fetch.RequiredLength, current.Length - offset);
            var sent = (int)Math.Min(length, deliverAtMost?.Invoke() ?? long.MaxValue);
            var buffer = Marshal.AllocHGlobal(Math.Max(length, 1));
            try
            {
                Marshal.Copy(current, offset, buffer, length);
                Transfer(info, buffer, offset, sent, 0);
                if (sent < length)
                {
                    Transfer(info, IntPtr.Zero, offset + sent, length - sent, unchecked((int)0xC0000001));
                }
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
            new CF_CALLBACK_REGISTRATION { Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE },
        };

        Assert.True(CfApi.Succeeded(CfApi.CfConnectSyncRoot(
            syncRoot, table, IntPtr.Zero, CF_CONNECT_FLAGS.CF_CONNECT_FLAG_NONE, out var key)));

        try
        {
            var placeholders = new CloudPlaceholderService();
            var created = placeholders.CreatePlaceholders(syncRoot,
            [
                new RcloneItemMetadata("file.txt", "file.txt", content().Length, DateTimeOffset.UtcNow.AddHours(-1), IsDirectory: false),
            ]);
            Assert.Equal(1, created.Created);

            test(syncRoot, placeholders, () => Volatile.Read(ref fetches));
        }
        finally
        {
            CfApi.CfDisconnectSyncRoot(key);
            registrar.Unregister(syncRoot);
            GC.KeepAlive(callback);

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

    private static void Transfer(CF_CALLBACK_INFO info, IntPtr buffer, long offset, long length, int status)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey,
        };
        var opParams = new CF_OPERATION_PARAMETERS
        {
            ParamSize = 8 + (uint)Marshal.SizeOf<CF_OPERATION_TRANSFER_DATA>(),
            TransferData = new CF_OPERATION_TRANSFER_DATA
            {
                CompletionStatus = status,
                Buffer = buffer,
                Offset = offset,
                Length = length,
            },
        };
        CfApi.CfExecute(ref opInfo, ref opParams);
    }
}
