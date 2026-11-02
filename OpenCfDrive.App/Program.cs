namespace OpenCfDrive.App;

/// <summary>
/// OpenCfDrive.exe: the settings and status window, behind an icon in the
/// notification area. It configures and starts the sync service
/// (OpenCfDrive.Service.exe); the service does the syncing on its own.
/// </summary>
/// <remarks>
///   (no option)    shows the window; a second start shows the running copy's
///   --background   only the icon (the sign-in start)
///   --quit         closes the running copy (the installer, before an upgrade)
///   --uninstall    --quit, and no longer starts at sign-in (the uninstaller)
/// </remarks>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (Has(args, "--quit") || Has(args, "--uninstall"))
        {
            SingleInstance.CloseRunningCopy(TimeSpan.FromSeconds(10));
            if (Has(args, "--uninstall"))
            {
                SignInStartup.Disable();
            }

            return 0;
        }

        using var instance = SingleInstance.TryClaim();
        if (instance is null)
        {
            SingleInstance.ShowRunningCopy();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => CrashLog.Write(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);
        Application.Run(new TrayApp(instance, showWindow: !Has(args, "--background")));
        return 0;
    }

    private static bool Has(string[] args, string option) => args.Contains(option, StringComparer.OrdinalIgnoreCase);
}
