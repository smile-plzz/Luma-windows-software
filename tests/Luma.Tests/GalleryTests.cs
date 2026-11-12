using Luma.Core;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Luma.Tests;
public sealed class GalleryTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"Luma-gallery-"+Guid.NewGuid());
    private readonly MediaCache catalog;
    private readonly Resolver resolver;
    public GalleryTests() { Directory.CreateDirectory(root); resolver=new(root); catalog=new(Path.Combine(root,"catalog.db"),resolver); }
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    private async Task Seed(int count=8)
    {
        for(int i=0;i<count;i++) { var path=Path.Combine(root,$"photo{i:000}.jpg"); File.WriteAllBytes(path,new byte[4+i%3]); File.SetLastWriteTimeUtc(path,new DateTime(2025,1,1,0,0,0,DateTimeKind.Utc).AddDays(i%5)); }
        await catalog.InitializeAsync(); await catalog.ScanAsync("source",root);
    }
    [Theory]
    [InlineData(MediaSort.Newest,true)] [InlineData(MediaSort.Oldest,false)]
    [InlineData(MediaSort.Name,true)] [InlineData(MediaSort.Name,false)]
    [InlineData(MediaSort.Largest,true)] [InlineData(MediaSort.Largest,false)]
    [InlineData(MediaSort.Captured,true)] [InlineData(MediaSort.Captured,false)]
    [InlineData(MediaSort.Type,true)] [InlineData(MediaSort.Type,false)]
    [InlineData(MediaSort.Filename,true)] [InlineData(MediaSort.Filename,false)]
    [InlineData(MediaSort.Resolution,true)] [InlineData(MediaSort.Resolution,false)]
    [InlineData(MediaSort.Duration,true)] [InlineData(MediaSort.Duration,false)]
    public async Task CursorTraversalMatchesWholeQueryWithoutDuplicates(MediaSort sort,bool descending)
    {
        await Seed(265); var query=new LibraryQuery(Sort:sort,Descending:descending,Limit:500);
        var expected=(await catalog.QueryAsync(query)).Items.Select(x=>x.Id).ToArray();
        var ids=new List<string>(); GalleryCursor? cursor=null;
        do
        {
            var page=await catalog.QueryAsync(query with { Limit=37,Cursor=cursor });
            ids.AddRange(page.Items.Select(x=>x.Id)); cursor=page.Next;
            if(page.Items.Count<37)break;
        } while(ids.Count<500);
        Assert.Equal(265,ids.Count); Assert.Equal(expected,ids); Assert.Equal(265,ids.Distinct().Count());
    }

    [Fact]
    public async Task CursorTraversalWithFavoritesFirst_MatchesFullOrderWithoutDuplicates()
    {
        await Seed(95);
        var all = (await catalog.QueryAsync(new(Limit: 100))).Items;
        for (int i = 0; i < all.Count; i += 7)
            await catalog.AnnotateAsync(all[i].Media, true, "fav");

        var query = new LibraryQuery(Sort: MediaSort.Captured, Descending: true, FavoritesFirst: true, Limit: 200);
        var expected = (await catalog.QueryAsync(query)).Items;
        Assert.True(expected[0].Favorite);
        Assert.False(expected[^1].Favorite);

        var paged = new List<string>();
        GalleryCursor? cursor = null;
        do
        {
            var page = await catalog.QueryAsync(query with { Limit = 19, Cursor = cursor });
            paged.AddRange(page.Items.Select(x => x.Id));
            cursor = page.Next;
            if (page.Items.Count < 19) break;
        } while (paged.Count < 200);

        Assert.Equal(expected.Select(x => x.Id), paged);
        Assert.Equal(95, paged.Distinct().Count());
    }
    [Fact] public async Task AlbumsAndAnnotationsFollowRenameAndMoveAcrossSources()
    {
        await Seed(); var item=(await catalog.QueryAsync(new(Limit:1))).Items[0];
        var album=await catalog.SaveAlbumAsync("Travel"); await catalog.SetAlbumItemsAsync(album,[item.Media],true);
        await catalog.AnnotateAsync(item.Media,true,"family");
        var destination=Path.Combine(root,"destination"); Directory.CreateDirectory(destination);
        // Register an empty second source before moving; resolver in this test points all sources at root.
        await catalog.ScanAsync("destination",root);
        var target=item.Media with { SourceId="destination",RelativePath="renamed.jpg" };
        File.Move(Path.Combine(root,item.Media.RelativePath),Path.Combine(root,target.RelativePath));
        await catalog.RelocateAsync(item.Media,target);
        var moved=Assert.Single((await catalog.QueryAsync(new(AlbumId:album))).Items);
        Assert.Equal(item.Id,moved.Id); Assert.Equal("renamed.jpg",moved.Media.RelativePath); Assert.True(moved.Favorite); Assert.Equal("family",moved.Tags);
        await catalog.DeleteAlbumAsync(album); Assert.Empty(await catalog.AlbumsAsync()); Assert.True(File.Exists(Path.Combine(root,"renamed.jpg")));
    }
    [Fact] public async Task MetadataIsVersionedAndCaptureSortingFallsBack()
    {
        await Seed(2); var items=(await catalog.QueryAsync(new(Sort:MediaSort.Name))).Items;
        var taken=new DateTime(2030,1,1,0,0,0,DateTimeKind.Utc).Ticks;
        await catalog.SaveMetadataAsync(items[0].Media,new(taken,"fixture",800,600));
        Assert.Equal(items[0].Id,(await catalog.QueryAsync(new(Sort:MediaSort.Captured))).Items[0].Id);
        File.WriteAllBytes(Path.Combine(root,items[0].Media.RelativePath),new byte[12]); await catalog.ScanAsync("source",root);
        var refreshed=(await catalog.QueryAsync(new(Sort:MediaSort.Name))).Items[0]; Assert.Null(refreshed.Metadata!.TakenTicks); Assert.Equal(0,refreshed.Metadata.Width);
    }
    [Fact] public async Task MetadataEnrichmentSkipsUnchangedVersionsAndOfflineSources()
    {
        await Seed(3); var reader=new Reader();
        Assert.Equal(3,await catalog.EnrichAsync(reader)); Assert.Equal(0,await catalog.EnrichAsync(reader)); Assert.Equal(3,reader.Calls);
        File.WriteAllBytes(Path.Combine(root,"photo000.jpg"),new byte[14]); await catalog.ScanAsync("source",root);
        Assert.Equal(1,await catalog.EnrichAsync(reader)); resolver.Online=false; Assert.Equal(0,await catalog.EnrichAsync(reader));
    }
    [Fact] public async Task FolderDepthCommonFormatsAndDateJumpRespectFilters()
    {
        await Seed(2); Directory.CreateDirectory(Path.Combine(root,"nested"));
        File.WriteAllBytes(Path.Combine(root,"nested","child.png"),new byte[4]); File.WriteAllBytes(Path.Combine(root,"raw.dng"),new byte[4]);
        await catalog.ScanAsync("source",root);
        Assert.Equal(2,(await catalog.QueryAsync(new(IncludeDescendants:false,CommonOnly:true))).Total);
        Assert.Single((await catalog.QueryAsync(new(Folder:"nested",IncludeDescendants:false))).Items);
        var before=new DateTime(2025,1,1,23,59,59,DateTimeKind.Utc).Ticks;
        Assert.Single((await catalog.QueryAsync(new(BeforeTicks:before))).Items);
        Assert.Contains("2025-01",await catalog.MonthsAsync(new()));
    }
    [Fact] public async Task AllFormatsDefaultAndExplicitFilterHaveConsistentCountsAndPages()
    {
        await Seed(2); File.WriteAllBytes(Path.Combine(root,"camera.dng"),new byte[4]);
        File.WriteAllBytes(Path.Combine(root,"archive.tiff"),new byte[4]); await catalog.ScanAsync("source",root);
        var all=await catalog.QueryAsync(new(Sort:MediaSort.Name,Limit:1));
        Assert.Equal(4,all.Total); Assert.Equal(4,(await catalog.SourceCountsAsync())["source"]);
        var common=await catalog.QueryAsync(new(CommonOnly:true,Limit:1)); Assert.Equal(2,common.Total);
        Assert.Equal(2,(await catalog.QueryAsync(new(CommonOnly:true,Limit:1,Cursor:common.Next))).Total);
        Assert.Contains((await catalog.QueryAsync(new())).Items,i=>i.Media.RelativePath=="camera.dng");
    }
    [Fact] public async Task InitializeIsRepeatableAndPreservesIdentityAndAlbums()
    {
        await Seed(1); var item=Assert.Single((await catalog.QueryAsync(new())).Items);
        var album=await catalog.SaveAlbumAsync("Keep"); await catalog.SetAlbumItemsAsync(album,[item.Media],true);
        await catalog.InitializeAsync(); await catalog.ScanAsync("source",root);
        Assert.Equal(item.Id,Assert.Single((await catalog.QueryAsync(new(AlbumId:album))).Items).Id);
        await catalog.RemoveSourceAsync("source"); Assert.Equal(0,Assert.Single(await catalog.AlbumsAsync()).Count);
    }
    [Fact] public async Task TransferCopiesThenMovesWithoutOverwriting()
    {
        var source=Path.Combine(root,"source.jpg"); var copy=Path.Combine(root,"copy.jpg"); var target=Path.Combine(root,"moved.jpg");
        File.WriteAllBytes(source,[1,2,3]);
        Assert.True((await FileTransfer.RunAsync(source,copy,false,()=>true)).Copied); Assert.True(File.Exists(source));
        var conflict=await FileTransfer.RunAsync(source,copy,true,()=>true); Assert.NotNull(conflict.Error); Assert.True(File.Exists(source));
        Assert.True((await FileTransfer.RunAsync(source,target,true,()=>true)).Moved); Assert.False(File.Exists(source)); Assert.Equal(new byte[]{1,2,3},File.ReadAllBytes(target));
    }
    [Fact] public async Task CancellationAndChangedSourceNeverPublishPartialFiles()
    {
        var source=Path.Combine(root,"source.jpg"); var target=Path.Combine(root,"target.jpg"); File.WriteAllBytes(source,new byte[500]);
        using var ct=new CancellationTokenSource(); ct.Cancel();
        var cancelled=await FileTransfer.RunAsync(source,target,true,()=>true,ct.Token); Assert.False(cancelled.Moved); Assert.False(File.Exists(target));
        int calls=0; var changed=await FileTransfer.RunAsync(source,target,true,()=>++calls==1);
        Assert.False(changed.Copied); Assert.True(File.Exists(source)); Assert.False(File.Exists(target)); Assert.Empty(Directory.GetFiles(root,"*.tmp"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrowsingDuringAnOpenScanReadsCommittedSnapshot(bool cancelScan)
    {
        await Seed(125);
        var before=await catalog.QueryAsync(new(Sort:MediaSort.Name,Limit:20));
        var album=await catalog.SaveAlbumAsync("During scan");
        await catalog.SetAlbumItemsAsync(album,[before.Items[0].Media],true);
        await catalog.AnnotateAsync(before.Items[0].Media,true,"keep");
        File.WriteAllBytes(Path.Combine(root,"new-photo.jpg"),new byte[4]);
        using var entered=new ManualResetEventSlim();
        using var release=new ManualResetEventSlim();
        using var cancellation=new CancellationTokenSource();
        var progress=new BlockingScanProgress(entered,release);
        var scan=Task.Run(()=>catalog.ScanAsync("source",root,cancellation.Token,progress));
        Task? browsing=null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)),"Scan never acquired its write transaction.");
            browsing=Task.Run(async ()=>
            {
                var page=await catalog.QueryAsync(new(Sort:MediaSort.Captured,Limit:20));
                Assert.Equal(125,page.Total);
                Assert.DoesNotContain(page.Items,x=>x.Media.RelativePath=="new-photo.jpg");
                var next=await catalog.QueryAsync(new(Sort:MediaSort.Captured,Limit:20,Cursor:page.Next));
                Assert.Empty(page.Items.Select(x=>x.Id).Intersect(next.Items.Select(x=>x.Id)));
                Assert.Single((await catalog.QueryAsync(new(AlbumId:album,FavoritesOnly:true,Search:"keep"))).Items);
                Assert.NotEmpty(await catalog.MonthsAsync(new(Sort:MediaSort.Captured)));
            });
            // The old immediate gallery transaction waits for the held writer.
            // Read completion must happen BEFORE releasing it, not merely eventually.
            await browsing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            if(cancelScan)cancellation.Cancel();
            release.Set();
            if(cancelScan)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>scan);
            else await scan;
            if(browsing is not null)await browsing;
        }
        Assert.Equal(cancelScan ? 125 : 126,(await catalog.QueryAsync(new())).Total);
        Assert.Single((await catalog.QueryAsync(new(AlbumId:album,FavoritesOnly:true))).Items);
    }
    [Fact]
    public async Task ReopeningKnownSourceUsesSavedCatalogWithoutDriveProbesOrRescan()
    {
        await Seed(3);
        var item=(await catalog.QueryAsync(new())).Items[0];
        await catalog.AnnotateAsync(item.Media,true,"keep");
        var album=await catalog.SaveAlbumAsync("Saved");
        await catalog.SetAlbumItemsAsync(album,[item.Media],true);
        var seen=Assert.Single(await catalog.SourcesAsync()).LastSeenUtc;
        foreach(var file in Directory.GetFiles(root,"*.jpg"))File.Delete(file);
        File.WriteAllBytes(Path.Combine(root,"not-indexed-yet.jpg"),new byte[5]);
        resolver.Online=false;
        var probes=resolver.Calls;
        // A fresh instance represents a restart; reopening must not access even the
        // supplied path, inspect changed files, or invoke indexing progress.
        var restarted=new MediaCache(Path.Combine(root,"catalog.db"),resolver);
        var result=await restarted.OpenSourceAsync("source",Path.Combine(root,"unavailable"),progress:new UnexpectedScanProgress());
        Assert.True(result.UsedSavedCatalog); Assert.Equal(3,result.MediaCount);
        Assert.Equal(probes,resolver.Calls);
        Assert.Equal(seen,Assert.Single(await restarted.SourcesAsync()).LastSeenUtc);
        Assert.Single((await restarted.QueryAsync(new(AlbumId:album,FavoritesOnly:true))).Items);
        Assert.Equal(0,(await restarted.QueryAsync(new(Search:"not-indexed-yet"))).Total);
        resolver.Online=true;
        await restarted.ScanAsync("source",root);
        Assert.Equal(1,(await restarted.QueryAsync(new(Search:"not-indexed-yet"))).Total);
    }
    [Fact]
    public async Task FirstOpenScansButEmptyKnownSourceIsNotScannedAgain()
    {
        await catalog.InitializeAsync();
        var first=await catalog.OpenSourceAsync("source",root);
        Assert.False(first.UsedSavedCatalog); Assert.Equal(0,first.MediaCount);
        File.WriteAllBytes(Path.Combine(root,"later.jpg"),new byte[5]);
        var second=await catalog.OpenSourceAsync("source",root,progress:new UnexpectedScanProgress());
        Assert.True(second.UsedSavedCatalog); Assert.Equal(0,second.MediaCount);
        await catalog.ScanAsync("source",root);
        Assert.Equal(1,(await catalog.OpenSourceAsync("source",root)).MediaCount);
    }
    private sealed class UnexpectedScanProgress:IProgress<int>
    {
        public void Report(int value)=>throw new InvalidOperationException("Reopening unexpectedly scanned the source.");
    }
    private sealed class BlockingScanProgress(ManualResetEventSlim entered,ManualResetEventSlim release):IProgress<int>
    {
        private int paused;
        public void Report(int value)
        {
            if(Interlocked.Exchange(ref paused,1)!=0)return;
            entered.Set();
            if(!release.Wait(TimeSpan.FromSeconds(20)))throw new TimeoutException("Test did not release scan.");
        }
    }
    private sealed class Resolver(string root):ISourceResolver { public bool Online=true; public int Calls; public string? ResolveRoot(string id) { Calls++; return Online ? root : null; } }
    [Fact] public async Task MonthBucketsAndExactLocalRangeRespectFiltersAndBothDirections()
    {
        await Seed(4); var items=(await catalog.QueryAsync(new(Sort:MediaSort.Name))).Items;
        long T(int month,int day)=>new DateTime(2024,month,day,12,0,0,DateTimeKind.Local).ToUniversalTime().Ticks;
        for(int i=0;i<4;i++)await catalog.SaveMetadataAsync(items[i].Media,new(T(i<2 ? 1 : 2,i+1),"fixture"));
        var buckets=await catalog.MonthBucketsAsync(new(Sort:MediaSort.Captured));
        Assert.Equal(new[]{"2024-02","2024-01"},buckets.Select(m=>m.Month)); Assert.All(buckets,m=>Assert.Equal(2,m.Count));
        var start=new DateTime(2024,2,1,0,0,0,DateTimeKind.Local).ToUniversalTime().Ticks;
        var end=new DateTime(2024,3,1,0,0,0,DateTimeKind.Local).ToUniversalTime().Ticks-1;
        foreach(var descending in new[]{true,false})
        {
            var query=new LibraryQuery(Sort:MediaSort.Captured,FromTicks:start,BeforeTicks:end,Descending:descending,Limit:1);
            var first=await catalog.QueryAsync(query); var next=await catalog.QueryAsync(query with {Cursor=first.Next,KnownTotal=first.Total});
            Assert.Equal(2,first.Total); Assert.Equal(2,next.Total); Assert.NotEqual(first.Items[0].Id,next.Items[0].Id);
            Assert.All(first.Items.Concat(next.Items),item=>Assert.InRange(item.Metadata!.TakenTicks!.Value,start,end));
        }
        await catalog.AnnotateAsync(items[2].Media,true,"");
        var filtered=Assert.Single(await catalog.MonthBucketsAsync(new(Sort:MediaSort.Captured,FavoritesOnly:true)));
        Assert.Equal("2024-02",filtered.Month); Assert.Equal(1,filtered.Count);
    }
    [Fact] public async Task TimelineIndexMigratesAndTracksStaleMetadataDeletionAndSourceRemoval()
    {
        await Seed(3); var item=(await catalog.QueryAsync(new(Sort:MediaSort.Name))).Items[0];
        var taken=new DateTime(2030,1,1,0,0,0,DateTimeKind.Utc).Ticks;
        await catalog.SaveMetadataAsync(item.Media,new(taken,"fixture"));
        await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"catalog.db")}.ToString()))
        {
            await db.OpenAsync(); await using var cmd=db.CreateCommand();
            cmd.CommandText="DROP TRIGGER browse_media_insert; DROP TRIGGER browse_media_update; DROP TRIGGER browse_media_delete; DROP TRIGGER browse_metadata_insert; DROP TRIGGER browse_metadata_update; DROP TRIGGER browse_metadata_delete; DROP TABLE browse_dates; DROP TABLE luma_migrations;";
            await cmd.ExecuteNonQueryAsync();
        }
        await catalog.InitializeAsync(); await catalog.InitializeAsync();
        Assert.Equal(item.Id,(await catalog.QueryAsync(new(Sort:MediaSort.Captured))).Items[0].Id);
        // A stale metadata replacement must restore the modified-date fallback in the index.
        await catalog.SaveMetadataAsync(item.Media with {Length=item.Media.Length+1},new(taken,"stale"));
        Assert.DoesNotContain(await catalog.MonthsAsync(new(Sort:MediaSort.Captured)),m=>m=="2030-01");
        await catalog.SaveMetadataAsync(item.Media,new(taken,"fixture"));
        await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"catalog.db")}.ToString()))
        {
            await db.OpenAsync(); await using var cmd=db.CreateCommand(); cmd.CommandText="DELETE FROM metadata"; await cmd.ExecuteNonQueryAsync();
        }
        Assert.DoesNotContain(await catalog.MonthsAsync(new(Sort:MediaSort.Captured)),m=>m=="2030-01");
        await catalog.RemoveSourceAsync("source"); Assert.Empty((await catalog.QueryAsync(new(Sort:MediaSort.Captured))).Items);
    }
    [Fact] public async Task MetadataBatchesResumeAfterCancellationWithoutRereadingSavedFiles()
    {
        await Seed(325); using var cancellation=new CancellationTokenSource();
        var reader=new CancellingReader(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.EnrichAsync(reader,ct:cancellation.Token));
        var resumed=new Reader(); Assert.Equal(225,await catalog.EnrichAsync(resumed)); Assert.Equal(225,resumed.Calls);
        Assert.Equal(0,await catalog.EnrichAsync(resumed));
    }
    private sealed class CancellingReader(CancellationTokenSource cancellation):IMetadataReader
    {
        private int calls;
        public Task<MediaMetadata> ReadAsync(string path,string kind,CancellationToken ct)
        { if(++calls==150)cancellation.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(new MediaMetadata(null,"fixture")); }
    }
    private sealed class Reader:IMetadataReader { public int Calls; public Task<MediaMetadata> ReadAsync(string path,string kind,CancellationToken ct) { Calls++; return Task.FromResult(new MediaMetadata(null,"fixture",640,480)); } }
}
