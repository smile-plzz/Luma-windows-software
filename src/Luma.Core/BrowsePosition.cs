using Microsoft.Data.Sqlite;
namespace Luma.Core;

public sealed partial class MediaCache
{
    // Locate within the saved filtered ordering using catalog data only. This does not
    // enumerate originals or allocate the records preceding the saved item.
    public async Task<int?> LocateAsync(LibraryQuery query, string source, string path, CancellationToken ct = default)
    {
        await using var db = await OpenConfiguredConnectionAsync(ct);
        var parsed = ParseSearch(query.Search);
        RegisterQueryFunctions(db, query.Sort, query, parsed.Year);
        await using var tx = db.BeginTransaction(deferred: true);
        var hasExclusions = await HasExclusionsAsync(db, tx, ct);

        var value = OrderValue(query.Sort);
        var descending = query.Descending ?? query.Sort is MediaSort.Newest or MediaSort.Largest or MediaSort.Captured or MediaSort.Resolution or MediaSort.Duration;
        var dateCol = query.Sort == MediaSort.Captured ? "b.date_ticks" : "m.modified_ticks";

        await using var anchorCmd = db.CreateCommand();
        anchorCmd.Transaction = tx;
        var anchorFrom = BuildFilteredFrom(
            query, parsed, anchorCmd, hasExclusions,
            out bool browseDatesOnly,
            includeAnnotations: query.FavoritesFirst, includeMediaIds: false, includeMetadata: false,
            requireMedia: false,
            includeBrowseDates: query.Sort == MediaSort.Captured,
            allowBrowseDatesIndexHint: false,
            allowBrowseDatesAlone: true);
        var sidCol = browseDatesOnly ? "b.source_id" : "m.source_id";
        var pathCol = browseDatesOnly ? "b.relative_path" : "m.relative_path";
        if (query.FromTicks is long start) { anchorFrom += " AND " + dateCol + ">=$from"; anchorCmd.Parameters.AddWithValue("$from", start); }
        if (query.BeforeTicks is long end) { anchorFrom += " AND " + dateCol + "<=$before"; anchorCmd.Parameters.AddWithValue("$before", end); }
        var favSel = query.FavoritesFirst ? "COALESCE(a.favorite,0)" : "0";
        anchorCmd.CommandText = $"SELECT {favSel},{value} {anchorFrom} AND {sidCol}=$anchorSource AND {pathCol}=$anchorPath LIMIT 1";
        anchorCmd.Parameters.AddWithValue("$anchorSource", source);
        anchorCmd.Parameters.AddWithValue("$anchorPath", path);

        int anchorFav;
        object anchorVal;
        await using (var r = await anchorCmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            anchorFav = r.GetInt32(0);
            anchorVal = r.GetValue(1);
        }

        var cmpStrict = descending ? ">" : "<";
        bool canSplitIndexCount = !query.FavoritesFirst && query.Sort is MediaSort.Captured or MediaSort.Newest or MediaSort.Oldest or MediaSort.Largest;

        if (canSplitIndexCount)
        {
            long strictBefore;
            await using (var strictCmd = db.CreateCommand())
            {
                strictCmd.Transaction = tx;
                var strictFrom = BuildFilteredFrom(
                    query, parsed, strictCmd, hasExclusions,
                    out _,
                    includeAnnotations: false, includeMediaIds: false, includeMetadata: false,
                    requireMedia: false,
                    includeBrowseDates: query.Sort == MediaSort.Captured,
                    allowBrowseDatesIndexHint: query.Sort == MediaSort.Captured,
                    allowBrowseDatesAlone: true);
                if (query.FromTicks is long s) { strictFrom += " AND " + dateCol + ">=$from"; strictCmd.Parameters.AddWithValue("$from", s); }
                if (query.BeforeTicks is long e) { strictFrom += " AND " + dateCol + "<=$before"; strictCmd.Parameters.AddWithValue("$before", e); }
                strictCmd.CommandText = $"SELECT COUNT(*) {strictFrom} AND {value} {cmpStrict} $aval";
                strictCmd.Parameters.AddWithValue("$aval", anchorVal);
                strictBefore = Convert.ToInt64(await strictCmd.ExecuteScalarAsync(ct));
            }

            long tieBefore;
            await using (var tieCmd = db.CreateCommand())
            {
                tieCmd.Transaction = tx;
                var tieFrom = BuildFilteredFrom(
                    query, parsed, tieCmd, hasExclusions,
                    out bool tieBdOnly,
                    includeAnnotations: false, includeMediaIds: false, includeMetadata: false,
                    requireMedia: false,
                    includeBrowseDates: query.Sort == MediaSort.Captured,
                    allowBrowseDatesIndexHint: query.Sort == MediaSort.Captured,
                    allowBrowseDatesAlone: true);
                var tSid = tieBdOnly ? "b.source_id" : "m.source_id";
                var tPath = tieBdOnly ? "b.relative_path" : "m.relative_path";
                if (query.FromTicks is long s) { tieFrom += " AND " + dateCol + ">=$from"; tieCmd.Parameters.AddWithValue("$from", s); }
                if (query.BeforeTicks is long e) { tieFrom += " AND " + dateCol + "<=$before"; tieCmd.Parameters.AddWithValue("$before", e); }
                tieCmd.CommandText = $"SELECT COUNT(*) {tieFrom} AND {value} = $aval AND ({tSid} < $anchorSource OR ({tSid} = $anchorSource AND {tPath} < $anchorPath))";
                tieCmd.Parameters.AddWithValue("$aval", anchorVal);
                tieCmd.Parameters.AddWithValue("$anchorSource", source);
                tieCmd.Parameters.AddWithValue("$anchorPath", path);
                tieBefore = Convert.ToInt64(await tieCmd.ExecuteScalarAsync(ct));
            }

            return checked((int)(strictBefore + tieBefore));
        }

        await using var countCmd = db.CreateCommand();
        countCmd.Transaction = tx;
        var countFrom = BuildFilteredFrom(
            query, parsed, countCmd, hasExclusions,
            out bool cBdOnly,
            includeAnnotations: query.FavoritesFirst, includeMediaIds: false, includeMetadata: false,
            requireMedia: false,
            includeBrowseDates: query.Sort == MediaSort.Captured,
            allowBrowseDatesIndexHint: false,
            allowBrowseDatesAlone: true);
        var cSid = cBdOnly ? "b.source_id" : "m.source_id";
        var cPath = cBdOnly ? "b.relative_path" : "m.relative_path";
        if (query.FromTicks is long cs) { countFrom += " AND " + dateCol + ">=$from"; countCmd.Parameters.AddWithValue("$from", cs); }
        if (query.BeforeTicks is long ce) { countFrom += " AND " + dateCol + "<=$before"; countCmd.Parameters.AddWithValue("$before", ce); }

        var tieExpr = $"({value} {cmpStrict} $aval OR ({value} = $aval AND ({cSid} < $anchorSource OR ({cSid} = $anchorSource AND {cPath} < $anchorPath))))";
        var predExpr = query.FavoritesFirst
            ? $"(COALESCE(a.favorite,0) > $afav OR (COALESCE(a.favorite,0) = $afav AND {tieExpr}))"
            : tieExpr;
        countCmd.CommandText = $"SELECT COUNT(*) {countFrom} AND {predExpr}";
        countCmd.Parameters.AddWithValue("$aval", anchorVal);
        if (query.FavoritesFirst) countCmd.Parameters.AddWithValue("$afav", anchorFav);
        countCmd.Parameters.AddWithValue("$anchorSource", source);
        countCmd.Parameters.AddWithValue("$anchorPath", path);
        return checked((int)Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct)));
    }
}
