using Microsoft.Win32;

namespace OpenCfDrive.App;

/// <summary>
/// "Start OpenCfDrive when I sign in": the app's entry under the user's Run
/// key, which starts it with only its notification area icon.
/// </summary>
internal static class SignInStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenCfDrive";

    private static string ExePath => Environment.ProcessPath!;

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command &&
                   command.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Enable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{ExePath}\" --background");
    }

    /// <summary>Removes the entry if it starts this copy of the app.</summary>
    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is string command && command.Contains(ExePath, StringComparison.OrdinalIgnoreCase))
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>
    /// Whether this is the copy the installer put in place (it records its
    /// folder): only that one starts at sign-in by default, never a build
    /// output someone runs to try a change.
    /// </summary>
    public static bool IsInstalledCopy
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\OpenCfDrive");
            return key?.GetValue("InstallFolder") is string folder &&
                   string.Equals(
                       Path.TrimEndingDirectorySeparator(folder),
                       Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                       StringComparison.OrdinalIgnoreCase);
        }
    }
}
