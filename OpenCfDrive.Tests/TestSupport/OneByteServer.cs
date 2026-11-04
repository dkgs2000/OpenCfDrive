using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace OpenCfDrive.Tests.TestSupport;

/// <summary>
/// Answers every GET with the one byte "z" (or, failing, with a 500), once
/// <c>beforeAnswer</c> is done. A <paramref name="body"/> is sent instead,
/// optionally a <paramref name="chunkSize"/> at a time with a
/// <paramref name="pause"/> between pieces, for slow large downloads.
/// </summary>
internal sealed class OneByteServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public OneByteServer(
        Func<Task> beforeAnswer,
        bool fail = false,
        byte[]? body = null,
        int chunkSize = 0,
        TimeSpan? pause = null)
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

                var content = fail ? Array.Empty<byte>() : body ?? "z"u8.ToArray();
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(fail ? "500 Internal Server Error" : "200 OK")}\r\n" +
                    $"Content-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, _stop.Token);

                var size = chunkSize > 0 ? chunkSize : content.Length;
                for (var offset = 0; !fail && offset < content.Length; offset += size)
                {
                    var count = Math.Min(size, content.Length - offset);
                    await stream.WriteAsync(content.AsMemory(offset, count), _stop.Token);
                    if (pause is { } wait)
                    {
                        await Task.Delay(wait, _stop.Token);
                    }
                }
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
