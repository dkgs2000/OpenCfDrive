using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenCfDrive.Core.Updates;

/// <summary>A published version of OpenCfDrive and its installer.</summary>
/// <param name="Version">From the tag: v1.2.3 is 1.2.3.</param>
/// <param name="Page">The release's page on GitHub, for installing by hand.</param>
/// <param name="Installer">The setup .exe, or null if the release has none.</param>
public sealed record AppRelease(Version Version, string Page, ReleaseAsset? Installer);

/// <summary>A file attached to a release.</summary>
/// <param name="Sha256">The hex SHA-256 GitHub computed when it was uploaded, or null.</param>
public sealed record ReleaseAsset(string Name, Uri Url, long Size, string? Sha256);

/// <summary>
/// Asks GitHub for the latest release of a repository: the newest one that is
/// neither a draft nor a pre-release (GitHub's releases/latest).
/// </summary>
public static partial class GitHubReleases
{
    public const string Api = "https://api.github.com";

    /// <summary>
    /// The installer an update runs: the setup .exe the release workflow
    /// attaches, not the bare MSI. It upgrades an install made with either:
    /// an older setup in place, and an MSI by taking it over. The MSI run
    /// over a setup's install would leave the old setup listed in Apps too.
    /// </summary>
    public static string InstallerName(Version version) => $"OpenCfDrive-{Format(version)}-x64-setup.exe";

    /// <summary>A version as releases name it: three numbers.</summary>
    public static string Format(Version version) => $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";

    /// <summary>The latest release, or null if the repository has none yet.</summary>
    /// <param name="repository">owner/name.</param>
    public static async Task<AppRelease?> LatestAsync(HttpClient http, string repository, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/repos/{repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Reads a release from GitHub's JSON.</summary>
    /// <exception cref="InvalidDataException">Not a release, or its tag is not a version.</exception>
    public static AppRelease Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            var version = ParseTag(tag) ?? throw new InvalidDataException($"The release tag '{tag}' is not a version like v1.2.3.");
            var page = root.TryGetProperty("html_url", out var url) ? url.GetString() ?? "" : "";

            ReleaseAsset? installer = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                var name = InstallerName(version);
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.GetProperty("name").GetString() == name)
                    {
                        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
                        installer = new ReleaseAsset(
                            name,
                            new Uri(asset.GetProperty("browser_download_url").GetString()!),
                            asset.GetProperty("size").GetInt64(),
                            digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                                ? digest["sha256:".Length..]
                                : null);
                    }
                }
            }

            return new AppRelease(version, page, installer);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or UriFormatException)
        {
            throw new InvalidDataException($"GitHub's answer is not a release: {ex.Message}", ex);
        }
    }

    /// <summary>v1.2.3 (or 1.2.3) as a version; anything else is null.</summary>
    public static Version? ParseTag(string tag) =>
        TagPattern().Match(tag.Trim()) is { Success: true } match &&
        int.TryParse(match.Groups[1].Value, out var major) &&
        int.TryParse(match.Groups[2].Value, out var minor) &&
        int.TryParse(match.Groups[3].Value, out var build)
            ? new Version(major, minor, build)
            : null;

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)$")]
    private static partial Regex TagPattern();
}
