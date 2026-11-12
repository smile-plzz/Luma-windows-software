using System.Diagnostics;
using System.Text.Json;
using Luma.Core;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Luma.Tests;
public sealed class ScaleTests
{
    [Theory]
    [InlineData(1000)] [InlineData(10000)] [InlineData(100000)] [InlineData(200000)]
    public async Task LargeCatalogReturnsBoundedBatchesAndDateIndex(int count)
    {
        var root=Path.Combine(Path.GetTempPath(),"Luma-scale-"+Guid.NewGuid()); Directory.CreateDirectory(root);
        var path=Path.Combine(root,"catalog.db"); var catalog=new MediaCache(path);
        try
        {
            await catalog.InitializeAsync();
            await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
            {
                await db.OpenAsync(); await using var cmd=db.CreateCommand();
                cmd.CommandText="""
                    INSERT INTO sources VALUES('fixture','offline','2026-01-01T00:00:00Z');
                    WITH RECURSIVE sequence(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM sequence WHERE n<$count)
                    INSERT INTO media SELECT 'fixture',printf('photo%06d.jpg',n),1000,638712864000000000+n*10000000,'photo' FROM sequence;
                    """;
                cmd.Parameters.AddWithValue("$count",count); await cmd.ExecuteNonQueryAsync();
            }
            await catalog.InitializeAsync();
            var timer=Stopwatch.StartNew(); var first=await catalog.QueryAsync(new(Sort:MediaSort.Captured)); var firstMs=timer.Elapsed.TotalMilliseconds;
            timer.Restart(); var second=await catalog.QueryAsync(new(Sort:MediaSort.Captured,Cursor:first.Next,KnownTotal:first.Total)); var nextMs=timer.Elapsed.TotalMilliseconds;
            timer.Restart(); var months=await catalog.MonthsAsync(new(Sort:MediaSort.Captured)); var monthsMs=timer.Elapsed.TotalMilliseconds;
            Assert.Equal(count,first.Total); Assert.Equal(120,first.Items.Count); Assert.Equal(120,second.Items.Count);
            Assert.Empty(first.Items.Select(x=>x.Id).Intersect(second.Items.Select(x=>x.Id))); Assert.NotEmpty(months);
            timer.Restart(); var anchorPosition=await catalog.LocateAsync(new(Sort:MediaSort.Captured),"fixture",$"photo{count/2:000000}.jpg"); var restoreLocateMs=timer.Elapsed.TotalMilliseconds;
            Assert.Equal(count-count/2,anchorPosition);
            double baselineSortMs;
            await using(var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path}.ToString()))
            {
                await db.OpenAsync(); await using var cmd=db.CreateCommand();
                // Previous capture-order expression, without count or annotation joins:
                // a conservative comparison of sorting cost, not an end-to-end UI claim.
                cmd.CommandText="SELECT m.relative_path FROM media m LEFT JOIN metadata d ON d.source_id=m.source_id AND d.relative_path=m.relative_path AND d.length=m.length AND d.modified_ticks=m.modified_ticks ORDER BY COALESCE(d.taken_ticks,m.modified_ticks) DESC,m.source_id,m.relative_path LIMIT 120";
                timer.Restart(); await using(var reader=await cmd.ExecuteReaderAsync()) {while(await reader.ReadAsync()) { }} baselineSortMs=timer.Elapsed.TotalMilliseconds;
            }
            var directory=Path.Combine(AppContext.BaseDirectory,"benchmarks"); Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory,$"catalog-{count}.json"),JsonSerializer.Serialize(new { Count=count,FirstBatchMs=firstMs,PreviousCaptureSortOnlyMs=baselineSortMs,NextBatchMs=nextMs,MonthIndexMs=monthsMs,RestoreLocateMs=restoreLocateMs,Environment=System.Runtime.InteropServices.RuntimeInformation.OSDescription,Processors=Environment.ProcessorCount,Scope="SQLite metadata/query only; not image decoding or USB hardware" },new JsonSerializerOptions{WriteIndented=true}));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root,true); }
    }
}
