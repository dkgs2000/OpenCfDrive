using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace OpenCfDrive.Core.Configuration;

/// <summary>
/// A secret kept in the settings file encrypted for the Windows user (DPAPI,
/// as Windows keeps saved credentials): only that user, on this PC, can read
/// it back. The settings file then never holds it in plain text, so it does
/// not leak with a copy of the file.
/// </summary>
public static class UserSecret
{
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    // Ties the encrypted value to its use: another program's DPAPI value is no login here.
    private static readonly byte[] Entropy = "OpenCfDrive rclone remote control login"u8.ToArray();

    /// <summary>The secret, encrypted for this Windows user, as base64.</summary>
    public static string Protect(string secret) =>
        Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(secret), protect: true));

    /// <summary>The secret back, or null when it cannot be read here (another user or PC, or not ours).</summary>
    public static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(protectedSecret), protect: false));
        }
        catch (Exception ex) when (ex is FormatException or Win32Exception)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = GCHandle.Alloc(data, GCHandleType.Pinned);
        var entropy = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var inBlob = new DATA_BLOB { cbData = data.Length, pbData = input.AddrOfPinnedObject() };
            var entropyBlob = new DATA_BLOB { cbData = Entropy.Length, pbData = entropy.AddrOfPinnedObject() };
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out var outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, out outBlob);
            if (!ok)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            input.Free();
            entropy.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn, string? szDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);
}
