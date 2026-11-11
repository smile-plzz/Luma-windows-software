using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Luma.Core;

public enum MediaSort { Newest = 0, Oldest = 1, Name = 2, Largest = 3, Captured = 4, Type = 5, Filename = 6, Resolution = 7, Duration = 8 }
public sealed record GeoBounds(double MinLat, double MaxLat, double MinLon, double MaxLon);
public sealed record GeoMarker(
    string SourceId,
    string RelativePath,
    double Latitude,
    double Longitude,
    long DateTicks,
    string Name,
    string Kind,
    bool Favorite,
    string? LocationName = null,
    string? LocationOrigin = null,
    int Width = 0,
    int Height = 0,
    string? CameraModel = null);
public sealed record PlaceTaggedItem(
    CachedMedia Media,
    string LocationName,
    long DateTicks,
    string Kind,
    bool Favorite);
public sealed record MapLocationSummary(
    long GeotaggedCount,
    long ManualPinCount,
    long PlaceTagOnlyCount,
    long UnlocatedCount);
public sealed record GalleryCursor(string Value, string SourceId, string RelativePath, int? Favorite = null);
public sealed record LibraryQuery(
    string? SourceId = null,
    string Search = "",
    string? Kind = null,
    bool FavoritesOnly = false,
    MediaSort Sort = MediaSort.Newest,
    int Offset = 0,
    int Limit = 120,
    string Folder = "",
    bool? Descending = null,
    string? AlbumId = null,
    bool IncludeDescendants = true,
    bool CommonOnly = false,
    long? BeforeTicks = null,
    GalleryCursor? Cursor = null,
    DateOnly? MemoriesOn = null,
    long? FromTicks = null,
    long? KnownTotal = null,
    bool FavoritesFirst = false,
    string? Orientation = null,
    bool UntaggedOnly = false,
    long? MinLength = null,
    long? MaxLength = null,
    bool HasGpsOnly = false,
    GeoBounds? MapBounds = null,
    string? LocationOriginFilter = null);

public sealed record MonthBucket(string Month, long Count)
{
    public string Label => DateTime.ParseExact(Month+"-01","yyyy-MM-dd",CultureInfo.InvariantCulture).ToString("MMMM yyyy");
    public override string ToString() => $"{Label} · {Count:N0}";
}
public sealed record LibraryItem(CachedMedia Media, bool Favorite, string Tags, string Id = "", MediaMetadata? Metadata = null);
public sealed record LibraryPage(IReadOnlyList<LibraryItem> Items, long Total, GalleryCursor? Next = null);

internal sealed record ParsedSearchTokens(
    string Text,
    string Tag,
    string NegTag,
    string Ext,
    string NegExt,
    int Year,
    string Folder,
    bool FavoritesOnly,
    bool UntaggedOnly,
    string? Orientation,
    bool Is4k,
    bool HasGps,
    long? MinLength,
    long? MaxLength,
    long? AfterTicks,
    long? BeforeTicks,
    string Location = "");

public sealed partial class MediaCache
{
    public async Task<IReadOnlyList<SourceInfo>> SourcesAsync(CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT id,root_path,last_seen FROM sources ORDER BY root_path";
        var list = new List<SourceInfo>(); await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var currentRoot=sourceResolver?.ResolveRoot(id);
            list.Add(new(id, currentRoot ?? reader.GetString(1), DateTime.Parse(reader.GetString(2), null,
                DateTimeStyles.RoundtripKind), currentRoot is not null));
        }
        return list;
    }

    private const string GalleryFrom = """
        FROM media m
        LEFT JOIN annotations a ON a.source_id=m.source_id AND a.relative_path=m.relative_path
        LEFT JOIN media_ids i ON i.source_id=m.source_id AND i.relative_path=m.relative_path
        LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks
        WHERE ($source IS NULL OR m.source_id=$source) AND ($kind IS NULL OR m.kind=$kind)
        AND ($favorites=0 OR a.favorite=1)
        AND ($untagged=0 OR a.tags IS NULL OR trim(a.tags)='')
        AND ($memoryDay IS NULL OR (d.taken_ticks IS NOT NULL AND luma_memory(d.taken_ticks)=1))
        AND ($search='' OR m.relative_path LIKE $search ESCAPE '\' OR a.tags LIKE $search ESCAPE '\' OR d.location_name LIKE $search ESCAPE '\' OR d.camera_model LIKE $search ESCAPE '\')
        AND ($locationFilter='' OR d.location_name LIKE $locationFilter ESCAPE '\')
        AND ($locOrigin IS NULL OR ($locOrigin='manual' AND d.location_origin='manual') OR ($locOrigin='file' AND d.latitude IS NOT NULL AND d.longitude IS NOT NULL AND (d.location_origin IS NULL OR d.location_origin!='manual')))
        AND ($tagFilter='' OR a.tags LIKE $tagFilter ESCAPE '\')
        AND ($negTagFilter='' OR a.tags IS NULL OR a.tags NOT LIKE $negTagFilter ESCAPE '\')
        AND ($extFilter='' OR lower(m.relative_path) LIKE $extFilter ESCAPE '\')
        AND ($negExtFilter='' OR lower(m.relative_path) NOT LIKE $negExtFilter ESCAPE '\')
        AND ($yearFilter=0 OR luma_year(coalesce(d.taken_ticks, m.modified_ticks))=$yearFilter)
        AND ($folderToken='' OR m.relative_path LIKE $folderToken ESCAPE '\')
        AND ($orientation IS NULL OR (
            d.width > 0 AND d.height > 0 AND (
                ($orientation='landscape' AND d.width > d.height)
                OR ($orientation='portrait' AND d.height > d.width)
                OR ($orientation='square' AND d.width = d.height)
            )))
        AND ($is4k=0 OR COALESCE(d.width,0)>=3840 OR COALESCE(d.height,0)>=3840 OR (CAST(COALESCE(d.width,0) AS INTEGER)*CAST(COALESCE(d.height,0) AS INTEGER))>=8294400)
        AND ($minLength IS NULL OR m.length >= $minLength)
        AND ($maxLength IS NULL OR m.length <= $maxLength)
        AND ($hasGps=0 OR (d.latitude IS NOT NULL AND d.longitude IS NOT NULL))
        AND ($hasBounds=0 OR (d.latitude IS NOT NULL AND d.longitude IS NOT NULL AND d.latitude BETWEEN $minLat AND $maxLat AND (($minLon <= $maxLon AND d.longitude BETWEEN $minLon AND $maxLon) OR ($minLon > $maxLon AND (d.longitude >= $minLon OR d.longitude <= $maxLon)))))
        AND ($searchAfter IS NULL OR COALESCE(d.taken_ticks, m.modified_ticks) >= $searchAfter)
        AND ($searchBefore IS NULL OR COALESCE(d.taken_ticks, m.modified_ticks) <= $searchBefore)
        AND ($folder='' OR m.relative_path LIKE $folder ESCAPE '\')
        AND ($recursive=1 OR instr(substr(m.relative_path,length($prefix)+1),$separator)=0)
        AND ($album IS NULL OR EXISTS(SELECT 1 FROM album_items ai WHERE ai.media_id=i.id AND ai.album_id=$album))
        AND ($common=0 OR lower(m.relative_path) GLOB '*.jpg' OR lower(m.relative_path) GLOB '*.jpeg'
          OR lower(m.relative_path) GLOB '*.png' OR lower(m.relative_path) GLOB '*.heic' OR lower(m.relative_path) GLOB '*.heif'
          OR lower(m.relative_path) GLOB '*.mp4' OR lower(m.relative_path) GLOB '*.mov' OR lower(m.relative_path) GLOB '*.gif'
          OR lower(m.relative_path) GLOB '*.webp' OR lower(m.relative_path) GLOB '*.bmp')
        """ + " AND " + AllowedMediaSql;

    private static string OrderValue(MediaSort sort) => sort switch
    {
        MediaSort.Name => "m.relative_path COLLATE NOCASE",
        MediaSort.Largest => "m.length",
        MediaSort.Captured => "b.date_ticks",
        MediaSort.Type => "luma_extension(m.relative_path) COLLATE NOCASE",
        MediaSort.Filename => "luma_filename(m.relative_path) COLLATE NOCASE",
        MediaSort.Resolution => "(CAST(COALESCE(d.width,0) AS INTEGER) * CAST(COALESCE(d.height,0) AS INTEGER))",
        MediaSort.Duration => "COALESCE(d.duration,0.0)",
        _ => "m.modified_ticks"
    };

    private static string QueryFrom(LibraryQuery query)
    {
        if (query.Sort != MediaSort.Captured) return GalleryFrom;
        var index = query.Descending == false ? "ix_browse_dates_asc" : "ix_browse_dates_desc";
        return GalleryFrom.Replace("FROM media m", $"FROM browse_dates b INDEXED BY {index} CROSS JOIN media m ON m.source_id=b.source_id AND m.relative_path=b.relative_path");
    }

    private static void RegisterQueryFunctions(SqliteConnection db)
    {
        db.CreateFunction<string, string>("luma_extension", path => Path.GetExtension(path).ToLowerInvariant(), isDeterministic: true);
        db.CreateFunction<string, string>("luma_filename", path => Path.GetFileName(path), isDeterministic: true);
    }

    private static long? ParseSizeToken(string raw)
    {
        raw = raw.Trim().ToLowerInvariant();
        if (raw.Length == 0) return null;
        double mult = 1;
        if (raw.EndsWith("gb")) { mult = 1024d * 1024 * 1024; raw = raw[..^2]; }
        else if (raw.EndsWith("mb")) { mult = 1024d * 1024; raw = raw[..^2]; }
        else if (raw.EndsWith("kb")) { mult = 1024d; raw = raw[..^2]; }
        else if (raw.EndsWith("b")) { mult = 1; raw = raw[..^1]; }
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var val) && val >= 0
            ? (long)(val * mult)
            : null;
    }

    private static (long? StartTicks, long? EndTicks) ParseDateToken(string raw)
    {
        if (DateTime.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var day))
        {
            var start = DateTime.SpecifyKind(day.Date, DateTimeKind.Local).ToUniversalTime().Ticks;
            var end = DateTime.SpecifyKind(day.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime().Ticks - 1;
            return (start, end);
        }
        if (DateTime.TryParseExact(raw + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var month))
        {
            var start = DateTime.SpecifyKind(month.Date, DateTimeKind.Local).ToUniversalTime().Ticks;
            var end = DateTime.SpecifyKind(month.Date.AddMonths(1), DateTimeKind.Local).ToUniversalTime().Ticks - 1;
            return (start, end);
        }
        return (null, null);
    }

    internal static ParsedSearchTokens ParseSearch(string search)
    {
        var raw = search.Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return new("", "", "", "", "", 0, "", false, false, null, false, false, null, null, null, null, "");

        var tokens = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var textTokens = new List<string>();
        string tag = "", negTag = "", ext = "", negExt = "", folder = "", location = "";
        int year = 0;
        bool fav = false, untagged = false, is4k = false, hasGps = false;
        string? orientation = null;
        long? minLen = null, maxLen = null, afterTicks = null, beforeTicks = null;

        foreach (var token in tokens)
        {
            if (token.StartsWith("tag:", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
                tag = token[4..];
            else if (token.StartsWith("-tag:", StringComparison.OrdinalIgnoreCase) && token.Length > 5)
                negTag = token[5..];
            else if (token.StartsWith("ext:", StringComparison.OrdinalIgnoreCase) && token.Length > 4)
                ext = token[4..].TrimStart('.');
            else if (token.StartsWith("-ext:", StringComparison.OrdinalIgnoreCase) && token.Length > 5)
                negExt = token[5..].TrimStart('.');
            else if (token.StartsWith("year:", StringComparison.OrdinalIgnoreCase) && int.TryParse(token[5..], out var y))
                year = y;
            else if (token.StartsWith("folder:", StringComparison.OrdinalIgnoreCase) && token.Length > 7)
                folder = token[7..];
            else if (token.StartsWith("location:", StringComparison.OrdinalIgnoreCase) && token.Length > 9)
                location = token[9..];
            else if (token.StartsWith("date:", StringComparison.OrdinalIgnoreCase) && token.Length > 5)
            {
                var (s, e) = ParseDateToken(token[5..]);
                if (s is not null) afterTicks = afterTicks is null ? s : Math.Max(afterTicks.Value, s.Value);
                if (e is not null) beforeTicks = beforeTicks is null ? e : Math.Min(beforeTicks.Value, e.Value);
            }
            else if (token.StartsWith("after:", StringComparison.OrdinalIgnoreCase) && token.Length > 6)
            {
                var (s, _) = ParseDateToken(token[6..]);
                if (s is not null) afterTicks = afterTicks is null ? s : Math.Max(afterTicks.Value, s.Value);
            }
            else if (token.StartsWith("before:", StringComparison.OrdinalIgnoreCase) && token.Length > 7)
            {
                var (_, e) = ParseDateToken(token[7..]);
                if (e is not null) beforeTicks = beforeTicks is null ? e : Math.Min(beforeTicks.Value, e.Value);
            }
            else if (token.StartsWith("size:>", StringComparison.OrdinalIgnoreCase))
            {
                var numStr = token.StartsWith("size:>=", StringComparison.OrdinalIgnoreCase) ? token[7..] : token[6..];
                var parsed = ParseSizeToken(numStr);
                if (parsed is not null) minLen = minLen is null ? parsed : Math.Max(minLen.Value, parsed.Value);
            }
            else if (token.StartsWith("size:<", StringComparison.OrdinalIgnoreCase))
            {
                var numStr = token.StartsWith("size:<=", StringComparison.OrdinalIgnoreCase) ? token[7..] : token[6..];
                var parsed = ParseSizeToken(numStr);
                if (parsed is not null) maxLen = maxLen is null ? parsed : Math.Min(maxLen.Value, parsed.Value);
            }
            else if (token.Equals("is:fav", StringComparison.OrdinalIgnoreCase) || token.Equals("is:favorite", StringComparison.OrdinalIgnoreCase))
                fav = true;
            else if (token.Equals("is:untagged", StringComparison.OrdinalIgnoreCase))
                untagged = true;
            else if (token.Equals("is:portrait", StringComparison.OrdinalIgnoreCase))
                orientation = "portrait";
            else if (token.Equals("is:landscape", StringComparison.OrdinalIgnoreCase))
                orientation = "landscape";
            else if (token.Equals("is:square", StringComparison.OrdinalIgnoreCase))
                orientation = "square";
            else if (token.Equals("is:4k", StringComparison.OrdinalIgnoreCase))
                is4k = true;
            else if (token.Equals("has:gps", StringComparison.OrdinalIgnoreCase) || token.Equals("is:geotagged", StringComparison.OrdinalIgnoreCase) || token.Equals("has:loc", StringComparison.OrdinalIgnoreCase) || token.Equals("is:located", StringComparison.OrdinalIgnoreCase))
                hasGps = true;
            else
                textTokens.Add(token);
        }

        return new(string.Join(" ", textTokens), tag, negTag, ext, negExt, year, folder, fav, untagged, orientation, is4k, hasGps, minLen, maxLen, afterTicks, beforeTicks, location);
    }

    private static void QueryParameters(SqliteCommand cmd, LibraryQuery query)
    {
        var reference = query.MemoriesOn.GetValueOrDefault();
        cmd.Connection!.CreateFunction<long?, bool>("luma_memory", ticks =>
        {
            if (ticks is null || query.MemoriesOn is null || ticks.Value < DateTime.MinValue.Ticks || ticks.Value > DateTime.MaxValue.Ticks) return false;
            var taken = new DateTime(ticks.Value, DateTimeKind.Utc).ToLocalTime();
            return taken.Year < reference.Year && taken.Month == reference.Month && taken.Day == reference.Day;
        });
        cmd.Connection!.CreateFunction<long?, int>("luma_year", ticks =>
        {
            if (ticks is null || ticks.Value < DateTime.MinValue.Ticks || ticks.Value > DateTime.MaxValue.Ticks) return 0;
            return new DateTime(ticks.Value, DateTimeKind.Utc).ToLocalTime().Year;
        }, isDeterministic: true);
        cmd.Parameters.AddWithValue("$memoryDay", (object?)query.MemoriesOn?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? DBNull.Value);
        static string Escape(string value) => value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var prefix = query.Folder.Length == 0 ? "" : query.Folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var parsed = ParseSearch(query.Search);

        var effectiveFav = query.FavoritesOnly || parsed.FavoritesOnly;
        var effectiveUntagged = query.UntaggedOnly || parsed.UntaggedOnly;
        var effectiveOrientation = query.Orientation ?? parsed.Orientation;
        long? effectiveMinLen = query.MinLength is not null && parsed.MinLength is not null
            ? Math.Max(query.MinLength.Value, parsed.MinLength.Value)
            : query.MinLength ?? parsed.MinLength;
        long? effectiveMaxLen = query.MaxLength is not null && parsed.MaxLength is not null
            ? Math.Min(query.MaxLength.Value, parsed.MaxLength.Value)
            : query.MaxLength ?? parsed.MaxLength;
        var effectiveHasGps = query.HasGpsOnly || parsed.HasGps || query.MapBounds is not null;

        cmd.Parameters.AddWithValue("$source", (object?)query.SourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", (object?)query.Kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$favorites", effectiveFav ? 1 : 0);
        cmd.Parameters.AddWithValue("$untagged", effectiveUntagged ? 1 : 0);

        cmd.Parameters.AddWithValue("$search", parsed.Text.Length == 0 ? "" : "%" + Escape(parsed.Text) + "%");
        cmd.Parameters.AddWithValue("$tagFilter", parsed.Tag.Length == 0 ? "" : "%" + Escape(parsed.Tag) + "%");
        cmd.Parameters.AddWithValue("$negTagFilter", parsed.NegTag.Length == 0 ? "" : "%" + Escape(parsed.NegTag) + "%");
        cmd.Parameters.AddWithValue("$extFilter", parsed.Ext.Length == 0 ? "" : "%." + Escape(parsed.Ext));
        cmd.Parameters.AddWithValue("$negExtFilter", parsed.NegExt.Length == 0 ? "" : "%." + Escape(parsed.NegExt));
        cmd.Parameters.AddWithValue("$yearFilter", parsed.Year);
        cmd.Parameters.AddWithValue("$folderToken", parsed.Folder.Length == 0 ? "" : "%" + Escape(parsed.Folder) + "%");
        cmd.Parameters.AddWithValue("$locationFilter", parsed.Location.Length == 0 ? "" : "%" + Escape(parsed.Location) + "%");
        cmd.Parameters.AddWithValue("$locOrigin", (object?)query.LocationOriginFilter ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$orientation", (object?)effectiveOrientation ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$is4k", parsed.Is4k ? 1 : 0);
        cmd.Parameters.AddWithValue("$minLength", (object?)effectiveMinLen ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$maxLength", (object?)effectiveMaxLen ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hasGps", effectiveHasGps ? 1 : 0);
        cmd.Parameters.AddWithValue("$hasBounds", query.MapBounds is not null ? 1 : 0);
        cmd.Parameters.AddWithValue("$minLat", query.MapBounds?.MinLat ?? 0.0);
        cmd.Parameters.AddWithValue("$maxLat", query.MapBounds?.MaxLat ?? 0.0);
        cmd.Parameters.AddWithValue("$minLon", query.MapBounds?.MinLon ?? 0.0);
        cmd.Parameters.AddWithValue("$maxLon", query.MapBounds?.MaxLon ?? 0.0);
        cmd.Parameters.AddWithValue("$searchAfter", (object?)parsed.AfterTicks ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$searchBefore", (object?)parsed.BeforeTicks ?? DBNull.Value);

        cmd.Parameters.AddWithValue("$folder", prefix.Length == 0 ? "" : Escape(prefix) + "%");
        cmd.Parameters.AddWithValue("$prefix", prefix); cmd.Parameters.AddWithValue("$separator", Path.DirectorySeparatorChar.ToString());
        cmd.Parameters.AddWithValue("$recursive", query.IncludeDescendants ? 1 : 0);
        cmd.Parameters.AddWithValue("$album", (object?)query.AlbumId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$common", query.CommonOnly ? 1 : 0);
    }

    public async Task<LibraryPage> QueryAsync(LibraryQuery query, CancellationToken ct = default)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(query));
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        RegisterQueryFunctions(db);
        // A gallery read must not reserve SQLite's single writer slot. WAL lets
        // this deferred, SELECT-only snapshot coexist with a long-running scan.
        // Keep count and rows in one snapshot rather than removing the transaction.
        ct.ThrowIfCancellationRequested();
        await using var tx = db.BeginTransaction(deferred: true);
        await using var cmd = db.CreateCommand(); cmd.Transaction = tx; QueryParameters(cmd, query);
        var value = OrderValue(query.Sort);
        var descending = query.Descending ?? query.Sort is MediaSort.Newest or MediaSort.Largest or MediaSort.Captured or MediaSort.Resolution or MediaSort.Duration;
        var from = QueryFrom(query);
        if (query.BeforeTicks is not null)
        {
            from += " AND " + (query.Sort == MediaSort.Captured ? OrderValue(MediaSort.Captured) : "m.modified_ticks") + " <= $before";
            cmd.Parameters.AddWithValue("$before", query.BeforeTicks.Value);
        }
        if (query.FromTicks is not null)
        {
            from += " AND " + (query.Sort == MediaSort.Captured ? OrderValue(MediaSort.Captured) : "m.modified_ticks") + " >= $from";
            cmd.Parameters.AddWithValue("$from", query.FromTicks.Value);
        }
        var simpleCount = query.Search.Trim().Length == 0 && !query.FavoritesOnly && !query.UntaggedOnly
            && query.Orientation is null && query.MinLength is null && query.MaxLength is null
            && !query.HasGpsOnly && query.MapBounds is null && query.LocationOriginFilter is null
            && query.MemoriesOn is null && query.AlbumId is null
            && !query.CommonOnly && query.Folder.Length == 0 && query.IncludeDescendants
            && query.BeforeTicks is null && query.FromTicks is null;
        cmd.CommandText = simpleCount
            ? "SELECT COUNT(*) FROM media m WHERE ($source IS NULL OR m.source_id=$source) AND ($kind IS NULL OR m.kind=$kind) AND " + AllowedMediaSql
            : "SELECT COUNT(*) " + from;
        var total = query.KnownTotal ?? Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        if (query.Cursor is not null)
        {
            var cmpEq = descending ? "<=" : ">=";
            var cmpStrict = descending ? "<" : ">";
            var tieClause = $"({value} {cmpEq} $cursor AND ({value} {cmpStrict} $cursor OR ({value}=$cursor AND (m.source_id>$cs OR (m.source_id=$cs AND m.relative_path>$cp)))))";
            if (query.FavoritesFirst && query.Cursor.Favorite is int cfav)
            {
                from += $" AND (COALESCE(a.favorite,0) < $cfav OR (COALESCE(a.favorite,0) = $cfav AND {tieClause}))";
                cmd.Parameters.AddWithValue("$cfav", cfav);
            }
            else
            {
                from += " AND " + tieClause;
            }
            object cursorParam = query.Sort switch
            {
                MediaSort.Name or MediaSort.Type or MediaSort.Filename => query.Cursor.Value,
                MediaSort.Duration => double.Parse(query.Cursor.Value, CultureInfo.InvariantCulture),
                _ => long.Parse(query.Cursor.Value, CultureInfo.InvariantCulture)
            };
            cmd.Parameters.AddWithValue("$cursor", cursorParam);
            cmd.Parameters.AddWithValue("$cs", query.Cursor.SourceId); cmd.Parameters.AddWithValue("$cp", query.Cursor.RelativePath);
        }
        var favOrder = query.FavoritesFirst ? "COALESCE(a.favorite,0) DESC, " : "";
        cmd.CommandText =
            "SELECT " + ItemColumnsSql + "," + value + " " +
            from + " ORDER BY " + favOrder + value + (descending ? " DESC" : " ASC") +
            (query.Sort == MediaSort.Captured && !query.FavoritesFirst ? ",b.source_id,b.relative_path" : ",m.source_id,m.relative_path") +
            " LIMIT $limit OFFSET $offset";
        cmd.Parameters.AddWithValue("$limit", query.Limit); cmd.Parameters.AddWithValue("$offset", query.Cursor is null ? query.Offset : 0);
        var roots = new Dictionary<string, bool>(); var items = new List<LibraryItem>(); GalleryCursor? next = null;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            items.Add(ReadLibraryItemRow(r, roots));
            var cursorRaw = query.Sort == MediaSort.Duration
                ? Convert.ToDouble(r.GetValue(24), CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture)
                : Convert.ToString(r.GetValue(24), CultureInfo.InvariantCulture)!;
            var last = items[^1].Media;
            next = new(cursorRaw, last.SourceId, last.RelativePath, query.FavoritesFirst ? (items[^1].Favorite ? 1 : 0) : null);
        }
        return new(items, total, next);
    }

    private const string ItemColumnsSql =
        "m.source_id,m.relative_path,m.length,m.modified_ticks,m.kind,COALESCE(a.favorite,0),COALESCE(a.tags,''),COALESCE(i.id,'')," +
        "d.taken_ticks,COALESCE(d.date_origin,'Modification date fallback'),COALESCE(d.width,0),COALESCE(d.height,0),COALESCE(d.duration,0),COALESCE(d.orientation,0)," +
        "d.latitude,d.longitude,d.camera_model,d.iso,d.f_number,d.exposure_seconds,d.focal_length,d.location_name,d.location_origin,COALESCE(d.meta_version,0)";

    private LibraryItem ReadLibraryItemRow(SqliteDataReader r, Dictionary<string, bool> roots)
    {
        var id = r.GetString(0);
        if (!roots.TryGetValue(id, out var online)) roots[id] = online = sourceResolver?.ResolveRoot(id) is not null;
        var fav = r.GetInt64(5) == 1;
        var meta = new MediaMetadata(
            TakenTicks: r.IsDBNull(8) ? null : r.GetInt64(8),
            DateOrigin: r.GetString(9),
            Width: r.GetInt32(10),
            Height: r.GetInt32(11),
            DurationSeconds: r.GetDouble(12),
            Orientation: r.GetInt32(13),
            Latitude: r.IsDBNull(14) ? null : r.GetDouble(14),
            Longitude: r.IsDBNull(15) ? null : r.GetDouble(15),
            CameraModel: r.IsDBNull(16) ? null : r.GetString(16),
            Iso: r.IsDBNull(17) ? null : r.GetInt32(17),
            FNumber: r.IsDBNull(18) ? null : r.GetDouble(18),
            ExposureSeconds: r.IsDBNull(19) ? null : r.GetDouble(19),
            FocalLength: r.IsDBNull(20) ? null : r.GetDouble(20),
            LocationName: r.IsDBNull(21) ? null : r.GetString(21),
            LocationOrigin: r.IsDBNull(22) ? null : r.GetString(22),
            MetaVersion: r.GetInt32(23));
        return new(new(id, r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), online), fav, r.GetString(6), r.GetString(7), meta);
    }

    public async Task<LibraryItem?> GetItemAsync(string sourceId, string relativePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(relativePath)) return null;
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT " + ItemColumnsSql + " " +
            "FROM media m " +
            "LEFT JOIN annotations a ON a.source_id=m.source_id AND a.relative_path=m.relative_path " +
            "LEFT JOIN media_ids i ON i.source_id=m.source_id AND i.relative_path=m.relative_path " +
            "LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks " +
            "WHERE m.source_id=$sid AND m.relative_path=$rpath AND " + AllowedMediaSql + " LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$rpath", relativePath);
        var roots = new Dictionary<string, bool>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadLibraryItemRow(r, roots) : null;
    }

    private static string AppendDateBounds(string from, SqliteCommand cmd, LibraryQuery query)
    {
        var dateExpr = query.Sort == MediaSort.Captured ? OrderValue(MediaSort.Captured) : "COALESCE(d.taken_ticks,m.modified_ticks)";
        if (query.BeforeTicks is not null)
        {
            from += " AND " + dateExpr + " <= $before";
            cmd.Parameters.AddWithValue("$before", query.BeforeTicks.Value);
        }
        if (query.FromTicks is not null)
        {
            from += " AND " + dateExpr + " >= $from";
            cmd.Parameters.AddWithValue("$from", query.FromTicks.Value);
        }
        return from;
    }

    public async Task<IReadOnlyList<GeoMarker>> QueryGeotaggedMarkersAsync(LibraryQuery query, int limit = 25000, CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        RegisterQueryFunctions(db);
        var scoped = query with { HasGpsOnly = true, MapBounds = null };
        await using var cmd = db.CreateCommand();
        QueryParameters(cmd, scoped);
        var from = AppendDateBounds(QueryFrom(scoped), cmd, scoped);
        cmd.CommandText =
            "SELECT m.source_id,m.relative_path,d.latitude,d.longitude,COALESCE(d.taken_ticks,m.modified_ticks),m.kind,COALESCE(a.favorite,0),d.location_name,d.location_origin,COALESCE(d.width,0),COALESCE(d.height,0),d.camera_model " +
            from + " ORDER BY COALESCE(d.taken_ticks,m.modified_ticks) DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        var markers = new List<GeoMarker>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var path = r.GetString(1);
            markers.Add(new(
                r.GetString(0),
                path,
                r.GetDouble(2),
                r.GetDouble(3),
                r.GetInt64(4),
                Path.GetFileName(path),
                r.GetString(5),
                r.GetInt64(6) == 1,
                r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8),
                r.GetInt32(9),
                r.GetInt32(10),
                r.IsDBNull(11) ? null : r.GetString(11)));
        }
        return markers;
    }

    public async Task<IReadOnlyList<PlaceTaggedItem>> QueryPlaceTaggedWithoutCoordsAsync(LibraryQuery query, int limit = 500, CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        RegisterQueryFunctions(db);
        var scoped = query with { HasGpsOnly = false, MapBounds = null, LocationOriginFilter = null };
        await using var cmd = db.CreateCommand();
        QueryParameters(cmd, scoped);
        var from = AppendDateBounds(QueryFrom(scoped), cmd, scoped) +
            " AND (d.latitude IS NULL OR d.longitude IS NULL) AND d.location_name IS NOT NULL AND trim(d.location_name) != ''";
        cmd.CommandText =
            "SELECT m.source_id,m.relative_path,m.length,m.modified_ticks,m.kind,d.location_name,COALESCE(d.taken_ticks,m.modified_ticks),COALESCE(a.favorite,0) " +
            from + " ORDER BY COALESCE(d.taken_ticks,m.modified_ticks) DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 5000));
        var roots = new Dictionary<string, bool>();
        var list = new List<PlaceTaggedItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            var sid = r.GetString(0);
            if (!roots.TryGetValue(sid, out var online)) roots[sid] = online = sourceResolver?.ResolveRoot(sid) is not null;
            list.Add(new(new(sid, r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetString(4), online), r.GetString(5), r.GetInt64(6), r.GetString(4), r.GetInt64(7) == 1));
        }
        return list;
    }

    public async Task<IReadOnlyList<LibraryItem>> QueryUnlocatedItemsAsync(LibraryQuery query, int limit = 200, CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        RegisterQueryFunctions(db);
        var scoped = query with { HasGpsOnly = false, MapBounds = null, LocationOriginFilter = null };
        await using var cmd = db.CreateCommand();
        QueryParameters(cmd, scoped);
        var from = AppendDateBounds(QueryFrom(scoped), cmd, scoped) + " AND (d.latitude IS NULL OR d.longitude IS NULL)";
        cmd.CommandText = "SELECT " + ItemColumnsSql + " " + from + " ORDER BY COALESCE(d.taken_ticks,m.modified_ticks) DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 2000));
        var roots = new Dictionary<string, bool>();
        var items = new List<LibraryItem>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) items.Add(ReadLibraryItemRow(r, roots));
        return items;
    }

    public async Task<MapLocationSummary> QueryMapSummaryAsync(LibraryQuery query, CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        RegisterQueryFunctions(db);
        var scoped = query with { HasGpsOnly = false, MapBounds = null, LocationOriginFilter = null };
        await using var cmd = db.CreateCommand();
        QueryParameters(cmd, scoped);
        var from = AppendDateBounds(QueryFrom(scoped), cmd, scoped);
        cmd.CommandText =
            "SELECT " +
            "COALESCE(SUM(CASE WHEN d.latitude IS NOT NULL AND d.longitude IS NOT NULL THEN 1 ELSE 0 END), 0), " +
            "COALESCE(SUM(CASE WHEN d.latitude IS NOT NULL AND d.longitude IS NOT NULL AND d.location_origin='manual' THEN 1 ELSE 0 END), 0), " +
            "COALESCE(SUM(CASE WHEN (d.latitude IS NULL OR d.longitude IS NULL) AND d.location_name IS NOT NULL AND trim(d.location_name)!='' THEN 1 ELSE 0 END), 0), " +
            "COALESCE(SUM(CASE WHEN d.latitude IS NULL OR d.longitude IS NULL THEN 1 ELSE 0 END), 0) " +
            from;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (await r.ReadAsync(ct))
            return new(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
        return new(0, 0, 0, 0);
    }

    public async Task<IReadOnlyList<string>> MonthsAsync(LibraryQuery query, CancellationToken ct = default) =>
        (await MonthBucketsAsync(query, ct)).Select(m => m.Month).ToArray();

    // Month navigation describes the filtered collection, before a month/date range is applied.
    public async Task<IReadOnlyList<MonthBucket>> MonthBucketsAsync(LibraryQuery query, CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        RegisterQueryFunctions(db);
        await using var cmd = db.CreateCommand(); QueryParameters(cmd, query);
        var date = query.Sort == MediaSort.Captured ? OrderValue(MediaSort.Captured) : "m.modified_ticks";
        cmd.CommandText = "SELECT strftime('%Y-%m',(" + date + "-621355968000000000)/10000000,'unixepoch','localtime'),COUNT(*) " + QueryFrom(query) + " GROUP BY 1 ORDER BY 1 DESC";
        var result = new List<MonthBucket>(); await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) if (!r.IsDBNull(0)) result.Add(new(r.GetString(0), r.GetInt64(1)));
        return result;
    }

    public async Task AnnotateAsync(CachedMedia media, bool favorite, string tags, CancellationToken ct = default)
    {
        tags = string.Join(", ", tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (tags.Length > 1000) throw new ArgumentException("Keep tags under 1,000 characters.", nameof(tags));
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "INSERT INTO annotations VALUES($id,$path,$favorite,$tags) ON CONFLICT(source_id,relative_path) DO UPDATE SET favorite=$favorite,tags=$tags";
        cmd.Parameters.AddWithValue("$id", media.SourceId); cmd.Parameters.AddWithValue("$path", media.RelativePath);
        cmd.Parameters.AddWithValue("$favorite", favorite ? 1 : 0); cmd.Parameters.AddWithValue("$tags", tags);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveSourceAsync(string id, CancellationToken ct = default)
    {
        using var lease = IndexingLease();
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var transaction = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using var cmd = db.CreateCommand(); cmd.Transaction = transaction;
        cmd.CommandText = "DELETE FROM exclusions WHERE source_id=$id; DELETE FROM scan_warnings WHERE source_id=$id; DELETE FROM scan_media WHERE source_id=$id; DELETE FROM scan_folders WHERE source_id=$id; DELETE FROM scan_jobs WHERE source_id=$id; DELETE FROM album_items WHERE media_id IN(SELECT id FROM media_ids WHERE source_id=$id); DELETE FROM media_ids WHERE source_id=$id; DELETE FROM metadata WHERE source_id=$id; DELETE FROM source_labels WHERE source_id=$id; DELETE FROM annotations WHERE source_id=$id; DELETE FROM media WHERE source_id=$id; DELETE FROM sources WHERE id=$id;";
        cmd.Parameters.AddWithValue("$id", id); await cmd.ExecuteNonQueryAsync(ct); await transaction.CommitAsync(ct);
    }
}
