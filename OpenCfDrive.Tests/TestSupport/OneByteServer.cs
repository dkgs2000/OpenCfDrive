using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenCfDrive.Tests.TestSupport;

/// <summary>
/// Answers every GET with the one byte "z" (or, failing, with a 500), once
/// <c>beforeAnswer</c> is done.
/// </summary>
internal sealed class OneByteServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public OneByteServer(Func<Task> beforeAnswer, bool fail = false)
    {
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                var request = new StringBuilder();
                var buffer = new byte[4096];
                while (!request.ToString().Contains("\r\n\r\n"))
                {
                    var read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    request.Append(Encoding.ASCII.GetString(buffer, 0, read));
                }

                Requests.Enqueue(request.ToString().Split("\r\n")[0]);
                await beforeAnswer();
                var answer = fail
                    ? "HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray()
                    : "HTTP/1.1 200 OK\r\nContent-Length: 1\r\nConnection: close\r\n\r\nz"u8.ToArray();
                await stream.WriteAsync(answer, _stop.Token);
            }
        });
    }

    public string Url { get; }

    /// <summary>Each request's first line ("GET /path HTTP/1.1").</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
