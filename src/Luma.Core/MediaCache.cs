using Microsoft.Data.Sqlite;
using System.Buffers;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Luma.Core;

public sealed record CachedMedia(string SourceId, string RelativePath, long Length, long ModifiedTicks, string Kind, bool IsAvailable);
public sealed record SourceInfo(string Id, string RootPath, DateTime LastSeenUtc, bool IsOnline);
public enum CatalogRepairOutcome { RestoredFromBackup, ReinitializedEmpty, Failed }

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

    private async Task<SqliteConnection> OpenConfiguredConnectionAsync(CancellationToken ct = default)
    {
        var db = CreateConnection();
        await db.OpenAsync(ct);
        await using var pragma = db.CreateCommand();
        pragma.CommandText = "PRAGMA cache_size=-32768;PRAGMA temp_store=MEMORY;PRAGMA busy_timeout=5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return db;
    }

    private static async Task<bool> HasExclusionsAsync(SqliteConnection db, SqliteTransaction? tx = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM exclusions LIMIT 1";
        return await cmd.ExecuteScalarAsync(ct) is not null;
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

    public async Task<bool> RepairCatalogAsync(CancellationToken ct = default) =>
        await RepairCatalogDetailedAsync(ct) != CatalogRepairOutcome.Failed;

    public async Task<CatalogRepairOutcome> RepairCatalogDetailedAsync(CancellationToken ct = default)
    {
        try
        {
            SqliteConnection.ClearAllPools();
            var backupPath = databasePath + ".bak";
            var walFile = databasePath + "-wal";
            var shmFile = databasePath + "-shm";
            var corruptArchive = Path.Combine(Path.GetDirectoryName(databasePath)!, $"catalog-corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}.db");

            if (File.Exists(databasePath))
            {
                File.Copy(databasePath, corruptArchive, true);
            }

            if (File.Exists(backupPath))
            {
                if (File.Exists(walFile)) File.Delete(walFile);
                if (File.Exists(shmFile)) File.Delete(shmFile);
                File.Copy(backupPath, databasePath, true);
                if (await QuickCheckAsync(ct))
                {
                    await InitializeAsync(ct);
                    return CatalogRepairOutcome.RestoredFromBackup;
                }
            }

            // If backup is missing or damaged, remove damaged files and initialize a fresh empty schema
            SqliteConnection.ClearAllPools();
            if (File.Exists(databasePath)) File.Delete(databasePath);
            if (File.Exists(walFile)) File.Delete(walFile);
            if (File.Exists(shmFile)) File.Delete(shmFile);

            await InitializeAsync(ct);
            return CatalogRepairOutcome.ReinitializedEmpty;
        }
        catch
        {
            return CatalogRepairOutcome.Failed;
        }
    }

    public async Task RemoveMediaItemsAsync(IEnumerable<CachedMedia> items, CancellationToken ct = default)
    {
        var list = items.ToList();
        if (list.Count == 0) return;
        using var lease = IndexingLease();
        await using var db = await OpenConfiguredConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "DELETE FROM media WHERE source_id=$source AND relative_path=$path";
        var pSource = cmd.Parameters.Add("$source", SqliteType.Text);
        var pPath = cmd.Parameters.Add("$path", SqliteType.Text);
        cmd.Prepare();
        foreach (var item in list)
        {
            pSource.Value = item.SourceId;
            pPath.Value = item.RelativePath;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task ResetCatalogAsync(CancellationToken ct = default)
    {
        using var lease = IndexingLease();
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", ".bak" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
        await InitializeAsync(ct);
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
        await EnsureExtendedSchemaAsync(db, migration, ct);
        await EnsureSmartCollectionSchemaAsync(db, migration, ct);
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

    internal static string ToLowerHex(ReadOnlySpan<byte> bytes)
    {
        const string Hex = "0123456789abcdef";
        Span<char> chars = bytes.Length <= 64 ? stackalloc char[bytes.Length * 2] : new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i * 2] = Hex[b >> 4];
            chars[i * 2 + 1] = Hex[b & 0x0F];
        }
        return new string(chars);
    }

    public static string ThumbnailKey(CachedMedia media)
    {
        // Exact UTF-8 format: "{SourceId}\n{RelativePath}\n{Length}\n{ModifiedTicks}"
        var maxBytes = Encoding.UTF8.GetMaxByteCount(media.SourceId.Length + media.RelativePath.Length) + 48;
        byte[]? rented = null;
        Span<byte> buf = maxBytes <= 512
            ? stackalloc byte[512]
            : (rented = ArrayPool<byte>.Shared.Rent(maxBytes));
        try
        {
            var written = Encoding.UTF8.GetBytes(media.SourceId, buf);
            buf[written++] = (byte)'\n';
            written += Encoding.UTF8.GetBytes(media.RelativePath, buf[written..]);
            buf[written++] = (byte)'\n';
            if (!Utf8Formatter.TryFormat(media.Length, buf[written..], out var lenBytes))
                throw new InvalidOperationException("Failed to format length.");
            written += lenBytes;
            buf[written++] = (byte)'\n';
            if (!Utf8Formatter.TryFormat(media.ModifiedTicks, buf[written..], out var ticksBytes))
                throw new InvalidOperationException("Failed to format modified ticks.");
            written += ticksBytes;
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(buf[..written], hash);
            return ToLowerHex(hash);
        }
        finally
        {
            if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v", ".mpg", ".mpeg", ".mts", ".m2ts", ".3gp", ".hevc" };
    private static readonly HashSet<string> Extensions = new(Videos.Concat(new[]
        { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic", ".heif", ".avif", ".svg", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2" }), StringComparer.OrdinalIgnoreCase);
}
