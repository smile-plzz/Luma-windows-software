using System.Globalization;
using System.Text;
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

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static void RegisterQueryFunctions(SqliteConnection db, MediaSort sort, LibraryQuery query, int parsedYear)
    {
        if (sort == MediaSort.Type)
            db.CreateFunction<string, string>("luma_extension", path => Path.GetExtension(path).ToLowerInvariant(), isDeterministic: true);
        else if (sort == MediaSort.Filename)
            db.CreateFunction<string, string>("luma_filename", path => Path.GetFileName(path), isDeterministic: true);

        if (query.MemoriesOn is not null)
        {
            var reference = query.MemoriesOn.Value;
            db.CreateFunction<long?, bool>("luma_memory", ticks =>
            {
                if (ticks is null || ticks.Value < DateTime.MinValue.Ticks || ticks.Value > DateTime.MaxValue.Ticks) return false;
                var taken = new DateTime(ticks.Value, DateTimeKind.Utc).ToLocalTime();
                return taken.Year < reference.Year && taken.Month == reference.Month && taken.Day == reference.Day;
            });
        }

        if (parsedYear != 0)
        {
            db.CreateFunction<long?, int>("luma_year", ticks =>
            {
                if (ticks is null || ticks.Value < DateTime.MinValue.Ticks || ticks.Value > DateTime.MaxValue.Ticks) return 0;
                return new DateTime(ticks.Value, DateTimeKind.Utc).ToLocalTime().Year;
            }, isDeterministic: true);
        }
    }

    internal static string BuildFilteredFrom(
        LibraryQuery query,
        ParsedSearchTokens parsed,
        SqliteCommand cmd,
        bool hasExclusions,
        out bool browseDatesOnly,
        bool includeAnnotations = false,
        bool includeMediaIds = false,
        bool includeMetadata = false,
        bool requireMedia = false,
        bool includeBrowseDates = false,
        bool allowBrowseDatesIndexHint = true,
        bool allowBrowseDatesAlone = true,
        string? extraWhere = null)
    {
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

        bool needAnn = includeAnnotations || effectiveFav || effectiveUntagged || query.FavoritesFirst
            || parsed.Text.Length > 0 || parsed.Tag.Length > 0 || parsed.NegTag.Length > 0;
        bool needIds = includeMediaIds || query.AlbumId is not null;
        bool needMeta = includeMetadata || query.MemoriesOn is not null || parsed.Text.Length > 0
            || parsed.Location.Length > 0 || query.LocationOriginFilter is not null || parsed.Year != 0
            || effectiveOrientation is not null || parsed.Is4k || effectiveHasGps || query.MapBounds is not null
            || parsed.AfterTicks is not null || parsed.BeforeTicks is not null
            || query.Sort is MediaSort.Resolution or MediaSort.Duration;
        bool filterNeedsMedia = query.Kind is not null || parsed.Ext.Length > 0 || parsed.NegExt.Length > 0
            || parsed.Folder.Length > 0 || effectiveMinLen is not null || effectiveMaxLen is not null
            || query.Folder.Length > 0 || !query.IncludeDescendants || query.CommonOnly || hasExclusions
            || parsed.Year != 0 || parsed.AfterTicks is not null || parsed.BeforeTicks is not null;

        bool joinBrowseDates = includeBrowseDates || query.Sort == MediaSort.Captured;
        browseDatesOnly = allowBrowseDatesAlone && joinBrowseDates && !requireMedia && !needAnn && !needIds && !needMeta && !filterNeedsMedia;
        bool innerJoinMeta = !includeMetadata && (query.MemoriesOn is not null || parsed.Location.Length > 0
            || query.LocationOriginFilter is not null || effectiveOrientation is not null
            || effectiveHasGps || query.MapBounds is not null);

        var sb = new StringBuilder(512);
        if (browseDatesOnly)
        {
            sb.Append("FROM browse_dates b");
            if (allowBrowseDatesIndexHint)
            {
                var index = query.Descending == false ? "ix_browse_dates_asc" : "ix_browse_dates_desc";
                sb.Append(" INDEXED BY ").Append(index);
            }
        }
        else if (joinBrowseDates)
        {
            sb.Append("FROM browse_dates b");
            if (allowBrowseDatesIndexHint)
            {
                var index = query.Descending == false ? "ix_browse_dates_asc" : "ix_browse_dates_desc";
                sb.Append(" INDEXED BY ").Append(index);
            }
            sb.Append(" CROSS JOIN media m ON m.source_id=b.source_id AND m.relative_path=b.relative_path");
        }
        else
        {
            sb.Append("FROM media m");
        }

        if (needAnn)
            sb.Append(" LEFT JOIN annotations a ON a.source_id=m.source_id AND a.relative_path=m.relative_path");
        if (needIds)
            sb.Append(" LEFT JOIN media_ids i ON i.source_id=m.source_id AND i.relative_path=m.relative_path");
        if (needMeta)
        {
            sb.Append(innerJoinMeta ? " JOIN metadata d ON " : " LEFT JOIN metadata d ON ");
            sb.Append("d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks");
        }

        sb.Append(" WHERE 1=1");
        if (query.SourceId is not null)
        {
            sb.Append(joinBrowseDates ? " AND b.source_id=$source" : " AND m.source_id=$source");
            cmd.Parameters.AddWithValue("$source", query.SourceId);
        }
        if (query.Kind is not null)
        {
            sb.Append(" AND m.kind=$kind");
            cmd.Parameters.AddWithValue("$kind", query.Kind);
        }
        if (effectiveFav)
            sb.Append(" AND a.favorite=1");
        if (effectiveUntagged)
            sb.Append(" AND (a.tags IS NULL OR trim(a.tags)='')");
        if (query.MemoriesOn is not null)
            sb.Append(" AND (d.taken_ticks IS NOT NULL AND luma_memory(d.taken_ticks)=1)");
        if (parsed.Text.Length > 0)
        {
            sb.Append(" AND (m.relative_path LIKE $search ESCAPE '\\' OR a.tags LIKE $search ESCAPE '\\' OR d.location_name LIKE $search ESCAPE '\\' OR d.camera_model LIKE $search ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$search", "%" + EscapeLike(parsed.Text) + "%");
        }
        if (parsed.Location.Length > 0)
        {
            sb.Append(" AND d.location_name LIKE $locationFilter ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$locationFilter", "%" + EscapeLike(parsed.Location) + "%");
        }
        if (query.LocationOriginFilter is not null)
        {
            sb.Append(" AND (($locOrigin='manual' AND d.location_origin='manual') OR ($locOrigin='file' AND d.latitude IS NOT NULL AND d.longitude IS NOT NULL AND (d.location_origin IS NULL OR d.location_origin!='manual')))");
            cmd.Parameters.AddWithValue("$locOrigin", query.LocationOriginFilter);
        }
        if (parsed.Tag.Length > 0)
        {
            sb.Append(" AND a.tags LIKE $tagFilter ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$tagFilter", "%" + EscapeLike(parsed.Tag) + "%");
        }
        if (parsed.NegTag.Length > 0)
        {
            sb.Append(" AND (a.tags IS NULL OR a.tags NOT LIKE $negTagFilter ESCAPE '\\')");
            cmd.Parameters.AddWithValue("$negTagFilter", "%" + EscapeLike(parsed.NegTag) + "%");
        }
        if (parsed.Ext.Length > 0)
        {
            sb.Append(" AND lower(m.relative_path) LIKE $extFilter ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$extFilter", "%." + EscapeLike(parsed.Ext));
        }
        if (parsed.NegExt.Length > 0)
        {
            sb.Append(" AND lower(m.relative_path) NOT LIKE $negExtFilter ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$negExtFilter", "%." + EscapeLike(parsed.NegExt));
        }
        if (parsed.Year != 0)
        {
            sb.Append(" AND luma_year(coalesce(d.taken_ticks, m.modified_ticks))=$yearFilter");
            cmd.Parameters.AddWithValue("$yearFilter", parsed.Year);
        }
        if (parsed.Folder.Length > 0)
        {
            sb.Append(" AND m.relative_path LIKE $folderToken ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$folderToken", "%" + EscapeLike(parsed.Folder) + "%");
        }
        if (effectiveOrientation is not null)
        {
            sb.Append(" AND (d.width > 0 AND d.height > 0 AND (($orientation='landscape' AND d.width > d.height) OR ($orientation='portrait' AND d.height > d.width) OR ($orientation='square' AND d.width = d.height)))");
            cmd.Parameters.AddWithValue("$orientation", effectiveOrientation);
        }
        if (parsed.Is4k)
            sb.Append(" AND (COALESCE(d.width,0)>=3840 OR COALESCE(d.height,0)>=3840 OR (CAST(COALESCE(d.width,0) AS INTEGER)*CAST(COALESCE(d.height,0) AS INTEGER))>=8294400)");
        if (effectiveMinLen is not null)
        {
            sb.Append(" AND m.length >= $minLength");
            cmd.Parameters.AddWithValue("$minLength", effectiveMinLen.Value);
        }
        if (effectiveMaxLen is not null)
        {
            sb.Append(" AND m.length <= $maxLength");
            cmd.Parameters.AddWithValue("$maxLength", effectiveMaxLen.Value);
        }
        if (query.MapBounds is { } mb)
        {
            sb.Append(" AND (d.latitude IS NOT NULL AND d.longitude IS NOT NULL AND d.latitude BETWEEN $minLat AND $maxLat AND (($minLon <= $maxLon AND d.longitude BETWEEN $minLon AND $maxLon) OR ($minLon > $maxLon AND (d.longitude >= $minLon OR d.longitude <= $maxLon))))");
            cmd.Parameters.AddWithValue("$minLat", mb.MinLat);
            cmd.Parameters.AddWithValue("$maxLat", mb.MaxLat);
            cmd.Parameters.AddWithValue("$minLon", mb.MinLon);
            cmd.Parameters.AddWithValue("$maxLon", mb.MaxLon);
        }
        else if (effectiveHasGps)
        {
            sb.Append(" AND (d.latitude IS NOT NULL AND d.longitude IS NOT NULL)");
        }
        if (parsed.AfterTicks is not null)
        {
            sb.Append(" AND COALESCE(d.taken_ticks, m.modified_ticks) >= $searchAfter");
            cmd.Parameters.AddWithValue("$searchAfter", parsed.AfterTicks.Value);
        }
        if (parsed.BeforeTicks is not null)
        {
            sb.Append(" AND COALESCE(d.taken_ticks, m.modified_ticks) <= $searchBefore");
            cmd.Parameters.AddWithValue("$searchBefore", parsed.BeforeTicks.Value);
        }
        if (query.Folder.Length > 0)
        {
            var prefix = query.Folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            sb.Append(" AND m.relative_path LIKE $folder ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$folder", EscapeLike(prefix) + "%");
        }
        if (!query.IncludeDescendants)
        {
            var prefix = query.Folder.Length == 0 ? "" : query.Folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            sb.Append(" AND instr(substr(m.relative_path,length($prefix)+1),$separator)=0");
            cmd.Parameters.AddWithValue("$prefix", prefix);
            cmd.Parameters.AddWithValue("$separator", Path.DirectorySeparatorChar.ToString());
        }
        if (query.AlbumId is not null)
        {
            sb.Append(" AND EXISTS(SELECT 1 FROM album_items ai WHERE ai.media_id=i.id AND ai.album_id=$album)");
            cmd.Parameters.AddWithValue("$album", query.AlbumId);
        }
        if (query.CommonOnly)
        {
            sb.Append(" AND (lower(m.relative_path) GLOB '*.jpg' OR lower(m.relative_path) GLOB '*.jpeg' OR lower(m.relative_path) GLOB '*.png' OR lower(m.relative_path) GLOB '*.heic' OR lower(m.relative_path) GLOB '*.heif' OR lower(m.relative_path) GLOB '*.mp4' OR lower(m.relative_path) GLOB '*.mov' OR lower(m.relative_path) GLOB '*.gif' OR lower(m.relative_path) GLOB '*.webp' OR lower(m.relative_path) GLOB '*.bmp')");
        }
        if (hasExclusions)
            sb.Append(" AND ").Append(AllowedMediaSql);
        if (!string.IsNullOrEmpty(extraWhere))
            sb.Append(extraWhere);
        return sb.ToString();
    }

    internal static string BuildFilteredFrom(
        LibraryQuery query,
        ParsedSearchTokens parsed,
        SqliteCommand cmd,
        bool hasExclusions,
        bool includeAnnotations = false,
        bool includeMediaIds = false,
        bool includeMetadata = false,
        bool requireMedia = false,
        bool includeBrowseDates = false,
        bool allowBrowseDatesIndexHint = true,
        bool allowBrowseDatesAlone = true,
        string? extraWhere = null) =>
        BuildFilteredFrom(query, parsed, cmd, hasExclusions, out _, includeAnnotations, includeMediaIds, includeMetadata, requireMedia, includeBrowseDates, allowBrowseDatesIndexHint, allowBrowseDatesAlone, extraWhere);

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

    public async Task<LibraryPage> QueryAsync(LibraryQuery query, CancellationToken ct = default)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(query));
        await using var db = await OpenConfiguredConnectionAsync(ct);
        var parsed = ParseSearch(query.Search);
        RegisterQueryFunctions(db, query.Sort, query, parsed.Year);
        // A gallery read must not reserve SQLite's single writer slot. WAL lets
        // this deferred, SELECT-only snapshot coexist with a long-running scan.
        // Keep count and rows in one snapshot rather than removing the transaction.
        ct.ThrowIfCancellationRequested();
        await using var tx = db.BeginTransaction(deferred: true);
        var hasExclusions = await HasExclusionsAsync(db, tx, ct);

        long total;
        if (query.KnownTotal is long knownTotal)
        {
            total = knownTotal;
        }
        else
        {
            await using var countCmd = db.CreateCommand();
            countCmd.Transaction = tx;
            var countFrom = BuildFilteredFrom(
                query, parsed, countCmd, hasExclusions,
                includeAnnotations: false, includeMediaIds: false, includeMetadata: false,
                requireMedia: false,
                includeBrowseDates: query.Sort == MediaSort.Captured || query.BeforeTicks is not null || query.FromTicks is not null
                    ? query.Sort == MediaSort.Captured
                    : false,
                allowBrowseDatesIndexHint: false,
                allowBrowseDatesAlone: true);
            var countDate = query.Sort == MediaSort.Captured ? "b.date_ticks" : "m.modified_ticks";
            if (query.BeforeTicks is not null)
            {
                countFrom += " AND " + countDate + " <= $before";
                countCmd.Parameters.AddWithValue("$before", query.BeforeTicks.Value);
            }
            if (query.FromTicks is not null)
            {
                countFrom += " AND " + countDate + " >= $from";
                countCmd.Parameters.AddWithValue("$from", query.FromTicks.Value);
            }
            countCmd.CommandText = countFrom switch
            {
                "FROM media m WHERE 1=1" => "SELECT COUNT(*) FROM media",
                "FROM browse_dates b WHERE 1=1" => "SELECT COUNT(*) FROM browse_dates",
                _ => "SELECT COUNT(*) " + countFrom
            };
            total = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));
        }

        await using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        var value = OrderValue(query.Sort);
        var descending = query.Descending ?? query.Sort is MediaSort.Newest or MediaSort.Largest or MediaSort.Captured or MediaSort.Resolution or MediaSort.Duration;
        var from = BuildFilteredFrom(
            query, parsed, cmd, hasExclusions,
            includeAnnotations: true, includeMediaIds: true, includeMetadata: true,
            requireMedia: true,
            includeBrowseDates: query.Sort == MediaSort.Captured,
            allowBrowseDatesIndexHint: true,
            allowBrowseDatesAlone: false);
        if (query.BeforeTicks is not null)
        {
            from += " AND " + (query.Sort == MediaSort.Captured ? "b.date_ticks" : "m.modified_ticks") + " <= $before";
            cmd.Parameters.AddWithValue("$before", query.BeforeTicks.Value);
        }
        if (query.FromTicks is not null)
        {
            from += " AND " + (query.Sort == MediaSort.Captured ? "b.date_ticks" : "m.modified_ticks") + " >= $from";
            cmd.Parameters.AddWithValue("$from", query.FromTicks.Value);
        }
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
        await using var db = await OpenConfiguredConnectionAsync(ct);
        var hasExclusions = await HasExclusionsAsync(db, ct: ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText =
            "SELECT " + ItemColumnsSql + " " +
            "FROM media m " +
            "LEFT JOIN annotations a ON a.source_id=m.source_id AND a.relative_path=m.relative_path " +
            "LEFT JOIN media_ids i ON i.source_id=m.source_id AND i.relative_path=m.relative_path " +
            "LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks " +
            "WHERE m.source_id=$sid AND m.relative_path=$rpath" + (hasExclusions ? " AND " + AllowedMediaSql : "") + " LIMIT 1";
        cmd.Parameters.AddWithValue("$sid", sourceId);
        cmd.Parameters.AddWithValue("$rpath", relativePath);
        var roots = new Dictionary<string, bool>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? ReadLibraryItemRow(r, roots) : null;
    }

    private static string AppendDateBounds(string from, SqliteCommand cmd, LibraryQuery query, bool useBrowseDates)
    {
        var dateExpr = useBrowseDates ? "b.date_ticks" : "COALESCE(d.taken_ticks,m.modified_ticks)";
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
        await using var db = await OpenConfiguredConnectionAsync(ct);
        var scoped = query with { HasGpsOnly = true, MapBounds = null };
        var parsed = ParseSearch(scoped.Search);
        RegisterQueryFunctions(db, scoped.Sort, scoped, parsed.Year);
        var hasExclusions = await HasExclusionsAsync(db, ct: ct);
        bool useBrowseDates = scoped.Sort == MediaSort.Captured && (scoped.BeforeTicks is not null || scoped.FromTicks is not null);
        await using var cmd = db.CreateCommand();
        var baseFrom = BuildFilteredFrom(
            scoped, parsed, cmd, hasExclusions,
            includeAnnotations: true, includeMediaIds: false, includeMetadata: true,
            requireMedia: true,
            includeBrowseDates: useBrowseDates,
            allowBrowseDatesIndexHint: false,
            allowBrowseDatesAlone: false);
        var from = AppendDateBounds(baseFrom, cmd, scoped, useBrowseDates);
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
        await using var db = await OpenConfiguredConnectionAsync(ct);
        var scoped = query with { HasGpsOnly = false, MapBounds = null, LocationOriginFilter = null };
        var parsed = ParseSearch(scoped.Search);
        RegisterQueryFunctions(db, scoped.Sort, scoped, parsed.Year);
        var hasExclusions = await HasExclusionsAsync(db, ct: ct);
        bool useBrowseDates = scoped.Sort == MediaSort.Captured && (scoped.BeforeTicks is not null || scoped.FromTicks is not null);
        await using var cmd = db.CreateCommand();
        var baseFrom = BuildFilteredFrom(
            scoped, parsed, cmd, hasExclusions,
            includeAnnotations: true, includeMediaIds: false, includeMetadata: true,
            requireMedia: true,
            includeBrowseDates: useBrowseDates,
            allowBrowseDatesIndexHint: false,
            allowBrowseDatesAlone: false,
            extraWhere: " AND (d.latitude IS NULL OR d.longitude IS NULL) AND d.location_name IS NOT NULL AND trim(d.location_name) != ''");
        var from = AppendDateBounds(baseFrom, cmd, scoped, useBrowseDates);
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
        await using var db = await OpenConfiguredConnectionAsync(ct);
        var scoped = query with { HasGpsOnly = false, MapBounds = null, LocationOriginFilter = null };
        var parsed = ParseSearch(scoped.Search);
        RegisterQueryFunctions(db, scoped.Sort, scoped, parsed.Year);
        var hasExclusions = await HasExclusionsAsync(db, ct: ct);
        bool useBrowseDates = scoped.Sort == MediaSort.Captured && (scoped.BeforeTicks is not null || scoped.FromTicks is not null);
        await using var cmd = db.CreateCommand();
        var baseFrom = BuildFilteredFrom(
            scoped, parsed, cmd, hasExclusions,
            includeAnnotations: true, includeMediaIds: true, includeMetadata: true,
            requireMedia: true,
            includeBrowseDates: useBrowseDates,
            allowBrowseDatesIndexHint: false,
            allowBrowseDatesAlone: false,
            extraWhere: " AND (d.latitude IS NULL OR d.longitude IS NULL)");
        var from = AppendDateBounds(baseFrom, cmd, scoped, useBrowseDates);
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
        await using var db = await OpenConfiguredConnectionAsync(ct);
        var scoped = query with { HasGpsOnly = false, MapBounds = null, LocationOriginFilter = null };
        var parsed = ParseSearch(scoped.Search);
        RegisterQueryFunctions(db, scoped.Sort, scoped, parsed.Year);
        var hasExclusions = await HasExclusionsAsync(db, ct: ct);
        bool useBrowseDates = scoped.Sort == MediaSort.Captured && (scoped.BeforeTicks is not null || scoped.FromTicks is not null);
        await using var cmd = db.CreateCommand();
        var baseFrom = BuildFilteredFrom(
            scoped, parsed, cmd, hasExclusions,
            includeAnnotations: false, includeMediaIds: false, includeMetadata: true,
            requireMedia: true,
            includeBrowseDates: useBrowseDates,
            allowBrowseDatesIndexHint: false,
            allowBrowseDatesAlone: false);
        var from = AppendDateBounds(baseFrom, cmd, scoped, useBrowseDates);
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
        await using var db = await OpenConfiguredConnectionAsync(ct);
        var parsed = ParseSearch(query.Search);
        RegisterQueryFunctions(db, query.Sort, query, parsed.Year);
        await using var tx = db.BeginTransaction(deferred: true);
        var hasExclusions = await HasExclusionsAsync(db, tx, ct);
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        var from = BuildFilteredFrom(
            query, parsed, cmd, hasExclusions,
            out bool browseDatesOnly,
            includeAnnotations: false, includeMediaIds: false, includeMetadata: false,
            requireMedia: false,
            includeBrowseDates: query.Sort == MediaSort.Captured,
            allowBrowseDatesIndexHint: false,
            allowBrowseDatesAlone: true);

        if (browseDatesOnly && query.SourceId is null && query.BeforeTicks is null && query.FromTicks is null)
        {
            var fast = await TryFastMonthBucketsAsync(db, tx, ct);
            if (fast is not null) return fast;
        }

        var date = query.Sort == MediaSort.Captured ? "b.date_ticks" : "m.modified_ticks";
        cmd.CommandText = "SELECT strftime('%Y-%m',(" + date + "-621355968000000000)/10000000,'unixepoch','localtime'),COUNT(*) " + from + " GROUP BY 1 ORDER BY 1 DESC";
        var result = new List<MonthBucket>(); await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) if (!r.IsDBNull(0)) result.Add(new(r.GetString(0), r.GetInt64(1)));
        return result;
    }

    private static async Task<IReadOnlyList<MonthBucket>?> TryFastMonthBucketsAsync(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        const long UnixEpochTicks = 621355968000000000L; // 1970-01-01T00:00:00Z
        long maxSupported = DateTime.MaxValue.Ticks - TimeSpan.TicksPerDay * 2;
        var buckets = new List<MonthBucket>();

        await using var probeCmd = db.CreateCommand();
        probeCmd.Transaction = tx;
        probeCmd.CommandText = "SELECT date_ticks FROM browse_dates INDEXED BY ix_browse_dates_desc WHERE date_ticks < $upper ORDER BY date_ticks DESC LIMIT 1";
        var pProbeUpper = probeCmd.Parameters.Add("$upper", SqliteType.Integer);
        probeCmd.Prepare();

        await using var countCmd = db.CreateCommand();
        countCmd.Transaction = tx;
        countCmd.CommandText = "SELECT COUNT(*) FROM browse_dates INDEXED BY ix_browse_dates_desc WHERE date_ticks >= $start AND date_ticks < $upper";
        var pCountStart = countCmd.Parameters.Add("$start", SqliteType.Integer);
        var pCountUpper = countCmd.Parameters.Add("$upper", SqliteType.Integer);
        countCmd.Prepare();

        long upperExclusive = long.MaxValue;
        while (true)
        {
            if (buckets.Count >= 120) return null;
            pProbeUpper.Value = upperExclusive;
            var obj = await probeCmd.ExecuteScalarAsync(ct);
            if (obj is null or DBNull) return buckets;

            long t = Convert.ToInt64(obj);
            if (t < UnixEpochTicks || t >= maxSupported) return null;

            long unixSeconds = (t - UnixEpochTicks) / TimeSpan.TicksPerSecond;
            var truncUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime;
            var local = TimeZoneInfo.ConvertTimeFromUtc(truncUtc, TimeZoneInfo.Local);
            var localStart = new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            if (TimeZoneInfo.Local.IsInvalidTime(localStart) || TimeZoneInfo.Local.IsAmbiguousTime(localStart)) return null;

            long monthStartTicks = TimeZoneInfo.ConvertTimeToUtc(localStart, TimeZoneInfo.Local).Ticks;
            if (monthStartTicks > t || monthStartTicks >= upperExclusive) return null;

            pCountStart.Value = monthStartTicks;
            pCountUpper.Value = upperExclusive;
            long count = Convert.ToInt64(await countCmd.ExecuteScalarAsync(ct));
            buckets.Add(new MonthBucket($"{local.Year:D4}-{local.Month:D2}", count));
            upperExclusive = monthStartTicks;
        }
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
