using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Luma.Core;

public sealed record SmartCollection(string Id, string Name, string? Search = null, string? Kind = null, bool? Favorite = null)
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
            favorite INTEGER
        );
        """;

    public async Task<IReadOnlyList<SmartCollection>> SmartCollectionsAsync(CancellationToken ct = default)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);

        await using (var initCmd = db.CreateCommand())
        {
            initCmd.CommandText = SmartCollectionSchema;
            await initCmd.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT id, name, search, kind, favorite FROM smart_collections ORDER BY name COLLATE NOCASE";

        var list = new List<SmartCollection>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var name = reader.GetString(1);
            var search = reader.IsDBNull(2) ? null : reader.GetString(2);
            var kind = reader.IsDBNull(3) ? null : reader.GetString(3);
            var fav = reader.IsDBNull(4) ? (bool?)null : reader.GetInt32(4) == 1;
            list.Add(new SmartCollection(id, name, search, kind, fav));
        }

        return list;
    }

    public async Task<string> SaveSmartCollectionAsync(string name, string? search = null, string? kind = null, bool? favorite = null, string? id = null, CancellationToken ct = default)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("Use a collection name of 1–100 characters.");
        id ??= Guid.NewGuid().ToString("N");

        await using var db = CreateConnection();
        await db.OpenAsync(ct);

        await using (var initCmd = db.CreateCommand())
        {
            initCmd.CommandText = SmartCollectionSchema;
            await initCmd.ExecuteNonQueryAsync(ct);
        }

        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            INSERT INTO smart_collections(id, name, search, kind, favorite)
            VALUES($id, $name, $search, $kind, $fav)
            ON CONFLICT(id) DO UPDATE SET name=$name, search=$search, kind=$kind, favorite=$fav;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$search", (object?)search ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fav", favorite.HasValue ? (favorite.Value ? 1 : 0) : DBNull.Value);

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

