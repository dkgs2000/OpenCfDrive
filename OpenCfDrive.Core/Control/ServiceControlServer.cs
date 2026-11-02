using System.IO.Pipes;
using System.Text.Json;

namespace OpenCfDrive.Core.Control;

/// <summary>
/// The service's end of <see cref="ServiceControl"/>: answers status
/// requests and stop requests until disposed. Each connection is handled on
/// its own, so a client that never sends anything holds up no other.
/// </summary>
public sealed class ServiceControlServer : IAsyncDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly string _pipeName;
    private readonly Func<ServiceStatus> _getStatus;
    private readonly Action _requestStop;
    private readonly Action<string>? _log;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public ServiceControlServer(string pipeName, Func<ServiceStatus> getStatus, Action requestStop, Action<string>? log = null)
    {
        _pipeName = pipeName;
        _getStatus = getStatus;
        _requestStop = requestStop;
        _log = log;
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
            var answer = stop
                ? """{"stopping":true}"""
                : string.Equals(request, ServiceControl.StatusRequest, StringComparison.OrdinalIgnoreCase)
                    ? JsonSerializer.Serialize(_getStatus(), ServiceControl.Json)
                    : """{"error":"unknown request"}""";

            await using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(answer.AsMemory(), timeout.Token).ConfigureAwait(false);
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
