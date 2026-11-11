using Luma.Core;
using Xunit;

namespace Luma.Tests;

public class NewFeaturesTests : IDisposable
{
    private readonly string tempDir;
    private readonly string catalogPath;
    private readonly MediaCache catalog;

    public NewFeaturesTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "luma-feat-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        catalogPath = Path.Combine(tempDir, "catalog.db");
        catalog = new MediaCache(catalogPath);
        catalog.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(tempDir, true); } catch { }
    }

    [Fact]
    public async Task BackupAndRestore_PreservesCatalogAndRecoversData()
    {
        // 1. Insert media and annotations
        var sourceFolder = Path.Combine(tempDir, "Source");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "photo1.jpg"), "dummy");
        await catalog.ScanAsync("src-1", sourceFolder);
        await catalog.AnnotateAsync(new CachedMedia("src-1", "photo1.jpg", 5, DateTime.UtcNow.Ticks, "photo", true), true, "vacation, nature");

        // 2. Create backup
        var backupFile = Path.Combine(tempDir, "test-backup.db");
        await catalog.CreateBackupAsync(backupFile);
        Assert.True(File.Exists(backupFile));

        // 3. Mutate catalog
        await catalog.AnnotateAsync(new CachedMedia("src-1", "photo1.jpg", 5, DateTime.UtcNow.Ticks, "photo", true), false, "changed");
        var modifiedTags = (await catalog.GetTagsWithCountsAsync()).Select(t => t.Tag).ToArray();
        Assert.Contains("changed", modifiedTags);

        // 4. Restore from backup
        var restored = await catalog.RestoreBackupAsync(backupFile);
        Assert.True(restored);

        var restoredTags = (await catalog.GetTagsWithCountsAsync()).Select(t => t.Tag).ToArray();
        Assert.Contains("vacation", restoredTags);
        Assert.Contains("nature", restoredTags);
        Assert.DoesNotContain("changed", restoredTags);
    }

    [Fact]
    public async Task TagManager_RenameMergeAndDelete_WorkCorrectly()
    {
        var media1 = new CachedMedia("s1", "a.jpg", 100, 1000, "photo", true);
        var media2 = new CachedMedia("s1", "b.jpg", 100, 1000, "photo", true);
        await catalog.AnnotateAsync(media1, false, "travel, summer");
        await catalog.AnnotateAsync(media2, false, "summer, beach");

        var tags = await catalog.GetTagsWithCountsAsync();
        Assert.Equal(3, tags.Count);
        var summerTag = tags.Single(t => t.Tag == "summer");
        Assert.Equal(2, summerTag.Count);

        // Rename summer -> holiday
        await catalog.RenameTagAsync("summer", "holiday");
        var afterRename = await catalog.GetTagsWithCountsAsync();
        Assert.DoesNotContain(afterRename, t => t.Tag == "summer");
        Assert.Contains(afterRename, t => t.Tag == "holiday" && t.Count == 2);

        // Delete beach
        await catalog.DeleteTagAsync("beach");
        var afterDelete = await catalog.GetTagsWithCountsAsync();
        Assert.DoesNotContain(afterDelete, t => t.Tag == "beach");

        // Merge holiday into travel
        await catalog.RenameTagAsync("holiday", "travel");
        var afterMerge = await catalog.GetTagsWithCountsAsync();
        Assert.Single(afterMerge);
        Assert.Equal("travel", afterMerge[0].Tag);
        Assert.Equal(2, afterMerge[0].Count);
    }

    [Fact]
    public async Task SearchOperators_ParseAndFilterByTagExtAndYear()
    {
        var sourceDir = Path.Combine(tempDir, "Media");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "mountain.jpg"), "12345");
        File.WriteAllText(Path.Combine(sourceDir, "ocean.png"), "12345");
        File.WriteAllText(Path.Combine(sourceDir, "clip.mp4"), "12345");

        await catalog.ScanAsync("src-s", sourceDir);

        var mediaJpg = new CachedMedia("src-s", "mountain.jpg", 5, new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, "photo", true);
        var mediaPng = new CachedMedia("src-s", "ocean.png", 5, new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, "photo", true);
        var mediaMp4 = new CachedMedia("src-s", "clip.mp4", 5, new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, "video", true);

        await catalog.AnnotateAsync(mediaJpg, false, "nature, scenic");
        await catalog.AnnotateAsync(mediaPng, false, "water, scenic");
        await catalog.AnnotateAsync(mediaMp4, false, "nature, video");

        // Search ext:png
        var extResult = await catalog.QueryAsync(new LibraryQuery(Search: "ext:png"));
        Assert.Single(extResult.Items);
        Assert.Equal("ocean.png", extResult.Items[0].Media.RelativePath);

        // Search tag:water
        var tagResult = await catalog.QueryAsync(new LibraryQuery(Search: "tag:water"));
        Assert.Single(tagResult.Items);
        Assert.Equal("ocean.png", tagResult.Items[0].Media.RelativePath);

        // Search tag:scenic
        var scenicResult = await catalog.QueryAsync(new LibraryQuery(Search: "tag:scenic"));
        Assert.Equal(2, scenicResult.Items.Count);

        // Search text + ext
        var combinedResult = await catalog.QueryAsync(new LibraryQuery(Search: "mountain ext:jpg"));
        Assert.Single(combinedResult.Items);
        Assert.Equal("mountain.jpg", combinedResult.Items[0].Media.RelativePath);
    }

    [Fact]
    public async Task SmartCollections_SaveListAndDelete_BehaveIdempotently()
    {
        var id = await catalog.SaveSmartCollectionAsync("2024 Favorites", search: "year:2024", favorite: true);
        Assert.False(string.IsNullOrWhiteSpace(id));

        var collections = await catalog.SmartCollectionsAsync();
        Assert.Single(collections);
        Assert.Equal("2024 Favorites", collections[0].Name);
        Assert.Equal("year:2024", collections[0].Search);
        Assert.True(collections[0].Favorite);

        // Update name
        await catalog.SaveSmartCollectionAsync("2024 Starred", search: "year:2024", favorite: true, id: id);
        var updated = await catalog.SmartCollectionsAsync();
        Assert.Single(updated);
        Assert.Equal("2024 Starred", updated[0].Name);

        // Delete
        await catalog.DeleteSmartCollectionAsync(id);
        var empty = await catalog.SmartCollectionsAsync();
        Assert.Empty(empty);
    }

    [Fact]
    public void PhotoStackBuilder_GroupsRawJpegPairsAndConsecutiveBursts()
    {
        var baseTime = new DateTime(2026, 6, 15, 14, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            new LibraryItem(new CachedMedia("s1", "IMG_001.CR3", 20_000_000, baseTime.Ticks, "photo", true), false, "", "1", new MediaMetadata(baseTime.Ticks)),
            new LibraryItem(new CachedMedia("s1", "IMG_001.JPG", 4_000_000, baseTime.Ticks, "photo", true), false, "", "2", new MediaMetadata(baseTime.Ticks)),
            new LibraryItem(new CachedMedia("s1", "BURST_1.JPG", 3_000_000, baseTime.AddMinutes(5).Ticks, "photo", true), false, "", "3", new MediaMetadata(baseTime.AddMinutes(5).Ticks)),
            new LibraryItem(new CachedMedia("s1", "BURST_2.JPG", 3_100_000, baseTime.AddMinutes(5).AddSeconds(10).Ticks, "photo", true), true, "", "4", new MediaMetadata(baseTime.AddMinutes(5).AddSeconds(10).Ticks)),
            new LibraryItem(new CachedMedia("s1", "LATER.JPG", 3_200_000, baseTime.AddHours(2).Ticks, "photo", true), false, "", "5", new MediaMetadata(baseTime.AddHours(2).Ticks)),
        };

        var stacks = PhotoStackBuilder.Build(items, allowBurstClustering: true, burstSeconds: 30);
        Assert.Equal(3, stacks.Count);

        // First stack: RAW+JPEG pair picks JPG cover
        Assert.True(stacks[0].IsStack);
        Assert.Equal("IMG_001.JPG", stacks[0].Cover.Media.RelativePath);
        Assert.Equal(2, stacks[0].Items.Count);

        // Second stack: Burst picks Favorited frame as cover
        Assert.True(stacks[1].IsStack);
        Assert.Equal("BURST_2.JPG", stacks[1].Cover.Media.RelativePath);
        Assert.Equal(2, stacks[1].Items.Count);

        // Third item: standalone
        Assert.False(stacks[2].IsStack);
        Assert.Equal("LATER.JPG", stacks[2].Cover.Media.RelativePath);
    }

    [Fact]
    public void BatchRenameFormatter_AppliesTokensAndLocksExtension()
    {
        var utc = new DateTime(2025, 8, 14, 9, 30, 15, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        var item = new LibraryItem(
            new CachedMedia("s1", "Trips/DCIM_001.JPG", 1024, utc.Ticks, "photo", true),
            false, "", "1", new MediaMetadata(utc.Ticks));

        var renamed = BatchRenameFormatter.FormatName("{date}_{time}_{orig}_{seq}", item, 7, 120);
        Assert.Equal($"{local:yyyy-MM-dd}_{local:HHmmss}_DCIM_001_007.JPG", renamed);

        var sanitized = BatchRenameFormatter.FormatName("bad:name*{orig}", item, 1, 5);
        Assert.Equal("bad_name_DCIM_001.JPG", sanitized);
    }

    [Fact]
    public async Task FileTransfer_ConflictModeRename_GeneratesUniqueFilenameWithoutOverwriting()
    {
        var srcDir = Path.Combine(tempDir, "Src");
        var dstDir = Path.Combine(tempDir, "Dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        var srcFile = Path.Combine(srcDir, "pic.jpg");
        var existingDst = Path.Combine(dstDir, "pic.jpg");
        File.WriteAllText(srcFile, "incoming-content");
        File.WriteAllText(existingDst, "original-dst-content");

        var result = await FileTransfer.RunAsync(srcFile, Path.Combine(dstDir, "pic.jpg"), move: false, () => true, CancellationToken.None, TransferConflictMode.Rename);
        Assert.True(result.Copied);
        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(dstDir, "pic (2).jpg"), result.Destination);
        Assert.Equal("original-dst-content", File.ReadAllText(existingDst));
        Assert.Equal("incoming-content", File.ReadAllText(result.Destination));
    }

    [Fact]
    public async Task ExtendedFiltersAndGeotaggedQueries_WorkAcrossOrientationSizeUntaggedAndMapBounds()
    {
        var sourceDir = Path.Combine(tempDir, "GeoMedia");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, "paris.jpg"), new byte[12000]);
        File.WriteAllBytes(Path.Combine(sourceDir, "tokyo.png"), new byte[300]);
        File.WriteAllBytes(Path.Combine(sourceDir, "notes.bmp"), new byte[500]);

        await catalog.ScanAsync("geo", sourceDir);

        var paris = (await catalog.QueryAsync(new LibraryQuery(Search: "paris"))).Items[0].Media;
        var tokyo = (await catalog.QueryAsync(new LibraryQuery(Search: "tokyo"))).Items[0].Media;
        var notes = (await catalog.QueryAsync(new LibraryQuery(Search: "notes"))).Items[0].Media;

        await catalog.SaveMetadataAsync(paris, new MediaMetadata(
            TakenTicks: new DateTime(2024, 5, 10, 12, 0, 0, DateTimeKind.Utc).Ticks,
            DateOrigin: "exif", Width: 4000, Height: 3000,
            Latitude: 48.8566, Longitude: 2.3522, CameraModel: "Canon R6", Iso: 200));
        await catalog.SaveMetadataAsync(tokyo, new MediaMetadata(
            TakenTicks: new DateTime(2024, 8, 20, 18, 0, 0, DateTimeKind.Utc).Ticks,
            DateOrigin: "exif", Width: 1080, Height: 1920,
            Latitude: 35.6762, Longitude: 139.6503));
        await catalog.SaveMetadataAsync(notes, new MediaMetadata(Width: 800, Height: 800));

        await catalog.AnnotateAsync(paris, true, "europe, trip");
        await catalog.AnnotateAsync(notes, false, "temp");

        // Search operators: size:>5kb
        var big = await catalog.QueryAsync(new LibraryQuery(Search: "size:>5kb"));
        Assert.Single(big.Items);
        Assert.Equal("paris.jpg", big.Items[0].Media.RelativePath);

        // Search operators: is:portrait
        var portrait = await catalog.QueryAsync(new LibraryQuery(Search: "is:portrait"));
        Assert.Single(portrait.Items);
        Assert.Equal("tokyo.png", portrait.Items[0].Media.RelativePath);

        // Search operators: is:untagged
        var untagged = await catalog.QueryAsync(new LibraryQuery(Search: "is:untagged"));
        Assert.Single(untagged.Items);
        Assert.Equal("tokyo.png", untagged.Items[0].Media.RelativePath);

        // Search operators: negation -ext:bmp -tag:europe
        var filtered = await catalog.QueryAsync(new LibraryQuery(Search: "-ext:bmp -tag:europe"));
        Assert.Single(filtered.Items);
        Assert.Equal("tokyo.png", filtered.Items[0].Media.RelativePath);

        // Map markers & bounding box filter
        var markers = await catalog.QueryGeotaggedMarkersAsync(new LibraryQuery());
        Assert.Equal(2, markers.Count);

        var parisBox = await catalog.QueryAsync(new LibraryQuery(MapBounds: new GeoBounds(48.0, 49.5, 1.5, 3.0)));
        Assert.Single(parisBox.Items);
        Assert.Equal("paris.jpg", parisBox.Items[0].Media.RelativePath);
        Assert.Equal("Canon R6", parisBox.Items[0].Metadata?.CameraModel);
        Assert.Equal(200, parisBox.Items[0].Metadata?.Iso);
    }

    [Fact]
    public async Task SeedDefaultSmartCollections_IsIdempotent()
    {
        await catalog.SeedDefaultSmartCollectionsAsync();
        await catalog.SeedDefaultSmartCollectionsAsync();
        var seeded = await catalog.SmartCollectionsAsync();
        Assert.Equal(4, seeded.Count);
    }
}
