using Luma.Core;
using Luma.App;
using Microsoft.Data.Sqlite;
using System.Text.Json;
using Xunit;
namespace Luma.Tests;
public sealed class FinalExperienceTests:IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"luma-final-"+Guid.NewGuid());
    private string Drive=>Path.Combine(root,"drive");
    public FinalExperienceTests()=>Directory.CreateDirectory(Drive);
    public void Dispose(){SqliteConnection.ClearAllPools();Directory.Delete(root,true);}
    private sealed class Resolver(string root):ISourceResolver {public string? Root=root;public string? ResolveRoot(string id)=>Root;}
    private sealed class Progress<T>(Action<T> action):IProgress<T>{public void Report(T value)=>action(value);}
    private sealed class Reader:IMetadataReader
    {
        public Task<MediaMetadata> ReadAsync(string path,string kind,CancellationToken ct=default)
        {if(path.EndsWith("bad.jpg"))throw new IOException("Unreadable");return Task.FromResult(new MediaMetadata(null,"No capture date"));}
    }
    private void FileAt(string path){var full=Path.Combine(Drive,path);Directory.CreateDirectory(Path.GetDirectoryName(full)!);File.WriteAllBytes(full,[1,2,3]);}
    [Fact] public void BrowseStateSurvivesSettingsRoundTripAndOlderSettingsStartFresh()
    {
        var place=new BrowsePlace("disk","Trips","family","album",3,"2026-09",null,"disk","Trips/photo.jpg",-35.5);
        var settings=AppSettings.FromJson(JsonSerializer.Serialize(new AppSettings { Place=place }));Assert.Equal(place,settings.Place);Assert.True(settings.RememberPlace);
        Assert.Null(AppSettings.FromJson("{}").Place);Assert.False(AppSettings.FromJson("{\"RememberPlace\":false}").RememberPlace);
    }
    [Fact] public async Task LocateMatchesAllSortOrdersAndFiltersWithoutConnectedOriginals()
    {
        for(int i=0;i<245;i++)FileAt($"Trips/{i:000}.jpg");var resolver=new Resolver(Drive);
        var c=new MediaCache(Path.Combine(root,"catalog.db"),resolver);await c.InitializeAsync();await c.ScanAsync("disk",Drive);
        resolver.Root=null;Directory.Delete(Drive,true);
        foreach(var sort in Enum.GetValues<MediaSort>())foreach(var descending in new[]{true,false})
        {
            var query=new LibraryQuery(Sort:sort,Descending:descending,SourceId:"disk",Folder:"Trips",Limit:500);
            var items=(await c.QueryAsync(query)).Items;var anchor=items[180].Media;
            Assert.Equal(180,await c.LocateAsync(query,anchor.SourceId,anchor.RelativePath));
            Assert.Equal(anchor,(await c.QueryAsync(query with {Offset=180,Limit=1})).Items.Single().Media);
        }
        await c.SetExclusionAsync(new("folder","disk","Trips"),true);
        Assert.Null(await c.LocateAsync(new(),"disk",Path.Combine("Trips","180.jpg")));
    }
    [Fact] public async Task LocateHonorsDatesFavoritesAndMissingAnchors()
    {
        FileAt("a.jpg");FileAt("b.jpg");var c=new MediaCache(Path.Combine(root,"catalog.db"));await c.InitializeAsync();await c.ScanAsync("disk",Drive);
        var item=(await c.QueryAsync(new(Sort:MediaSort.Name))).Items[0];var ticks=new DateTime(2025,9,2,12,0,0,DateTimeKind.Utc).Ticks;
        await c.SaveMetadataAsync(item.Media,new(ticks,"Capture"));await c.AnnotateAsync(item.Media,true,"family");
        var query=new LibraryQuery(Sort:MediaSort.Captured,FavoritesOnly:true,Search:"family",FromTicks:ticks,BeforeTicks:ticks);
        Assert.Equal(0,await c.LocateAsync(query,"disk","a.jpg"));Assert.Null(await c.LocateAsync(query,"disk","b.jpg"));
        Assert.Null(await c.LocateAsync(query,"removed","a.jpg"));
    }
    [Fact] public async Task ImportReportsDiscoveryAndOnlyPendingMetadataWithSkippedCounts()
    {
        FileAt("nested/good.jpg");FileAt("bad.jpg");FileAt("skip.png");FileAt("readme.txt");var c=new MediaCache(Path.Combine(root,"catalog.db"),new Resolver(Drive));await c.InitializeAsync();
        await c.SetExclusionAsync(new("type","",".png"),true);
        var discovery=new List<DiscoveryProgress>();Assert.Equal(2,await c.ScanAsync("disk",Drive,details:new Progress<DiscoveryProgress>(discovery.Add)));
        Assert.Equal(0,discovery[0].SavedFiles);Assert.Equal(2,discovery[^1].SavedFiles);Assert.Equal(4,discovery[^1].ExaminedFiles);Assert.Equal(2,discovery[^1].CompletedFolders);
        var metadata=new List<MetadataProgress>();Assert.Equal(1,await c.EnrichAsync(new Reader(),details:new Progress<MetadataProgress>(metadata.Add)));
        Assert.Equal(2,metadata[0].Total);Assert.Equal(2,metadata[^1].Checked);Assert.Equal(1,metadata[^1].Saved);Assert.Equal(1,metadata[^1].Skipped);
        metadata.Clear();await c.EnrichAsync(new Reader(),details:new Progress<MetadataProgress>(metadata.Add));Assert.Equal(1,metadata[0].Total);
    }
    [Fact] public async Task DiscoveryDetailsRemainCompatibleWithCheckpointCancellation()
    {
        for(int i=0;i<220;i++)FileAt($"{i}.jpg");var c=new MediaCache(Path.Combine(root,"catalog.db"));await c.InitializeAsync();using var cancel=new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>c.ScanAsync("disk",Drive,cancel.Token,new Progress<int>(_=>cancel.Cancel()),new Progress<DiscoveryProgress>(_=>{})));
        Assert.Single(await c.IndexingCheckpointsAsync());Assert.Equal(220,await c.ScanAsync("disk",Drive));
    }
}
