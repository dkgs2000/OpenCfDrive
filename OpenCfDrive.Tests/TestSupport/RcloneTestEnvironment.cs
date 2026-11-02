using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace OpenCfDrive.Tests.TestSupport;

/// <summary>
/// Starts an isolated WebDAV server (<c>rclone serve webdav</c> over a temp
/// folder, in its own process) plus a dedicated <c>rclone rcd</c> instance on
/// ephemeral ports. Nothing is shared with the developer's environment: temp
/// config, temp folders. Needs nothing but rclone.
/// </summary>
public sealed class RcloneTestEnvironment : IDisposable
{
    private readonly Process _rcd;
    private readonly Process _server;

    private RcloneTestEnvironment(
        Process rcd,
        Process server,
        string tempRoot,
        string rcUrl,
        string webDavUrl,
        int rcPort,
        string remoteName)
    {
        _rcd = rcd;
        _server = server;
        TempRoot = tempRoot;
        RcUrl = rcUrl;
        WebDavUrl = webDavUrl;
        RcPort = rcPort;
        RemoteName = remoteName;
        ConfigPath = Path.Combine(tempRoot, "rclone.conf");
    }

    public string TempRoot { get; }
    public string RcUrl { get; }
    public string WebDavUrl { get; }
    public int RcPort { get; }
    public string RemoteName { get; }
    public string ConfigPath { get; }
    public string RemoteSpec => $"{RemoteName}:";

    private const string BasicAuthHeader =
        "Basic " + "ZGVtbzpkZW1v"; // demo:demo

    public static RcloneTestEnvironment Start(IReadOnlyDictionary<string, byte[]>? seedFiles = null)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "opencfdrive-e2e", Guid.NewGuid().ToString("N"));
        var serverData = Path.Combine(tempRoot, "server-data");
        Directory.CreateDirectory(serverData);

        var webDavPort = GetFreePort();
        var server = StartRclone(
            "serve", "webdav", serverData,
            "--addr", $"127.0.0.1:{webDavPort}",
            "--user", "demo", "--pass", "demo",
            "--log-file", Path.Combine(tempRoot, "server.log"));

        var webDavUrl = $"http://127.0.0.1:{webDavPort}/";
        WaitForHttp(webDavUrl, TimeSpan.FromSeconds(60));

        foreach (var (name, bytes) in seedFiles ?? new Dictionary<string, byte[]>())
        {
            PutFile(webDavUrl, name, bytes);
        }

        var configPath = Path.Combine(tempRoot, "rclone.conf");
        Run(Prerequisites.RcloneExe, "--config", configPath, "config", "create",
            "e2e", "webdav", $"url={webDavUrl}", "user=demo", "pass=demo", "vendor=other");

        var rcPort = GetFreePort();
        var rcd = StartRclone(
            "--config", configPath,
            "rcd",
            "--rc-addr", $"127.0.0.1:{rcPort}",
            "--rc-no-auth",
            "--rc-serve",
            "--log-file", Path.Combine(tempRoot, "rcd.log"),
            "--log-level", "INFO");

        var rcUrl = $"http://127.0.0.1:{rcPort}";
        WaitForRcloneRc(rcUrl, TimeSpan.FromSeconds(60));

        return new RcloneTestEnvironment(rcd, server, tempRoot, rcUrl, webDavUrl, rcPort, "e2e");
    }

    private static Process StartRclone(params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = Prerequisites.RcloneExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        return Process.Start(info) ?? throw new InvalidOperationException($"Failed to start rclone {arguments[0]}.");
    }

    public static void PutFile(string webDavUrl, string relativePath, byte[] content)
    {
        EnsureParentCollections(webDavUrl, relativePath);

        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Put, webDavUrl + relativePath)
        {
            Content = new ByteArrayContent(content),
        };
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicAuthHeader);

        using var response = client.Send(request);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>WebDAV refuses PUTs into missing collections; create them first.</summary>
    private static void EnsureParentCollections(string webDavUrl, string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            return;
        }

        using var client = new HttpClient();
        for (var i = 0; i < segments.Length - 1; i++)
        {
            var collection = string.Join('/', segments.Take(i + 1)) + "/";
            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), webDavUrl + collection);
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicAuthHeader);

            using var response = client.Send(request);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MethodNotAllowed)
            {
                throw new InvalidOperationException(
                    $"MKCOL {collection} failed with {(int)response.StatusCode}.");
            }
        }
    }

    public static string GetFile(string webDavUrl, string relativePath)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, webDavUrl + relativePath);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicAuthHeader);
        using var response = client.Send(request);
        response.EnsureSuccessStatusCode();
        return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
    }

    /// <summary>True when the WebDAV object exists; false on 404.</summary>
    public static bool RemoteFileExists(string webDavUrl, string relativePath)
    {
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, webDavUrl + relativePath);
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicAuthHeader);
        using var response = client.Send(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        response.EnsureSuccessStatusCode();
        return true;
    }

    private static void WaitForHttp(string url, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(BasicAuthHeader);
                using var response = client.Send(request);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // server still starting
            }

            Thread.Sleep(500);
        }

        throw new TimeoutException($"WebDAV did not become ready at {url}.");
    }

    private static void WaitForRcloneRc(string rcUrl, TimeSpan timeout)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var content = new StringContent(
                    """{"fs":"e2e:","remote":"","opt":{"recurse":true}}""",
                    System.Text.Encoding.UTF8,
                    "application/json");
                using var response = client.PostAsync($"{rcUrl}/operations/list", content).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // rcd still starting
            }

            Thread.Sleep(500);
        }

        throw new TimeoutException($"rclone rcd did not become ready at {rcUrl}.");
    }

    public static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string Run(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{fileName} {string.Join(' ', arguments)} failed ({process.ExitCode}): {stderr}{stdout}");
        }

        return stdout;
    }

    public void Dispose()
    {
        foreach (var process in new[] { _rcd, _server })
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // ignored
            }
            finally
            {
                process.Dispose();
            }
        }

        try
        {
            Directory.Delete(TempRoot, recursive: true);
        }
        catch
        {
            // ignored
        }
    }
}
