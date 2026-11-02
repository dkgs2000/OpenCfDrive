using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.MockService;

/// <summary>
/// Test stand-in for <c>OpenCfDrive.Service</c> (used by scripts\tests\E2E-Run-Mock.ps1).
///
/// It performs the exact same startup, config and sync-engine work as the real
/// service, except that the Cloud Files registration/connection step is
/// replaced with <see cref="CloudSyncEngine"/>'s forced mock mode: the sync
/// root is a normal directory and placeholders are real hydrated files.
///
/// Environment (identical to the real service):
///   OPENCFDRIVE_ROOT  - local sync folder
///   RCLONE_FS         - rclone remote spec, e.g. "e2e:"
///   RCLONE_RC_URL     - rclone RC base URL, e.g. "http://127.0.0.1:5572"
/// </summary>
internal static class Program
{
    private static readonly object LogLock = new();

    public static async Task<int> Main(string[] args)
    {
        var resetState = args.Contains("--reset-state", StringComparer.OrdinalIgnoreCase);

        var envRoot = Environment.GetEnvironmentVariable("OPENCFDRIVE_ROOT");
        var syncRoot = string.IsNullOrWhiteSpace(envRoot)
            ? Path.Combine(Path.GetTempPath(), "OpenCfDriveMock", "drive")
            : envRoot;

        var envRemote = Environment.GetEnvironmentVariable("RCLONE_FS");
        var envRcUrl = Environment.GetEnvironmentVariable("RCLONE_RC_URL");

        try
        {
            Directory.CreateDirectory(syncRoot);
        }
        catch (Exception ex)
        {
            Log($"MOCK: cannot create sync root '{syncRoot}': {ex.Message}");
            return 2;
        }

        // State lives in %LOCALAPPDATA%\OpenCfDrive\state, never in the sync root.
        Directory.CreateDirectory(SyncStatePaths.StateRoot);
        var dbPath = SyncStatePaths.DatabasePath(syncRoot);

        if (resetState)
        {
            foreach (var path in new[] { dbPath, dbPath + "-journal", dbPath + "-wal", dbPath + "-shm" })
            {
                try { File.Delete(path); } catch (IOException) { }
            }

            Log($"MOCK: reset state at '{dbPath}'");
        }

        SqliteMetadataStore store;
        try
        {
            store = new SqliteMetadataStore(dbPath);
            store.Initialize();
        }
        catch (Exception ex)
        {
            dbPath = SyncStatePaths.FallbackDatabasePath(syncRoot);
            Log($"MOCK: LocalAppData state is not writable ({ex.Message}); using '{dbPath}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
            store = new SqliteMetadataStore(dbPath);
            store.Initialize();
        }

        // Explicit environment values win so scripted runs stay deterministic.
        if (string.IsNullOrWhiteSpace(envRoot))
        {
            var persistedRoot = store.GetConfig("OpenCfDriveRoot");
            if (!string.IsNullOrWhiteSpace(persistedRoot))
            {
                syncRoot = persistedRoot;
            }
        }

        var remote = SyncStatePaths.NormalizeRemote(
            string.IsNullOrWhiteSpace(envRemote) ? store.GetConfig("RcloneFs") : envRemote);

        var rcUrl = !string.IsNullOrWhiteSpace(envRcUrl)
            ? envRcUrl
            : store.GetConfig("RcloneRcUrl");
        if (string.IsNullOrWhiteSpace(rcUrl))
        {
            rcUrl = "http://127.0.0.1:5572";
        }

        store.SetConfig("OpenCfDriveRoot", syncRoot);
        store.SetConfig("RcloneFs", remote);
        store.SetConfig("RcloneRcUrl", rcUrl);

        Log($"MOCK: starting syncRoot='{syncRoot}' remote='{remote}' rc='{rcUrl}' db='{dbPath}'");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        var engine = new CloudSyncEngine(
            syncRoot,
            remote,
            rcUrl,
            dbPath,
            Log,
            forceSimulation: true);

        try
        {
            var status = await engine.InitializeAsync(cts.Token).ConfigureAwait(false);

            Log($"MOCK: initialized mode={(engine.IsSimulated ? "mock" : "cloud")} items={status.RegisteredItems}");

            engine.StartWatcher();
            Log($"MOCK: watching '{syncRoot}'");
            Log("MOCK: READY");

            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
        }
        catch (Exception ex)
        {
            Log($"MOCK: FAILED {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            await engine.DisposeAsync().ConfigureAwait(false);
        }

        Log("MOCK: stopped");
        return 0;
    }

    private static void Log(string message)
    {
        lock (LogLock)
        {
            Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}");
            Console.Out.Flush();
        }
    }
}
