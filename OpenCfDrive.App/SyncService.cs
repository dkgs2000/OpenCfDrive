using System.ComponentModel;
using System.Diagnostics;
using OpenCfDrive.Core.Control;

namespace OpenCfDrive.App;

/// <summary>Starts and stops the sync service (OpenCfDrive.Service.exe) for the configured drive.</summary>
internal static class SyncService
{
    private const string ExeName = "OpenCfDrive.Service.exe";

    /// <summary>
    /// Next to this app when installed. A build of this repository has it in
    /// the service project's output for the same configuration instead:
    /// OpenCfDrive.App\bin\Release\&lt;framework&gt;\win-x64\ pairs with
    /// OpenCfDrive.Service\bin\Release\&lt;framework&gt;\win-x64\.
    /// </summary>
    public static string? ExePath
    {
        get
        {
            var installed = Path.Combine(AppContext.BaseDirectory, ExeName);
            if (File.Exists(installed))
            {
                return installed;
            }

            var bin = new DirectoryInfo(AppContext.BaseDirectory);
            while (bin is not null && !string.Equals(bin.Name, "bin", StringComparison.OrdinalIgnoreCase))
            {
                bin = bin.Parent;
            }

            if (bin?.Parent?.Parent is not { } repository)
            {
                return null;
            }

            var output = Path.GetRelativePath(bin.FullName, AppContext.BaseDirectory);
            var built = Path.Combine(repository.FullName, "OpenCfDrive.Service", "bin", output, ExeName);
            return File.Exists(built) ? built : null;
        }
    }

    /// <summary>Starts the service and waits until it runs; returns why not otherwise.</summary>
    public static async Task<string?> StartAsync(string syncRoot)
    {
        if (ServiceControl.IsRunning(syncRoot))
        {
            return null;
        }

        var exe = ExePath;
        if (exe is null)
        {
            return $"{ExeName} was not found next to the app, in {AppContext.BaseDirectory}";
        }

        Process process;
        try
        {
            process = Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            })!;
        }
        catch (Win32Exception ex)
        {
            return $"{ExeName} could not be started: {ex.Message}";
        }

        using (process)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (ServiceControl.IsRunning(syncRoot))
                {
                    return null;
                }

                if (process.HasExited)
                {
                    return $"The service stopped right away (exit code {process.ExitCode}); its log says why.";
                }

                await Task.Delay(200);
            }
        }

        return "The service did not start within 15 seconds; its log says why.";
    }

    /// <summary>Asks the service to stop, and ends it if it does not; returns why it still runs otherwise.</summary>
    public static async Task<string?> StopAsync(string syncRoot)
    {
        if (!ServiceControl.IsRunning(syncRoot))
        {
            return null;
        }

        var status = await ServiceControl.GetStatusAsync(syncRoot, TimeSpan.FromSeconds(2));
        await ServiceControl.RequestStopAsync(syncRoot, TimeSpan.FromSeconds(2));
        if (await WaitUntilStoppedAsync(syncRoot, TimeSpan.FromSeconds(15)))
        {
            return null;
        }

        // It did not answer or did not finish: end it the way the installer does.
        if (status is not null)
        {
            try
            {
                using var process = Process.GetProcessById(status.ProcessId);
                process.Kill();
            }
            catch (Exception ex) when (ex is ArgumentException or Win32Exception or InvalidOperationException)
            {
                // Already gone.
            }
        }

        return await WaitUntilStoppedAsync(syncRoot, TimeSpan.FromSeconds(10))
            ? null
            : "The service did not stop.";
    }

    private static async Task<bool> WaitUntilStoppedAsync(string syncRoot, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!ServiceControl.IsRunning(syncRoot))
            {
                return true;
            }

            await Task.Delay(200);
        }

        return false;
    }
}
