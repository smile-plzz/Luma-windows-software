using Luma.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Luma.Tests;

public sealed class UpgradeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Luma-upgrade-" + Guid.NewGuid());
    private string DbPath => Path.Combine(root, "catalog.db");

    public UpgradeTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task UpgradeFromV020_PreservesSourcesMediaAndAnnotations()
    {
        // 1. Construct v0.2.0 schema without later tables or browse_date_ticks
        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sources(id TEXT PRIMARY KEY, root_path TEXT NOT NULL, last_seen TEXT NOT NULL);
                CREATE TABLE media(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, kind TEXT NOT NULL,
                    PRIMARY KEY(source_id, relative_path),
                    FOREIGN KEY(source_id) REFERENCES sources(id));
                CREATE TABLE annotations(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    favorite INTEGER NOT NULL DEFAULT 0, tags TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY(source_id, relative_path));
                INSERT INTO sources VALUES('src-1', 'C:\Photos', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO media VALUES('src-1', 'pic1.jpg', 1024, 638000000000000000, 'photo');
                INSERT INTO annotations VALUES('src-1', 'pic1.jpg', 1, 'family, vacation');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // 2. Run InitializeAsync() via MediaCache
        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        // 3. Verify backup file exists
        Assert.True(File.Exists(DbPath + ".bak"), "catalog.db.bak must be created before migration");

        // 4. Verify database quick check
        var isHealthy = await catalog.QuickCheckAsync();
        Assert.True(isHealthy, "Database quick_check must pass after migration");

        // 5. Verify data integrity and upgraded schema
        var sources = await catalog.SourcesAsync();
        Assert.Single(sources);
        Assert.Equal("src-1", sources[0].Id);
        Assert.Equal(@"C:\Photos", sources[0].RootPath);

        var page = await catalog.QueryAsync(new LibraryQuery());
        Assert.Equal(1, page.Total);
        Assert.Single(page.Items);
        var item = page.Items[0];
        Assert.Equal("pic1.jpg", item.Media.RelativePath);
        Assert.True(item.Favorite);
        Assert.Equal("family, vacation", item.Tags);

        // 6. Verify browse_dates was added and backfilled with modified_ticks
        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT date_ticks FROM browse_dates WHERE relative_path = 'pic1.jpg';";
            var browseDate = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Assert.Equal(638000000000000000, browseDate);
        }
    }

    [Fact]
    public async Task UpgradeFromV040_PreservesExclusionsAndIndexState()
    {
        // Construct v0.4.0 schema with exclusions and scan tracking
        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sources(id TEXT PRIMARY KEY, root_path TEXT NOT NULL, last_seen TEXT NOT NULL);
                CREATE TABLE media(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, kind TEXT NOT NULL,
                    PRIMARY KEY(source_id, relative_path));
                CREATE TABLE annotations(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    favorite INTEGER NOT NULL DEFAULT 0, tags TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY(source_id, relative_path));
                CREATE TABLE exclusions(kind TEXT NOT NULL, source_id TEXT NOT NULL, value TEXT NOT NULL,
                    PRIMARY KEY(kind, source_id, value));
                CREATE TABLE scan_runs(run_id TEXT PRIMARY KEY, source_id TEXT NOT NULL, root_path TEXT NOT NULL,
                    started_ticks INTEGER NOT NULL, state TEXT NOT NULL);
                INSERT INTO sources VALUES('src-1', 'D:\\Media', '2026-02-01T00:00:00.0000000Z');
                INSERT INTO media VALUES('src-1', 'travel/dubai.jpg', 2048, 638100000000000000, 'photo');
                INSERT INTO exclusions VALUES('folder', 'src-1', 'raw');
                INSERT INTO exclusions VALUES('extension', '', '.tmp');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        Assert.True(File.Exists(DbPath + ".bak"));
        Assert.True(await catalog.QuickCheckAsync());

        var exclusions = await catalog.ExclusionsAsync();
        Assert.Equal(2, exclusions.Count);
        Assert.Contains(exclusions, e => e.Kind == "folder" && e.Value == "raw");
        Assert.Contains(exclusions, e => e.Kind == "extension" && e.Value == ".tmp");
    }

    [Fact]
    public async Task UpgradeFromV050_PreservesAlbumsAndMetadata()
    {
        // Construct v0.5.0 schema with albums and media_metadata
        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE sources(id TEXT PRIMARY KEY, root_path TEXT NOT NULL, last_seen TEXT NOT NULL);
                CREATE TABLE media(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, kind TEXT NOT NULL,
                    PRIMARY KEY(source_id, relative_path));
                CREATE TABLE annotations(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    favorite INTEGER NOT NULL DEFAULT 0, tags TEXT NOT NULL DEFAULT '',
                    PRIMARY KEY(source_id, relative_path));
                CREATE TABLE exclusions(kind TEXT NOT NULL, source_id TEXT NOT NULL, value TEXT NOT NULL,
                    PRIMARY KEY(kind, source_id, value));
                CREATE TABLE media_ids(id TEXT PRIMARY KEY, source_id TEXT NOT NULL, relative_path TEXT NOT NULL, UNIQUE(source_id, relative_path));
                CREATE TABLE albums(id TEXT PRIMARY KEY, name TEXT NOT NULL);
                CREATE TABLE album_items(album_id TEXT NOT NULL, media_id TEXT NOT NULL, PRIMARY KEY(album_id, media_id));
                CREATE TABLE metadata(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, taken_ticks INTEGER,
                    date_origin TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL,
                    duration REAL NOT NULL, orientation INTEGER NOT NULL,
                    PRIMARY KEY(source_id, relative_path));

                INSERT INTO sources VALUES('src-1', 'E:\Archive', '2026-03-01T00:00:00.0000000Z');
                INSERT INTO media VALUES('src-1', '2026/party.jpg', 4096, 638200000000000000, 'photo');
                INSERT INTO media_ids VALUES('mid-1', 'src-1', '2026/party.jpg');
                INSERT INTO albums VALUES('alb-1', 'Summer 2026');
                INSERT INTO album_items VALUES('alb-1', 'mid-1');
                INSERT INTO metadata VALUES('src-1', '2026/party.jpg', 4096, 638200000000000000, 638190000000000000, 'exif', 3840, 2160, 0, 1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        Assert.True(File.Exists(DbPath + ".bak"));
        Assert.True(await catalog.QuickCheckAsync());

        var albums = await catalog.AlbumsAsync();
        Assert.Single(albums);
        Assert.Equal("Summer 2026", albums[0].Name);

        // Verify that browse_dates prefers taken_ticks from metadata
        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT date_ticks FROM browse_dates WHERE relative_path = '2026/party.jpg';";
            var browseTicks = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            Assert.Equal(638190000000000000, browseTicks);
        }
    }

    [Fact]
    public async Task UpgradeFromV060_IdempotentReinitialization()
    {
        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sources VALUES('src-test', 'D:\Test', '2026-04-01T00:00:00.0000000Z');
                INSERT INTO media(source_id, relative_path, length, modified_ticks, kind)
                    VALUES('src-test', 'item1.png', 512, 638300000000000000, 'photo');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Re-run InitializeAsync 3 times
        await catalog.InitializeAsync();
        await catalog.InitializeAsync();
        await catalog.InitializeAsync();

        Assert.True(await catalog.QuickCheckAsync());
        var sources = await catalog.SourcesAsync();
        Assert.Single(sources);
        var page = await catalog.QueryAsync(new LibraryQuery());
        Assert.Equal(1, page.Total);
    }
}
