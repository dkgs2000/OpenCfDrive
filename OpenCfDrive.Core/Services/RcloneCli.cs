using System.Diagnostics;
using System.Text.Json;

namespace OpenCfDrive.Core.Services;

/// <summary>
/// Runs rclone commands directly (rclone listremotes, config dump, about), for
/// the app: it reads rclone's config before any remote control runs, and
/// without the service's login.
/// </summary>
public sealed class RcloneCli(string program, string? configPath)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public string Program => program;

    /// <summary>"v1.75.2", or null when it does not run.</summary>
    public async Task<string?> VersionAsync()
    {
        var (code, output, _) = await RunAsync(["version"]).ConfigureAwait(false);
        var first = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
        return code == 0 && first.StartsWith("rclone ", StringComparison.Ordinal) ? first["rclone ".Length..] : null;
    }

    /// <summary>The config file it uses: the one chosen, or rclone's default.</summary>
    public async Task<string?> ConfigFileAsync()
    {
        var (code, output, _) = await RunAsync(["config", "file"]).ConfigureAwait(false);
        return code == 0
            ? output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault()
            : null;
    }

    /// <summary>The remotes in its config, without the trailing ':'.</summary>
    public async Task<IReadOnlyList<string>> ListRemotesAsync()
    {
        var (code, output, error) = await RunAsync(["listremotes"]).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException(LastLine(error) ?? $"rclone listremotes failed ({code}).");
        }

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimEnd(':'))
            .Where(name => name.Length > 0)
            .ToList();
    }

    /// <summary>
    /// One remote's settings (rclone config dump), or null when it has none.
    /// They hold its secrets too; callers show only descriptive ones.
    /// </summary>
    public async Task<JsonElement?> RemoteConfigAsync(string name)
    {
        var (code, output, error) = await RunAsync(["config", "dump"]).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException(LastLine(error) ?? $"rclone config dump failed ({code}).");
        }

        using var dump = JsonDocument.Parse(output);
        return dump.RootElement.TryGetProperty(name, out var remote) ? remote.Clone() : null;
    }

    /// <summary>rclone about --json: the space the server reports (fields it does not report are absent).</summary>
    public async Task<(JsonElement? About, string? Error)> AboutAsync(string remote)
    {
        var (code, output, error) = await RunAsync(["about", remote, "--json"]).ConfigureAwait(false);
        if (code != 0)
        {
            return (null, LastLine(error) ?? $"rclone about failed ({code}).");
        }

        using var about = JsonDocument.Parse(output);
        return (about.RootElement.Clone(), null);
    }

    private async Task<(int Code, string Output, string Error)> RunAsync(IReadOnlyList<string> arguments)
    {
        var info = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (configPath is not null)
        {
            info.ArgumentList.Add("--config");
            info.ArgumentList.Add(configPath);
        }

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // Never stop to ask for a config password: there is no one to answer.
        info.ArgumentList.Add("--ask-password=false");

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"'{program}' did not start.");
        using var timeout = new CancellationTokenSource(Timeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            return (-1, "", $"rclone did not finish within {Timeout.TotalSeconds:0} seconds.");
        }
    }

    private static string? LastLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
