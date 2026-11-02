using OpenCfDrive.Core.CloudFiles;
using OpenCfDrive.Core.Models;

namespace OpenCfDrive.Tests;

/// <summary>
/// Pins how a flat rclone listing becomes cfapi placeholder batches. These are
/// the invariants that keep the sync root fully populated without ever relying
/// on on-demand directory population (which nothing handles and which used to
/// stall Explorer for the 60 s cfapi timeout).
/// </summary>
public class CloudNamespacePlanningTests
{
    private static CloudItem File(string path, long size = 7) => new()
    {
        Path = path,
        Name = Path.GetFileName(path),
        IsDirectory = false,
        Size = size,
        ModifiedTime = DateTimeOffset.UnixEpoch,
    };

    private static CloudItem Directory(string path) => new()
    {
        Path = path,
        Name = Path.GetFileName(path.TrimEnd('/')),
        IsDirectory = true,
        Size = 0,
        ModifiedTime = DateTimeOffset.UnixEpoch,
    };

    [Fact]
    public void BuildNamespace_AddsAncestorDirectories_BeforeFiles()
    {
        var namespaceItems = CloudSyncEngine.BuildNamespace(new[]
        {
            File("top.txt"),
            File("nested/data.txt"),
            File("deep/a/b/c.txt"),
        });

        var directories = namespaceItems.Where(i => i.IsDirectory).Select(i => i.RelativePath).ToList();
        Assert.Equal(new[] { "deep", "nested", "deep/a", "deep/a/b" }, directories);

        // Every file comes after every directory so its parent exists first.
        var firstFile = namespaceItems.ToList().FindIndex(i => !i.IsDirectory);
        Assert.All(namespaceItems.Skip(firstFile), i => Assert.False(i.IsDirectory));

        var files = namespaceItems.Where(i => !i.IsDirectory).Select(i => i.RelativePath).ToList();
        Assert.Equal(new[] { "top.txt", "nested/data.txt", "deep/a/b/c.txt" }, files);
    }

    [Fact]
    public void BuildNamespace_KeepsExplicitDirectories_AndNormalisesRemotePaths()
    {
        var namespaceItems = CloudSyncEngine.BuildNamespace(new[]
        {
            Directory("folder/"),
            File("folder/nested.txt"),
        });

        var folder = Assert.Single(namespaceItems, i => i.IsDirectory);
        Assert.Equal("folder", folder.RelativePath);
        Assert.Equal("folder/", folder.RemotePath);

        Assert.Contains(namespaceItems, i => !i.IsDirectory && i.RemotePath == "folder/nested.txt");
    }

    [Fact]
    public void PlanBatches_GroupsByParent_WithSingleNameEntries_DirectoriesFirst()
    {
        var namespaceItems = CloudSyncEngine.BuildNamespace(new[]
        {
            File("alpha.txt"),
            Directory("folder/"),
            File("folder/nested.txt"),
        });

        var batches = CloudPlaceholderService.PlanBatches(namespaceItems);

        Assert.Equal(3, batches.Count);

        // Directory batches always come first, so a file is never created in a
        // plain directory that cfapi does not know about.
        Assert.Equal(string.Empty, batches[0].ParentRelativePath);
        var folderEntry = Assert.Single(batches[0].Entries);
        Assert.True(folderEntry.IsDirectory);
        Assert.Equal("folder", folderEntry.RelativeName);

        Assert.Equal(string.Empty, batches[1].ParentRelativePath);
        Assert.Equal(new[] { "alpha.txt" }, batches[1].Entries.Select(e => e.RelativeName));

        Assert.Equal("folder", batches[2].ParentRelativePath);
        var nestedEntry = Assert.Single(batches[2].Entries);
        Assert.False(nestedEntry.IsDirectory);
        Assert.Equal("nested.txt", nestedEntry.RelativeName);

        // CfCreatePlaceholders rejects names that contain separators.
        Assert.All(
            batches.SelectMany(b => b.Entries),
            e => Assert.DoesNotContain('\\', e.RelativeName) );
        Assert.All(
            batches.SelectMany(b => b.Entries),
            e => Assert.DoesNotContain('/', e.RelativeName) );
    }

    [Fact]
    public void PlanBatches_DirectoriesAreDepthOrdered_SoParentsExistFirst()
    {
        var namespaceItems = CloudSyncEngine.BuildNamespace(new[]
        {
            File("a/b/deep.txt"),
        });

        var batches = CloudPlaceholderService.PlanBatches(namespaceItems);
        var directoryParents = batches
            .Where(b => b.Entries.Any(e => e.IsDirectory))
            .Select(b => b.ParentRelativePath)
            .ToList();

        Assert.Equal(new[] { string.Empty, "a" }, directoryParents);
    }
}
