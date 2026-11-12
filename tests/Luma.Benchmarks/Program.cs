using System.Diagnostics;
using System.Text.Json;
using Luma.Core;
using Microsoft.Data.Sqlite;

Console.WriteLine("=================================================");
Console.WriteLine("Luma Large-Library Benchmark Harness (PRF-1)");
Console.WriteLine("=================================================");

int[] sizes = [1_000, 10_000, 100_000, 200_000, 500_000];
var results = new List<BenchmarkResult>();

foreach (var count in sizes)
{
    Console.WriteLine($"\nRunning benchmark for {count:N0} catalog records...");
    var tempDir = Path.Combine(Path.GetTempPath(), "luma-bench-" + Guid.NewGuid());
    Directory.CreateDirectory(tempDir);
    var dbPath = Path.Combine(tempDir, "catalog.db");

    try
    {
        var catalog = new MediaCache(dbPath);
        await catalog.InitializeAsync();

        // 1. Bulk generate records
        var genTimer = Stopwatch.StartNew();
        await using (var db = new SqliteConnection($"Data Source={dbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sources VALUES('bench-src', 'C:\Synthetic', '2026-01-01T00:00:00Z');
                WITH RECURSIVE sequence(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM sequence WHERE n < $count)
                INSERT INTO media SELECT 'bench-src', printf('photo%07d.jpg', n), 2048, 638000000000000000 + (n * 10000000), 'photo' FROM sequence;
                """;
            cmd.Parameters.AddWithValue("$count", count);
            await cmd.ExecuteNonQueryAsync();
        }
        await catalog.InitializeAsync();
        var genMs = genTimer.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  Populated in {genMs:N1} ms");

        // 2. Query first page (120 items + count)
        GC.Collect();
        var memBefore = GC.GetTotalMemory(true);
        var timer = Stopwatch.StartNew();
        var firstPage = await catalog.QueryAsync(new LibraryQuery(Sort: MediaSort.Captured, Limit: 120));
        var firstMs = timer.Elapsed.TotalMilliseconds;
        var memAfter = GC.GetTotalMemory(false);

        // 3. Query next page using cursor & known total
        timer.Restart();
        var nextPage = await catalog.QueryAsync(new LibraryQuery(Sort: MediaSort.Captured, Limit: 120, Cursor: firstPage.Next, KnownTotal: firstPage.Total));
        var nextMs = timer.Elapsed.TotalMilliseconds;

        // 4. Query month buckets
        timer.Restart();
        var months = await catalog.MonthsAsync(new LibraryQuery(Sort: MediaSort.Captured));
        var monthsMs = timer.Elapsed.TotalMilliseconds;

        // 5. Locate middle anchor
        var middleName = $"photo{count / 2:0000000}.jpg";
        timer.Restart();
        var anchorPos = await catalog.LocateAsync(new LibraryQuery(Sort: MediaSort.Captured), "bench-src", middleName);
        var locateMs = timer.Elapsed.TotalMilliseconds;

        var result = new BenchmarkResult(
            Count: count,
            FirstBatchMs: Math.Round(firstMs, 2),
            NextBatchMs: Math.Round(nextMs, 2),
            MonthBreakdownMs: Math.Round(monthsMs, 2),
            LocateMiddleMs: Math.Round(locateMs, 2),
            AllocatedMB: Math.Round((memAfter - memBefore) / (1024.0 * 1024.0), 2)
        );
        results.Add(result);

        Console.WriteLine($"  First 120 + Count: {result.FirstBatchMs} ms");
        Console.WriteLine($"  Next 120 (Cursor): {result.NextBatchMs} ms");
        Console.WriteLine($"  Month Breakdown:   {result.MonthBreakdownMs} ms");
        Console.WriteLine($"  Locate Anchor:     {result.LocateMiddleMs} ms");
    }
    finally
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(tempDir, true); } catch { }
    }
}

Console.WriteLine("\n=========================================================================");
Console.WriteLine("| Records   | First 120 + Count | Next Page | Months    | Locate Anchor |");
Console.WriteLine("|-----------|-------------------|-----------|-----------|---------------|");
foreach (var r in results)
{
    Console.WriteLine($"| {r.Count,9:N0} | {r.FirstBatchMs,14} ms | {r.NextBatchMs,6} ms | {r.MonthBreakdownMs,6} ms | {r.LocateMiddleMs,10} ms |");
}
Console.WriteLine("=========================================================================");

// Save to repo-root docs/performance
var repoRoot = AppContext.BaseDirectory;
while (repoRoot is not null && (!Directory.Exists(Path.Combine(repoRoot, "docs")) || !Directory.Exists(Path.Combine(repoRoot, "src"))))
    repoRoot = Directory.GetParent(repoRoot)?.FullName;
var outDir = repoRoot is not null
    ? Path.Combine(repoRoot, "docs", "performance")
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../docs/performance"));
if (!Directory.Exists(outDir)) Directory.CreateDirectory(outDir);
var jsonFile = Path.Combine(outDir, "windows-v0.7.0-benchmarks.json");
File.WriteAllText(jsonFile, JsonSerializer.Serialize(new
{
    Date = DateTime.UtcNow.ToString("O"),
    OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Processors = Environment.ProcessorCount,
    Results = results,
    Note = "Single-run synthetic SQLite catalog query timings; not physical HDD scan or image decode."
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nResults saved to: {Path.GetFullPath(jsonFile)}");

public sealed record BenchmarkResult(
    int Count,
    double FirstBatchMs,
    double NextBatchMs,
    double MonthBreakdownMs,
    double LocateMiddleMs,
    double AllocatedMB
);

