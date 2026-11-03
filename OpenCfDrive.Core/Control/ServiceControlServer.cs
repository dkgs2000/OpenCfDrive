using System.IO.Pipes;
using System.Text.Json;

namespace OpenCfDrive.Core.Control;

/// <summary>
/// The service's end of <see cref="ServiceControl"/>: answers status, stop
/// and sync requests until disposed. Each connection is handled on its own,
/// so a client that never sends anything holds up no other.
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
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    /// <param name="syncNow">A sync run with the server; the request is answered when it has finished.</param>
    public ServiceControlServer(
        string pipeName,
        Func<ServiceStatus> getStatus,
        Action requestStop,
        Action<string>? log = null,
        Func<Task>? syncNow = null)
    {
        _pipeName = pipeName;
        _getStatus = getStatus;
        _requestStop = requestStop;
        _log = log;
        _syncNow = syncNow;
    }

    public void Start() => _loop ??= Task.Run(AcceptLoopAsync);

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
