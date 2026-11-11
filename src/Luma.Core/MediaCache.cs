using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Luma.Core;

public sealed record CachedMedia(string SourceId, string RelativePath, long Length, long ModifiedTicks, string Kind, bool IsAvailable);
public sealed record SourceInfo(string Id, string RootPath, DateTime LastSeenUtc, bool IsOnline);

/// <summary>Persistent metadata for removable media. Database lives on the PC, never on the drive.</summary>
public sealed partial class MediaCache(string databasePath, ISourceResolver? sourceResolver = null)
{
    public string DatabasePath => databasePath;
    private readonly string connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    private SqliteConnection CreateConnection()
    {
        var db = new SqliteConnection(connectionString);
        // SQLite's built-in NOCASE only handles ASCII. Windows folder names need Unicode comparison.
        db.CreateCollation("LUMA_PATH", (a,b) => StringComparer.OrdinalIgnoreCase.Compare(a,b));
        return db;
    }

    public async Task<bool> QuickCheckAsync(CancellationToken ct = default)
    {
        if (!File.Exists(databasePath)) return true;
        try
        {
            await using var db = CreateConnection();
            await db.OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check(100);";
            var result = Convert.ToString(await cmd.ExecuteScalarAsync(ct));
            return string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        if (!File.Exists(databasePath)) return;
        try
        {
            await using var db = CreateConnection();
            await db.OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch { }
    }

    public async Task CreateBackupAsync(string targetPath, CancellationToken ct = default)
    {
        if (!File.Exists(databasePath)) return;
        await CheckpointAsync(ct);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
        File.Copy(databasePath, targetPath, true);
    }

    public async Task<bool> RestoreBackupAsync(string backupPath, CancellationToken ct = default)
    {
        if (!File.Exists(backupPath)) return false;
        try
        {
            await using (var testConn = new SqliteConnection($"Data Source={backupPath};Mode=ReadOnly"))
            {
                await testConn.OpenAsync(ct);
                await using var testCmd = testConn.CreateCommand();
                testCmd.CommandText = "PRAGMA quick_check(100);";
                var check = Convert.ToString(await testCmd.ExecuteScalarAsync(ct));
                if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            SqliteConnection.ClearAllPools();
            var safetyArchive = Path.Combine(Path.GetDirectoryName(databasePath)!, $"catalog-pre-restore-{DateTime.UtcNow:yyyyMMddHHmmss}.db");
            if (File.Exists(databasePath)) File.Copy(databasePath, safetyArchive, true);

            File.Copy(backupPath, databasePath, true);
            var wal = databasePath + "-wal";
            var shm = databasePath + "-shm";
            if (File.Exists(wal)) File.Delete(wal);
            if (File.Exists(shm)) File.Delete(shm);

            await InitializeAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> RepairCatalogAsync(CancellationToken ct = default)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            var backupPath = databasePath + ".bak";
            var corruptArchive = Path.Combine(Path.GetDirectoryName(databasePath)!, $"catalog-corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.db");

            if (File.Exists(databasePath))
            {
                File.Copy(databasePath, corruptArchive, true);
            }

            if (File.Exists(backupPath))
            {
                File.Copy(backupPath, databasePath, true);
                if (await QuickCheckAsync(ct))
                {
                    await InitializeAsync(ct);
                    return true;
                }
            }

            // If backup is missing or damaged, delete damaged database and initialize fresh schema
            if (File.Exists(databasePath)) File.Delete(databasePath);
            var walFile = databasePath + "-wal";
            var shmFile = databasePath + "-shm";
            if (File.Exists(walFile)) File.Delete(walFile);
            if (File.Exists(shmFile)) File.Delete(shmFile);

            await InitializeAsync(ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var fullDbPath = Path.GetFullPath(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullDbPath)!);

        // Pre-migration backup: if catalog exists, create catalog.db.bak before applying changes
        if (File.Exists(fullDbPath) && new FileInfo(fullDbPath).Length > 0)
        {
            try
            {
                File.Copy(fullDbPath, fullDbPath + ".bak", true);
            }
            catch { }
        }

        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS sources(id TEXT PRIMARY KEY, root_path TEXT NOT NULL, last_seen TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS media(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, kind TEXT NOT NULL,
                PRIMARY KEY(source_id, relative_path),
                FOREIGN KEY(source_id) REFERENCES sources(id));
            CREATE INDEX IF NOT EXISTS ix_media_source ON media(source_id);
            CREATE INDEX IF NOT EXISTS ix_media_modified ON media(modified_ticks DESC,source_id,relative_path);
            CREATE INDEX IF NOT EXISTS ix_media_length ON media(length DESC,source_id,relative_path);
            CREATE TABLE IF NOT EXISTS annotations(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                favorite INTEGER NOT NULL DEFAULT 0, tags TEXT NOT NULL DEFAULT '', PRIMARY KEY(source_id,relative_path));
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = ExclusionSchema; await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = ScanSchema; await cmd.ExecuteNonQueryAsync(ct);
        cmd.CommandText = OrganizationSchema; await cmd.ExecuteNonQueryAsync(ct);
        await using var migration = db.BeginTransaction();
        cmd.Transaction = migration;
        cmd.CommandText = BrowseDateSchema;
        await cmd.ExecuteNonQueryAsync(ct);
        await migration.CommitAsync(ct);

        // Run passive checkpoint after initialization
        cmd.Transaction = null;
        cmd.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        try { await cmd.ExecuteNonQueryAsync(ct); } catch { }
    }

    public async Task<IReadOnlyList<CachedMedia>> ListAsync(string sourceId, CancellationToken ct = default)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT relative_path,length,modified_ticks,kind FROM media m WHERE source_id=$id AND " + AllowedMediaSql + " ORDER BY relative_path";
        cmd.Parameters.AddWithValue("$id", sourceId);
        var result = new List<CachedMedia>();
        var root = sourceResolver?.ResolveRoot(sourceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result.Add(new(sourceId, reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), root is not null && File.Exists(SourcePaths.Combine(root, reader.GetString(0)))));
        return result;
    }

    public static string ThumbnailKey(CachedMedia media)
    {
        var data = Encoding.UTF8.GetBytes($"{media.SourceId}\n{media.RelativePath}\n{media.Length}\n{media.ModifiedTicks}");
        return Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".mts", ".m2ts", ".3gp", ".hevc" };
    private static readonly HashSet<string> Extensions = new(Videos.Concat(new[]
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".svg", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2" }), StringComparer.OrdinalIgnoreCase);
}
