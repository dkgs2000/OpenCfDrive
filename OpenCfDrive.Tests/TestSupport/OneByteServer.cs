using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenCfDrive.Tests.TestSupport;

/// <summary>
/// Answers every GET with the one byte "z" (or, failing, with a 500), once
/// <c>beforeAnswer</c> is done. A <paramref name="body"/> is sent instead,
/// optionally a <paramref name="chunkSize"/> at a time with a
/// <paramref name="pause"/> between pieces, for slow large downloads. A range
/// request gets just that range (206), as rclone's server answers: Windows can
/// ask for a large file in several ranges. Each connection is answered on its
/// own, and one the client closes early (it has the range it wanted) does not
/// stop the next.
/// </summary>
internal sealed class OneByteServer : IDisposable
{
    private static readonly Regex RangeHeader = new(@"^Range:\s*bytes=(\d+)-(\d*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<Task> _beforeAnswer;
    private readonly bool _fail;
    private readonly byte[] _body;
    private readonly int _chunkSize;
    private readonly TimeSpan? _pause;

    public OneByteServer(
        Func<Task> beforeAnswer,
        bool fail = false,
        byte[]? body = null,
        int chunkSize = 0,
        TimeSpan? pause = null)
    {
        _beforeAnswer = beforeAnswer;
        _fail = fail;
        _body = body ?? "z"u8.ToArray();
        _chunkSize = chunkSize;
        _pause = pause;
        _listener.Start();
        Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = Task.Run(() => AnswerAsync(client));
            }
        });
    }

    public string Url { get; }

    /// <summary>Each request's first line ("GET /path HTTP/1.1").</summary>
    public ConcurrentQueue<string> Requests { get; } = new();

    private async Task AnswerAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
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

                var text = request.ToString();
                Requests.Enqueue(text.Split("\r\n")[0]);
                await _beforeAnswer();

                if (_fail)
                {
                    await stream.WriteAsync("HTTP/1.1 500 Internal Server Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), _stop.Token);
                    return;
                }

                // The range asked for, else the whole body.
                var (start, end, status) = (0L, _body.Length - 1L, "200 OK");
                var range = RangeHeader.Match(text);
                if (range.Success && _body.Length > 0)
                {
                    start = Math.Min(long.Parse(range.Groups[1].Value), _body.Length - 1L);
                    end = range.Groups[2].Value.Length > 0 ? Math.Min(long.Parse(range.Groups[2].Value), _body.Length - 1L) : _body.Length - 1L;
                    status = "206 Partial Content";
                }

                var length = (int)(end - start + 1);
                var header = $"HTTP/1.1 {status}\r\nContent-Length: {length}\r\n" +
                             (status.StartsWith("206", StringComparison.Ordinal) ? $"Content-Range: bytes {start}-{end}/{_body.Length}\r\n" : "") +
                             "Connection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);

                var size = _chunkSize > 0 ? _chunkSize : length;
                for (var offset = 0; offset < length; offset += size)
                {
                    await stream.WriteAsync(_body.AsMemory((int)start + offset, Math.Min(size, length - offset)), _stop.Token);
                    if (_pause is { } wait)
                    {
                        await Task.Delay(wait, _stop.Token);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                // The client closed the connection, or the server is stopping.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
