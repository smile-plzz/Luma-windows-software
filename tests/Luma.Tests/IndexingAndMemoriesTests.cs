using Luma.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Luma.Tests;

public sealed class IndexingAndMemoriesTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"Luma-resume-"+Guid.NewGuid());
    private string Drive => Path.Combine(root,"drive");
    private string Database => Path.Combine(root,"catalog.db");
    public IndexingAndMemoriesTests() => Directory.CreateDirectory(Drive);
    public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    private void Files(string folder,int count)
    {
        Directory.CreateDirectory(Path.Combine(Drive,folder));
        for(var i=0;i<count;i++) File.WriteAllBytes(Path.Combine(Drive,folder,$"{i:000}.jpg"),[1,2,3]);
    }
    private sealed class Progress(Action<int> callback) : IProgress<int> { public void Report(int value)=>callback(value); }
    private sealed class Resolver(string root) : ISourceResolver { public string? Root=root; public string? ResolveRoot(string id)=>Root; }

    [Fact] public async Task ResumeAfterRestartAndMountChangeSkipsCompletedFoldersAndPreservesOrganization()
    {
        Files("a",100); Files("b",120);
        var resolver=new Resolver(Drive); var catalog=new MediaCache(Database,resolver); await catalog.InitializeAsync(); await catalog.ScanAsync("disk",Drive);
        var original=(await catalog.QueryAsync(new(Sort:MediaSort.Name))).Items[0];
        await catalog.AnnotateAsync(original.Media,true,"keep"); var album=await catalog.SaveAlbumAsync("Trip"); await catalog.SetAlbumItemsAsync(album,[original.Media],true);
        Files("c",1);
        using var cancellation=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.ScanAsync("disk",Drive,cancellation.Token,new Progress(n=>{ if(n>=200)cancellation.Cancel(); })));
        var checkpoint=Assert.Single(await catalog.IndexingCheckpointsAsync());
        Assert.Equal(2,checkpoint.CompletedFolders); Assert.Equal(200,checkpoint.MediaCount);
        Assert.Equal(220,(await catalog.QueryAsync(new())).Total);
        var remount=Path.Combine(root,"new-letter"); Directory.Move(Drive,remount); resolver.Root=remount;
        var reopened=new MediaCache(Database,resolver); await reopened.InitializeAsync();
        var reported=new List<int>(); var result=await reopened.OpenSourceAsync("disk",Drive,progress:new Progress(reported.Add));
        Assert.False(result.UsedSavedCatalog); Assert.Equal(221,result.MediaCount);
        Assert.All(reported,n=>Assert.True(n>=200,"Completed folder a should not be indexed again."));
        Assert.Empty(await reopened.IndexingCheckpointsAsync());
        var kept=Assert.Single((await reopened.QueryAsync(new(AlbumId:album,FavoritesOnly:true))).Items);
        Assert.Equal(original.Id,kept.Id); Assert.Equal("keep",kept.Tags);
    }

    [Fact] public async Task ChangedCompletedFolderRestartsSnapshotAndReconcilesSafely()
    {
        Files("a",100); Files("b",120); var catalog=new MediaCache(Database); await catalog.InitializeAsync();
        using var cancellation=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.ScanAsync("disk",Drive,cancellation.Token,new Progress(n=>{if(n>=200)cancellation.Cancel();})));
        File.Delete(Path.Combine(Drive,"a","000.jpg"));
        Directory.SetLastWriteTimeUtc(Path.Combine(Drive,"a"),DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(219,await catalog.ScanAsync("disk",Drive));
        Assert.DoesNotContain((await catalog.QueryAsync(new(Limit:500))).Items,i=>i.Media.RelativePath==Path.Combine("a","000.jpg"));
    }

    [Fact] public async Task DisconnectKeepsCheckpointAndPublishedLibraryUntilResume()
    {
        Files("",1); var resolver=new Resolver(Drive); var catalog=new MediaCache(Database,resolver); await catalog.InitializeAsync(); await catalog.ScanAsync("disk",Drive);
        Files("a",120);
        await Assert.ThrowsAsync<IOException>(()=>catalog.ScanAsync("disk",Drive,progress:new Progress(n=>{if(n>=100)resolver.Root=null;})));
        Assert.Single((await catalog.QueryAsync(new())).Items); Assert.Single(await catalog.IndexingCheckpointsAsync());
        resolver.Root=Drive; Assert.Equal(121,await catalog.ScanAsync("disk",Drive));
        Assert.Empty(await catalog.IndexingCheckpointsAsync());
    }

    [Fact] public async Task DiscardAndRemoveCleanCheckpointsWithoutTouchingOriginals()
    {
        Files("",120); var catalog=new MediaCache(Database); await catalog.InitializeAsync();
        async Task Pause()
        {
            using var cancellation=new CancellationTokenSource();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>catalog.ScanAsync("disk",Drive,cancellation.Token,new Progress(_=>cancellation.Cancel())));
        }
        await Pause(); await catalog.DiscardIndexingAsync("disk"); Assert.Empty(await catalog.IndexingCheckpointsAsync());
        await Pause(); await catalog.RemoveSourceAsync("disk"); Assert.Empty(await catalog.IndexingCheckpointsAsync()); Assert.Empty(await catalog.SourcesAsync());
        Assert.Equal(120,Directory.GetFiles(Drive).Length);
    }

    [Fact] public async Task IndexingLeaseRejectsConcurrentWriterAndReleasesAfterCancellation()
    {
        Files("",120); var catalog=new MediaCache(Database); await catalog.InitializeAsync();
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim(); using var ct=new CancellationTokenSource();
        var scan=Task.Run(()=>catalog.ScanAsync("disk",Drive,ct.Token,new Progress(_=>{entered.Set();release.Wait(TimeSpan.FromSeconds(10));})));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<IOException>(()=>new MediaCache(Database).ScanAsync("other",Drive));
            await Assert.ThrowsAsync<IOException>(()=>catalog.DiscardIndexingAsync("disk"));
        }
        finally { ct.Cancel(); release.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>scan); }
        Assert.Equal(120,await catalog.ScanAsync("disk",Drive));
    }

    private static long LocalTicks(int year,int month,int day) => new DateTime(year,month,day,12,0,0,DateTimeKind.Local).ToUniversalTime().Ticks;
    [Fact] public async Task MemoriesUseCaptureDatePriorYearsAndSurviveOfflineWithFiltersAndCursor()
    {
        Files("",6); var resolver=new Resolver(Drive); var catalog=new MediaCache(Database,resolver); await catalog.InitializeAsync(); await catalog.ScanAsync("disk",Drive);
        var items=(await catalog.QueryAsync(new(Sort:MediaSort.Name))).Items;
        var dates=new long?[]{LocalTicks(2025,10,12),LocalTicks(2020,10,12),LocalTicks(2026,10,12),LocalTicks(2027,10,12),LocalTicks(2020,10,11),null};
        for(var i=0;i<items.Count;i++) await catalog.SaveMetadataAsync(items[i].Media,new(dates[i],"Photo capture date"));
        await catalog.AnnotateAsync(items[1].Media,true,"family"); var album=await catalog.SaveAlbumAsync("Family"); await catalog.SetAlbumItemsAsync(album,[items[1].Media],true);
        resolver.Root=null; Directory.Delete(Drive,true);
        var reopened=new MediaCache(Database,resolver); var query=new LibraryQuery(MemoriesOn:new(2026,10,12),Sort:MediaSort.Captured,Limit:1);
        var first=await reopened.QueryAsync(query); Assert.Equal(2,first.Total); Assert.Equal(items[0].Id,Assert.Single(first.Items).Id); Assert.False(first.Items[0].Media.IsAvailable);
        var second=await reopened.QueryAsync(query with { Cursor=first.Next }); Assert.Equal(items[1].Id,Assert.Single(second.Items).Id);
        Assert.Empty((await reopened.QueryAsync(query with { Cursor=second.Next })).Items);
        Assert.Single((await reopened.QueryAsync(query with { FavoritesOnly=true,Search="family",AlbumId=album,SourceId="disk" })).Items);
        Assert.Equal(2,(await reopened.MonthsAsync(query)).Count);
        Assert.Empty((await reopened.QueryAsync(query with { SourceId="another" })).Items);
    }

    [Fact] public async Task MemoriesExcludeStaleMetadataAndDoNotInventLeapDayAnniversaries()
    {
        Files("",2); var catalog=new MediaCache(Database); await catalog.InitializeAsync(); await catalog.ScanAsync("disk",Drive);
        var items=(await catalog.QueryAsync(new(Sort:MediaSort.Name))).Items;
        foreach(var item in items) await catalog.SaveMetadataAsync(item.Media,new(LocalTicks(2020,2,29),"Capture"));
        Assert.Equal(2,(await catalog.QueryAsync(new(MemoriesOn:new(2024,2,29)))).Total);
        Assert.Empty((await catalog.QueryAsync(new(MemoriesOn:new(2025,2,28)))).Items);
        Assert.Empty((await catalog.QueryAsync(new(MemoriesOn:new(2025,3,1)))).Items);
        File.WriteAllBytes(Path.Combine(Drive,items[0].Media.RelativePath),new byte[10]); await catalog.ScanAsync("disk",Drive);
        Assert.Single((await catalog.QueryAsync(new(MemoriesOn:new(2024,2,29)))).Items);
    }
}
