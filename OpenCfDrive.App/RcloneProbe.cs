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

    /// <summary>
    /// What kind of remote it is, where it points, and how much space it has.
    /// The space comes from the server itself, so this is asked for when a
    /// remote is chosen or the window opens, not on every refresh.
    /// </summary>
    public static async Task<RemoteInfo> DescribeAsync(string rcUrl, string remote)
    {
        if (!Uri.TryCreate(rcUrl, UriKind.Absolute, out var baseUri))
        {
            return new RemoteInfo(remote, null, null, null, "rclone's address is not valid.");
        }

        string? kind = null;
        string? problem = null;
        if (RemoteName(remote) is { } name)
        {
            try
            {
                using var response = await PostAsync(baseUri, "config/get", new { name });
                var config = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (!response.IsSuccessStatusCode || config.ValueKind != JsonValueKind.Object)
                {
                    problem = "rclone did not describe it.";
                }
                else if (!config.TryGetProperty("type", out var type))
                {
                    problem = $"\"{name}\" is not in rclone's config.";
                }
                else
                {
                    kind = Kind(type.GetString() ?? "", config);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                problem = "rclone did not answer.";
            }
        }

        if (problem is not null)
        {
            return new RemoteInfo(remote, null, null, null, problem);
        }

        try
        {
            using var response = await PostAsync(baseUri, "operations/about", new { fs = remote }, SlowHttp);
            var about = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (!response.IsSuccessStatusCode)
            {
                var error = about.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                var unsupported = error.Contains("doesn't support", StringComparison.OrdinalIgnoreCase) ||
                                  error.Contains("not supported", StringComparison.OrdinalIgnoreCase);
                return new RemoteInfo(remote, kind, null,
                    unsupported ? "Not reported by this kind of remote." : $"rclone could not ask the server: {error}", null);
            }

            long? Number(string property) =>
                about.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : null;
            return new RemoteInfo(remote, kind, new RemoteSpace(Number("total"), Number("used"), Number("free")), null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new RemoteInfo(remote, kind, null, "The server did not say in time how much space it has.", null);
        }
    }

    /// <summary>
    /// "webdav (other) at https://dav.example.com/", "crypt over gdrive:secret".
    /// Only these descriptive settings are shown. Passwords, keys and tokens
    /// are in the same config (obscured or not) and never leave this method.
    /// </summary>
    private static string Kind(string type, JsonElement config)
    {
        string? Setting(string key) =>
            config.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : null;

        var kind = new StringBuilder(type);
        if ((Setting("provider") ?? Setting("vendor")) is { } maker)
        {
            kind.Append($" ({maker})");
        }

        if ((Setting("url") ?? Setting("endpoint") ?? Setting("host")) is { } address)
        {
            kind.Append($" at {address}");
        }

        if (Setting("region") is { } region)
        {
            kind.Append($", region {region}");
        }

        if ((Setting("remote") ?? Setting("upstreams")) is { } wrapped)
        {
            kind.Append($" over {wrapped}");
        }

        return kind.ToString();
    }

    private static readonly HttpClient SlowHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    private static Task<HttpResponseMessage> PostAsync(Uri baseUri, string command, object? body = null, HttpClient? http = null) =>
        (http ?? Http).PostAsync(
            new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/" + command),
            new StringContent(body is null ? "{}" : JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"));
}

/// <summary>What rclone says about the chosen remote.</summary>
/// <param name="Kind">Its type and where it points, e.g. "s3 (AWS), region eu-west-1".</param>
/// <param name="Space">How much space the server reports, if it does.</param>
/// <param name="SpaceProblem">Why there is no space to show.</param>
/// <param name="Problem">Why there is nothing to show at all.</param>
internal sealed record RemoteInfo(string Remote, string? Kind, RemoteSpace? Space, string? SpaceProblem, string? Problem);

internal sealed record RemoteSpace(long? Total, long? Used, long? Free);
