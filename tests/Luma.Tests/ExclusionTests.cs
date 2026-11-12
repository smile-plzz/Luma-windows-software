using Luma.Core;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Luma.Tests;
public sealed class ExclusionTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"luma-exclusions-"+Guid.NewGuid());
    private MediaCache Catalog=>new(Path.Combine(root,"catalog.db"));
    public ExclusionTests()=>Directory.CreateDirectory(root);
    private void FileAt(string path) {var full=Path.Combine(root,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllBytes(full,new byte[4]);}
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    [Fact] public async Task FolderExclusionPreservesOrganizationAndDoesNotMatchSiblingPrefix()
    {
        FileAt("App/cache.jpg");FileAt("App/nested/photo.png");FileAt("Apple/keep.jpg");
        var c=Catalog;await c.InitializeAsync();await c.ScanAsync("disk",root);
        var photo=(await c.QueryAsync(new(Search:"cache.jpg"))).Items.Single();
        await c.AnnotateAsync(photo.Media,true,"keep me");var album=await c.SaveAlbumAsync("Saved");await c.SetAlbumItemsAsync(album,[photo.Media],true);
        var rule=new LibraryExclusion("folder","disk","App");await c.SetExclusionAsync(rule,true);
        Assert.Single((await c.QueryAsync(new())).Items);Assert.Equal(0,(await c.AlbumsAsync()).Single().Count);
        Assert.Single(await c.FoldersAsync("disk"));Assert.Equal(1,(await c.SourceCountsAsync())["disk"]);
        Assert.Equal(3,(await c.PreviewKeysAsync()).Count);
        FileAt("App/skipped.jpg");await c.ScanAsync("disk",root);
        await c.SetExclusionAsync(rule,false);
        Assert.Equal(3,(await c.QueryAsync(new())).Total);Assert.Equal(1,(await c.AlbumsAsync()).Single().Count);
        var restored=(await c.QueryAsync(new(FavoritesOnly:true))).Items.Single();Assert.Equal("keep me",restored.Tags);
        await c.ScanAsync("disk",root);Assert.Equal(4,(await c.QueryAsync(new())).Total);Assert.True(File.Exists(Path.Combine(root,"App/cache.jpg")));
    }
    [Fact] public async Task FileTypesAreCaseInsensitiveGlobalPersistentAndAppliedToCountsAndMonths()
    {
        FileAt("a.JPG");FileAt("b.png");var c=Catalog;await c.InitializeAsync();await c.ScanAsync("one",root);await c.ScanAsync("two",root);
        await c.SetExclusionAsync(new("type","ignored",".JPG"),true);
        c=Catalog;await c.InitializeAsync();Assert.Single(await c.ExclusionsAsync());Assert.Equal(2,(await c.QueryAsync(new())).Total);
        Assert.Equal(2,(await c.MonthBucketsAsync(new())).Sum(m=>m.Count));
        var visible=new List<CachedMedia>();await foreach(var media in c.EnumerateCachedAsync())visible.Add(media);Assert.Equal(2,visible.Count);
        FileAt("new.jpg");await c.ScanAsync("one",root);await c.ScanAsync("two",root);
        await c.SetExclusionAsync(new("type","",".jpg"),false);Assert.Equal(4,(await c.QueryAsync(new())).Total);
        await c.ScanAsync("one",root);Assert.Equal(5,(await c.QueryAsync(new())).Total);
    }
    [Fact] public async Task EntireSourceCanBeHiddenAndRestoredWithoutScanningOrDeletingItsFiles()
    {
        FileAt("keep.jpg");var c=Catalog;await c.InitializeAsync();await c.ScanAsync("one",root);await c.ScanAsync("two",root);
        await c.SetExclusionAsync(new("source","one",""),true);Assert.Equal(1,(await c.QueryAsync(new())).Total);
        Assert.Equal(0,await c.ScanAsync("one",Path.Combine(root,"offline")));
        Assert.Equal(0,(await c.OpenSourceAsync("one",root)).MediaCount);
        await c.SetExclusionAsync(new("source","one",""),false);Assert.Equal(2,(await c.QueryAsync(new())).Total);
        Assert.True(File.Exists(Path.Combine(root,"keep.jpg")));
    }
    [Fact] public async Task RuleChangeInvalidatesPausedTraversalAndRestoredFilesAreDiscovered()
    {
        for(int i=0;i<220;i++)FileAt($"photo{i}.jpg");var c=Catalog;await c.InitializeAsync();using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>c.ScanAsync("disk",root,cancel.Token,new ImmediateProgress(_=>cancel.Cancel())));
        Assert.Single(await c.IndexingCheckpointsAsync());await c.SetExclusionAsync(new("type","",".jpg"),true);Assert.Empty(await c.IndexingCheckpointsAsync());
        Assert.Equal(0,await c.ScanAsync("disk",root));await c.SetExclusionAsync(new("type","",".jpg"),false);Assert.Equal(220,await c.ScanAsync("disk",root));
    }
    [Fact] public async Task LiteralFolderNamesAndInvalidRulesAreHandledSafely()
    {
        FileAt("ÉTÉ/photo.jpg");FileAt("a_100%/photo.jpg");FileAt("a_100percent/keep.jpg");var c=Catalog;await c.InitializeAsync();await c.ScanAsync("disk",root);
        await c.SetExclusionAsync(new("folder","disk","été"),true);await c.SetExclusionAsync(new("folder","disk","a_100%"),true);Assert.Single((await c.QueryAsync(new())).Items);
        await Assert.ThrowsAsync<ArgumentException>(()=>c.SetExclusionAsync(new("type","",".*"),true));
        await Assert.ThrowsAsync<ArgumentException>(()=>c.SetExclusionAsync(new("folder","disk","../escape"),true));
    }
    [Fact] public async Task ActiveScanRejectsRuleChangesAndRemovingSourceClearsOnlyItsRules()
    {
        FileAt("photo.jpg");var c=Catalog;await c.InitializeAsync();await c.ScanAsync("disk",root);
        using(var lease=new FileStream(Path.Combine(root,"catalog.db.indexing.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
            await Assert.ThrowsAsync<IOException>(()=>c.SetExclusionAsync(new("source","disk",""),true));
        Assert.Empty(await c.ExclusionsAsync());
        await c.SetExclusionAsync(new("source","disk",""),true);await c.SetExclusionAsync(new("type","",".png"),true);
        await c.RemoveSourceAsync("disk");Assert.Equal("type",Assert.Single(await c.ExclusionsAsync()).Kind);
    }
    private sealed class ImmediateProgress(Action<int> action):IProgress<int>{public void Report(int n)=>action(n);}
}
