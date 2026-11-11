using Microsoft.Data.Sqlite;

namespace Luma.Core;

public sealed record Album(string Id, string Name, long Count)
{
    public override string ToString() => $"{Name} ({Count:N0})";
}
public sealed record MediaMetadata(
    long? TakenTicks = null,
    string DateOrigin = "Modification date fallback",
    int Width = 0,
    int Height = 0,
    double DurationSeconds = 0,
    int Orientation = 0,
    double? Latitude = null,
    double? Longitude = null,
    string? CameraModel = null,
    int? Iso = null,
    double? FNumber = null,
    double? ExposureSeconds = null,
    double? FocalLength = null,
    string? LocationName = null,
    string? LocationOrigin = null,
    int MetaVersion = MediaCache.CurrentMetadataVersion);
public interface IMetadataReader
{
    Task<MediaMetadata> ReadAsync(string path, string kind, CancellationToken ct);
}

public sealed partial class MediaCache
{
    public const int CurrentMetadataVersion = 2;

    private const string OrganizationSchema = """
        CREATE TABLE IF NOT EXISTS media_ids(id TEXT PRIMARY KEY, source_id TEXT NOT NULL, relative_path TEXT NOT NULL, UNIQUE(source_id,relative_path));
        INSERT OR IGNORE INTO media_ids SELECT lower(hex(randomblob(16))),source_id,relative_path FROM media;
        CREATE TABLE IF NOT EXISTS albums(id TEXT PRIMARY KEY,name TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS album_items(album_id TEXT NOT NULL,media_id TEXT NOT NULL,PRIMARY KEY(album_id,media_id));
        CREATE TABLE IF NOT EXISTS metadata(source_id TEXT NOT NULL,relative_path TEXT NOT NULL,length INTEGER NOT NULL,modified_ticks INTEGER NOT NULL,
            taken_ticks INTEGER,date_origin TEXT NOT NULL,width INTEGER NOT NULL,height INTEGER NOT NULL,duration REAL NOT NULL,orientation INTEGER NOT NULL,
            latitude REAL,longitude REAL,camera_model TEXT,iso INTEGER,f_number REAL,exposure_seconds REAL,focal_length REAL,
            location_name TEXT,location_origin TEXT,meta_version INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(source_id,relative_path));
        CREATE TABLE IF NOT EXISTS source_labels(source_id TEXT PRIMARY KEY,label TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_metadata_taken ON metadata(taken_ticks,source_id,relative_path);
        """;

    private static async Task EnsureExtendedSchemaAsync(SqliteConnection db, SqliteTransaction tx, CancellationToken ct)
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var infoCmd = db.CreateCommand())
        {
            infoCmd.Transaction = tx;
            infoCmd.CommandText = "PRAGMA table_info(metadata);";
            await using var reader = await infoCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) existingCols.Add(reader.GetString(1));
        }

        (string Name, string Type)[] required =
        [
            ("latitude", "REAL"),
            ("longitude", "REAL"),
            ("camera_model", "TEXT"),
            ("iso", "INTEGER"),
            ("f_number", "REAL"),
            ("exposure_seconds", "REAL"),
            ("focal_length", "REAL"),
            ("location_name", "TEXT"),
            ("location_origin", "TEXT"),
            ("meta_version", "INTEGER NOT NULL DEFAULT 0")
        ];

        foreach (var (col, type) in required)
        {
            if (!existingCols.Contains(col))
            {
                await using var alterCmd = db.CreateCommand();
                alterCmd.Transaction = tx;
                alterCmd.CommandText = $"ALTER TABLE metadata ADD COLUMN {col} {type};";
                await alterCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await using var idxCmd = db.CreateCommand();
        idxCmd.Transaction = tx;
        idxCmd.CommandText = """
            CREATE INDEX IF NOT EXISTS ix_metadata_coords ON metadata(latitude, longitude) WHERE latitude IS NOT NULL AND longitude IS NOT NULL;
            CREATE INDEX IF NOT EXISTS ix_metadata_dims ON metadata(width, height, duration);
            CREATE INDEX IF NOT EXISTS ix_metadata_locname ON metadata(location_name) WHERE location_name IS NOT NULL;
            """;
        await idxCmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<Album>> AlbumsAsync(CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT a.id,a.name,COUNT(m.source_id) FROM albums a LEFT JOIN album_items ai ON ai.album_id=a.id LEFT JOIN media_ids i ON i.id=ai.media_id LEFT JOIN media m ON m.source_id=i.source_id AND m.relative_path=i.relative_path AND " + AllowedMediaSql + " GROUP BY a.id ORDER BY a.name COLLATE NOCASE";
        var result = new List<Album>(); await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2)));
        return result;
    }

    public async Task<string> SaveAlbumAsync(string name, string? id = null, CancellationToken ct = default)
    {
        name = name.Trim(); if (name.Length is < 1 or > 100) throw new ArgumentException("Use an album name of 1–100 characters.");
        id ??= Guid.NewGuid().ToString("N");
        await ExecuteAsync("INSERT INTO albums VALUES($id,$name) ON CONFLICT(id) DO UPDATE SET name=$name", ct, ("$id", id), ("$name", name));
        return id;
    }

    public Task DeleteAlbumAsync(string id, CancellationToken ct = default) => ExecuteAsync(
        "DELETE FROM album_items WHERE album_id=$id; DELETE FROM albums WHERE id=$id", ct, ("$id", id));

    public async Task SetAlbumItemsAsync(string albumId, IEnumerable<CachedMedia> media, bool add, CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        foreach (var item in media)
        {
            await using var cmd = db.CreateCommand(); cmd.Transaction = tx;
            cmd.CommandText = add ? "INSERT OR IGNORE INTO album_items SELECT $album,id FROM media_ids WHERE source_id=$source AND relative_path=$path AND EXISTS(SELECT 1 FROM albums WHERE id=$album)" :
                "DELETE FROM album_items WHERE album_id=$album AND media_id IN(SELECT id FROM media_ids WHERE source_id=$source AND relative_path=$path)";
            cmd.Parameters.AddWithValue("$album", albumId); cmd.Parameters.AddWithValue("$source", item.SourceId); cmd.Parameters.AddWithValue("$path", item.RelativePath);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public Task LabelSourceAsync(string source, string label, CancellationToken ct = default) => ExecuteAsync(
        "INSERT INTO source_labels VALUES($source,$label) ON CONFLICT(source_id) DO UPDATE SET label=$label", ct,
        ("$source", source), ("$label", label.Trim().Length > 100 ? throw new ArgumentException("Keep the name under 100 characters.") : label.Trim()));

    public async Task<Dictionary<string,string>> SourceLabelsAsync(CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand(); cmd.CommandText = "SELECT source_id,label FROM source_labels";
        var labels = new Dictionary<string,string>(); await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) labels[r.GetString(0)] = r.GetString(1);
        return labels;
    }

    public async Task<long> CountPendingMetadataBackfillAsync(string? sourceId, IEnumerable<string>? onlineSourceIds = null, CancellationToken ct = default)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        var onlineList = onlineSourceIds?.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToArray();
        if (onlineList is { Length: 0 }) return 0;

        await using var cmd = db.CreateCommand();
        var onlineClause = "";
        if (onlineList is { Length: > 0 })
        {
            var names = new List<string>(onlineList.Length);
            for (int i = 0; i < onlineList.Length; i++)
            {
                var pName = $"$on{i}";
                names.Add(pName);
                cmd.Parameters.AddWithValue(pName, onlineList[i]);
            }
            onlineClause = $" AND m.source_id IN ({string.Join(",", names)})";
        }
        cmd.CommandText =
            "SELECT COUNT(*) FROM media m LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks " +
            "WHERE ($source IS NULL OR m.source_id=$source)" + onlineClause +
            " AND (d.source_id IS NULL OR COALESCE(d.meta_version,0) < $targetVer) AND " + AllowedMediaSql;
        cmd.Parameters.AddWithValue("$source", (object?)sourceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$targetVer", CurrentMetadataVersion);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
    }

    public async Task AssignLocationAsync(IEnumerable<CachedMedia> media, double? latitude, double? longitude, string? locationName, CancellationToken ct = default)
    {
        var items = media.ToArray();
        if (items.Length == 0) return;
        var (nLat, nLon) = MinimalGpsExtractor.NormalizeCoords(latitude, longitude);
        var cleanName = string.IsNullOrWhiteSpace(locationName) ? null : locationName.Trim();
        var origin = nLat.HasValue || cleanName is not null ? "manual" : null;

        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO metadata(source_id,relative_path,length,modified_ticks,taken_ticks,date_origin,width,height,duration,orientation,latitude,longitude,location_name,location_origin,meta_version)
            SELECT source_id,relative_path,length,modified_ticks,NULL,'Modification date fallback',0,0,0.0,0,$lat,$lon,$locName,$locOrigin,0
            FROM media WHERE source_id=$id AND relative_path=$path
            ON CONFLICT(source_id,relative_path) DO UPDATE SET
                latitude=$lat,
                longitude=$lon,
                location_name=$locName,
                location_origin=$locOrigin
            """;
        cmd.Parameters.Add("$id", SqliteType.Text);
        cmd.Parameters.Add("$path", SqliteType.Text);
        cmd.Parameters.Add("$lat", SqliteType.Real).Value = (object?)nLat ?? DBNull.Value;
        cmd.Parameters.Add("$lon", SqliteType.Real).Value = (object?)nLon ?? DBNull.Value;
        cmd.Parameters.Add("$locName", SqliteType.Text).Value = (object?)cleanName ?? DBNull.Value;
        cmd.Parameters.Add("$locOrigin", SqliteType.Text).Value = (object?)origin ?? DBNull.Value;
        cmd.Prepare();

        foreach (var item in items)
        {
            cmd.Parameters["$id"].Value = item.SourceId;
            cmd.Parameters["$path"].Value = item.RelativePath;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<int> EnrichAsync(IMetadataReader reader, string? sourceId = null, CancellationToken ct = default, IProgress<int>? progress = null, IProgress<MetadataProgress>? details = null)
    {
        int count = 0; string lastSource="",lastPath="";
        await using var db=CreateConnection(); await db.OpenAsync(ct);
        long checkedCount=0,totalPending=0,skippedCount=0;
        if(details is not null)
        {
            await using var pending=db.CreateCommand();
            pending.CommandText="SELECT COUNT(*) FROM media m LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks WHERE ($source IS NULL OR m.source_id=$source) AND (d.source_id IS NULL OR COALESCE(d.meta_version,0) < $targetVer) AND "+AllowedMediaSql;
            pending.Parameters.AddWithValue("$source",(object?)sourceId ?? DBNull.Value);
            pending.Parameters.AddWithValue("$targetVer",CurrentMetadataVersion);
            totalPending=Convert.ToInt64(await pending.ExecuteScalarAsync(ct));
            details.Report(new("",0,totalPending,0,0));
        }
        var heartbeat=System.Diagnostics.Stopwatch.StartNew();
        void Report(string path,bool force=false)
        {
            if(details is null || (!force && heartbeat.ElapsedMilliseconds<500))return;
            details.Report(new(path,checkedCount,totalPending,count,skippedCount)); heartbeat.Restart();
        }
        while(true)
        {
            // Read bounded batches of missing/stale/legacy-version metadata; release the reader before
            // native decoding or writes. An unchanged current-version library never opens its originals.
            var batch=new List<CachedMedia>();
            await using(var cmd=db.CreateCommand())
            {
                cmd.CommandText="""
                    SELECT m.source_id,m.relative_path,m.length,m.modified_ticks,m.kind FROM media m
                    LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks
                    WHERE ($source IS NULL OR m.source_id=$source) AND (d.source_id IS NULL OR COALESCE(d.meta_version,0) < $targetVer)
                    AND (m.source_id,m.relative_path)>($lastSource,$lastPath)
                    ORDER BY m.source_id,m.relative_path LIMIT 100
                    """;
                cmd.CommandText=cmd.CommandText.Replace("AND (d.source_id IS NULL OR COALESCE(d.meta_version,0) < $targetVer)","AND (d.source_id IS NULL OR COALESCE(d.meta_version,0) < $targetVer) AND " + AllowedMediaSql);
                cmd.Parameters.AddWithValue("$source",(object?)sourceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$targetVer",CurrentMetadataVersion);
                cmd.Parameters.AddWithValue("$lastSource",lastSource); cmd.Parameters.AddWithValue("$lastPath",lastPath);
                await using var r=await cmd.ExecuteReaderAsync(ct);
                while(await r.ReadAsync(ct))batch.Add(new(r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetInt64(3),r.GetString(4),false));
            }
            if(batch.Count==0)break;
            var updates=new List<(CachedMedia Media,MediaMetadata Data)>();
            foreach(var media in batch)
            {
                ct.ThrowIfCancellationRequested(); lastSource=media.SourceId; lastPath=media.RelativePath;
                Report(media.RelativePath);
                bool saved=false;
                try
                {
                    var root=sourceResolver?.ResolveRoot(media.SourceId); if(root is null)continue;
                    var path=SourcePaths.Combine(root,media.RelativePath);
                    if(!VersionMatches(path,media))continue;
                    var data=await reader.ReadAsync(path,media.Kind,ct);
                    if(sourceResolver?.ResolveRoot(media.SourceId)!=root || !VersionMatches(path,media))continue;
                    updates.Add((media,data)); saved=true;
                }
                catch(IOException) { } catch(UnauthorizedAccessException) { }
                finally { checkedCount++; if(!saved)skippedCount++; }
            }
            // No filesystem/codec work while holding SQLite's write transaction.
            await using(var tx=db.BeginTransaction())
            {
                await using var save=db.CreateCommand(); save.Transaction=tx;
                save.CommandText="""
                    INSERT INTO metadata(source_id,relative_path,length,modified_ticks,taken_ticks,date_origin,width,height,duration,orientation,latitude,longitude,camera_model,iso,f_number,exposure_seconds,focal_length,location_name,location_origin,meta_version)
                    SELECT source_id,relative_path,length,modified_ticks,$taken,$origin,$width,$height,$duration,$orientation,$lat,$lon,$cam,$iso,$fnum,$exp,$focal,$locName,$locOrigin,$metaVer
                    FROM media WHERE source_id=$id AND relative_path=$path AND length=$len AND modified_ticks=$ticks
                    ON CONFLICT(source_id,relative_path) DO UPDATE SET
                        length=excluded.length,
                        modified_ticks=excluded.modified_ticks,
                        taken_ticks=CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN COALESCE(excluded.taken_ticks,metadata.taken_ticks) ELSE excluded.taken_ticks END,
                        date_origin=CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks AND excluded.taken_ticks IS NULL AND metadata.taken_ticks IS NOT NULL THEN metadata.date_origin ELSE excluded.date_origin END,
                        width=CASE WHEN excluded.width>0 THEN excluded.width ELSE metadata.width END,
                        height=CASE WHEN excluded.height>0 THEN excluded.height ELSE metadata.height END,
                        duration=CASE WHEN excluded.duration>0 THEN excluded.duration ELSE metadata.duration END,
                        orientation=CASE WHEN excluded.orientation>0 THEN excluded.orientation ELSE metadata.orientation END,
                        latitude=CASE WHEN metadata.location_origin='manual' THEN metadata.latitude ELSE COALESCE(excluded.latitude, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.latitude ELSE NULL END) END,
                        longitude=CASE WHEN metadata.location_origin='manual' THEN metadata.longitude ELSE COALESCE(excluded.longitude, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.longitude ELSE NULL END) END,
                        camera_model=COALESCE(excluded.camera_model, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.camera_model ELSE NULL END),
                        iso=COALESCE(excluded.iso, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.iso ELSE NULL END),
                        f_number=COALESCE(excluded.f_number, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.f_number ELSE NULL END),
                        exposure_seconds=COALESCE(excluded.exposure_seconds, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.exposure_seconds ELSE NULL END),
                        focal_length=COALESCE(excluded.focal_length, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.focal_length ELSE NULL END),
                        location_name=CASE WHEN metadata.location_origin='manual' THEN metadata.location_name ELSE COALESCE(excluded.location_name, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.location_name ELSE NULL END) END,
                        location_origin=CASE WHEN metadata.location_origin='manual' THEN metadata.location_origin ELSE COALESCE(excluded.location_origin, CASE WHEN metadata.length=excluded.length AND metadata.modified_ticks=excluded.modified_ticks THEN metadata.location_origin ELSE NULL END) END,
                        meta_version=excluded.meta_version
                    """;
                foreach(var name in new[]{"$id","$path","$origin","$cam","$locName","$locOrigin"})save.Parameters.Add(name,SqliteType.Text);
                foreach(var name in new[]{"$len","$ticks","$taken","$width","$height","$orientation","$iso","$metaVer"})save.Parameters.Add(name,SqliteType.Integer);
                foreach(var name in new[]{"$duration","$lat","$lon","$fnum","$exp","$focal"})save.Parameters.Add(name,SqliteType.Real);
                save.Prepare();
                foreach(var (media,data) in updates)
                {
                    save.Parameters["$id"].Value=media.SourceId; save.Parameters["$path"].Value=media.RelativePath;
                    save.Parameters["$len"].Value=media.Length; save.Parameters["$ticks"].Value=media.ModifiedTicks;
                    save.Parameters["$taken"].Value=(object?)data.TakenTicks ?? DBNull.Value; save.Parameters["$origin"].Value=data.DateOrigin;
                    save.Parameters["$width"].Value=data.Width; save.Parameters["$height"].Value=data.Height;
                    save.Parameters["$duration"].Value=data.DurationSeconds; save.Parameters["$orientation"].Value=data.Orientation;
                    save.Parameters["$lat"].Value=(object?)data.Latitude ?? DBNull.Value; save.Parameters["$lon"].Value=(object?)data.Longitude ?? DBNull.Value;
                    save.Parameters["$cam"].Value=(object?)data.CameraModel ?? DBNull.Value; save.Parameters["$iso"].Value=(object?)data.Iso ?? DBNull.Value;
                    save.Parameters["$fnum"].Value=(object?)data.FNumber ?? DBNull.Value; save.Parameters["$exp"].Value=(object?)data.ExposureSeconds ?? DBNull.Value;
                    save.Parameters["$focal"].Value=(object?)data.FocalLength ?? DBNull.Value;
                    save.Parameters["$locName"].Value=(object?)data.LocationName ?? DBNull.Value;
                    save.Parameters["$locOrigin"].Value=(object?)data.LocationOrigin ?? DBNull.Value;
                    save.Parameters["$metaVer"].Value=data.MetaVersion > 0 ? data.MetaVersion : CurrentMetadataVersion;
                    count+=await save.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
            }
            progress?.Report(count); Report(lastPath,true);
        }
        Report("",true); return count;
    }

    public Task SaveMetadataAsync(CachedMedia media, MediaMetadata data, CancellationToken ct = default) => ExecuteAsync("""
        INSERT INTO metadata(source_id,relative_path,length,modified_ticks,taken_ticks,date_origin,width,height,duration,orientation,latitude,longitude,camera_model,iso,f_number,exposure_seconds,focal_length,location_name,location_origin,meta_version)
        VALUES($id,$path,$len,$ticks,$taken,$origin,$width,$height,$duration,$orientation,$lat,$lon,$cam,$iso,$fnum,$exp,$focal,$locName,$locOrigin,$metaVer)
        ON CONFLICT(source_id,relative_path) DO UPDATE SET length=$len,modified_ticks=$ticks,taken_ticks=$taken,date_origin=$origin,width=$width,height=$height,duration=$duration,orientation=$orientation,latitude=$lat,longitude=$lon,camera_model=$cam,iso=$iso,f_number=$fnum,exposure_seconds=$exp,focal_length=$focal,location_name=$locName,location_origin=$locOrigin,meta_version=$metaVer
        """, ct, ("$id",media.SourceId),("$path",media.RelativePath),("$len",media.Length),("$ticks",media.ModifiedTicks),
        ("$taken",(object?)data.TakenTicks ?? DBNull.Value),("$origin",data.DateOrigin),("$width",data.Width),("$height",data.Height),
        ("$duration",data.DurationSeconds),("$orientation",data.Orientation),
        ("$lat",(object?)data.Latitude ?? DBNull.Value),("$lon",(object?)data.Longitude ?? DBNull.Value),
        ("$cam",(object?)data.CameraModel ?? DBNull.Value),("$iso",(object?)data.Iso ?? DBNull.Value),
        ("$fnum",(object?)data.FNumber ?? DBNull.Value),("$exp",(object?)data.ExposureSeconds ?? DBNull.Value),
        ("$focal",(object?)data.FocalLength ?? DBNull.Value),
        ("$locName",(object?)data.LocationName ?? DBNull.Value),("$locOrigin",(object?)data.LocationOrigin ?? DBNull.Value),
        ("$metaVer",Math.Max(0, data.MetaVersion)));

    // Call only after a confirmed individual filesystem move. Organization follows the stable ID.
    public async Task RelocateAsync(CachedMedia old, CachedMedia target, CancellationToken ct = default)
    {
        using var lease = IndexingLease();
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using var cmd = db.CreateCommand(); cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO media VALUES($newid,$newpath,$len,$ticks,$kind);
            UPDATE media_ids SET source_id=$newid,relative_path=$newpath WHERE source_id=$id AND relative_path=$path;
            UPDATE annotations SET source_id=$newid,relative_path=$newpath WHERE source_id=$id AND relative_path=$path;
            UPDATE metadata SET source_id=$newid,relative_path=$newpath WHERE source_id=$id AND relative_path=$path;
            DELETE FROM media WHERE source_id=$id AND relative_path=$path;
            DELETE FROM scan_media WHERE source_id IN($id,$newid);
            DELETE FROM scan_folders WHERE source_id IN($id,$newid);
            DELETE FROM scan_jobs WHERE source_id IN($id,$newid);
            """;
        cmd.Parameters.AddWithValue("$id",old.SourceId); cmd.Parameters.AddWithValue("$path",old.RelativePath);
        cmd.Parameters.AddWithValue("$newid",target.SourceId); cmd.Parameters.AddWithValue("$newpath",target.RelativePath);
        cmd.Parameters.AddWithValue("$len",target.Length); cmd.Parameters.AddWithValue("$ticks",target.ModifiedTicks); cmd.Parameters.AddWithValue("$kind",target.Kind);
        await cmd.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
    }

    public static bool VersionMatches(string path, CachedMedia media)
    {
        var info = new FileInfo(path); return info.Exists && info.Length == media.Length && info.LastWriteTimeUtc.Ticks == media.ModifiedTicks;
    }

    private async Task ExecuteAsync(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var db = CreateConnection(); await db.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        await using var cmd = db.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var p in parameters) cmd.Parameters.AddWithValue(p.Name,p.Value);
        await cmd.ExecuteNonQueryAsync(ct); await tx.CommitAsync(ct);
    }
}
