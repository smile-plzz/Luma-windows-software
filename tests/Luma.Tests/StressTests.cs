using Luma.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Luma.Tests;

public sealed class StressTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Luma-stress-" + Guid.NewGuid());
    private string DbPath => Path.Combine(root, "catalog.db");

    public StressTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task FiftyCycle_ConcurrentRefreshAndQuery_StressTest()
    {
        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        // Seed catalog with 300 items across 3 sources
        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "INSERT INTO sources VALUES('s1', 'C:\\S1', '2026-01-01T00:00:00Z'), ('s2', 'C:\\S2', '2026-01-01T00:00:00Z'), ('s3', 'C:\\S3', '2026-01-01T00:00:00Z');";
            await cmd.ExecuteNonQueryAsync();

            await using var tx = db.BeginTransaction();
            cmd.Transaction = tx;
            for (int i = 0; i < 300; i++)
            {
                var src = $"s{(i % 3) + 1}";
                var rel = $"folder_{i % 5}/image_{i:D4}.jpg";
                var ticks = 638000000000000000L + (i * 10000000L);
                cmd.CommandText = $"INSERT INTO media(source_id, relative_path, length, modified_ticks, kind) VALUES('{src}', '{rel}', {1000 + i}, {ticks}, 'photo');";
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        // Run 50 concurrent query cycles with rapid cancellation tokens
        var tasks = new List<Task>();
        var random = new Random(42);

        for (int i = 0; i < 50; i++)
        {
            var cycle = i;
            tasks.Add(Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource();
                // Randomly cancel 20% of requests mid-flight to stress cancellation safety
                if (cycle % 5 == 0)
                {
                    cts.CancelAfter(TimeSpan.FromMilliseconds(cycle % 10));
                }

                try
                {
                    var sort = (MediaSort)(cycle % 6);
                    var query = new LibraryQuery(
                        Sort: sort,
                        Limit: 40,
                        Offset: (cycle % 4) * 40,
                        Search: cycle % 3 == 0 ? "image_00" : ""
                    );

                    var page = await catalog.QueryAsync(query, cts.Token);
                    Assert.NotNull(page);
                    Assert.True(page.Items.Count <= 40);
                }
                catch (OperationCanceledException)
                {
                    // Expected when token is cancelled
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Ensure database remains completely healthy after rapid concurrent stress
        var isHealthy = await catalog.QuickCheckAsync();
        Assert.True(isHealthy, "Database must remain healthy after 50 concurrent query/cancellation cycles");
    }

    [Fact]
    public async Task ConcurrentAnnotationAndQuery_NoLocksOrCorruption()
    {
        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sources VALUES('s1', 'C:\S1', '2026-01-01T00:00:00Z');
                INSERT INTO media(source_id, relative_path, length, modified_ticks, kind)
                    VALUES('s1', 'item.jpg', 1024, 638000000000000000, 'photo');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var testMedia = new CachedMedia("s1", "item.jpg", 1024, 638000000000000000, "photo", true);

        // Concurrently query while updating annotations 30 times
        var writer = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                await catalog.AnnotateAsync(testMedia, i % 2 == 0, $"tag-{i}");
                await Task.Yield();
            }
        });

        var reader = Task.Run(async () =>
        {
            for (int i = 0; i < 30; i++)
            {
                var page = await catalog.QueryAsync(new LibraryQuery());
                Assert.Single(page.Items);
                await Task.Yield();
            }
        });

        await Task.WhenAll(writer, reader);
        Assert.True(await catalog.QuickCheckAsync());
    }
}

