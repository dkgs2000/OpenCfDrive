using Microsoft.Data.Sqlite;

namespace OpenCfDrive.Core.Storage;

public sealed class SqliteMetadataStore
{
    private readonly string _dbPath;

    public SqliteMetadataStore(string dbPath)
    {
        _dbPath = dbPath;
    }

    public string DbPath => _dbPath;

    /// <summary>Deletes a state database and its SQLite side files.</summary>
    public static void Delete(string dbPath)
    {
        foreach (var path in new[] { dbPath, dbPath + "-journal", dbPath + "-wal", dbPath + "-shm" })
        {
            File.Delete(path);
        }
    }

    public void Initialize()
    {
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
        }

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS Files (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                RemotePath TEXT NOT NULL UNIQUE,
                LocalPath TEXT NOT NULL,
                FileIdentity TEXT NOT NULL DEFAULT '',
                FileId INTEGER NOT NULL DEFAULT 0,
                IsPlaceholder INTEGER NOT NULL DEFAULT 0,
                IsHydrated INTEGER NOT NULL DEFAULT 0,
                InSync INTEGER NOT NULL DEFAULT 0,
                IsDirectory INTEGER NOT NULL DEFAULT 0,
                Size INTEGER NOT NULL DEFAULT 0,
                ModifiedUtc TEXT,
                CreatedUtc TEXT NOT NULL DEFAULT (datetime('now'))
            );
        ";
        command.ExecuteNonQuery();

        // Columns are added before indexes: a legacy database has the table but
        // not these columns, and indexing a missing column would throw.
        EnsureColumn(connection, "FileIdentity", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "FileId", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "IsHydrated", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "InSync", "INTEGER NOT NULL DEFAULT 0");

        // Legacy builds stored a GUID string in FileId; normalise before indexing.
        using (var normalise = connection.CreateCommand())
        {
            normalise.CommandText = "UPDATE Files SET FileId = 0 WHERE typeof(FileId) = 'text';";
            normalise.ExecuteNonQuery();
        }

        using var indexes = connection.CreateCommand();
        indexes.CommandText = @"
            CREATE INDEX IF NOT EXISTS idx_files_remote_path ON Files(RemotePath);
            CREATE INDEX IF NOT EXISTS idx_files_local_path ON Files(LocalPath);
            CREATE INDEX IF NOT EXISTS idx_files_file_identity ON Files(FileIdentity);
            CREATE INDEX IF NOT EXISTS idx_files_file_id ON Files(FileId);
        ";
        indexes.ExecuteNonQuery();

        // Config table for engine settings (no machine env needed)
        using var configTable = connection.CreateCommand();
        configTable.CommandText = @"
            CREATE TABLE IF NOT EXISTS Config (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL DEFAULT ''
            );
        ";
        configTable.ExecuteNonQuery();
    }

    private static void EnsureColumn(SqliteConnection connection, string name, string definition)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM pragma_table_info('Files') WHERE name = @name;";
        check.Parameters.AddWithValue("@name", name);
        var exists = Convert.ToInt64(check.ExecuteScalar() ?? 0L) > 0;
        if (exists)
        {
            return;
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE Files ADD COLUMN {name} {definition};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection($"Data Source={_dbPath}");
        connection.Open();
        return connection;
    }

    public void UpsertFile(
        string remotePath,
        string localPath,
        string fileIdentity,
        long fileId,
        bool isPlaceholder,
        bool isDirectory,
        long size,
        DateTimeOffset? modifiedUtc,
        bool inSync = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO Files(RemotePath, LocalPath, FileIdentity, FileId, IsPlaceholder, IsDirectory, Size, ModifiedUtc, InSync)
            VALUES (@remotePath, @localPath, @fileIdentity, @fileId, @isPlaceholder, @isDirectory, @size, @modifiedUtc, @inSync)
            ON CONFLICT(RemotePath) DO UPDATE SET
                LocalPath = excluded.LocalPath,
                FileIdentity = excluded.FileIdentity,
                FileId = excluded.FileId,
                IsPlaceholder = excluded.IsPlaceholder,
                IsDirectory = excluded.IsDirectory,
                Size = excluded.Size,
                ModifiedUtc = excluded.ModifiedUtc,
                InSync = excluded.InSync;
        ";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.Parameters.AddWithValue("@localPath", localPath);
        command.Parameters.AddWithValue("@fileIdentity", fileIdentity);
        command.Parameters.AddWithValue("@fileId", fileId);
        command.Parameters.AddWithValue("@isPlaceholder", isPlaceholder ? 1 : 0);
        command.Parameters.AddWithValue("@isDirectory", isDirectory ? 1 : 0);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@modifiedUtc", modifiedUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@inSync", inSync ? 1 : 0);
        command.ExecuteNonQuery();
    }

    public FileRecord? GetByRemotePath(string remotePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE RemotePath = @remotePath LIMIT 1;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? FileRecord.FromReader(reader) : null;
    }

    /// <summary>Primary hydration lookup: the identity blob we attached at creation time.</summary>
    public FileRecord? GetByFileIdentity(string fileIdentity)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE FileIdentity = @identity LIMIT 1;";
        command.Parameters.AddWithValue("@identity", fileIdentity);
        using var reader = command.ExecuteReader();
        return reader.Read() ? FileRecord.FromReader(reader) : null;
    }

    /// <summary>Fallback hydration lookup by the Windows file id reported by cfapi callbacks.</summary>
    public FileRecord? GetByFileId(long fileId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE FileId = @fileId LIMIT 1;";
        command.Parameters.AddWithValue("@fileId", fileId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? FileRecord.FromReader(reader) : null;
    }

    public FileRecord? GetByLocalPath(string localPath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE LocalPath = @localPath COLLATE NOCASE LIMIT 1;";
        command.Parameters.AddWithValue("@localPath", localPath);
        using var reader = command.ExecuteReader();
        return reader.Read() ? FileRecord.FromReader(reader) : null;
    }

    public void MarkHydrated(string remotePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET IsHydrated = 1 WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.ExecuteNonQuery();
    }

    public void MarkDehydrated(string remotePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET IsHydrated = 0 WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.ExecuteNonQuery();
    }

    public void MarkInSync(string remotePath, long size)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE Files SET InSync = 1, Size = @size, IsPlaceholder = 1 WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.Parameters.AddWithValue("@size", size);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Records the server's current metadata for a file. <paramref name="dehydrated"/>
    /// is true when the local copy was dropped because the server content changed.
    /// </summary>
    public void UpdateRemoteMetadata(string remotePath, long size, DateTimeOffset? modifiedUtc, bool dehydrated)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Files
               SET Size = @size,
                   ModifiedUtc = @modifiedUtc,
                   InSync = 1,
                   IsHydrated = CASE WHEN @dehydrated = 1 THEN 0 ELSE IsHydrated END
             WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        command.Parameters.AddWithValue("@size", size);
        command.Parameters.AddWithValue("@modifiedUtc", modifiedUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("@dehydrated", dehydrated ? 1 : 0);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Tracked items directly inside a sync-root-relative directory ('/'
    /// separated, empty for the root). Directory rows end in '/'.
    /// </summary>
    public IReadOnlyList<FileRecord> GetChildren(string relativeDirectory)
    {
        var prefix = relativeDirectory.Length == 0 ? string.Empty : relativeDirectory.TrimEnd('/') + "/";

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE substr(RemotePath, 1, length(@prefix)) = @prefix AND RemotePath <> @prefix;";
        command.Parameters.AddWithValue("@prefix", prefix);
        using var reader = command.ExecuteReader();
        var results = new List<FileRecord>();
        while (reader.Read())
        {
            var record = FileRecord.FromReader(reader);
            if (!record.RemotePath[prefix.Length..].TrimEnd('/').Contains('/'))
            {
                results.Add(record);
            }
        }

        return results;
    }

    /// <summary>Rows at or below a directory (the directory row ends in '/').</summary>
    public IReadOnlyList<FileRecord> GetTree(string directoryRemotePath)
    {
        var prefix = directoryRemotePath.TrimEnd('/') + "/";

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files WHERE substr(RemotePath, 1, length(@prefix)) = @prefix;";
        command.Parameters.AddWithValue("@prefix", prefix);
        using var reader = command.ExecuteReader();
        var results = new List<FileRecord>();
        while (reader.Read())
        {
            results.Add(FileRecord.FromReader(reader));
        }

        return results;
    }

    /// <summary>
    /// Re-keys a directory and everything below it after a rename/move: remote
    /// paths, identities (which equal the remote paths) and local paths.
    /// </summary>
    public int RenameTree(string oldDirectory, string newDirectory, string oldLocalDirectory, string newLocalDirectory)
    {
        var oldPrefix = oldDirectory.TrimEnd('/') + "/";
        var newPrefix = newDirectory.TrimEnd('/') + "/";
        var oldLocal = oldLocalDirectory.TrimEnd('\\');
        var newLocal = newLocalDirectory.TrimEnd('\\');

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Files
               SET RemotePath = @newPrefix || substr(RemotePath, length(@oldPrefix) + 1),
                   FileIdentity = @newPrefix || substr(FileIdentity, length(@oldPrefix) + 1),
                   LocalPath = @newLocal || substr(LocalPath, length(@oldLocal) + 1)
             WHERE substr(RemotePath, 1, length(@oldPrefix)) = @oldPrefix;";
        command.Parameters.AddWithValue("@oldPrefix", oldPrefix);
        command.Parameters.AddWithValue("@newPrefix", newPrefix);
        command.Parameters.AddWithValue("@oldLocal", oldLocal);
        command.Parameters.AddWithValue("@newLocal", newLocal);
        return command.ExecuteNonQuery();
    }

    /// <summary>Removes a directory row and every row below it.</summary>
    public int DeleteTree(string directoryRemotePath)
    {
        var prefix = directoryRemotePath.TrimEnd('/') + "/";

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Files WHERE substr(RemotePath, 1, length(@prefix)) = @prefix;";
        command.Parameters.AddWithValue("@prefix", prefix);
        return command.ExecuteNonQuery();
    }

    /// <summary>Removes a tracked item after it was deleted locally and remotely.</summary>
    public bool DeleteByRemotePath(string remotePath)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM Files WHERE RemotePath = @remotePath;";
        command.Parameters.AddWithValue("@remotePath", remotePath);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Re-keys a tracked item after a local rename/move.</summary>
    public bool RenameRemotePath(string oldRemotePath, string newRemotePath, string newLocalPath, string newFileIdentity)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = @"
            UPDATE Files
               SET RemotePath = @newRemotePath,
                   LocalPath = @newLocalPath,
                   FileIdentity = @newFileIdentity
             WHERE RemotePath = @oldRemotePath;";
        command.Parameters.AddWithValue("@oldRemotePath", oldRemotePath);
        command.Parameters.AddWithValue("@newRemotePath", newRemotePath);
        command.Parameters.AddWithValue("@newLocalPath", newLocalPath);
        command.Parameters.AddWithValue("@newFileIdentity", newFileIdentity);
        return command.ExecuteNonQuery() > 0;
    }

    public string? GetFileIdByRemotePath(string remotePath) => GetByRemotePath(remotePath)?.FileId.ToString();

    /// <summary>How many files and folders the drive tracks.</summary>
    public int Count()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM Files;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<FileRecord> GetAll()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM Files ORDER BY Id;";
        using var reader = command.ExecuteReader();
        var results = new List<FileRecord>();
        while (reader.Read())
        {
            results.Add(FileRecord.FromReader(reader));
        }

        return results;
    }

    public void SetConfig(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO Config (Key, Value) VALUES (@key, @value);";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }

    public string GetConfig(string key, string defaultValue = "")
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Value FROM Config WHERE Key = @key;";
        command.Parameters.AddWithValue("@key", key);
        var scalar = command.ExecuteScalar();
        return scalar is null ? defaultValue : (string)scalar;
    }
}

public sealed class FileRecord
{
    public long Id { get; init; }
    public string RemotePath { get; init; } = string.Empty;
    public string LocalPath { get; init; } = string.Empty;
    public string FileIdentity { get; init; } = string.Empty;
    public long FileId { get; init; }
    public bool IsPlaceholder { get; init; }
    public bool IsHydrated { get; init; }
    public bool InSync { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string? ModifiedUtc { get; init; }

    internal static FileRecord FromReader(Microsoft.Data.Sqlite.SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("Id")),
        RemotePath = reader.GetString(reader.GetOrdinal("RemotePath")),
        LocalPath = reader.GetString(reader.GetOrdinal("LocalPath")),
        FileIdentity = reader.GetString(reader.GetOrdinal("FileIdentity")),
        FileId = reader.GetInt64(reader.GetOrdinal("FileId")),
        IsPlaceholder = reader.GetInt64(reader.GetOrdinal("IsPlaceholder")) != 0,
        IsHydrated = reader.GetInt64(reader.GetOrdinal("IsHydrated")) != 0,
        InSync = reader.GetInt64(reader.GetOrdinal("InSync")) != 0,
        IsDirectory = reader.GetInt64(reader.GetOrdinal("IsDirectory")) != 0,
        Size = reader.GetInt64(reader.GetOrdinal("Size")),
        ModifiedUtc = reader.IsDBNull(reader.GetOrdinal("ModifiedUtc")) ? null : reader.GetString(reader.GetOrdinal("ModifiedUtc")),
    };
}
