using System.ComponentModel;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OpenCfDrive.Core.Configuration;
using OpenCfDrive.Core.Services;

namespace OpenCfDrive.App;

/// <summary>
/// How the app reaches rclone: through the rclone program OpenCfDrive runs
/// (the default; the app runs it directly, so this works before the service
/// starts), or through a remote control the user runs at <paramref name="RcUrl"/>.
/// </summary>
internal sealed record RcloneAccess(bool Managed, string? ChosenProgram, string? ConfigPath, string RcUrl)
{
    public static RcloneAccess From(EffectiveSettings settings) =>
        new(settings.ManageRclone, settings.RclonePath, settings.RcloneConfig, settings.RcUrl.Value);

    /// <summary>Two probes with the same key asked the same rclone.</summary>
    public string Key => Managed ? $"run|{ChosenProgram}|{ConfigPath}" : $"url|{RcUrl}";
}

/// <summary>What rclone answered.</summary>
/// <param name="Answers">The program runs, or something answered at the URL.</param>
/// <param name="Usable">The service can use it: it runs and reads its config, or answers and lets OpenCfDrive in.</param>
/// <param name="Remotes">The remotes in rclone's config, when it listed them.</param>
/// <param name="Program">The rclone program OpenCfDrive would run, and how it was found.</param>
/// <param name="ConfigFile">The config file that program reads.</param>
internal sealed record RcloneProbeResult(
    string Key,
    bool Answers,
    bool Usable,
    string? Version,
    string? Problem,
    IReadOnlyList<string>? Remotes,
    RcloneProgram? Program = null,
    string? ConfigFile = null);

/// <summary>
/// Asks rclone what the service needs: that it runs, and which remotes its
/// config has; and, for one remote, what it is and how much space it has.
/// </summary>
internal static class RcloneProbe
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly HttpClient SlowHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static Task<RcloneProbeResult> CheckAsync(RcloneAccess access) =>
        access.Managed ? CheckProgramAsync(access) : CheckRemoteControlAsync(access);

    private static async Task<RcloneProbeResult> CheckProgramAsync(RcloneAccess access)
    {
        var program = RcloneLocator.Find(access.ChosenProgram);
        if (program is null)
        {
            return new RcloneProbeResult(access.Key, false, false, null,
                access.ChosenProgram is null
                    ? "rclone was not found: it is not on PATH, and none was downloaded."
                    : $"There is no rclone at '{access.ChosenProgram}'.",
                null);
        }

        var cli = new RcloneCli(program.Path, access.ConfigPath);
        try
        {
            var version = await cli.VersionAsync();
            if (version is null)
            {
                return new RcloneProbeResult(access.Key, false, false, null, $"'{program.Path}' does not run as rclone.", null, program);
            }

            var configFile = await cli.ConfigFileAsync();
            try
            {
                return new RcloneProbeResult(access.Key, true, true, version, null, await cli.ListRemotesAsync(), program, configFile);
            }
            catch (InvalidOperationException ex)
            {
                return new RcloneProbeResult(access.Key, true, false, version,
                    $"rclone cannot read its config: {ex.Message}", null, program, configFile);
            }
        }
        catch (Win32Exception ex)
        {
            return new RcloneProbeResult(access.Key, false, false, null, $"'{program.Path}' does not run: {ex.Message}", null, program);
        }
    }

    private static async Task<RcloneProbeResult> CheckRemoteControlAsync(RcloneAccess access)
    {
        var rcUrl = access.RcUrl;
        var key = access.Key;
        if (!Uri.TryCreate(rcUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
        {
            return new RcloneProbeResult(key, false, false, null, "This is not an http:// or https:// address.", null);
        }

        string? version;
        try
        {
            using var response = await PostAsync(baseUri, "core/version");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new RcloneProbeResult(key, true, false, null,
                    "rclone asks for a login, which OpenCfDrive does not send. Run rclone rcd with --rc-no-auth.", null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new RcloneProbeResult(key, true, false, null,
                    $"It answered {(int)response.StatusCode} {response.ReasonPhrase}; is this rclone's remote control?", null);
            }

            version = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("version").GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new RcloneProbeResult(key, false, false, null, "Nothing answers there. Is rclone rcd running?", null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return new RcloneProbeResult(key, true, false, null, "It answered, but not like rclone does.", null);
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
                return new RcloneProbeResult(key, true, false, version,
                    $"rclone refuses OpenCfDrive's requests ({error}). Run rclone rcd with --rc-no-auth.", null);
            }

            var remotes = body.TryGetProperty("remotes", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Select(r => r.GetString() ?? "").Where(r => r.Length > 0).ToList()
                : [];
            return new RcloneProbeResult(key, true, true, version, null, remotes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new RcloneProbeResult(key, true, false, version, $"Listing rclone's remotes failed: {ex.Message}", null);
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
    public static Task<RemoteInfo> DescribeAsync(RcloneAccess access, string remote) =>
        access.Managed ? DescribeWithProgramAsync(access, remote) : DescribeWithRemoteControlAsync(access.RcUrl, remote);

    private static async Task<RemoteInfo> DescribeWithProgramAsync(RcloneAccess access, string remote)
    {
        if (RcloneLocator.Find(access.ChosenProgram) is not { } program)
        {
            return new RemoteInfo(remote, null, null, null, "rclone was not found.");
        }

        var cli = new RcloneCli(program.Path, access.ConfigPath);
        string? kind = null;
        try
        {
            if (RemoteName(remote) is { } name)
            {
                if (await cli.RemoteConfigAsync(name) is not { } config || !config.TryGetProperty("type", out var type))
                {
                    return new RemoteInfo(remote, null, null, null, $"\"{name}\" is not in rclone's config.");
                }

                kind = Kind(type.GetString() ?? "", config);
            }

            var (about, error) = await cli.AboutAsync(remote);
            return about is { } space
                ? new RemoteInfo(remote, kind, SpaceOf(space), null, null)
                : new RemoteInfo(remote, kind, null, SpaceProblem(error ?? ""), null);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or JsonException)
        {
            return new RemoteInfo(remote, kind, null, null, $"rclone could not describe it: {ex.Message}");
        }
    }

    private static async Task<RemoteInfo> DescribeWithRemoteControlAsync(string rcUrl, string remote)
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
                return new RemoteInfo(remote, kind, null, SpaceProblem(error), null);
            }

            return new RemoteInfo(remote, kind, SpaceOf(about), null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new RemoteInfo(remote, kind, null, "The server did not say in time how much space it has.", null);
        }
    }

    private static RemoteSpace SpaceOf(JsonElement about)
    {
        long? Number(string property) =>
            about.TryGetProperty(property, out var value) && value.TryGetInt64(out var number) ? number : null;
        return new RemoteSpace(Number("total"), Number("used"), Number("free"));
    }

    private static string SpaceProblem(string error) =>
        error.Contains("doesn't support", StringComparison.OrdinalIgnoreCase) ||
        error.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            ? "Not reported by this kind of remote."
            : $"rclone could not ask the server: {error}";

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
