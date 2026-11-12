using Luma.Core;
using Microsoft.Data.Sqlite;
using System.Text;
using Xunit;

namespace Luma.Tests;

public sealed class StabilizationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Luma-stab-" + Guid.NewGuid());
    private string DbPath => Path.Combine(root, "catalog.db");
    private string ThumbDir => Path.Combine(root, "thumbnails");

    public StabilizationTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            try { Directory.Delete(root, true); } catch { }
    }

    private sealed class StubResolver(Dictionary<string, string> roots) : ISourceResolver
    {
        public string? ResolveRoot(string sourceId) => roots.GetValueOrDefault(sourceId);
    }

    private sealed class FixedGenerator : IThumbnailGenerator
    {
        public int Calls { get; private set; }
        public Task<byte[]?> GenerateAsync(string path, int pixels, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<byte[]?>([0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4]);
        }
    }

    [Fact]
    public async Task MultiWordTags_PreservedAcrossCountsRenameAndBulkTag()
    {
        var sourceDir = Path.Combine(root, "src");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, "a.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(sourceDir, "b.jpg"), [4, 5, 6]);

        var resolver = new StubResolver(new() { ["s1"] = sourceDir });
        var catalog = new MediaCache(DbPath, resolver);
        await catalog.InitializeAsync();
        await catalog.ScanAsync("s1", sourceDir);

        var items = (await catalog.QueryAsync(new LibraryQuery())).Items;
        Assert.Equal(2, items.Count);

        await catalog.AnnotateAsync(items[0].Media, true, "Family Trip, Old Dhaka");
        await catalog.AnnotateAsync(items[1].Media, false, "Family Trip");

        var counts = await catalog.GetTagsWithCountsAsync();
        Assert.Contains(counts, tc => tc.Tag == "Family Trip" && tc.Count == 2);
        Assert.Contains(counts, tc => tc.Tag == "Old Dhaka" && tc.Count == 1);
        Assert.DoesNotContain(counts, tc => tc.Tag == "Family");
        Assert.DoesNotContain(counts, tc => tc.Tag == "Trip");

        await catalog.RenameTagAsync("Family Trip", "Summer Retreat");
        counts = await catalog.GetTagsWithCountsAsync();
        Assert.Contains(counts, tc => tc.Tag == "Summer Retreat" && tc.Count == 2);
        Assert.DoesNotContain(counts, tc => tc.Tag == "Family Trip");

        await catalog.BulkTagAsync([items[1].Media], ["Weekend Away"], []);
        var refreshed = await catalog.GetItemAsync("s1", items[1].Media.RelativePath);
        Assert.NotNull(refreshed);
        Assert.Contains("Summer Retreat", refreshed!.Tags);
        Assert.Contains("Weekend Away", refreshed.Tags);
    }

    [Fact]
    public async Task SearchParser_SupportsQuotedOperatorsAndMultipleTags()
    {
        var sourceDir = Path.Combine(root, "search-src", "Summer Trips");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, "beach.jpg"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(sourceDir, "city.jpg"), [4, 5, 6]);

        var rootDir = Path.Combine(root, "search-src");
        var resolver = new StubResolver(new() { ["s1"] = rootDir });
        var catalog = new MediaCache(DbPath, resolver);
        await catalog.InitializeAsync();
        await catalog.ScanAsync("s1", rootDir);

        var items = (await catalog.QueryAsync(new LibraryQuery())).Items;
        var beach = items.Single(i => i.Media.RelativePath.EndsWith("beach.jpg"));
        var city = items.Single(i => i.Media.RelativePath.EndsWith("city.jpg"));

        await catalog.AnnotateAsync(beach.Media, false, "Family Trip, Coast Line");
        await catalog.AnnotateAsync(city.Media, false, "Family Trip, Neon Lights");
        await catalog.SaveMetadataAsync(beach.Media, new MediaMetadata(null, "Fallback", LocationName: "Cox's Bazar"));
        await catalog.SaveMetadataAsync(city.Media, new MediaMetadata(null, "Fallback", LocationName: "New York"));

        var nyResult = await catalog.QueryAsync(new LibraryQuery(Search: "location:\"New York\""));
        Assert.Single(nyResult.Items);
        Assert.EndsWith("city.jpg", nyResult.Items[0].Media.RelativePath);

        var multiTagResult = await catalog.QueryAsync(new LibraryQuery(Search: "tag:\"Family Trip\" tag:\"Coast Line\""));
        Assert.Single(multiTagResult.Items);
        Assert.EndsWith("beach.jpg", multiTagResult.Items[0].Media.RelativePath);

        var negQuoted = await catalog.QueryAsync(new LibraryQuery(Search: "folder:\"Summer Trips\" -tag:\"Coast Line\""));
        Assert.Single(negQuoted.Items);
        Assert.EndsWith("city.jpg", negQuoted.Items[0].Media.RelativePath);
    }

    [Fact]
    public async Task RepairCatalogDetailed_DistinguishesBackupFromFreshReset_AndCleansStaleWal()
    {
        var srcDir = Path.Combine(root, "originals");
        Directory.CreateDirectory(srcDir);
        File.WriteAllBytes(Path.Combine(srcDir, "keep.jpg"), [1, 2, 3]);

        var catalog = new MediaCache(DbPath, new StubResolver(new() { ["s1"] = srcDir }));
        await catalog.InitializeAsync();
        await catalog.ScanAsync("s1", srcDir);
        var item = (await catalog.QueryAsync(new LibraryQuery())).Items.Single();
        await catalog.AnnotateAsync(item.Media, true, "Precious Tag");
        await catalog.CreateBackupAsync(DbPath + ".bak");

        // Corrupt primary DB and leave stale garbage WAL/SHM beside backup
        SqliteConnection.ClearAllPools();
        await File.WriteAllBytesAsync(DbPath, [0x42, 0x41, 0x44, 0x21, 0x00, 0x00]);
        await File.WriteAllBytesAsync(DbPath + "-wal", [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02]);
        await File.WriteAllBytesAsync(DbPath + "-shm", [0xDE, 0xAD, 0xBE, 0xEF, 0x03, 0x04]);

        var restoredOutcome = await catalog.RepairCatalogDetailedAsync();
        Assert.Equal(CatalogRepairOutcome.RestoredFromBackup, restoredOutcome);
        var restoredItem = await catalog.GetItemAsync("s1", "keep.jpg");
        Assert.NotNull(restoredItem);
        Assert.True(restoredItem!.Favorite);
        Assert.Equal("Precious Tag", restoredItem.Tags);

        // Now corrupt BOTH primary DB and backup -> must report ReinitializedEmpty honestly
        SqliteConnection.ClearAllPools();
        await File.WriteAllBytesAsync(DbPath, [0x01, 0x02, 0x03, 0x04]);
        await File.WriteAllBytesAsync(DbPath + ".bak", [0x05, 0x06, 0x07, 0x08]);

        var reinitOutcome = await catalog.RepairCatalogDetailedAsync();
        Assert.Equal(CatalogRepairOutcome.ReinitializedEmpty, reinitOutcome);
        Assert.True(await catalog.QuickCheckAsync());
        Assert.Empty(await catalog.SourcesAsync());
        Assert.True(File.Exists(Path.Combine(srcDir, "keep.jpg")));
    }

    [Fact]
    public async Task ResetCatalogAsync_ClearsDatabaseWhilePreservingOriginals()
    {
        var srcDir = Path.Combine(root, "reset-orig");
        Directory.CreateDirectory(srcDir);
        var file = Path.Combine(srcDir, "stay.jpg");
        await File.WriteAllBytesAsync(file, [9, 8, 7]);

        var catalog = new MediaCache(DbPath, new StubResolver(new() { ["s1"] = srcDir }));
        await catalog.InitializeAsync();
        await catalog.ScanAsync("s1", srcDir);
        Assert.Equal(1, (await catalog.QueryAsync(new LibraryQuery())).Total);

        await catalog.ResetCatalogAsync();
        Assert.Empty(await catalog.SourcesAsync());
        Assert.Equal(0, (await catalog.QueryAsync(new LibraryQuery())).Total);
        Assert.True(File.Exists(file));
    }

    [Fact]
    public async Task ExternalReconciliation_RespectsActiveExclusions()
    {
        var srcDir = Path.Combine(root, "recon-src");
        var movedDir = Path.Combine(srcDir, "Organized");
        var excludedDir = Path.Combine(srcDir, "Private");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(movedDir);
        Directory.CreateDirectory(excludedDir);

        var orig = Path.Combine(srcDir, "trip.jpg");
        await File.WriteAllBytesAsync(orig, new byte[4096]);
        var stamp = new DateTime(2025, 8, 15, 10, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(orig, stamp);

        var catalog = new MediaCache(DbPath, new StubResolver(new() { ["s1"] = srcDir }));
        await catalog.InitializeAsync();
        await catalog.ScanAsync("s1", srcDir);
        var before = (await catalog.QueryAsync(new LibraryQuery())).Items.Single();
        await catalog.AnnotateAsync(before.Media, true, "Memory");

        await catalog.SetExclusionAsync(new LibraryExclusion("folder", "s1", "Private"), true);
        await File.WriteAllBytesAsync(Path.Combine(excludedDir, "secret.jpg"), new byte[4096]);
        File.SetLastWriteTimeUtc(Path.Combine(excludedDir, "secret.jpg"), stamp);

        // Move indexed file externally on disk into Organized
        File.Move(orig, Path.Combine(movedDir, "trip_renamed.jpg"));

        var report = await catalog.ReconcileExternalMovesAsync("s1");
        Assert.Equal(1, report.Count);
        Assert.Equal(Path.Combine("Organized", "trip_renamed.jpg"), report.ReconciledMoves[0].NewRelativePath);

        var all = await catalog.QueryAsync(new LibraryQuery());
        Assert.Equal(1, all.Total);
        Assert.True(all.Items[0].Favorite);
        Assert.Equal("Memory", all.Items[0].Tags);
    }

    [Fact]
    public async Task UnicodeBanglaRtlEmojiAndReservedNames_HandledSafely()
    {
        var srcDir = Path.Combine(root, "intl-src");
        var nested = Path.Combine(srcDir, "বাংলা", "القاهرة", "🌴Vacation");
        Directory.CreateDirectory(nested);
        var banglaFile = Path.Combine(nested, "স্মৃতি_🎉_dhaka.jpg");
        await File.WriteAllBytesAsync(banglaFile, [10, 20, 30]);

        var catalog = new MediaCache(DbPath, new StubResolver(new() { ["s1"] = srcDir }));
        await catalog.InitializeAsync();
        await catalog.ScanAsync("s1", srcDir);

        var found = await catalog.QueryAsync(new LibraryQuery(Search: "স্মৃতি"));
        Assert.Equal(1, found.Total);
        Assert.Contains("বাংলা", found.Items[0].Media.RelativePath);

        // Reserved device names validation
        Assert.False(SourcePaths.ValidateWindowsFileName("CON", out _));
        Assert.False(SourcePaths.ValidateWindowsFileName("nul.jpg", out _));
        Assert.False(SourcePaths.ValidateWindowsFileName("COM1.png", out _));
        Assert.False(SourcePaths.ValidateWindowsFileName("lpt9.mp4", out _));
        Assert.False(SourcePaths.ValidateWindowsFileName("bad.", out _));
        Assert.True(SourcePaths.ValidateWindowsFileName("concord.jpg", out _));
        Assert.True(SourcePaths.ValidateWindowsFileName("বাংলা_ছবি.jpg", out _));
    }

    [Fact]
    public async Task MidScanCancellation_SavesCheckpointAndPreservesOrganizationOnResume()
    {
        var srcDir = Path.Combine(root, "resume-src");
        Directory.CreateDirectory(srcDir);
        for (int i = 0; i < 220; i++)
        {
            var sub = Path.Combine(srcDir, $"Folder{i / 50}");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, $"img_{i:000}.jpg"), [(byte)(i % 250), 1, 2]);
        }

        var catalog = new MediaCache(DbPath, new StubResolver(new() { ["s1"] = srcDir }));
        await catalog.InitializeAsync();

        // Seed 1 committed file with favorite BEFORE interrupted scan
        await catalog.ScanAsync("s1", srcDir);
        var seed = (await catalog.QueryAsync(new LibraryQuery(Search: "img_000.jpg"))).Items.Single();
        await catalog.AnnotateAsync(seed.Media, true, "Saved Tag");

        // Add one more folder & simulate interrupted scan
        var extraSub = Path.Combine(srcDir, "FolderExtra");
        Directory.CreateDirectory(extraSub);
        for (int i = 0; i < 150; i++)
            File.WriteAllBytes(Path.Combine(extraSub, $"extra_{i:000}.jpg"), [5, 6, 7]);

        using var cts = new CancellationTokenSource();
        int batches = 0;
        var progress = new Progress<int>(_ =>
        {
            batches++;
            if (batches >= 2) cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ScanAsync("s1", srcDir, cts.Token, progress));

        // Checkpoint must exist and existing annotated item must remain readable
        var reopening = new MediaCache(DbPath, new StubResolver(new() { ["s1"] = srcDir }));
        var kept = await reopening.GetItemAsync("s1", seed.Media.RelativePath);
        Assert.NotNull(kept);
        Assert.True(kept!.Favorite);
        Assert.Equal("Saved Tag", kept.Tags);

        var totalAfterResume = await reopening.ScanAsync("s1", srcDir);
        Assert.Equal(370, totalAfterResume);
    }

    [Fact]
    public async Task ThumbnailService_TryReadCachedAsync_WorksOfflineWithoutGenerating()
    {
        var srcDir = Path.Combine(root, "thumb-src");
        Directory.CreateDirectory(srcDir);
        var path = Path.Combine(srcDir, "frame.jpg");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        var fi = new FileInfo(path);

        using var thumbCache = new ThumbnailCache(ThumbDir);
        var gen = new FixedGenerator();
        var resolver = new StubResolver(new() { ["s1"] = srcDir });
        var service = new ThumbnailService(thumbCache, resolver, gen);

        var media = new CachedMedia("s1", "frame.jpg", fi.Length, fi.LastWriteTimeUtc.Ticks, "photo", true);
        var warm = await service.GetAsync(media, 256);
        Assert.NotNull(warm);
        Assert.Equal(1, gen.Calls);

        // Delete original / disconnect source and verify cache-only read works with zero generation calls
        File.Delete(path);
        var cachedOnly = await service.TryReadCachedAsync(media, 256);
        Assert.NotNull(cachedOnly);
        Assert.Equal(warm, cachedOnly);
        Assert.Equal(1, gen.Calls);
    }

    [Fact]
    public async Task RemoveMediaItemsAsync_DeletesTargetRowsWithoutAffectingSiblings()
    {
        var srcDir = Path.Combine(root, "del-src");
        Directory.CreateDirectory(srcDir);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "one.jpg"), [1, 2]);
        await File.WriteAllBytesAsync(Path.Combine(srcDir, "two.jpg"), [3, 4]);

        var catalog = new MediaCache(DbPath, new StubResolver(new() { ["s1"] = srcDir }));
        await catalog.InitializeAsync();
        await catalog.ScanAsync("s1", srcDir);

        var one = (await catalog.QueryAsync(new LibraryQuery(Search: "one.jpg"))).Items.Single();
        await catalog.RemoveMediaItemsAsync([one.Media]);

        var remaining = await catalog.QueryAsync(new LibraryQuery());
        Assert.Equal(1, remaining.Total);
        Assert.Equal("two.jpg", remaining.Items[0].Media.RelativePath);
    }

    [Fact]
    public void MinimalGpsExtractor_RejectsXmpWithDtdSafely()
    {
        const string maliciousXmp = """
            <?xml version="1.0"?>
            <!DOCTYPE foo [
              <!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">
            ]>
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:exif="http://ns.adobe.com/exif/1.0/" exif:GPSLatitude="48.85" exif:GPSLongitude="2.35" />
              </rdf:RDF>
            </x:xmpmeta>
            """;

        var result = MinimalGpsExtractor.ParseXmpBytes(Encoding.UTF8.GetBytes(maliciousXmp), "test");
        Assert.False(result.HasAnyLocation);
    }

    [Fact]
    public async Task UpgradeFromV060_PreservesFullOrganizationExclusionsAndMultiWordTags()
    {
        // Construct realistic v0.6.0 catalog including multi-word tags, favorites, albums, exclusions, and metadata
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
                CREATE TABLE media_ids(id TEXT PRIMARY KEY, source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    UNIQUE(source_id, relative_path));
                CREATE TABLE albums(id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE);
                CREATE TABLE album_items(album_id TEXT NOT NULL, media_id TEXT NOT NULL,
                    PRIMARY KEY(album_id, media_id));
                CREATE TABLE exclusions(kind TEXT NOT NULL, source_id TEXT NOT NULL, value TEXT NOT NULL,
                    PRIMARY KEY(kind, source_id, value));
                CREATE TABLE metadata(source_id TEXT NOT NULL, relative_path TEXT NOT NULL,
                    length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL,
                    taken_ticks INTEGER NULL, date_origin TEXT NOT NULL,
                    width INTEGER NOT NULL DEFAULT 0, height INTEGER NOT NULL DEFAULT 0,
                    duration REAL NOT NULL DEFAULT 0, orientation INTEGER NOT NULL DEFAULT 1,
                    PRIMARY KEY(source_id, relative_path, length, modified_ticks));

                INSERT INTO sources VALUES('vol-old', 'D:\Photos', '2026-09-01T00:00:00.0000000Z');
                INSERT INTO media VALUES('vol-old', 'Trips\keep.jpg', 2048, 638500000000000000, 'photo');
                INSERT INTO media VALUES('vol-old', 'Private\skip.jpg', 1024, 638500000000000000, 'photo');
                INSERT INTO annotations VALUES('vol-old', 'Trips\keep.jpg', 1, 'Family Trip, Coxs Bazar');
                INSERT INTO media_ids VALUES('mid-keep', 'vol-old', 'Trips\keep.jpg');
                INSERT INTO albums VALUES('alb-keep', 'Best Memories');
                INSERT INTO album_items VALUES('alb-keep', 'mid-keep');
                INSERT INTO exclusions VALUES('folder', 'vol-old', 'private');
                INSERT INTO metadata VALUES('vol-old', 'Trips\keep.jpg', 2048, 638500000000000000, 638490000000000000, 'exif', 4000, 3000, 0, 1);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        var albums = await catalog.AlbumsAsync();
        Assert.Single(albums);
        Assert.Equal("Best Memories", albums[0].Name);

        var exclusions = await catalog.ExclusionsAsync();
        Assert.Single(exclusions);

        var visible = await catalog.QueryAsync(new LibraryQuery());
        Assert.Equal(1, visible.Total);
        Assert.Equal(@"Trips\keep.jpg", visible.Items[0].Media.RelativePath);
        Assert.True(visible.Items[0].Favorite);
        Assert.Equal("Family Trip, Coxs Bazar", visible.Items[0].Tags);

        var tags = await catalog.GetTagsWithCountsAsync();
        Assert.Contains(tags, t => t.Tag == "Family Trip" && t.Count == 1);
    }
}
