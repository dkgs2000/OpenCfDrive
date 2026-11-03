using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OpenCfDrive.Core.Services;

/// <summary>
/// Downloads rclone from its official site, for when it is not installed: the
/// current release's Windows zip, checked against the release's SHA256SUMS,
/// and only rclone.exe from it, into <see cref="RcloneLocator.DownloadFolder"/>.
/// </summary>
/// <remarks>
/// SHA256SUMS comes over HTTPS from the same site, so the check catches a
/// damaged or swapped file, not a compromised site. (The file is also PGP
/// signed; checking that needs rclone's key and a PGP implementation.)
/// </remarks>
public static partial class RcloneDownloader
{
    public const string Site = "https://downloads.rclone.org";

    /// <summary>Downloads the current rclone; returns the path of rclone.exe.</summary>
    /// <param name="progress">Short lines on what it is doing, for the app.</param>
    public static async Task<string> DownloadAsync(
        string folder, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OpenCfDrive");

        progress?.Report("Asking rclone.org for the current version...");
        var current = (await http.GetStringAsync($"{Site}/version.txt", cancellationToken).ConfigureAwait(false)).Trim();
        var version = VersionPattern().Match(current) is { Success: true } match
            ? match.Value
            : throw new InvalidDataException($"rclone.org gave no version ('{current}').");

        var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
        var zipName = $"rclone-{version}-windows-{architecture}.zip";
        var sums = await http.GetStringAsync($"{Site}/{version}/SHA256SUMS", cancellationToken).ConfigureAwait(false);
        var expected = ExpectedHash(sums, zipName)
            ?? throw new InvalidDataException($"rclone's SHA256SUMS lists no {zipName}.");

        Directory.CreateDirectory(folder);
        var zipPath = Path.Combine(folder, zipName + ".download");
        try
        {
            using (var response = await http.GetAsync($"{Site}/{version}/{zipName}", HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                       .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var target = File.Create(zipPath);
                var buffer = new byte[81920];
                long done = 0;
                var reported = -1L;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    var megabytes = done / (1024 * 1024);
                    if (megabytes != reported)
                    {
                        reported = megabytes;
                        progress?.Report(total is { } size
                            ? $"Downloading rclone {version}: {megabytes} of {size / (1024 * 1024)} MB..."
                            : $"Downloading rclone {version}: {megabytes} MB...");
                    }
                }
            }

            progress?.Report("Checking the download...");
            string actual;
            await using (var zip = File.OpenRead(zipPath))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(zip, cancellationToken).ConfigureAwait(false));
            }

            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The download does not match rclone's published checksum for {zipName}.");
            }

            var exe = Path.Combine(folder, "rclone.exe");
            var fresh = exe + ".new";
            using (var archive = ZipFile.OpenRead(zipPath))
            {
                var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.Name, "rclone.exe", StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidDataException($"{zipName} holds no rclone.exe.");
                entry.ExtractToFile(fresh, overwrite: true);
            }

            await CheckRunsAsync(fresh, version, cancellationToken).ConfigureAwait(false);
            Replace(exe, fresh);
            progress?.Report($"Downloaded rclone {version}.");
            return exe;
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    /// <summary>The hash SHA256SUMS gives for a file ("&lt;hash&gt;  &lt;name&gt;" lines inside the signed message).</summary>
    public static string? ExpectedHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            if (SumLinePattern().Match(line.Trim()) is { Success: true } match &&
                string.Equals(match.Groups[2].Value, fileName, StringComparison.Ordinal))
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static async Task CheckRunsAsync(string exe, string version, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, "version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("The downloaded rclone did not start.");
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || !output.Contains(version, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The downloaded rclone does not run.");
        }
    }

    /// <summary>
    /// Puts the new rclone.exe in place. The old one may be running (a
    /// service started it): Windows lets a running program be renamed, not
    /// overwritten, so it is moved aside and removed when it can be.
    /// </summary>
    private static void Replace(string exe, string fresh)
    {
        foreach (var old in Directory.EnumerateFiles(Path.GetDirectoryName(exe)!, "rclone.exe.*.old"))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still running; it goes next time.
            }
        }

        if (File.Exists(exe))
        {
            File.Move(exe, $"{exe}.{Guid.NewGuid():N}.old");
        }

        File.Move(fresh, exe);
    }

    [GeneratedRegex(@"v\d+\.\d+\.\d+")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(\S+)$")]
    private static partial Regex SumLinePattern();
}
