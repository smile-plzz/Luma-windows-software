using Luma.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Luma.Tests;

public sealed class CacheTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Luma-tests-" + Guid.NewGuid());
    private string CachePath => Path.Combine(root, "cache");
    private static CachedMedia Media(string name = "a.jpg") => new("volume-one", name, 4, 100, "photo", false);
    public CacheTests() => Directory.CreateDirectory(root);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }

    [Fact] public async Task ThumbnailsSurviveRestart()
    {
        var key = ThumbnailCache.Key(Media());
        using (var cache = new ThumbnailCache(CachePath)) await cache.StoreAsync(key, new byte[] { 1, 2, 3 });
        using var reopened = new ThumbnailCache(CachePath);
        Assert.Equal(new byte[] { 1, 2, 3 }, await reopened.ReadAsync(key));
    }

    [Fact] public async Task LeastRecentlyUsedEntryIsEvicted()
    {
        using var cache = new ThumbnailCache(CachePath, 6);
        var a = ThumbnailCache.Key(Media("a")); var b = ThumbnailCache.Key(Media("b")); var c = ThumbnailCache.Key(Media("c"));
        await cache.StoreAsync(a, new byte[3]); await cache.StoreAsync(b, new byte[3]);
        await cache.ReadAsync(a);
        await cache.StoreAsync(c, new byte[3]);
        Assert.Null(await cache.ReadAsync(b));
        Assert.NotNull(await cache.ReadAsync(a)); Assert.NotNull(await cache.ReadAsync(c));
        Assert.Equal(6, new DirectoryInfo(CachePath).GetFiles("*.thumb").Sum(f => f.Length));
    }

    [Fact] public async Task CancelledReplacementPreservesOriginal()
    {
        using var cache = new ThumbnailCache(CachePath);
        var key = ThumbnailCache.Key(Media());
        await cache.StoreAsync(key, new byte[] { 1 });
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.StoreAsync(key, new byte[] { 2 }, cancelled.Token));
        Assert.Equal(new byte[] { 1 }, await cache.ReadAsync(key));
        Assert.Empty(Directory.GetFiles(CachePath, "*.tmp"));
    }

    [Fact] public void StartupCleansCrashDebrisAndEnforcesReducedBudget()
    {
        Directory.CreateDirectory(CachePath);
        File.WriteAllBytes(Path.Combine(CachePath, "orphan.tmp"), new byte[10]);
        File.WriteAllBytes(Path.Combine(CachePath, ThumbnailCache.Key(Media()) + ".thumb"), new byte[10]);
        using var cache = new ThumbnailCache(CachePath, 5);
        Assert.Empty(Directory.GetFiles(CachePath, "*.tmp")); Assert.Empty(Directory.GetFiles(CachePath, "*.thumb"));
    }

    [Fact] public async Task RejectsOversizedEntriesAndTraversalKeys()
    {
        using var cache = new ThumbnailCache(CachePath, 5);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => cache.StoreAsync(ThumbnailCache.Key(Media()), new byte[6]));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.ReadAsync("../../escape"));
    }

    [Fact] public void KeyChangesWithContentVersionSizeAndSource()
    {
        var media = Media(); var key = ThumbnailCache.Key(media);
        Assert.NotEqual(key, ThumbnailCache.Key(media with { Length = 9 }));
        Assert.NotEqual(key, ThumbnailCache.Key(media with { ModifiedTicks = 101 }));
        Assert.NotEqual(key, ThumbnailCache.Key(media with { SourceId = "other" }));
        Assert.NotEqual(key, ThumbnailCache.Key(media, 512));
        Assert.Equal(key, ThumbnailCache.Key(media with { IsAvailable = true }));
    }

    [Fact] public async Task ConcurrentWritesLeaveOneCompleteEntry()
    {
        using var cache = new ThumbnailCache(CachePath);
        var key = ThumbnailCache.Key(Media());
        await Task.WhenAll(Enumerable.Range(1, 20).Select(i => cache.StoreAsync(key, Enumerable.Repeat((byte)i, 100).ToArray())));
        var data = await cache.ReadAsync(key);
        Assert.Equal(100, data!.Length); Assert.All(data, b => Assert.Equal(data[0], b));
        Assert.Empty(Directory.GetFiles(CachePath, "*.tmp"));
    }

    [Fact] public async Task CachedHitDoesNotProbeDisconnectedDrive()
    {
        using var cache = new ThumbnailCache(CachePath);
        await cache.StoreAsync(ThumbnailCache.Key(Media()), new byte[] { 42 });
        var resolver = new Resolver { ThrowOnResolve = true }; var generator = new Generator();
        var service = new ThumbnailService(cache, resolver, generator);
        Assert.Equal(new byte[] { 42 }, await service.GetAsync(Media())); Assert.Equal(0, generator.Calls);
    }

    [Fact] public async Task OfflineMissDoesNotDecode()
    {
        using var cache = new ThumbnailCache(CachePath); var generator = new Generator();
        Assert.Null(await new ThumbnailService(cache, new Resolver(), generator).GetAsync(Media()));
        Assert.Equal(0, generator.Calls);
    }

    [Fact] public async Task ReconnectUsesNewRootAndCoalescesDecoding()
    {
        var first = Path.Combine(root, "first"); var second = Path.Combine(root, "second");
        Directory.CreateDirectory(first); var file = Path.Combine(first, "a.jpg"); await File.WriteAllBytesAsync(file, new byte[4]);
        var media = Media() with { ModifiedTicks = File.GetLastWriteTimeUtc(file).Ticks };
        Directory.Move(first, second);
        using var cache = new ThumbnailCache(CachePath); var generator = new Generator();
        var service = new ThumbnailService(cache, new Resolver { Root = second }, generator);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.GetAsync(media)));
        Assert.Equal(1, generator.Calls); Assert.Equal(Path.Combine(second, "a.jpg"), generator.LastPath);
    }

    [Fact] public async Task ChangedFileDoesNotGenerateUnderStaleKey()
    {
        await File.WriteAllBytesAsync(Path.Combine(root, "a.jpg"), new byte[10]);
        using var cache = new ThumbnailCache(CachePath); var generator = new Generator();
        Assert.Null(await new ThumbnailService(cache, new Resolver { Root = root }, generator).GetAsync(Media()));
        Assert.Equal(0, generator.Calls);
    }

    [Fact] public async Task UnplugDuringGenerationDoesNotPersistThumbnail()
    {
        var file = Path.Combine(root, "a.jpg"); await File.WriteAllBytesAsync(file, new byte[4]);
        var media = Media() with { ModifiedTicks = File.GetLastWriteTimeUtc(file).Ticks };
        var resolver = new Resolver { Root = root }; var generator = new Generator { OnGenerate = () => resolver.Root = null };
        using var cache = new ThumbnailCache(CachePath);
        Assert.Null(await new ThumbnailService(cache, resolver, generator).GetAsync(media));
        Assert.Null(await cache.ReadAsync(ThumbnailCache.Key(media)));
    }

    [Fact] public void VolumeRootAcceptsRelativePaths()
    {
        var volumeRoot = Path.GetPathRoot(root)!;
        Assert.Equal(Path.Combine(volumeRoot, "sample.jpg"), SourcePaths.Combine(volumeRoot, "sample.jpg"));
    }

    [Fact] public void SourcePathCannotEscapeRoot() => Assert.Throws<ArgumentException>(() => SourcePaths.Combine(root, "../outside.jpg"));

    [Fact] public async Task CatalogPersistsOfflineAndResolvesNewMount()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "a.jpg"), new byte[4]);
        var resolver = new Resolver { Root = source };
        var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver); await catalog.InitializeAsync();
        Assert.Equal(1, await catalog.ScanAsync("volume-one", source));
        Assert.True(Assert.Single(await catalog.ListAsync("volume-one")).IsAvailable);
        resolver.Root = null;
        Assert.False(Assert.Single(await catalog.ListAsync("volume-one")).IsAvailable);
        var newMount = Path.Combine(root, "new-letter"); Directory.Move(source, newMount); resolver.Root = newMount;
        var reopened = new MediaCache(Path.Combine(root, "catalog.db"), resolver);
        Assert.True(Assert.Single(await reopened.ListAsync("volume-one")).IsAvailable);
        Assert.Equal(1, await reopened.ScanAsync("volume-one", newMount));
        Assert.Single(await reopened.ListAsync("volume-one"));
    }

    [Fact] public async Task CancelledScanLeavesCatalogUnchanged()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "a.jpg"), new byte[4]);
        var catalog = new MediaCache(Path.Combine(root, "catalog.db")); await catalog.InitializeAsync();
        await catalog.ScanAsync("volume-one", source);
        await File.WriteAllBytesAsync(Path.Combine(source, "b.jpg"), new byte[4]);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ScanAsync("volume-one", source, cancelled.Token));
        Assert.Single(await catalog.ListAsync("volume-one"));
    }

    [Fact] public async Task QueriesPageFilterAndPersistAnnotations()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        for (int i = 0; i < 5; i++) await File.WriteAllBytesAsync(Path.Combine(source, $"photo{i}.jpg"), new byte[i + 1]);
        await File.WriteAllBytesAsync(Path.Combine(source, "clip.mp4"), new byte[4]);
        await File.WriteAllBytesAsync(Path.Combine(source, "100%.jpg"), new byte[4]);
        var catalog = new MediaCache(Path.Combine(root, "catalog.db")); await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        var page = await catalog.QueryAsync(new(Limit: 2, Sort: MediaSort.Name));
        Assert.Equal(7, page.Total); Assert.Equal(2, page.Items.Count);
        Assert.NotEqual(page.Items[0].Media.RelativePath, (await catalog.QueryAsync(new(Offset: 2, Limit: 2, Sort: MediaSort.Name))).Items[0].Media.RelativePath);
        Assert.Single((await catalog.QueryAsync(new(Kind: "video"))).Items);
        Assert.Single((await catalog.QueryAsync(new(Search: "%"))).Items);
        var media = page.Items[0].Media;
        await catalog.AnnotateAsync(media, true, "Family, family, Travel");
        var favorite = Assert.Single((await catalog.QueryAsync(new(FavoritesOnly: true))).Items);
        Assert.Equal("Family, Travel", favorite.Tags);
        Assert.Single((await catalog.QueryAsync(new(Search: "Travel"))).Items);
        Assert.Single(await catalog.SourcesAsync());
        await catalog.RemoveSourceAsync("source"); Assert.Empty((await catalog.QueryAsync(new())).Items);
        Assert.True(File.Exists(Path.Combine(source, "clip.mp4")));
    }

    [Fact] public async Task CompletedScanReconcilesDeletedOriginals()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        var path = Path.Combine(source, "a.jpg"); await File.WriteAllBytesAsync(path, new byte[4]);
        var catalog = new MediaCache(Path.Combine(root, "catalog.db")); await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        File.Delete(path); await catalog.ScanAsync("source", source);
        Assert.Empty(await catalog.ListAsync("source"));
    }

    [Fact] public async Task InFlightCancellationRollsBackPartialScan()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "existing.jpg"), new byte[4]);
        var catalog = new MediaCache(Path.Combine(root, "catalog.db")); await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        for (int i = 0; i < 120; i++) File.WriteAllBytes(Path.Combine(source, $"new{i}.jpg"), new byte[1]);
        using var cancelled = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.ScanAsync("source", source, cancelled.Token,
            new CallbackProgress(_ => cancelled.Cancel())));
        Assert.Single(await catalog.ListAsync("source"));
    }

    [Fact] public async Task LostIdentityDuringScanRollsBackPartialCatalog()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        await File.WriteAllBytesAsync(Path.Combine(source, "existing.jpg"), new byte[4]);
        var resolver = new Resolver { Root = source };
        var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver); await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        for (int i = 0; i < 120; i++) File.WriteAllBytes(Path.Combine(source, $"new{i}.jpg"), new byte[1]);
        await Assert.ThrowsAsync<IOException>(() => catalog.ScanAsync("source", source, progress: new CallbackProgress(_ => resolver.Root = null)));
        Assert.Single(await catalog.ListAsync("source"));
    }

    [Fact] public async Task FolderNavigationUsesOnlyCatalogAndPreservesLiteralNames()
    {
        var source = Path.Combine(root, "drive");
        Directory.CreateDirectory(Path.Combine(source, "100%_done", "nested"));
        Directory.CreateDirectory(Path.Combine(source, "other"));
        File.WriteAllBytes(Path.Combine(source, "100%_done", "a.jpg"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "100%_done", "nested", "b.jpg"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "other", "c.jpg"), new byte[4]);
        var resolver = new Resolver { Root = source };
        var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        resolver.ThrowOnResolve = true; Directory.Delete(source, true);
        var folders = await catalog.FoldersAsync("source");
        Assert.Equal(2, folders.Count); Assert.Equal("100%_done", folders[0].Name); Assert.Equal(2, folders[0].MediaCount);
        var nested = Assert.Single(await catalog.FoldersAsync("source", "100%_done"));
        Assert.Equal(Path.Combine("100%_done", "nested"), nested.RelativePath);
        Assert.Empty(await catalog.FoldersAsync("source", nested.RelativePath));
        Assert.Empty(await catalog.FoldersAsync("another"));
    }

    [Fact] public async Task PreparationResumesAfterCancellationAndSurvivesRestartOffline()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(source, $"{i}.jpg"), new byte[4]);
        var resolver = new Resolver { Root = source }; var generator = new Generator();
        var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        using (var cache = new ThumbnailCache(CachePath))
        {
            var preparation = new OfflinePreparation(catalog, cache, new(cache, resolver, generator));
            using var cancelled = new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.PrepareAsync("source",
                new LongProgress(_ => cancelled.Cancel()), cancelled.Token));
            Assert.Equal(1, (await preparation.InspectAsync()).Cached);
            Assert.Equal(3, (await preparation.PrepareAsync("source")).Cached);
            Assert.Equal(3, generator.Calls); // The completed first preview was reused.
        }
        resolver.ThrowOnResolve = true; Directory.Delete(source, true);
        using var reopened = new ThumbnailCache(CachePath);
        var offline = new OfflinePreparation(catalog, reopened, new(reopened, resolver, generator));
        Assert.Equal(3, (await offline.PrepareAsync("source")).Cached);
        Assert.Equal(3, generator.Calls);
    }

    [Fact] public async Task PreparationReportsPartialCoverageWithoutEvictingExistingPreviews()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "a.jpg"), new byte[4]);
        File.WriteAllBytes(Path.Combine(source, "b.jpg"), new byte[4]);
        var resolver = new Resolver { Root = source };
        var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        using var cache = new ThumbnailCache(CachePath, 6);
        var otherKey = ThumbnailCache.Key(Media("unplugged.jpg")); await cache.StoreAsync(otherKey, new byte[3]);
        var preparation = new OfflinePreparation(catalog, cache, new(cache, resolver, new Generator()));
        var coverage = await preparation.PrepareAsync("source");
        Assert.Equal(2, coverage.Total); Assert.Equal(1, coverage.Cached); Assert.Equal(6, coverage.CacheBytes);
        Assert.NotNull(await cache.ReadAsync(otherKey));
        Assert.Equal(1, (await preparation.PrepareAsync("source")).Cached);
        Assert.NotNull(await cache.ReadAsync(otherKey));
    }

    [Fact] public async Task CoverageExcludesOldVersionsAndMissingCacheFiles()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        var path = Path.Combine(source, "a.jpg"); File.WriteAllBytes(path, new byte[4]);
        var resolver = new Resolver { Root = source };
        var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        using var cache = new ThumbnailCache(CachePath);
        var preparation = new OfflinePreparation(catalog, cache, new(cache, resolver, new Generator()));
        Assert.Equal(1, (await preparation.PrepareAsync()).Cached);
        File.WriteAllBytes(path, new byte[8]); await catalog.ScanAsync("source", source);
        Assert.Equal(0, (await preparation.InspectAsync()).Cached);
        Assert.Equal(1, (await preparation.PrepareAsync()).Cached);
        foreach (var file in Directory.GetFiles(CachePath, "*.thumb")) File.Delete(file);
        Assert.Equal(0, (await preparation.InspectAsync()).Cached);
        await preparation.PrepareAsync();
        foreach (var file in Directory.GetFiles(CachePath, "*.thumb")) File.WriteAllBytes(file, Array.Empty<byte>());
        var coverage = await preparation.InspectAsync();
        Assert.Equal(0, coverage.Cached); Assert.Equal(0, coverage.CacheBytes);
        Assert.Empty(Directory.GetFiles(CachePath, "*.thumb"));
    }

    [Fact] public async Task ClearCachePreservesCatalogAnnotationsAndOriginals()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        var path = Path.Combine(source, "a.jpg"); File.WriteAllBytes(path, new byte[4]);
        var resolver = new Resolver { Root = source };
        var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source", source);
        var media = Assert.Single(await catalog.ListAsync("source"));
        await catalog.AnnotateAsync(media, true, "keep");
        using var cache = new ThumbnailCache(CachePath);
        var preparation = new OfflinePreparation(catalog, cache, new(cache, resolver, new Generator()));
        await preparation.PrepareAsync(); await cache.ClearAsync();
        Assert.Empty((await cache.SnapshotAsync()).Keys); Assert.Empty(Directory.GetFiles(CachePath, "*.thumb"));
        Assert.True(File.Exists(path));
        var item = Assert.Single((await catalog.QueryAsync(new())).Items);
        Assert.True(item.Favorite); Assert.Equal("keep", item.Tags);
        Assert.Equal(1, (await preparation.PrepareAsync()).Cached);
    }

    [Fact] public async Task OfflineMissingPreviewsStayMissingWithoutGeneratorCalls()
    {
        var source = Path.Combine(root, "drive"); Directory.CreateDirectory(source);
        File.WriteAllBytes(Path.Combine(source, "a.jpg"), new byte[4]);
        var resolver = new Resolver { Root = source }; var generator = new Generator();
        var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source", source); resolver.Root = null;
        using var cache = new ThumbnailCache(CachePath);
        var preparation = new OfflinePreparation(catalog, cache, new(cache, resolver, generator));
        var coverage = await preparation.PrepareAsync();
        Assert.Equal(1, coverage.Total); Assert.Equal(0, coverage.Cached); Assert.Equal(0, generator.Calls);
    }

    [Fact] public async Task RebuildReplacesOnlySuccessfullyGeneratedPreviewsAndPreservesOfflineHits()
    {
        var source=Path.Combine(root,"drive"); Directory.CreateDirectory(source);
        var path=Path.Combine(source,"a.jpg"); File.WriteAllBytes(path,new byte[4]);
        var resolver=new Resolver { Root=source }; var catalog=new MediaCache(Path.Combine(root,"catalog.db"),resolver);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source",source);
        var media=Assert.Single(await catalog.ListAsync("source")); var key=ThumbnailCache.Key(media);
        using var cache=new ThumbnailCache(CachePath); await cache.StoreAsync(key,new byte[]{42});
        var generator=new Generator(); var preparation=new OfflinePreparation(catalog,cache,new(cache,resolver,generator));
        Assert.Equal(1,(await preparation.RebuildAsync()).Updated);
        Assert.Equal(new byte[]{1,2,3},await cache.ReadAsync(key)); Assert.Equal(1,generator.Calls);
        resolver.Root=null;
        Assert.Equal(0,(await preparation.RebuildAsync()).Updated);
        Assert.Equal(new byte[]{1,2,3},await cache.ReadAsync(key)); Assert.Equal(1,generator.Calls);
    }

    [Fact] public async Task RebuildFailureAndCancellationKeepPreviousPreview()
    {
        var path=Path.Combine(root,"a.jpg"); File.WriteAllBytes(path,new byte[4]);
        var media=Media() with { ModifiedTicks=File.GetLastWriteTimeUtc(path).Ticks };
        var resolver=new Resolver { Root=root }; var generator=new Generator { OnGenerate=()=>resolver.Root=null };
        using var cache=new ThumbnailCache(CachePath); var key=ThumbnailCache.Key(media); await cache.StoreAsync(key,new byte[]{42});
        var service=new ThumbnailService(cache,resolver,generator);
        Assert.Null(await service.GetAsync(media,allowEviction:false,forceRefresh:true));
        Assert.Equal(new byte[]{42},await cache.ReadAsync(key));
        using var cancel=new CancellationTokenSource(); resolver.Root=root; generator.OnGenerate=()=>cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.GetAsync(media,ct:cancel.Token,allowEviction:false,forceRefresh:true));
        Assert.Equal(new byte[]{42},await cache.ReadAsync(key));
    }

    [Fact] public async Task RebuildAtCapacityPreservesOtherSourcesAndOldPreview()
    {
        var path=Path.Combine(root,"a.jpg"); File.WriteAllBytes(path,new byte[4]);
        var media=Media() with { ModifiedTicks=File.GetLastWriteTimeUtc(path).Ticks };
        using var cache=new ThumbnailCache(CachePath,5); var key=ThumbnailCache.Key(media);
        var offline=ThumbnailCache.Key(Media("offline.jpg")); await cache.StoreAsync(key,new byte[]{42}); await cache.StoreAsync(offline,new byte[]{7,7,7});
        var service=new ThumbnailService(cache,new Resolver { Root=root },new Generator());
        Assert.Null(await service.GetAsync(media,allowEviction:false,forceRefresh:true));
        Assert.Equal(new byte[]{42},await cache.ReadAsync(key)); Assert.Equal(new byte[]{7,7,7},await cache.ReadAsync(offline));
    }

    [Fact] public async Task CleanupRemovesObsoleteKeysButRetainsCurrentOfflineSources()
    {
        var source=Path.Combine(root,"drive"); Directory.CreateDirectory(source); var path=Path.Combine(source,"a.jpg"); File.WriteAllBytes(path,new byte[4]);
        var resolver=new Resolver { Root=source }; var catalog=new MediaCache(Path.Combine(root,"catalog.db"),resolver); await catalog.InitializeAsync(); await catalog.ScanAsync("source",source);
        var media=Assert.Single(await catalog.ListAsync("source"));
        using var cache=new ThumbnailCache(CachePath);
        var current=ThumbnailCache.Key(media); var old=ThumbnailCache.Key(media with { Length=1 }); var orphan=ThumbnailCache.Key(Media("removed.jpg"));
        await cache.StoreAsync(current,new byte[]{1,2}); await cache.StoreAsync(old,new byte[]{3,4,5}); await cache.StoreAsync(orphan,new byte[]{6});
        resolver.ThrowOnResolve=true;
        var keys=await catalog.PreviewKeysAsync(); var snapshot=await cache.SnapshotAsync();
        var unused=snapshot.Keys.Where(k=>!keys.Contains(k)).ToHashSet();
        Assert.Equal(new CacheRemoval(2,4),await cache.InspectRemovalAsync(unused));
        Assert.Equal(new CacheRemoval(2,4),await cache.RemoveAsync(unused));
        Assert.Equal(new byte[]{1,2},await cache.ReadAsync(current)); Assert.Null(await cache.ReadAsync(old));
        Assert.Equal(2,(await cache.SnapshotAsync()).Bytes);
    }

    [Fact] public async Task SourceRemovalUpdatesCountsAlbumsAndScopedPreviewsWithoutDeletingOriginals()
    {
        var source=Path.Combine(root,"drive"); Directory.CreateDirectory(source); File.WriteAllBytes(Path.Combine(source,"a.jpg"),new byte[4]);
        var catalog=new MediaCache(Path.Combine(root,"catalog.db")); await catalog.InitializeAsync(); await catalog.ScanAsync("a",source); await catalog.ScanAsync("b",source);
        var a=Assert.Single((await catalog.QueryAsync(new(SourceId:"a"))).Items); var b=Assert.Single((await catalog.QueryAsync(new(SourceId:"b"))).Items);
        var album=await catalog.SaveAlbumAsync("Keep"); await catalog.SetAlbumItemsAsync(album,[a.Media,b.Media],true);
        var keys=await catalog.PreviewKeysAsync("a");
        using var cache=new ThumbnailCache(CachePath); await cache.StoreAsync(ThumbnailCache.Key(a.Media),new byte[]{1}); await cache.StoreAsync(ThumbnailCache.Key(b.Media),new byte[]{2});
        await catalog.RemoveSourceAsync("a"); await cache.RemoveAsync(keys);
        Assert.Single(await catalog.SourceCountsAsync()); Assert.Equal(1,Assert.Single(await catalog.AlbumsAsync()).Count);
        Assert.Equal(b.Id,Assert.Single((await catalog.QueryAsync(new(AlbumId:album))).Items).Id);
        Assert.NotNull(await cache.ReadAsync(ThumbnailCache.Key(b.Media))); Assert.True(File.Exists(Path.Combine(source,"a.jpg")));
    }

    private sealed class LongProgress(Action<long> callback) : IProgress<long> { public void Report(long value) => callback(value); }

    [Fact] public async Task SourceListShowsCurrentMountPathAfterReconnect()
    {
        var first=Path.Combine(root,"first"); var second=Path.Combine(root,"second"); Directory.CreateDirectory(first);
        File.WriteAllBytes(Path.Combine(first,"a.jpg"),new byte[4]);
        var resolver=new Resolver { Root=first }; var catalog=new MediaCache(Path.Combine(root,"catalog.db"),resolver);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source",first);
        Directory.Move(first,second); resolver.Root=second;
        Assert.Equal(second,Assert.Single(await catalog.SourcesAsync()).RootPath);
    }

    private sealed class CallbackProgress(Action<int> callback) : IProgress<int> { public void Report(int value) => callback(value); }

    private sealed class Resolver : ISourceResolver
    {
        public string? Root; public bool ThrowOnResolve;
        public string? ResolveRoot(string id) => ThrowOnResolve ? throw new InvalidOperationException("Must not probe") : Root;
    }
    private sealed class Generator : IThumbnailGenerator
    {
        public int Calls; public string? LastPath; public Action? OnGenerate;
        public Task<byte[]?> GenerateAsync(string path, int pixels, CancellationToken ct)
        { Calls++; LastPath = path; OnGenerate?.Invoke(); return Task.FromResult<byte[]?>(new byte[] { 1, 2, 3 }); }
    }
}
