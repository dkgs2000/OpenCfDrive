using System.Runtime.InteropServices;
using System.Text;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Interop;
using OpenCfDrive.Tests.TestSupport;
using Xunit.Abstractions;

namespace OpenCfDrive.Tests;

/// <summary>
/// Pins the CF_OPERATION_INFO/CF_OPERATION_PARAMETERS contract that CfExecute
/// enforces. Getting it wrong returns ERROR_CLOUD_FILE_INVALID_REQUEST
/// (0x8007017C) and every hydration fails, so these run against the real
/// platform rather than a mocked expectation.
/// </summary>
public class CfExecuteContractTest(ITestOutputHelper output)
{
    [SkippableFact]
    public void TransferData_AcceptsTheDocumentedStructSizes()
    {
        var content = "probe"u8.ToArray();
        var results = new List<string>();

        var bytes = RunScenario("probe.txt", content, (info, _, buffer) =>
        {
            foreach (var (structSize, paramSize) in new (uint, uint)[]
                     {
                         (48, 40), (48, 48), (32, 40), (32, 48), (40, 40), (56, 56),
                     })
            {
                var hr = Execute(info, buffer, 0, content.Length, structSize, paramSize);
                results.Add($"StructSize={structSize} ParamSize={paramSize} -> 0x{hr:X8}");
                if (hr >= 0)
                {
                    return;
                }
            }
        });

        foreach (var result in results)
        {
            output.WriteLine(result);
        }

        Assert.Equal(content, bytes);
    }

    [SkippableFact]
    public void TransferData_RejectsAZeroLengthCompletionCall()
    {
        var content = new byte[300];
        Random.Shared.NextBytes(content);
        var results = new List<string>();

        // There is no "done" call: a zero-length TRANSFER_DATA after the
        // range has arrived is rejected and must not be sent.
        var bytes = RunScenario("single.bin", content, (info, _, buffer) =>
        {
            var transfer = Execute(info, buffer, 0, content.Length, 48, 40);
            results.Add($"transfer offset=0 length={content.Length} -> 0x{transfer:X8}");

            var completion = Execute(info, IntPtr.Zero, content.Length, 0, 48, 40);
            results.Add($"zero-length completion -> 0x{completion:X8}");
        });

        foreach (var result in results)
        {
            output.WriteLine(result);
        }

        Assert.Equal(content, bytes);
    }

    [SkippableFact]
    public void TransferData_AcceptsARangeInAlignedChunks()
    {
        // 1 MiB plus an unaligned tail, sent 64 KiB at a time: every chunk
        // starts on a 4 KiB boundary and only the last one ends off it (at EOF).
        const int chunk = 64 * 1024;
        var content = new byte[1024 * 1024 + 300];
        Random.Shared.NextBytes(content);
        var results = new List<string>();
        var failures = 0;
        var maxCallsPerCallback = 0;

        var bytes = RunScenario("chunked.bin", content, (info, fetch, buffer) =>
        {
            var start = fetch.RequiredFileOffset;
            var end = Math.Min(content.Length, fetch.RequiredFileOffset + fetch.RequiredLength);
            results.Add($"FETCH_DATA required={fetch.RequiredFileOffset}+{fetch.RequiredLength}");

            var calls = 0;
            for (var offset = start; offset < end; offset += chunk)
            {
                var length = Math.Min(chunk, end - offset);
                var hr = Execute(info, buffer + (int)offset, offset, length, 48, 40);
                calls++;
                if (hr < 0)
                {
                    failures++;
                    results.Add($"  chunk {offset}+{length} -> 0x{hr:X8}");
                    return;
                }
            }

            maxCallsPerCallback = Math.Max(maxCallsPerCallback, calls);
            results.Add($"  {calls} chunk(s) accepted");
        });

        foreach (var result in results)
        {
            output.WriteLine(result);
        }

        Assert.Equal(0, failures);
        Assert.True(maxCallsPerCallback > 1, "No callback was answered in more than one chunk.");
        Assert.Equal(content, bytes);
    }

    [SkippableTheory]
    [InlineData(0)]
    [InlineData(1)]
    public void TransferData_FailsTheReadAtTheUndeliveredRange(int chunksDelivered)
    {
        // A failure is reported for the bytes that did not arrive: the offset
        // where delivery stopped and the rest of the required range. A
        // zero-length failure is rejected (0x8007017C) and the reading
        // program then waits for the platform timeout instead.
        const int chunk = 64 * 1024;
        var content = new byte[4 * chunk];
        Random.Shared.NextBytes(content);
        var results = new List<string>();
        var rejected = 0;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var error = Record.Exception(() => RunScenario("broken.bin", content, (info, fetch, buffer) =>
        {
            var delivered = 0L;
            for (var i = 0; i < chunksDelivered; i++)
            {
                var hr = Execute(info, buffer + (int)delivered, fetch.RequiredFileOffset + delivered, chunk, 48, 40);
                results.Add($"chunk {i} -> 0x{hr:X8}");
                delivered += chunk;
            }

            var failure = Execute(
                info,
                IntPtr.Zero,
                fetch.RequiredFileOffset + delivered,
                fetch.RequiredLength - delivered,
                48,
                40,
                StatusUnsuccessful);
            results.Add($"failure -> 0x{failure:X8}");
            if (failure < 0)
            {
                rejected++;
            }
        }));

        foreach (var result in results)
        {
            output.WriteLine(result);
        }

        output.WriteLine($"read ended after {clock.ElapsedMilliseconds} ms: {error?.GetType().Name} {error?.Message}");
        Assert.Equal(0, rejected);
        Assert.NotNull(error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "The read waited for the platform timeout.");
    }

    private const int StatusUnsuccessful = unchecked((int)0xC0000001);

    private byte[] RunScenario(
        string fileName,
        byte[] content,
        Action<CF_CALLBACK_INFO, CF_CALLBACK_FETCH_DATA_PARAMETERS, IntPtr> transfer)
    {
        Skip.IfNot(
            Prerequisites.CloudFilesAvailable,
            $"Cloud Files API unavailable on this host: {Prerequisites.CloudFilesReason}");

        var root = Path.Combine(Path.GetTempPath(), "opencfdrive-cfexec", Guid.NewGuid().ToString("N"));
        var syncRoot = Path.Combine(root, "drive");
        Directory.CreateDirectory(syncRoot);

        var registrar = new SyncRootRegistrar();
        var registration = registrar.Register(syncRoot, Guid.NewGuid(), Guid.NewGuid());
        Assert.True(registration.Success, registration.Message);

        var callback = new CF_CALLBACK((infoPtr, parametersPtr) =>
        {
            var info = Marshal.PtrToStructure<CF_CALLBACK_INFO>(infoPtr);
            var parameters = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS>(parametersPtr);
            var buffer = Marshal.AllocHGlobal(content.Length);
            try
            {
                Marshal.Copy(content, 0, buffer, content.Length);
                transfer(info, parameters.FetchData, buffer);
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
            var identity = Encoding.UTF8.GetBytes(fileName);
            var identityPtr = Marshal.AllocHGlobal(identity.Length);
            Marshal.Copy(identity, 0, identityPtr, identity.Length);

            try
            {
                var now = DateTime.UtcNow.ToFileTimeUtc();
                var infos = new[]
                {
                    new CF_PLACEHOLDER_CREATE_INFO
                    {
                        RelativeFileName = fileName,
                        FsMetadata = new CF_FS_METADATA
                        {
                            BasicInfo = new CF_FILE_BASIC_INFO
                            {
                                FileAttributes = 0x80,
                                CreationTime = now,
                                LastAccessTime = now,
                                LastWriteTime = now,
                                ChangeTime = now,
                            },
                            FileSize = content.Length,
                        },
                        FileIdentity = identityPtr,
                        FileIdentityLength = (uint)identity.Length,
                    },
                };

                var create = CfApi.CfCreatePlaceholders(
                    syncRoot, infos, 1, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out _);
                Assert.True(CfApi.Succeeded(create), $"CfCreatePlaceholders failed: 0x{create:X8}");

                return File.ReadAllBytes(Path.Combine(syncRoot, fileName));
            }
            finally
            {
                Marshal.FreeHGlobal(identityPtr);
                GC.KeepAlive(callback);
            }
        }
        finally
        {
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

    private static int Execute(
        CF_CALLBACK_INFO info,
        IntPtr buffer,
        long offset,
        long length,
        uint structSize,
        uint paramSize,
        int status = 0)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = structSize,
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = info.ConnectionKey,
            TransferKey = info.TransferKey,
        };

        var opParams = new CF_OPERATION_PARAMETERS
        {
            ParamSize = paramSize,
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
