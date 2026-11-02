using System.Net;
using System.Net.Http.Headers;

namespace OpenCfDrive.Core.Services;

/// <summary>
/// Reads object bytes from the rclone daemon's <c>--rc-serve</c> HTTP server.
/// Verified against rclone v1.75.2: the URL shape is
/// <c>http://host:port/[remote:path]/relative/file</c> and byte ranges are
/// honoured with HTTP 206 + Content-Range.
/// </summary>
public sealed class RcloneServeClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _remotePrefix;
    private long _bytesFetched;

    public RcloneServeClient(string rcUrl, string remote)
    {
        _baseUrl = rcUrl.TrimEnd('/');
        _remotePrefix = remote.TrimEnd('/');
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>Total payload bytes read from rclone; proves range hydration.</summary>
    public long BytesFetched => Interlocked.Read(ref _bytesFetched);

    public Uri BuildUri(string remoteRelativePath)
    {
        var segments = remoteRelativePath
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);

        return new Uri($"{_baseUrl}/[{_remotePrefix}]/{string.Join('/', segments)}");
    }

    /// <summary>
    /// Requests exactly <paramref name="length"/> bytes at <paramref name="offset"/>.
    /// When the server ignores Range and returns 200 the stream is advanced
    /// manually so callers always observe the requested window.
    /// </summary>
    public async Task<Stream> OpenRangeAsync(
        string remoteRelativePath,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildUri(remoteRelativePath));
        if (offset > 0 || length >= 0)
        {
            request.Headers.Range = length >= 0
                ? new RangeHeaderValue(offset, offset + length - 1)
                : new RangeHeaderValue(offset, null);
        }

        var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return new LengthLimitedStream(stream, length, CountBytes);
        }

        // Server ignored the range request (200 OK): discard the prefix.
        if (offset > 0)
        {
            await SkipAsync(stream, offset, cancellationToken);
        }

        return new LengthLimitedStream(stream, length, CountBytes);
    }

    public async Task<bool> ExistsAsync(string remoteRelativePath, CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Head, BuildUri(remoteRelativePath)),
            cancellationToken);

        return response.IsSuccessStatusCode;
    }

    private static async Task SkipAsync(Stream stream, long count, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        var remaining = count;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            remaining -= read;
        }
    }

    private void CountBytes(int count) => Interlocked.Add(ref _bytesFetched, count);

    public void ResetBytesFetched() => Interlocked.Exchange(ref _bytesFetched, 0);

    public void Dispose() => _httpClient.Dispose();

    /// <summary>Bounded read-only view over a stream, used for exact range reads.</summary>
    private sealed class LengthLimitedStream(Stream inner, long limit, Action<int>? onRead = null) : Stream
    {
        private long _remaining = limit < 0 ? long.MaxValue : limit;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => limit < 0 ? inner.Length : limit;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, _remaining);
            var read = inner.Read(buffer, offset, toRead);
            _remaining -= read;
            onRead?.Invoke(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(buffer.Length, _remaining);
            var read = await inner.ReadAsync(buffer[..toRead], cancellationToken);
            _remaining -= read;
            onRead?.Invoke(read);
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
