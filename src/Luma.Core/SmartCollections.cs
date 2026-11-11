using Microsoft.Data.Sqlite;

namespace Luma.Core;

public sealed record SmartCollection(
    string Id,
    string Name,
    string? Search = null,
    string? Kind = null,
    bool? Favorite = null,
    string? Orientation = null,
    bool UntaggedOnly = false,
    long? MinLength = null,
    bool HasGpsOnly = false)
{
    public override string ToString() => Name;
}

public sealed partial class MediaCache
{
    private const string SmartCollectionSchema = """
        CREATE TABLE IF NOT EXISTS smart_collections(
            id TEXT PRIMARY KEY,
            name TEXT NOT NULL,
            search TEXT,
            kind TEXT,
            favorite INTEGER,
            orientation TEXT,
            untagged_only INTEGER NOT NULL DEFAULT 0,
            min_length INTEGER,
            has_gps_only INTEGER NOT NULL DEFAULT 0
        );
        """;

    private static async Task EnsureSmartCollectionSchemaAsync(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        await using (var createCmd = db.CreateCommand())
        {
            createCmd.Transaction = tx;
            createCmd.CommandText = SmartCollectionSchema;
            await createCmd.ExecuteNonQueryAsync(ct);
        }

        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var infoCmd = db.CreateCommand())
        {
            infoCmd.Transaction = tx;
            infoCmd.CommandText = "PRAGMA table_info(smart_collections);";
            await using var reader = await infoCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) existingCols.Add(reader.GetString(1));
        }

        (string Name, string Type)[] required =
        [
            ("orientation", "TEXT"),
            ("untagged_only", "INTEGER NOT NULL DEFAULT 0"),
            ("min_length", "INTEGER"),
            ("has_gps_only", "INTEGER NOT NULL DEFAULT 0")
        ];

        foreach (var (col, type) in required)
        {
            if (!existingCols.Contains(col))
            {
                await using var alterCmd = db.CreateCommand();
                alterCmd.Transaction = tx;
                alterCmd.CommandText = $"ALTER TABLE smart_collections ADD COLUMN {col} {type};";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
        }
    }

    public async Task SeedDefaultSmartCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);

        await using var checkCmd = db.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM luma_migrations WHERE name='smart_collections_seeded_v1';";
        if (Convert.ToInt64(await checkCmd.ExecuteScalarAsync(ct)) > 0) return;

        await using var countCmd = db.CreateCommand();
        countCmd.CommandText = "SELECT COUNT(*) FROM smart_collections;";
        if (Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct)) == 0)
        {
            await SaveSmartCollectionAsync("Large videos (>100 MB)", kind: "video", minLength: 100L * 1024 * 1024, ct: ct);
            await SaveSmartCollectionAsync("Untagged photos", kind: "photo", untaggedOnly: true, ct: ct);
            await SaveSmartCollectionAsync("Portrait shots", kind: "photo", orientation: "portrait", ct: ct);
            await SaveSmartCollectionAsync("Geotagged moments", hasGpsOnly: true, ct: ct);
        }

        await using var markCmd = db.CreateCommand();
        markCmd.CommandText = "INSERT OR IGNORE INTO luma_migrations VALUES('smart_collections_seeded_v1');";
        await markCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SmartCollection>> SmartCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);

        await using (var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct))
        {
            await EnsureSmartCollectionSchemaAsync(db, tx, ct);
            await tx.CommitAsync(ct);
        }

        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, name, search, kind, favorite, orientation, untagged_only, min_length, has_gps_only FROM smart_collections ORDER BY name COLLATE NOCASE";

        var list = new List<SmartCollection>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new SmartCollection(
                Id: reader.GetString(0),
                Name: reader.GetString(1),
                Search: reader.IsDBNull(2) ? null : reader.GetString(2),
                Kind: reader.IsDBNull(3) ? null : reader.GetString(3),
                Favorite: reader.IsDBNull(4) ? null : reader.GetInt32(4) == 1,
                Orientation: reader.IsDBNull(5) ? null : reader.GetString(5),
                UntaggedOnly: !reader.IsDBNull(6) && reader.GetInt32(6) == 1,
                MinLength: reader.IsDBNull(7) ? null : reader.GetInt64(7),
                HasGpsOnly: !reader.IsDBNull(8) && reader.GetInt32(8) == 1));
        }

        return list;
    }

    public async Task<string> SaveSmartCollectionAsync(
        string name,
        string? search = null,
        string? kind = null,
        bool? favorite = null,
        string? id = null,
        string? orientation = null,
        bool untaggedOnly = false,
        long? minLength = null,
        bool hasGpsOnly = false,
        CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("Use a collection name of 1 to 100 characters.");
        id ??= Guid.NewGuid().ToString("N");

        await using var db = CreateConnection();
        await db.OpenAsync(ct);

        await using (var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct))
        {
            await EnsureSmartCollectionSchemaAsync(db, tx, ct);
            await tx.CommitAsync(ct);
        }

        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO smart_collections(id, name, search, kind, favorite, orientation, untagged_only, min_length, has_gps_only)
            VALUES($id, $name, $search, $kind, $fav, $orientation, $untagged, $minLen, $hasGps)
            ON CONFLICT(id) DO UPDATE SET
                name=$name, search=$search, kind=$kind, favorite=$fav,
                orientation=$orientation, untagged_only=$untagged, min_length=$minLen, has_gps_only=$hasGps;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$search", (object?)search ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fav", favorite.HasValue ? (favorite.Value ? 1 : 0) : DBNull.Value);
        cmd.Parameters.AddWithValue("$orientation", (object?)orientation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$untagged", untaggedOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$minLen", (object?)minLength ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hasGps", hasGpsOnly ? 1 : 0);

        await cmd.ExecuteNonQueryAsync(ct);
        return id;
    }

    public async Task DeleteSmartCollectionAsync(string id, CancellationToken ct = default)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "DELETE FROM smart_collections WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
