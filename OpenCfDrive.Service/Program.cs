using System.ComponentModel;
using System.Diagnostics;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Storage;
using OpenCfDrive.Service;

ConsoleOutput.Connect();

var syncRoot = Environment.GetEnvironmentVariable("OPENCFDRIVE_ROOT") ?? SyncStatePaths.DefaultSyncRoot;

// --stop: ends the other running copies of this same exe (the installer runs
// it before replacing or removing the files). Copies elsewhere are left alone.
if (args.Contains("--stop", StringComparer.OrdinalIgnoreCase))
{
    var stopped = StopOtherInstances();
    Console.WriteLine($"Stopped {stopped} running instance(s).");
    if (!args.Contains("--unregister", StringComparer.OrdinalIgnoreCase))
    {
        return 0;
    }
}

if (args.Contains("--unregister", StringComparer.OrdinalIgnoreCase))
{
    var registrar = new SyncRootRegistrar();
    var result = registrar.Unregister(syncRoot);
    Console.WriteLine(result.Message);
    return result.Success ? 0 : 1;
}

// One provider per drive: a second start (the Start Menu shortcut clicked
// twice) would connect to the same sync root and upload everything twice.
using var instance = new Mutex(
    initiallyOwned: true,
    "Local\\" + Path.GetFileNameWithoutExtension(SyncStatePaths.DatabasePath(syncRoot)),
    out var firstInstance);
if (!firstInstance)
{
    Console.WriteLine($"OpenCfDrive is already running for '{syncRoot}'.");
    return 0;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<SyncWorker>();

var host = builder.Build();
host.Run();
return 0;

static int StopOtherInstances()
{
    var self = Environment.ProcessPath;
    var stopped = 0;
    foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(self)))
    {
        using (process)
        {
            try
            {
                if (process.Id == Environment.ProcessId ||
                    !string.Equals(process.MainModule?.FileName, self, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                process.Kill();
                process.WaitForExit(10_000);
                stopped++;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Another user's copy, or already gone.
            }
        }
    }

    return stopped;
}
