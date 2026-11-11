using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace Luma.Core;

public sealed record CachedFolder(string RelativePath, string Name, long MediaCount);
public sealed record OfflineCoverage(long Cached, long Total, long CacheBytes, long CacheLimitBytes);

public sealed partial class MediaCache
{
    public async Task<Dictionary<string,long>> SourceCountsAsync(CancellationToken ct=default)
    {
        await using var db=CreateConnection(); await db.OpenAsync(ct);
        await using var cmd=db.CreateCommand(); cmd.CommandText="SELECT source_id,COUNT(*) FROM media m WHERE " + AllowedMediaSql + " GROUP BY source_id";
        var result=new Dictionary<string,long>(); await using var r=await cmd.ExecuteReaderAsync(ct);
        while(await r.ReadAsync(ct)) result[r.GetString(0)]=r.GetInt64(1);
        return result;
    }

    public async Task<HashSet<string>> PreviewKeysAsync(string? sourceId=null,CancellationToken ct=default)
    {
        var keys=new HashSet<string>(StringComparer.Ordinal);
        await foreach(var media in EnumerateCachedAsync(sourceId,ct,includeExcluded:true)) keys.Add(ThumbnailCache.Key(media));
        return keys;
    }

    // Derive folders from the catalog, including Phase 1 databases. Never enumerate the drive.
    public async Task<IReadOnlyList<CachedFolder>> FoldersAsync(string sourceId, string parent = "", CancellationToken ct = default)
    {
        var prefix = parent.Length == 0 ? "" : parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            WITH descendants AS (
                SELECT substr(relative_path,length($prefix)+1) AS tail FROM media m
                WHERE source_id=$id AND substr(relative_path,1,length($prefix))=$prefix
            )
            SELECT substr(tail,1,instr(tail,$separator)-1),COUNT(*) FROM descendants
            WHERE instr(tail,$separator)>0 GROUP BY 1 ORDER BY 1 COLLATE NOCASE
            """;
        cmd.CommandText=cmd.CommandText.Replace("WHERE source_id=$id", "WHERE " + AllowedMediaSql + " AND source_id=$id");
        cmd.Parameters.AddWithValue("$id", sourceId); cmd.Parameters.AddWithValue("$prefix", prefix);
        cmd.Parameters.AddWithValue("$separator", Path.DirectorySeparatorChar.ToString());
        var folders = new List<CachedFolder>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            folders.Add(new(prefix + reader.GetString(0), reader.GetString(0), reader.GetInt64(1)));
        return folders;
    }

    // A single SQLite reader gives a stable snapshot without loading all media into memory.
    public async IAsyncEnumerable<CachedMedia> EnumerateCachedAsync(string? sourceId = null,
        [EnumeratorCancellation] CancellationToken ct = default, bool includeExcluded=false)
    {
        await using var db = await OpenConfiguredConnectionAsync(ct);
        bool checkExclusions = !includeExcluded && await HasExclusionsAsync(db, ct: ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = sourceId is null
            ? "SELECT source_id,relative_path,length,modified_ticks,kind FROM media m" + (checkExclusions ? " WHERE " + AllowedMediaSql : "") + " ORDER BY source_id,relative_path"
            : "SELECT source_id,relative_path,length,modified_ticks,kind FROM media m WHERE source_id=$id" + (checkExclusions ? " AND " + AllowedMediaSql : "") + " ORDER BY relative_path";
        if (sourceId is not null) cmd.Parameters.AddWithValue("$id", sourceId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            yield return new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), false);
    }
}

public sealed class OfflinePreparation(MediaCache catalog, ThumbnailCache cache, ThumbnailService thumbnails)
{
    public async Task<OfflineCoverage> InspectAsync(string? sourceId = null, CancellationToken ct = default)
    {
        var snapshot = await cache.SnapshotAsync(ct);
        long total = 0, cached = 0;
        await foreach (var media in catalog.EnumerateCachedAsync(sourceId, ct))
        {
            total++;
            if (snapshot.Keys.Contains(ThumbnailCache.Key(media))) cached++;
        }
        return new(cached, total, snapshot.Bytes, snapshot.LimitBytes);
    }

    public async Task<PreviewRebuild> RebuildAsync(string? sourceId=null,IProgress<long>? progress=null,CancellationToken ct=default)
    {
        long visited=0,updated=0;
        await foreach(var media in catalog.EnumerateCachedAsync(sourceId,ct))
        {
            ct.ThrowIfCancellationRequested();
            // Generate before replacing. Offline, failed or oversized decodes retain the old entry.
            if(await thumbnails.GetAsync(media,ct:ct,allowEviction:false,forceRefresh:true) is not null) updated++;
            progress?.Report(++visited);
        }
        return new(visited,updated);
    }

    public async Task<OfflineCoverage> PrepareAsync(string? sourceId = null,
        IProgress<long>? progress = null, CancellationToken ct = default)
    {
        var existing = (await cache.SnapshotAsync(ct)).Keys;
        long visited = 0;
        await foreach (var media in catalog.EnumerateCachedAsync(sourceId, ct))
        {
            ct.ThrowIfCancellationRequested();
            // Skip reading/re-touching cached PNG bytes when this media version is already retained.
            if (!existing.Contains(ThumbnailCache.Key(media)))
                await thumbnails.GetAsync(media, ct: ct, allowEviction: false);
            progress?.Report(++visited);
        }
        // Count retained versions, not successful decodes. A browsing request may have evicted entries.
        return await InspectAsync(sourceId, ct);
    }
}

public sealed record PreviewRebuild(long Checked,long Updated);
