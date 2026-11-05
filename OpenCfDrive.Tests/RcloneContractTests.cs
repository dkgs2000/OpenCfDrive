using System.Net;
using System.Net.Http.Headers;
using OpenCfDrive.Core.Services;
using OpenCfDrive.Tests.TestSupport;

namespace OpenCfDrive.Tests;

/// <summary>
/// Pins the rclone RC/serve contract our sync engine depends on. If rclone
/// changes these behaviours, these tests fail before the mount tests do.
/// Requires the rclone binary (it also serves the WebDAV server); skipped
/// otherwise.
/// </summary>
public class RcloneContractTests
{
    [SkippableFact]
    public void OperationsList_RequiresFsAndRemote_AndReturnsSeededFiles()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        using var env = RcloneTestEnvironment.Start(new Dictionary<string, byte[]>
        {
            ["alpha.txt"] = "content-for-alpha.txt"u8.ToArray(),
            ["beta.txt"] = "content-for-beta.txt"u8.ToArray(),
            ["nested/data.txt"] = "nested-content"u8.ToArray(),
        });

        using var client = new HttpClient();

        // fs alone is rejected by rclone 1.75 (it insists on both keys).
        using var partial = new StringContent(
            """{"fs":"e2e:","recursive":true}""",
            System.Text.Encoding.UTF8,
            "application/json");
        using var partialResponse = client.PostAsync($"{env.RcUrl}/operations/list", partial).GetAwaiter().GetResult();
        Assert.Equal(HttpStatusCode.BadRequest, partialResponse.StatusCode);

        var rc = new RcloneRcClient(env.RcUrl);
        var items = rc.ListFilesAsync(env.RemoteSpec).GetAwaiter().GetResult();

        Assert.Equal(4, items.Count); // alpha, beta, nested/ (dir), nested/data.txt
        Assert.Contains(items, i => i.Path == "alpha.txt" && i.Size == 21);
        Assert.Contains(items, i => i.Path == "beta.txt" && i.Size == 20);
        Assert.Contains(items, i => i.Path == "nested/data.txt" && i.Size == 14);
    }

    [SkippableFact]
    public void OperationsList_DoesNotRecurse_WhenOptRecurseIsAbsent()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        using var env = RcloneTestEnvironment.Start(new Dictionary<string, byte[]>
        {
            ["root.txt"] = "root"u8.ToArray(),
            ["nested/data.txt"] = "nested-content"u8.ToArray(),
        });

        using var client = new HttpClient();
        using var content = new StringContent(
            """{"fs":"e2e:","remote":"","opt":{}}""",
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = client.PostAsync($"{env.RcUrl}/operations/list", content).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();

        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

        // Regression guard: nested children only appear with opt.recurse.
        Assert.Contains("root.txt", body);
        Assert.DoesNotContain("nested/data.txt", body);
    }

    [SkippableFact]
    public void RcServe_HonoursByteRanges()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        using var env = RcloneTestEnvironment.Start(new Dictionary<string, byte[]>
        {
            ["alpha.txt"] = "content-for-alpha.txt"u8.ToArray(),
        });

        using var serve = new RcloneServeClient(env.RcUrl, env.RemoteSpec);
        using var stream = serve.OpenRangeAsync("alpha.txt", 8, 5).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        Assert.Equal("for-a", System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    [SkippableFact]
    public void RcServe_OpenEndedRange_StartsAtOffset()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        using var env = RcloneTestEnvironment.Start(new Dictionary<string, byte[]>
        {
            ["alpha.txt"] = "content-for-alpha.txt"u8.ToArray(),
        });

        using var serve = new RcloneServeClient(env.RcUrl, env.RemoteSpec);
        using var stream = serve.OpenRangeAsync("alpha.txt", 12, -1).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        Assert.Equal("alpha.txt", System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>
    /// A drive whose remote has a path ("remote:folder") downloads through the
    /// remote's root, the path in the URL: rclone never opens the folder
    /// itself as a root, which some servers answer with a redirect it refuses.
    /// </summary>
    [SkippableFact]
    public void RcServe_ARemoteWithAPath_ReadsThroughTheRemotesRoot()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        using var env = RcloneTestEnvironment.Start(new Dictionary<string, byte[]>
        {
            ["drive folder/sub/alpha.txt"] = "content-in-a-folder"u8.ToArray(),
        });

        using var serve = new RcloneServeClient(env.RcUrl, env.RemoteSpec + "drive folder/");
        Assert.Equal($"{env.RcUrl}/[{env.RemoteSpec}]/drive%20folder/sub/alpha.txt", serve.BuildUri("sub/alpha.txt").AbsoluteUri);
        using var stream = serve.OpenRangeAsync("sub/alpha.txt", 0, -1).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        Assert.Equal("content-in-a-folder", System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    [SkippableFact]
    public void Hashes_ComeWithListingsAndLookups_AndALocalFileHashesTheSame()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        using var env = RcloneTestEnvironment.Start();
        var folder = Path.Combine(env.TempRoot, "hashed");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "a.txt");
        File.WriteAllText(file, "hello");
        const string helloMd5 = "md5:5d41402abc4b2a76b9719d911017c592";
        CreateRemote(env, "hashed", "alias", new { remote = folder });

        var rc = new RcloneRcClient(env.RcUrl);
        var listed = Assert.Single(rc.ListDirectoryAsync("hashed:", string.Empty, new ListingOptions("md5")).GetAwaiter().GetResult());
        Assert.Equal(helloMd5, listed.Hash);
        Assert.NotNull(listed.ModifiedTime);

        // Without times: one request for a whole folder on remotes like S3.
        var untimed = Assert.Single(rc.ListDirectoryAsync("hashed:", string.Empty, new ListingOptions("md5", WithTimes: false)).GetAwaiter().GetResult());
        Assert.Equal(helloMd5, untimed.Hash);
        Assert.Null(untimed.ModifiedTime);

        Assert.Equal(helloMd5, rc.StatAsync("hashed:", "a.txt", new ListingOptions("md5")).GetAwaiter().GetResult()?.Hash);
        Assert.Null(rc.StatAsync("hashed:", "a.txt").GetAwaiter().GetResult()?.Hash);

        // Any type rclone knows, a remote's own included.
        Assert.Equal(helloMd5["md5:".Length..], rc.HashLocalFileAsync(file, "md5").GetAwaiter().GetResult());
        Assert.Equal("6828031bd8f00600000000000500000000000000", rc.HashLocalFileAsync(file, "quickxor").GetAwaiter().GetResult());
    }

    [SkippableFact]
    public void Capabilities_TellTheHashToUse()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        using var env = RcloneTestEnvironment.Start();
        CreateRemote(env, "mem", "memory", new { });
        CreateRemote(env, "disk", "alias", new { remote = env.TempRoot });
        var rc = new RcloneRcClient(env.RcUrl);

        // rclone's in-memory backend keeps MD5.
        Assert.Equal("md5", rc.GetCapabilitiesAsync("mem:").GetAwaiter().GetResult().HashType);

        // A local disk makes every hash by reading the file: none used.
        var local = rc.GetCapabilitiesAsync("disk:").GetAwaiter().GetResult();
        Assert.True(local.SlowHash);
        Assert.Null(local.HashType);

        // The WebDAV this environment serves: no hashes, no settable times.
        var webdav = rc.GetCapabilitiesAsync(env.RemoteSpec).GetAwaiter().GetResult();
        Assert.Null(webdav.HashType);
        Assert.False(webdav.KeepsFileTimes);
    }

    /// <summary>A named remote, as the drive is always given one ("name:path").</summary>
    private static void CreateRemote(RcloneTestEnvironment env, string name, string type, object parameters)
    {
        using var client = new HttpClient();
        using var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { name, type, parameters }),
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = client.PostAsync($"{env.RcUrl}/config/create", content).GetAwaiter().GetResult();
        Assert.True(response.IsSuccessStatusCode, response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
    }

    [SkippableFact]
    public void UploadFile_Multipart_LandsOnRemote()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        using var env = RcloneTestEnvironment.Start();

        var localFile = Path.Combine(env.TempRoot, "upload-source.txt");
        File.WriteAllText(localFile, "uploaded-through-rc");

        var rc = new RcloneRcClient(env.RcUrl);
        rc.UploadFileAsync("e2e:", string.Empty, localFile).GetAwaiter().GetResult();

        Assert.Equal("uploaded-through-rc", RcloneTestEnvironment.GetFile(env.WebDavUrl, "upload-source.txt"));
    }

    [SkippableFact]
    public void RcServe_ReturnsAllSeededBytes()
    {
        Skip.IfNot(Prerequisites.RcloneEnvironmentAvailable, "rclone.exe unavailable.");

        var payload = new byte[64 * 1024];
        Random.Shared.NextBytes(payload);

        using var env = RcloneTestEnvironment.Start(new Dictionary<string, byte[]>
        {
            ["blob.bin"] = payload,
        });

        using var serve = new RcloneServeClient(env.RcUrl, env.RemoteSpec);
        using var stream = serve.OpenRangeAsync("blob.bin", 0, -1).GetAwaiter().GetResult();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);

        Assert.Equal(payload, buffer.ToArray());
    }
}
