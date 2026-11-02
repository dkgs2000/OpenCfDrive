using System.Runtime.InteropServices;
using OpenCfDrive.Core.Interop;

namespace OpenCfDrive.Core.CloudFiles;

public sealed record CloudFilesSupport(bool IsSupported, string Reason, int? BuildNumber = null);

/// <summary>
/// Probes whether the Cloud Files API runtime is usable on this host.
/// Registration/mounting must never be attempted when this returns false.
/// </summary>
public static class CloudFilesCapability
{
    private static readonly Lazy<CloudFilesSupport> Cached = new(Probe, isThreadSafe: true);

    public static CloudFilesSupport Current => Cached.Value;

    /// <summary>
    /// The Cloud Filter API is exported from cldapi.dll (CldApi.lib in the
    /// Windows SDK). There is no cfapi.dll on any Windows build; probing for it
    /// would disable the provider on every machine.
    /// </summary>
    public static string LibraryPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "cldapi.dll");

    private static CloudFilesSupport Probe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new CloudFilesSupport(false, "Not running on Windows.");
        }

        if (!File.Exists(LibraryPath))
        {
            return new CloudFilesSupport(false, $"cldapi.dll not present at {LibraryPath}.");
        }

        try
        {
            var hr = CfApi.CfGetPlatformInfo(out var info);
            if (!CfApi.Succeeded(hr))
            {
                return new CloudFilesSupport(false, $"CfGetPlatformInfo failed with 0x{hr:X8}.");
            }

            return new CloudFilesSupport(true, "Cloud Files API available.", (int)info.BuildNumber);
        }
        catch (DllNotFoundException ex)
        {
            return new CloudFilesSupport(false, $"cldapi.dll could not be loaded: {ex.Message}");
        }
        catch (EntryPointNotFoundException ex)
        {
            return new CloudFilesSupport(false, $"cldapi.dll entry point missing: {ex.Message}");
        }
        catch (BadImageFormatException ex)
        {
            return new CloudFilesSupport(false, $"cldapi.dll architecture mismatch: {ex.Message}");
        }
    }
}

public static class HResult
{
    public static bool Failed(int hr) => hr < 0;

    public static string Describe(int hr) => $"0x{hr:X8}";
}
