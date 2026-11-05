using OpenCfDrive.Core.Services;

namespace OpenCfDrive.Tests;

/// <summary>
/// A folder that rclone opens as the root of an fs (a folder rename) is
/// named with a trailing slash. Without it the WebDAV backend asks for it as
/// a file first, which a server behind a TLS proxy answers with a redirect to
/// http:// that rclone refuses (seen on a real server: every folder rename
/// failed).
/// </summary>
public sealed class RcloneRcClientTests
{
    [Theory]
    [InlineData("webdav:", "webdav:")]
    [InlineData("webdav:New folder", "webdav:New folder/")]
    [InlineData("webdav:a/b/", "webdav:a/b/")]
    [InlineData("s3:bucket/dir", "s3:bucket/dir/")]
    [InlineData("crypt,password=x:secret", "crypt,password=x:secret/")]
    public void AsFolderRoot_EndsAFolderWithASlash(string fs, string expected) =>
        Assert.Equal(expected, RcloneRcClient.AsFolderRoot(fs));

    /// <summary>A local folder (as the local-file hash sends) is left as it is.</summary>
    [Theory]
    [InlineData(@"C:\Users\someone\OpenCfDrive")]
    [InlineData("C:/data")]
    [InlineData(@"\\server\share\folder")]
    [InlineData("relative-folder")]
    public void AsFolderRoot_LeavesALocalPathAlone(string fs) =>
        Assert.Equal(fs, RcloneRcClient.AsFolderRoot(fs));

    /// <summary>
    /// Downloads name only the remote's root in the brackets; its path goes in
    /// the URL's path, as every other request sends it.
    /// </summary>
    [Theory]
    [InlineData("webdav:", "a b/c.txt", "http://127.0.0.1:5572/[webdav:]/a%20b/c.txt")]
    [InlineData("webdav:Documents", "c.txt", "http://127.0.0.1:5572/[webdav:]/Documents/c.txt")]
    [InlineData("webdav:Documents/Work/", "sub/c.txt", "http://127.0.0.1:5572/[webdav:]/Documents/Work/sub/c.txt")]
    [InlineData("e2e:opencfdrive", "x.bin", "http://127.0.0.1:5572/[e2e:]/opencfdrive/x.bin")]
    public void ServeUri_PutsTheRemotesPathInTheUrl(string remote, string file, string expected)
    {
        using var serve = new RcloneServeClient("http://127.0.0.1:5572", remote);
        Assert.Equal(expected, serve.BuildUri(file).AbsoluteUri);
    }
}
