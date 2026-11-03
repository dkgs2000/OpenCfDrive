using System.Net.NetworkInformation;

namespace OpenCfDrive.Core.CloudFiles;

/// <summary>
/// Whether the server answers, kept the way Nextcloud's desktop client keeps
/// its account state. A request that cannot reach the server puts the drive
/// offline: folders then show what is on this PC at once, and nothing waits
/// for the network (as in Nextcloud and OneDrive). While offline it checks
/// again when Windows reports a network change, when the drive is used, and
/// on Nextcloud's schedule: 62 s after going offline, then 62 s more after
/// each failed check, at most 8 × 62 s. A check that reaches the server puts
/// the drive back online.
/// </summary>
public sealed class ServerConnection : IDisposable
{
    /// <summary>Nextcloud's ConnectionValidator::DefaultCallingIntervalMsec.</summary>
    public static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(62);

    /// <summary>Nextcloud's cap: DefaultCallingIntervalMaxMsec is 8 intervals.</summary>
    private const int MaxIntervals = 8;

    private readonly Func<CancellationToken, Task> _check;
    private readonly TimeSpan _checkTimeout;
    private readonly Action<string>? _log;
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private bool _online = true;
    private bool _checking;
    private bool _disposed;
    private int _failedChecks;
    private string? _offlineReason;

    /// <param name="check">A small request to the server; throws when it cannot be reached.</param>
    /// <param name="checkTimeout">How long a check may take before it counts as unreachable.</param>
    public ServerConnection(
        Func<CancellationToken, Task> check,
        TimeSpan checkTimeout,
        Action<string>? log = null,
        TimeProvider? time = null)
    {
        _check = check;
        _checkTimeout = checkTimeout;
        _log = log;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => CheckNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    public bool IsOnline
    {
        get
        {
            lock (_gate)
            {
                return _online;
            }
        }
    }

    /// <summary>Why the server could not be reached, while offline.</summary>
    public string? OfflineReason
    {
        get
        {
            lock (_gate)
            {
                return _online ? null : _offlineReason;
            }
        }
    }

    /// <summary>Raised with true when the server answers again, false when it stops answering.</summary>
    public event Action<bool>? Changed;

    /// <summary>A request reached the server.</summary>
    public void Succeeded()
    {
        lock (_gate)
        {
            if (_online || _disposed)
            {
                return;
            }

            _online = true;
            _failedChecks = 0;
            _offlineReason = null;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _log?.Invoke("The server answers again; working online.");
        Changed?.Invoke(true);
    }

    /// <summary>A request could not reach the server (see <see cref="IsUnreachable"/>).</summary>
    public void Failed(string reason)
    {
        lock (_gate)
        {
            if (!_online || _disposed)
            {
                return;
            }

            _online = false;
            _failedChecks = 0;
            _offlineReason = reason;
            _timer.Change(CheckInterval, Timeout.InfiniteTimeSpan);
        }

        _log?.Invoke($"The server cannot be reached ({reason}); working offline until it answers.");
        Changed?.Invoke(false);
    }

    /// <summary>While offline, checks the server now, unless a check is already running.</summary>
    public void CheckNow()
    {
        lock (_gate)
        {
            if (_online || _checking || _disposed)
            {
                return;
            }

            _checking = true;
        }

        _ = RunCheckAsync();
    }

    private async Task RunCheckAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(_checkTimeout);
            await _check(timeout.Token).ConfigureAwait(false);
            lock (_gate)
            {
                _checking = false;
            }

            Succeeded();
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _checking = false;
                if (_online || _disposed)
                {
                    return;
                }

                _failedChecks++;
                _timer.Change(NextCheckAfter(_failedChecks), Timeout.InfiniteTimeSpan);
            }

            _log?.Invoke($"The server still cannot be reached: {Describe(ex)}");
        }
    }

    /// <summary>Nextcloud's backoff: one interval per failed check, at least one, at most eight.</summary>
    public static TimeSpan NextCheckAfter(int failedChecks) =>
        CheckInterval * Math.Clamp(failedChecks, 1, MaxIntervals);

    /// <summary>
    /// Whether a failed request means the server cannot be reached, as opposed
    /// to an answer it gave (a missing file, a refused login): no answer in
    /// time, no connection to rclone, or rclone's own report of a network
    /// failure on its way to the server.
    /// </summary>
    public static bool IsUnreachable(Exception ex) => ex switch
    {
        TimeoutException or OperationCanceledException => true,
        System.Net.Http.HttpRequestException { StatusCode: null } => true,
        _ when ex.InnerException is { } inner && IsUnreachable(inner) => true,
        _ => NetworkFailures.Any(text => ex.Message.Contains(text, StringComparison.OrdinalIgnoreCase)),
    };

    /// <summary>How Go's network stack, under rclone, words a server it cannot reach.</summary>
    private static readonly string[] NetworkFailures =
    [
        "dial tcp", "no such host", "i/o timeout", "deadline exceeded", "network is unreachable",
        "connection refused", "connection reset", "TLS handshake timeout", "No connection could be made",
    ];

    private static string Describe(Exception ex) =>
        ex is OperationCanceledException ? "no answer in time" : ex.Message;

    private void OnNetworkChanged(object? sender, EventArgs e) => CheckNow();

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _timer.Dispose();
    }
}
