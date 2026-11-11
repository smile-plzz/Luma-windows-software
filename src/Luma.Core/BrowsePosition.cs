using Microsoft.Data.Sqlite;
namespace Luma.Core;

public sealed partial class MediaCache
{
    // Locate within the saved filtered ordering using catalog data only. This does not
    // enumerate originals or allocate the records preceding the saved item.
    public async Task<int?> LocateAsync(LibraryQuery query, string source, string path, CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        RegisterQueryFunctions(db);
        await using var cmd = db.CreateCommand(); QueryParameters(cmd, query);
        var from = QueryFrom(query);
        var date = query.Sort == MediaSort.Captured ? "b.date_ticks" : "m.modified_ticks";
        if (query.FromTicks is long start) { from += " AND " + date + ">=$from"; cmd.Parameters.AddWithValue("$from", start); }
        if (query.BeforeTicks is long end) { from += " AND " + date + "<=$before"; cmd.Parameters.AddWithValue("$before", end); }
        var descending = query.Descending ?? query.Sort is MediaSort.Newest or MediaSort.Largest or MediaSort.Captured or MediaSort.Resolution or MediaSort.Duration;
        var favOrder = query.FavoritesFirst ? "COALESCE(a.favorite,0) DESC, " : "";
        var order = favOrder + OrderValue(query.Sort) + (descending ? " DESC" : " ASC") + ",m.source_id,m.relative_path";
        cmd.CommandText = "WITH ordered AS (SELECT m.source_id,m.relative_path,ROW_NUMBER() OVER(ORDER BY " + order + ")-1 AS position " + from + ") SELECT position FROM ordered WHERE source_id=$anchorSource AND relative_path=$anchorPath";
        cmd.Parameters.AddWithValue("$anchorSource", source); cmd.Parameters.AddWithValue("$anchorPath", path);
        var result = await cmd.ExecuteScalarAsync(ct); return result is null ? null : checked((int)Convert.ToInt64(result));
    }
}
