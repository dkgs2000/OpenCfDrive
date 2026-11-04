using System.Net.Http.Headers;
using System.Text.Json;
using OpenCfDrive.Core.Models;

namespace OpenCfDrive.Core.Services;

/// <summary>
/// What a listing or lookup returns besides names and sizes. Mime types are
/// never asked for: nothing uses them, and on some remotes (S3) each costs a
/// request per file.
/// </summary>
/// <param name="HashType">The hash to return with each file (rclone's name), or none.</param>
/// <param name="WithTimes">
/// Each file's time. On remotes where it costs a request per file (S3) a
/// listing without them is one request for the whole folder.
/// </param>
public sealed record ListingOptions(string? HashType = null, bool WithTimes = true)
{
    public static ListingOptions Default { get; } = new();
}

/// <summary>
/// Thin typed client over the rclone Remote Control API (rclone rcd).
///
/// Endpoint quirks verified against rclone v1.75.2:
///  - <c>operations/list</c> requires BOTH <c>fs</c> and <c>remote</c> keys.
///  - <c>operations/readfile</c> does not exist; raw bytes come from --rc-serve
///    (see <see cref="RcloneServeClient"/>).
///  - <c>operations/uploadfile</c> takes <c>fs</c>/<c>remote</c> as query
///    parameters and the payload in a multipart field named <c>file</c>.
/// </summary>
public sealed class RcloneRcClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _rcUrl;

    public RcloneRcClient(string rcUrl)
        : this(new RcloneEndpoint(rcUrl))
    {
    }

    public RcloneRcClient(RcloneEndpoint endpoint)
    {
        _rcUrl = endpoint.BaseUrl;
        _httpClient = endpoint.CreateHttpClient(TimeSpan.FromMinutes(10));
    }

    public string RcUrl => _rcUrl;

    public static (string Fs, string Remote) SplitRemote(string remotePath)
    {
        var value = remotePath.Trim();
        var colon = value.IndexOf(':');
        if (colon < 0)
        {
            return (value, string.Empty);
        }

        var fs = value[..(colon + 1)];
        var remote = value[(colon + 1)..].Trim('/');
        return (fs, remote);
    }

    /// <summary>
    /// Lists everything below <paramref name="remotePath"/>, recursively. Like
    /// <see cref="ListDirectoryAsync"/>, paths are relative to it.
    /// </summary>
    public async Task<IReadOnlyList<CloudItem>> ListFilesAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var (fs, remote) = SplitRemote(remotePath);
        return RelativeTo(remote, await ListAsync(fs, remote, recurse: true, ListingOptions.Default, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists one directory (files and subdirectories, not recursive). The
    /// returned paths are relative to <paramref name="remotePath"/>, like the
    /// paths the metadata store and the serve client use.
    /// </summary>
    public Task<IReadOnlyList<CloudItem>> ListDirectoryAsync(
        string remotePath,
        string relativeDirectory,
        CancellationToken cancellationToken = default) =>
        ListDirectoryAsync(remotePath, relativeDirectory, ListingOptions.Default, cancellationToken);

    public async Task<IReadOnlyList<CloudItem>> ListDirectoryAsync(
        string remotePath,
        string relativeDirectory,
        ListingOptions options,
        CancellationToken cancellationToken = default)
    {
        var (fs, baseRemote) = SplitRemote(remotePath);
        var directory = relativeDirectory.Replace('\\', '/').Trim('/');
        var remote = string.Join('/', new[] { baseRemote, directory }.Where(p => p.Length > 0));

        var items = await ListAsync(fs, remote, recurse: false, options, cancellationToken).ConfigureAwait(false);
        return RelativeTo(baseRemote, items);
    }

    /// <summary>The <c>opt</c> of operations/list and operations/stat.</summary>
    private static Dictionary<string, object> ListingOpt(ListingOptions options, bool recurse)
    {
        var opt = new Dictionary<string, object> { ["recurse"] = recurse, ["noMimeType"] = true };
        if (!options.WithTimes)
        {
            opt["noModTime"] = true;
        }

        if (options.HashType is { } hashType)
        {
            opt["showHash"] = true;
            opt["hashTypes"] = new[] { hashType };
        }

        return opt;
    }

    /// <summary>
    /// operations/list reports paths relative to the fs root, which includes a
    /// configured sub path ("remote:bucket" lists "bucket/file"). The rest of
    /// the client works with paths relative to the configured remote.
    /// </summary>
    private static IReadOnlyList<CloudItem> RelativeTo(string baseRemote, IReadOnlyList<CloudItem> items)
    {
        if (baseRemote.Length == 0)
        {
            return items;
        }

        var prefix = baseRemote + "/";
        return items
            .Select(item => item.Path.StartsWith(prefix, StringComparison.Ordinal)
                ? new CloudItem
                {
                    Path = item.Path[prefix.Length..],
                    Name = item.Name,
                    IsDirectory = item.IsDirectory,
                    Size = item.Size,
                    ModifiedTime = item.ModifiedTime,
                    MimeType = item.MimeType,
                    Hash = item.Hash,
                }
                : item)
            .ToList();
    }

    private async Task<IReadOnlyList<CloudItem>> ListAsync(
        string fs,
        string remote,
        bool recurse,
        ListingOptions options,
        CancellationToken cancellationToken)
    {
        // rclone v1.75 ignores a top-level "recursive" key: recursion is a
        // listing option and must be sent as opt.recurse, otherwise nested
        // files are silently omitted from the namespace.
        var payload = new
        {
            fs,
            remote,
            opt = ListingOpt(options, recurse),
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_rcUrl}/operations/list")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var items = new List<CloudItem>();
        if (!doc.RootElement.TryGetProperty("list", out var listElement) || listElement.ValueKind != JsonValueKind.Array)
        {
            return items;
        }

        foreach (var entry in listElement.EnumerateArray())
        {
            if (ParseItem(entry, options.HashType) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>
    /// One item's metadata (<c>operations/stat</c>), or null if there is
    /// nothing at that path. Costs the server one lookup, where listing the
    /// parent costs a read of the whole folder. The path is relative to
    /// <paramref name="remotePath"/>.
    /// </summary>
    public Task<CloudItem?> StatAsync(
        string remotePath,
        string relativePath,
        CancellationToken cancellationToken = default) =>
        StatAsync(remotePath, relativePath, ListingOptions.Default, cancellationToken);

    public async Task<CloudItem?> StatAsync(
        string remotePath,
        string relativePath,
        ListingOptions options,
        CancellationToken cancellationToken = default)
    {
        var (fs, baseRemote) = SplitRemote(remotePath);
        var remote = string.Join('/', new[] { baseRemote, relativePath.Replace('\\', '/').Trim('/') }.Where(p => p.Length > 0));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_rcUrl}/operations/stat")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { fs, remote, opt = ListingOpt(options, recurse: false) }),
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!doc.RootElement.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ParseItem(itemElement, options.HashType) is { } item ? RelativeTo(baseRemote, [item])[0] : null;
    }

    /// <param name="hashType">The hash asked for, kept as "type:value"; files without one have none.</param>
    private static CloudItem? ParseItem(JsonElement entry, string? hashType)
    {
        var path = entry.TryGetProperty("Path", out var pathProp) ? pathProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        DateTimeOffset? modified = null;
        if (entry.TryGetProperty("ModTime", out var modProp) && modProp.ValueKind == JsonValueKind.String)
        {
            var raw = modProp.GetString();
            if (!string.IsNullOrWhiteSpace(raw) && DateTimeOffset.TryParse(raw, out var parsed))
            {
                modified = parsed;
            }
        }

        return new CloudItem
        {
            Path = path,
            Name = entry.TryGetProperty("Name", out var nameProp)
                ? nameProp.GetString() ?? Path.GetFileName(path)
                : Path.GetFileName(path),
            IsDirectory = entry.TryGetProperty("IsDir", out var dirProp) && dirProp.ValueKind == JsonValueKind.True,
            Size = entry.TryGetProperty("Size", out var sizeProp) && sizeProp.TryGetInt64(out var size) ? size : 0,
            ModifiedTime = modified,
            MimeType = entry.TryGetProperty("MimeType", out var mimeProp) ? mimeProp.GetString() : null,
            Hash = hashType is not null &&
                   entry.TryGetProperty("Hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object &&
                   hashes.TryGetProperty(hashType, out var hashProp) && hashProp.GetString() is { Length: > 0 } hash
                ? $"{hashType}:{hash}"
                : null,
        };
    }

    /// <summary>
    /// Uploads a local file via <c>POST /operations/uploadfile</c>
    /// (multipart/form-data with the bytes in the <c>file</c> field).
    /// </summary>
    public async Task UploadFileAsync(
        string fs,
        string remoteDirectory,
        string localFilePath,
        CancellationToken cancellationToken = default)
    {
        var fsQuery = Uri.EscapeDataString(fs);
        var remoteQuery = Uri.EscapeDataString(remoteDirectory.Trim('/'));
        var url = $"{_rcUrl}/operations/uploadfile?fs={fsQuery}&remote={remoteQuery}";

        using var form = new MultipartFormDataContent();
        await using var fileStream = File.OpenRead(localFilePath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(localFilePath));

        using var response = await _httpClient.PostAsync(url, form, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"operations/uploadfile failed ({(int)response.StatusCode}): {body}");
        }
    }

    public async Task DeleteFileAsync(string fs, string remoteFile, CancellationToken cancellationToken = default)
    {
        // The error body is what says "object not found"; TryDeleteFileAsync
        // needs it to treat a second delete of the same file as done.
        await PostAsync("operations/deletefile", new { fs, remote = remoteFile }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a remote object, treating "already gone" as success. Delete
    /// notifications are delivered by both cfapi and the filesystem watcher, so
    /// this has to be idempotent.
    /// </summary>
    public async Task<bool> TryDeleteFileAsync(string fs, string remoteFile, CancellationToken cancellationToken = default)
    {
        try
        {
            await DeleteFileAsync(fs, remoteFile, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException ex) when (IsMissingObject(ex.Message))
        {
            return true;
        }
        catch (Exception ex) when (IsMissingObject(ex.Message))
        {
            return true;
        }
    }

    /// <summary>Moves/renames a remote object within the same remote.</summary>
    public async Task MoveFileAsync(
        string fs,
        string sourceRemote,
        string destinationRemote,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            srcFs = fs,
            srcRemote = sourceRemote,
            dstFs = fs,
            dstRemote = destinationRemote,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_rcUrl}/operations/movefile")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"operations/movefile failed ({(int)response.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Moves a whole directory (<c>sync/move</c>). Uses the server's own
    /// directory move where it has one; otherwise rclone copies and deletes.
    /// </summary>
    /// <summary>
    /// What the remote keeps and what costs extra to read
    /// (<c>operations/fsinfo</c>): its hashes, how exact its times are, and
    /// whether it keeps a folder with nothing in it (bucket stores such as S3
    /// only have folders as parts of file names).
    /// </summary>
    public async Task<RemoteCapabilities> GetCapabilitiesAsync(string remotePath, CancellationToken cancellationToken = default)
    {
        var (fs, _) = SplitRemote(remotePath);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_rcUrl}/operations/fsinfo")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { fs }), System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return RemoteCapabilities.FromFsInfo(doc.RootElement);
    }

    /// <summary>
    /// A local file's hash of the given type (<c>operations/hashsumfile</c>),
    /// lowercase hex, or null if rclone has none for it. rclone makes every
    /// type it knows, a remote's own (QuickXorHash, Dropbox's) included, so a
    /// local file can be compared with any remote.
    /// </summary>
    public async Task<string?> HashLocalFileAsync(string localPath, string hashType, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            fs = Path.GetDirectoryName(Path.GetFullPath(localPath)),
            remote = Path.GetFileName(localPath),
            hashType,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_rcUrl}/operations/hashsumfile")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), System.Text.Encoding.UTF8, "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"operations/hashsumfile failed ({(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("hash", out var hash) && hash.GetString() is { Length: > 0 } value
            ? value.ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Creates a folder and any missing parents (<c>operations/mkdir</c>); one
    /// that exists already is fine. Remotes without real folders (S3 without
    /// directory markers) accept it and keep nothing until a file is inside.
    /// </summary>
    public async Task MakeDirectoryAsync(string fs, string remoteDirectory, CancellationToken cancellationToken = default)
    {
        await PostAsync("operations/mkdir", new { fs, remote = remoteDirectory }, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveDirectoryAsync(string srcFs, string dstFs, CancellationToken cancellationToken = default)
    {
        // Empty folders move too: a folder made here is on the server even
        // with nothing in it, and must stay there under its new name.
        await PostAsync("sync/move", new { srcFs, dstFs, createEmptySrcDirs = true, deleteEmptySrcDirs = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes empty directories at and below a path (<c>operations/rmdirs</c>).
    /// A directory that still holds files is left alone, so this never
    /// deletes anything the client has not seen. Missing paths are fine.
    /// </summary>
    public async Task RemoveEmptyDirectoriesAsync(string fs, string remoteDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            await PostAsync("operations/rmdirs", new { fs, remote = remoteDirectory, leaveRoot = false }, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Best effort: missing, not a directory, or not empty all mean
            // there is nothing more to remove.
        }
    }

    private async Task PostAsync(string command, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_rcUrl}/{command}")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8,
                "application/json"),
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"{command} failed ({(int)response.StatusCode}): {body}");
        }
    }

    private static bool IsMissingObject(string message) =>
        message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("object not found", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("directory not found", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _httpClient.Dispose();
}
