using Microsoft.Data.Sqlite;
using OpenCfDrive.Core.Storage;

namespace OpenCfDrive.Tests;

public class SqliteMetadataStoreTests
{
    private static string TempDbPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "opencfdrive-db", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "test.db");
    }

    [Fact]
    public void Initialize_CreatesSchema()
    {
        var path = TempDbPath();
        var store = new SqliteMetadataStore(path);
        store.Initialize();

        Assert.True(File.Exists(path));

        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info('Files');";
        using var reader = command.ExecuteReader();

        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        Assert.Contains("RemotePath", columns);
        Assert.Contains("FileIdentity", columns);
        Assert.Contains("FileId", columns);
        Assert.Contains("IsHydrated", columns);
        Assert.Contains("InSync", columns);
    }

    [Fact]
    public void Delete_RemovesADatabaseThatWasJustRead()
    {
        var path = TempDbPath();
        var store = new SqliteMetadataStore(path);
        store.Initialize();
        store.SetConfig("RcloneFs", "box:");
        Assert.Equal("box:", store.GetConfig("RcloneFs"));
        Assert.Equal(0, store.Count());

        SqliteMetadataStore.Delete(path);

        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Upsert_AndLookups_RoundTrip()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();

        store.UpsertFile(
            remotePath: "folder/file.txt",
            localPath: @"C:\root\folder\file.txt",
            fileIdentity: "folder/file.txt",
            fileId: 0x1234_5678_9ABC,
            isPlaceholder: true,
            isDirectory: false,
            size: 4096,
            modifiedUtc: DateTimeOffset.UtcNow);

        var byRemote = store.GetByRemotePath("folder/file.txt");
        Assert.NotNull(byRemote);
        Assert.Equal(0x1234_5678_9ABC, byRemote!.FileId);
        Assert.True(byRemote.IsPlaceholder);
        Assert.False(byRemote.IsHydrated);

        var byIdentity = store.GetByFileIdentity("folder/file.txt");
        Assert.NotNull(byIdentity);
        Assert.Equal(byRemote.Id, byIdentity!.Id);

        var byFileId = store.GetByFileId(0x1234_5678_9ABC);
        Assert.NotNull(byFileId);
        Assert.Equal("folder/file.txt", byFileId!.RemotePath);

        var byLocal = store.GetByLocalPath(@"C:\ROOT\folder\file.txt");
        Assert.NotNull(byLocal);

        store.MarkHydrated("folder/file.txt");
        Assert.True(store.GetByRemotePath("folder/file.txt")!.IsHydrated);

        store.MarkInSync("folder/file.txt", 4097);
        var updated = store.GetByRemotePath("folder/file.txt")!;
        Assert.True(updated.InSync);
        Assert.Equal(4097, updated.Size);
    }

    [Fact]
    public void Upsert_IsIdempotentPerRemotePath()
    {
        var store = new SqliteMetadataStore(TempDbPath());
        store.Initialize();

        store.UpsertFile("a.txt", @"C:\root\a.txt", "a.txt", 1, true, false, 10, null);
        store.UpsertFile("a.txt", @"C:\root\a.txt", "a.txt", 2, true, false, 20, null);

        var all = store.GetAll();
        Assert.Single(all);
        Assert.Equal(2, all[0].FileId);
        Assert.Equal(20, all[0].Size);
    }

    [Fact]
    public void Initialize_MigratesLegacySchema()
    {
        var path = TempDbPath();

        // Old scaffold schema (no FileIdentity/FileId/IsHydrated/InSync).
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE Files (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    RemotePath TEXT NOT NULL UNIQUE,
                    LocalPath TEXT NOT NULL,
                    FileId TEXT NOT NULL,
                    IsPlaceholder INTEGER NOT NULL DEFAULT 0,
                    IsDirectory INTEGER NOT NULL DEFAULT 0,
                    Size INTEGER NOT NULL DEFAULT 0,
                    ModifiedUtc TEXT,
                    CreatedUtc TEXT NOT NULL DEFAULT (datetime('now'))
                );";
            command.ExecuteNonQuery();
        }

        var store = new SqliteMetadataStore(path);
        store.Initialize();

        using var verify = new SqliteConnection($"Data Source={path}");
        verify.Open();
        using var verifyCommand = verify.CreateCommand();
        verifyCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name IN ('FileIdentity','IsHydrated','InSync');";
        var count = Convert.ToInt64(verifyCommand.ExecuteScalar());
        Assert.Equal(3, count);
    }
}
