using System.IO.Pipes;
using System.Text.Json;

namespace OpenCfDrive.Core.Control;

/// <summary>
/// The service's end of <see cref="ServiceControl"/>: answers status, stop,
/// sync and watch requests until disposed. Each connection is handled on its
/// own, so a client that never sends anything holds up no other.
/// </summary>
public sealed class ServiceControlServer : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long a sync request may take before it is answered anyway.</summary>
    private static readonly TimeSpan SyncTimeout = TimeSpan.FromMinutes(5);

    private readonly string _pipeName;
    private readonly Func<ServiceStatus> _getStatus;
    private readonly Action _requestStop;
    private readonly Action<string>? _log;
    private readonly Func<Task>? _syncNow;
    private readonly Func<ServicePulse>? _pulse;
    private readonly List<SemaphoreSlim> _watchers = [];
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <param name="syncNow">A sync run with the server; the request is answered when it has finished.</param>
    /// <param name="pulse">What a watching app is sent, at once and after each <see cref="NotifyChanged"/>.</param>
    public ServiceControlServer(
        string pipeName,
        Func<ServiceStatus> getStatus,
        Action requestStop,
        Action<string>? log = null,
        Func<Task>? syncNow = null,
        Func<ServicePulse>? pulse = null)
    {
        _pipeName = pipeName;
        _getStatus = getStatus;
        _requestStop = requestStop;
        _log = log;
        _syncNow = syncNow;
        _pulse = pulse;
    }

    public void Start() => _loop ??= Task.Run(AcceptLoopAsync);

    /// <summary>
    /// What a pulse says has changed: each watching app is sent the new one.
    /// Changes made while one is being sent are sent together, as the next.
    /// </summary>
    public void NotifyChanged()
    {
        lock (_watchers)
        {
            foreach (var changed in _watchers)
            {
                // Only the watcher takes from it, so it stays at most 1.
                if (changed.CurrentCount == 0)
                {
                    changed.Release();
                }
            }
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stopping.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log?.Invoke($"Status channel unavailable: {ex.Message}");
                return;
            }

            _ = HandleAsync(pipe);
        }
    }

    private async Task HandleAsync(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var request = (await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false))?.Trim();

            if (string.Equals(request, ServiceControl.WatchRequest, StringComparison.OrdinalIgnoreCase) && _pulse is not null)
            {
                await WatchAsync(pipe, reader).ConfigureAwait(false);
                return;
            }

            var stop = string.Equals(request, ServiceControl.StopRequest, StringComparison.OrdinalIgnoreCase);
            string answer;
            if (stop)
            {
                answer = """{"stopping":true}""";
            }
            else if (string.Equals(request, ServiceControl.StatusRequest, StringComparison.OrdinalIgnoreCase))
            {
                answer = JsonSerializer.Serialize(_getStatus(), ServiceControl.Json);
            }
            else if (string.Equals(request, ServiceControl.SyncRequest, StringComparison.OrdinalIgnoreCase) && _syncNow is not null)
            {
                // Answered once the run has finished, so the client can wait on it.
                var run = _syncNow();
                var finished = await Task.WhenAny(run, Task.Delay(SyncTimeout, _stopping.Token)).ConfigureAwait(false) == run;
                answer = finished && run.IsCompletedSuccessfully ? """{"synced":true}""" : """{"synced":false}""";
            }
            else
            {
                answer = """{"error":"unknown request"}""";
            }

            // Its own time limit: a sync may have taken longer than the request's.
            using var writing = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            writing.CancelAfter(RequestTimeout);
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(answer.AsMemory(), writing.Token).ConfigureAwait(false);
            pipe.WaitForPipeDrain();

            if (stop)
            {
                _log?.Invoke("Stop requested by the OpenCfDrive app.");
                _requestStop();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The client went away or never asked; nothing to answer.
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Status request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a pulse now and after each change, until the app leaves (its
    /// end of the pipe closes) or the service stops.
    /// </summary>
    private async Task WatchAsync(NamedPipeServerStream pipe, StreamReader reader)
    {
        var changed = new SemaphoreSlim(0, 1);
        lock (_watchers)
        {
            _watchers.Add(changed);
        }

        try
        {
            // The app sends nothing more; the read ends when it leaves.
            var gone = reader.ReadLineAsync(_stopping.Token).AsTask();
            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            while (true)
            {
                var line = JsonSerializer.Serialize(_pulse!(), ServiceControl.Json);
                using (var writing = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
                {
                    writing.CancelAfter(RequestTimeout);
                    await writer.WriteLineAsync(line.AsMemory(), writing.Token).ConfigureAwait(false);
                }

                var next = changed.WaitAsync(_stopping.Token);
                if (await Task.WhenAny(next, gone).ConfigureAwait(false) == gone)
                {
                    return;
                }

                await next.ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_watchers)
            {
                _watchers.Remove(changed);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            await _loop.ConfigureAwait(false);
        }

        _stopping.Dispose();
    }
}
