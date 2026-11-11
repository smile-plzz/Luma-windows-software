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
}
