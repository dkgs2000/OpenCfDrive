using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OpenCfDrive.Core.Interop;

namespace OpenCfDrive.Core.Services;

public enum RcloneDaemonState
{
    Starting,
    Running,

    /// <summary>It stopped; it is started again after a delay (Detail says why and when).</summary>
    Restarting,
}

/// <summary>
/// The rclone remote control (rclone rcd --rc-serve) that the service runs
/// for itself. It listens on a free port of 127.0.0.1 only, with a login made
/// up for this run (given through the environment, so it never shows in a
/// process list). It runs in a job that ends it with the service, however the
/// service ends; if it stops on its own, it is started again on the same port
/// and login, so the engine's clients keep working.
/// </summary>
public sealed class RcloneDaemon : IAsyncDisposable
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RestartDelays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1)];
    private const long MaxLogBytes = 10 * 1024 * 1024;

    private readonly string? _configPath;
    private readonly Action<string>? _log;
    private readonly JobObject _job = new();
    private readonly CancellationTokenSource _disposed = new();
    private Process? _process;
    private int _restarts;

    private RcloneDaemon(RcloneProgram program, string? configPath, string logPath, int port, Action<string>? log)
    {
        Program = program;
        _configPath = configPath;
        LogPath = logPath;
        _log = log;
        Endpoint = new RcloneEndpoint($"http://127.0.0.1:{port}", "opencfdrive", NewPassword());
    }

    public RcloneProgram Program { get; }

    public string LogPath { get; }

    /// <summary>Where it listens, with this run's login.</summary>
    public RcloneEndpoint Endpoint { get; }

    public RcloneDaemonState State { get; private set; } = RcloneDaemonState.Starting;

    /// <summary>What happened last, in a sentence.</summary>
    public string Detail { get; private set; } = "Starting rclone.";

    public string? Version { get; private set; }

    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    /// <summary>Starts rclone and waits until it answers.</summary>
    /// <exception cref="InvalidOperationException">It did not start; the message says why.</exception>
    public static async Task<RcloneDaemon> StartAsync(
        RcloneProgram program, string? configPath, string logPath, Action<string>? log, CancellationToken cancellationToken)
    {
        var daemon = new RcloneDaemon(program, configPath, logPath, FreePort(), log);
        try
        {
            await daemon.LaunchAsync(cancellationToken).ConfigureAwait(false);
            return daemon;
        }
        catch
        {
            await daemon.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task LaunchAsync(CancellationToken cancellationToken)
    {
        RotateLog();
        var info = new ProcessStartInfo(Program.Path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(Program.Path)!,
        };
        foreach (var argument in new[]
                 {
                     "rcd", "--rc-addr", new Uri(Endpoint.Url).Authority, "--rc-serve",
                     "--log-file", LogPath, "--log-level", "INFO",
                     // No one can type a config password: fail instead of waiting.
                     "--ask-password=false",
                 })
        {
            info.ArgumentList.Add(argument);
        }

        if (_configPath is not null)
        {
            info.ArgumentList.Add("--config");
            info.ArgumentList.Add(_configPath);
        }

        // rclone reads RCLONE_<FLAG> variables as flags: the login goes in
        // this way, and an inherited RCLONE_RC_NO_AUTH must not turn it off.
        info.Environment["RCLONE_RC_USER"] = Endpoint.User;
        info.Environment["RCLONE_RC_PASS"] = Endpoint.Password;
        info.Environment.Remove("RCLONE_RC_NO_AUTH");

        var process = Process.Start(info) ?? throw new InvalidOperationException($"'{Program.Path}' did not start.");
        _process = process;
        try
        {
            _job.Add(process);
        }
        catch (System.ComponentModel.Win32Exception) when (process.HasExited)
        {
            // Ended already; that is reported below.
        }

        process.Exited += (_, _) => OnExited(process);
        process.EnableRaisingEvents = true;

        using var http = Endpoint.CreateHttpClient(TimeSpan.FromSeconds(2));
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                throw new InvalidOperationException(
                    $"rclone stopped right away (exit code {process.ExitCode}): {LastLogLine() ?? "see its log"}");
            }

            try
            {
                using var response = await http.PostAsync(
                    $"{Endpoint.BaseUrl}/core/version",
                    new StringContent("{}", Encoding.UTF8, "application/json"),
                    cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                    Version = body.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
                    State = RcloneDaemonState.Running;
                    Detail = $"rclone {Version} runs (process {process.Id}).";
                    _log?.Invoke($"Started rclone {Version} from '{Program.Path}' at {Endpoint} (process {process.Id}).");
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                // Not listening yet.
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        // Its exit (and, after a restart, the next try) follows from ending it.
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException($"rclone did not answer within {ReadyTimeout.TotalSeconds:0} seconds.");
    }

    /// <summary>The rclone this daemon runs ended: start it again, unless the daemon is closing.</summary>
    private void OnExited(Process process)
    {
        if (_disposed.IsCancellationRequested || !ReferenceEquals(process, _process))
        {
            return;
        }

        ScheduleRestart($"rclone stopped (exit code {process.ExitCode}).");
    }

    /// <summary>One restart per failure, later each time it fails in a row.</summary>
    private void ScheduleRestart(string why)
    {
        var delay = RestartDelays[Math.Min(_restarts, RestartDelays.Length - 1)];
        _restarts++;
        State = RcloneDaemonState.Restarting;
        Detail = $"{why} Starting it again in {delay.TotalSeconds:0} s.";
        _log?.Invoke(LastLogLine() is { } last ? $"{Detail} rclone's last log line: {last}" : Detail);
        _ = RestartAfterAsync(delay);
    }

    private async Task RestartAfterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _disposed.Token).ConfigureAwait(false);
            await LaunchAsync(_disposed.Token).ConfigureAwait(false);
            _restarts = 0;
        }
        catch (OperationCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
            // It started and ended (or was ended): its exit schedules the next try.
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // It could not even start (the program is gone): nothing exits, so try later from here.
            ScheduleRestart($"rclone could not be started: {ex.Message}.");
        }
    }

    /// <summary>The last line rclone logged, which says why it stopped.</summary>
    private string? LastLogLine()
    {
        try
        {
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void RotateLog()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
            {
                File.Delete(LogPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // rclone appends to it anyway.
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string NewPassword() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));

    public async ValueTask DisposeAsync()
    {
        await _disposed.CancelAsync().ConfigureAwait(false);
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                // Gone already; the job ends it otherwise.
            }

            process.Dispose();
        }

        _job.Dispose();
        _disposed.Dispose();
    }
}
