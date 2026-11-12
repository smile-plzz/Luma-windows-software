namespace Luma.Core;

public sealed partial class MediaCache
{
    // Materialize the version-matched capture/modified fallback so each timeline page
    // can seek an index instead of sorting the entire joined catalog again.
    private const string BrowseDateSchema = """
        CREATE TABLE IF NOT EXISTS luma_migrations(name TEXT PRIMARY KEY);
        CREATE TABLE IF NOT EXISTS browse_dates(source_id TEXT NOT NULL,relative_path TEXT NOT NULL,date_ticks INTEGER NOT NULL,PRIMARY KEY(source_id,relative_path));
        CREATE INDEX IF NOT EXISTS ix_browse_dates_desc ON browse_dates(date_ticks DESC,source_id,relative_path);
        CREATE INDEX IF NOT EXISTS ix_browse_dates_asc ON browse_dates(date_ticks ASC,source_id,relative_path);
        INSERT OR IGNORE INTO browse_dates
          SELECT m.source_id,m.relative_path,COALESCE(d.taken_ticks,m.modified_ticks) FROM media m
          LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks
          WHERE NOT EXISTS(SELECT 1 FROM luma_migrations WHERE name='browse_dates_v1');
        CREATE TRIGGER IF NOT EXISTS browse_media_insert AFTER INSERT ON media BEGIN
          INSERT OR REPLACE INTO browse_dates VALUES(NEW.source_id,NEW.relative_path,COALESCE((SELECT taken_ticks FROM metadata WHERE source_id=NEW.source_id AND relative_path=NEW.relative_path AND length=NEW.length AND modified_ticks=NEW.modified_ticks),NEW.modified_ticks));
        END;
        CREATE TRIGGER IF NOT EXISTS browse_media_update AFTER UPDATE ON media BEGIN
          DELETE FROM browse_dates WHERE source_id=OLD.source_id AND relative_path=OLD.relative_path;
          INSERT OR REPLACE INTO browse_dates VALUES(NEW.source_id,NEW.relative_path,COALESCE((SELECT taken_ticks FROM metadata WHERE source_id=NEW.source_id AND relative_path=NEW.relative_path AND length=NEW.length AND modified_ticks=NEW.modified_ticks),NEW.modified_ticks));
        END;
        CREATE TRIGGER IF NOT EXISTS browse_media_delete AFTER DELETE ON media BEGIN
          DELETE FROM browse_dates WHERE source_id=OLD.source_id AND relative_path=OLD.relative_path;
        END;
        CREATE TRIGGER IF NOT EXISTS browse_metadata_insert AFTER INSERT ON metadata BEGIN
          UPDATE browse_dates SET date_ticks=COALESCE(NEW.taken_ticks,(SELECT modified_ticks FROM media WHERE source_id=NEW.source_id AND relative_path=NEW.relative_path))
          WHERE source_id=NEW.source_id AND relative_path=NEW.relative_path AND EXISTS(SELECT 1 FROM media WHERE source_id=NEW.source_id AND relative_path=NEW.relative_path AND length=NEW.length AND modified_ticks=NEW.modified_ticks);
        END;
        CREATE TRIGGER IF NOT EXISTS browse_metadata_update AFTER UPDATE ON metadata BEGIN
          UPDATE browse_dates SET date_ticks=(SELECT COALESCE(d.taken_ticks,m.modified_ticks) FROM media m LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks WHERE m.source_id=browse_dates.source_id AND m.relative_path=browse_dates.relative_path)
          WHERE (source_id=OLD.source_id AND relative_path=OLD.relative_path) OR (source_id=NEW.source_id AND relative_path=NEW.relative_path);
        END;
        CREATE TRIGGER IF NOT EXISTS browse_metadata_delete AFTER DELETE ON metadata BEGIN
          UPDATE browse_dates SET date_ticks=(SELECT modified_ticks FROM media WHERE source_id=OLD.source_id AND relative_path=OLD.relative_path) WHERE source_id=OLD.source_id AND relative_path=OLD.relative_path;
        END;
        INSERT OR IGNORE INTO luma_migrations VALUES('browse_dates_v1');
        """;
}
