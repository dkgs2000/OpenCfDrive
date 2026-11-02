using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace OpenCfDrive.App;

/// <summary>What rclone's remote control answered.</summary>
/// <param name="Answers">Something answered at the URL.</param>
/// <param name="Usable">The service can use it: it answers and lets OpenCfDrive in.</param>
/// <param name="Remotes">The remotes in rclone's config, when it listed them.</param>
internal sealed record RcloneProbeResult(
    string RcUrl,
    bool Answers,
    bool Usable,
    string? Version,
    string? Problem,
    IReadOnlyList<string>? Remotes);

/// <summary>
/// Asks rclone's remote control what the service needs: that it answers, and
/// which remotes its config has. The app never starts rclone; whoever runs
/// rclone rcd does.
/// </summary>
internal static class RcloneProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    public static async Task<RcloneProbeResult> CheckAsync(string rcUrl)
    {
        if (!Uri.TryCreate(rcUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            return new RcloneProbeResult(rcUrl, false, false, null, "This is not an http:// or https:// address.", null);
        }

        string? version;
        try
        {
            using var response = await PostAsync(baseUri, "core/version");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new RcloneProbeResult(rcUrl, true, false, null,
                    "rclone asks for a login, which OpenCfDrive does not send. Run rclone rcd with --rc-no-auth.", null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new RcloneProbeResult(rcUrl, true, false, null,
                    $"It answered {(int)response.StatusCode} {response.ReasonPhrase}; is this rclone's remote control?", null);
            }

            version = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new RcloneProbeResult(rcUrl, false, false, null,
                "Nothing answers there. Is rclone rcd running?", null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new RcloneProbeResult(rcUrl, true, false, null, "It answered, but not like rclone does.", null);
        }

        // Listing remotes needs the same access as syncing: rclone allows it
        // only with --rc-no-auth (or a login, which OpenCfDrive does not send).
        try
        {
            using var response = await PostAsync(baseUri, "config/listremotes");
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!response.IsSuccessStatusCode)
            {
                var error = body.TryGetProperty("error", out var e) ? e.GetString() : response.ReasonPhrase;
                return new RcloneProbeResult(rcUrl, true, false, version,
                    $"rclone refuses OpenCfDrive's requests ({error}). Run rclone rcd with --rc-no-auth.", null);
            }

            var remotes = body.TryGetProperty("remotes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0).ToList()
                : [];
            return new RcloneProbeResult(rcUrl, true, true, version, null, remotes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new RcloneProbeResult(rcUrl, true, false, version, $"Listing rclone's remotes failed: {ex.Message}", null);
        }
    }

    /// <summary>The remote's name in rclone's config: "myremote" for "myremote:backup".</summary>
    public static string? RemoteName(string remote)
    {
        // ":backend:" and "name,option=value:" are not plain config entries.
        var colon = remote.IndexOf(':');
        if (colon <= 0 || remote[..colon].Contains(','))
        {
            return null;
        }

        return remote[..colon];
    }

    private static Task<HttpResponseMessage> PostAsync(Uri baseUri, string command) =>
        Http.PostAsync(
            new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + command),
            new StringContent("{}", Encoding.UTF8, "application/json"));
}
