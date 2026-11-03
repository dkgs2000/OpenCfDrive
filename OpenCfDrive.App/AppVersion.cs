using System.Reflection;

namespace OpenCfDrive.App;

/// <summary>What this build of the app is: its version, and where its releases are.</summary>
internal static class AppVersion
{
    private static readonly Assembly App = typeof(AppVersion).Assembly;

    /// <summary>The version the build was given (Build-Installer.ps1 -Version), without the commit the SDK appends.</summary>
    public static Version Current =>
        Version.TryParse(
            App.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0],
            out var version)
            ? version
            : App.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>The GitHub repository (owner/name) the build updates from, or null.</summary>
    public static string? UpdateRepository =>
        App.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepository")?.Value;
}
