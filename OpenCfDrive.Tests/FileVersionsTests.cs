using System.Text.Json;
using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Services;

namespace OpenCfDrive.Tests;

/// <summary>
/// What tells a file's version on each kind of remote: its own hash where it
/// keeps one cheaply, else size and time, as rclone itself decides.
/// </summary>
public class FileVersionsTests
{
    private const string Known = "2026-11-04T10:00:00.0000000Z";
    private static readonly DateTimeOffset SameTime = new(2026, 11, 4, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = SameTime.AddMinutes(5);

    [Theory]
    // The size first.
    [InlineData(5L, null, null, 6L, false, null, true)]
    // The hash over the time: rewritten with the same content, or edited keeping size and time.
    [InlineData(5L, "md5:aa", "md5:AA", 5L, true, null, false)]
    [InlineData(5L, "md5:aa", "md5:bb", 5L, false, null, true)]
    // No hash on either side, or of different types: the time.
    [InlineData(5L, null, "md5:aa", 5L, true, null, true)]
    [InlineData(5L, "sha1:aa", "md5:aa", 5L, false, null, false)]
    [InlineData(5L, null, null, 5L, true, null, true)]
    [InlineData(5L, null, null, 5L, false, null, false)]
    // A listing without times and without a hash cannot tell: unchanged.
    [InlineData(5L, null, null, 5L, false, "none", false)]
    public void Differs_BySizeThenHashThenTime(long knownSize, string? knownHash, string? hash, long size, bool later, string? noTime, bool expected)
    {
        DateTimeOffset? modified = noTime is null ? later ? Later : SameTime : null;
        Assert.Equal(expected, FileVersions.Differs(knownSize, Known, knownHash, size, modified, hash));
    }

    [Theory]
    [InlineData("md5:abc", "MD5:ABC", true)]
    [InlineData("md5:abc", "md5:abd", false)]
    [InlineData("md5:abc", "sha1:abc", null)]
    [InlineData("md5:abc", null, null)]
    [InlineData("md5:", "md5:", null)]
    public void SameHash_IsOnlyToldForTheSameType(string? a, string? b, bool? expected) =>
        Assert.Equal(expected, FileVersions.SameHash(a, b));

    private static RemoteCapabilities FromFsInfo(string json) =>
        RemoteCapabilities.FromFsInfo(JsonDocument.Parse(json).RootElement);

    [Theory]
    // S3 and Swift: MD5, read with the listing; times cost a request each.
    [InlineData("""{"Hashes":["md5"],"Precision":1,"Features":{"SlowHash":false,"SlowModTime":true}}""", "md5", true, true)]
    // Google Drive keeps several: SHA-1 is used.
    [InlineData("""{"Hashes":["md5","sha1","sha256"],"Precision":1000000,"Features":{}}""", "sha1", true, false)]
    // OneDrive's own.
    [InlineData("""{"Hashes":["quickxor"],"Precision":1000000000,"Features":{}}""", "quickxor", true, false)]
    // A local disk or SFTP hashes by reading every file: not used.
    [InlineData("""{"Hashes":["md5","sha1"],"Precision":100,"Features":{"SlowHash":true}}""", null, true, false)]
    // Plain WebDAV: no hashes, and times it cannot set (rclone's 100 years).
    [InlineData("""{"Hashes":[],"Precision":3153600000000000000,"Features":{}}""", null, false, false)]
    // An encrypted (crypt) remote: no hashes.
    [InlineData("""{"Hashes":null,"Precision":1,"Features":{}}""", null, true, false)]
    public void TheHashUsed_IsTheRemotesOwn_WhenItCostsNothingExtra(string fsinfo, string? hashType, bool keepsFileTimes, bool listsWithoutTimes)
    {
        var capabilities = FromFsInfo(fsinfo);

        Assert.Equal(hashType, capabilities.HashType);
        Assert.Equal(keepsFileTimes, capabilities.KeepsFileTimes);
        Assert.Equal(listsWithoutTimes, capabilities.ListsWithoutTimes);
    }

    [Fact]
    public void TimesAreComparedToTheRemotesPrecision_AtLeastASecond()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), FromFsInfo("""{"Precision":1}""").TimeTolerance);
        Assert.Equal(TimeSpan.FromSeconds(2), FromFsInfo("""{"Precision":2000000000}""").TimeTolerance);
        Assert.False(FromFsInfo("""{"Precision":3153600000000000000}""").KeepsFileTimes);
    }

    [Fact]
    public void EmptyFolders_AreKeptUnlessTheRemoteSaysNot()
    {
        Assert.True(FromFsInfo("""{"Features":{}}""").CanHaveEmptyDirectories);
        Assert.False(FromFsInfo("""{"Features":{"CanHaveEmptyDirectories":false}}""").CanHaveEmptyDirectories);
    }
}
