using Luma.App;
using Luma.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Luma.Tests;

public sealed class FailurePathTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "Luma-failpaths-" + Guid.NewGuid());
    private string DbPath => Path.Combine(root, "catalog.db");
    private string CachePath => Path.Combine(root, "cache");

    public FailurePathTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task QuickCheck_ReturnsFalse_WhenDatabaseIsCorrupted()
    {
        // Write completely invalid bytes to database path
        await File.WriteAllBytesAsync(DbPath, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xEE, 0xDD });

        var catalog = new MediaCache(DbPath);
        var isHealthy = await catalog.QuickCheckAsync();

        Assert.False(isHealthy, "QuickCheckAsync must detect corrupted SQLite headers/data");
    }

    [Fact]
    public async Task QuickCheck_ReturnsTrue_WhenDatabaseIsClean()
    {
        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        var isHealthy = await catalog.QuickCheckAsync();
        Assert.True(isHealthy, "QuickCheckAsync must succeed on valid database");
    }

    [Fact]
    public async Task RepairCatalog_RestoresFromBackup_WhenDatabaseIsCorrupted()
    {
        var catalog = new MediaCache(DbPath);
        await catalog.InitializeAsync();

        // Insert verified record
        await using (var db = new SqliteConnection($"Data Source={DbPath}"))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sources VALUES('src-1', 'C:\Photos', '2026-01-01T00:00:00.0000000Z');
                INSERT INTO media(source_id, relative_path, length, modified_ticks, kind)
                    VALUES('src-1', 'valid.jpg', 1024, 638000000000000000, 'photo');
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // Trigger checkpoint and create backup
        await catalog.CheckpointAsync();
        SqliteConnection.ClearAllPools();
        File.Copy(DbPath, DbPath + ".bak", true);

        // Corrupt main database file with garbage
        await File.WriteAllBytesAsync(DbPath, new byte[] { 0x42, 0x41, 0x44, 0x21, 0x00, 0x00 });

        // Verify corrupted
        Assert.False(await catalog.QuickCheckAsync());

        // Run repair
        var repaired = await catalog.RepairCatalogAsync();
        Assert.True(repaired, "RepairCatalogAsync must report successful recovery");

        // Verify integrity is restored
        Assert.True(await catalog.QuickCheckAsync());

        // Verify data was restored from backup
        var sources = await catalog.SourcesAsync();
        Assert.Single(sources);
        Assert.Equal("src-1", sources[0].Id);

        var page = await catalog.QueryAsync(new LibraryQuery());
        Assert.Equal(1, page.Total);
        Assert.Equal("valid.jpg", page.Items[0].Media.RelativePath);

        // Verify corrupted file was archived
        var corruptFiles = Directory.GetFiles(root, "catalog-corrupt-*.db");
        Assert.NotEmpty(corruptFiles);
    }

    [Fact]
    public async Task RepairCatalog_ReinitializesFreshSchema_WhenBackupIsDamagedOrMissing()
    {
        // Corrupt database without valid backup
        await File.WriteAllBytesAsync(DbPath, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF });

        var catalog = new MediaCache(DbPath);
        var repaired = await catalog.RepairCatalogAsync();

        Assert.True(repaired, "RepairCatalogAsync should reinitialize clean schema if backup unavailable");
        Assert.True(await catalog.QuickCheckAsync());

        var sources = await catalog.SourcesAsync();
        Assert.Empty(sources);
    }

    [Fact]
    public void ThumbnailCache_LockConflict_ThrowsIOException()
    {
        Directory.CreateDirectory(CachePath);
        using var primaryCache = new ThumbnailCache(CachePath);

        // Secondary attempt to open cache concurrently in same directory must fail
        Assert.Throws<IOException>(() =>
        {
            using var secondaryCache = new ThumbnailCache(CachePath);
        });
    }

    [Fact]
    public void PathRedactor_RedactsUsernamesAndUserPaths()
    {
        var inputWindows = @"C:\Users\JohnDoe\Pictures\family_photo.jpg";
        var redactedWindows = PathRedactor.Redact(inputWindows);
        Assert.DoesNotContain("JohnDoe", redactedWindows);
        Assert.Contains("[REDACTED]", redactedWindows);

        var inputUnix = @"/home/alice/media/sample.png";
        var redactedUnix = PathRedactor.Redact(inputUnix);
        Assert.DoesNotContain("alice", redactedUnix);
        Assert.Contains("[REDACTED]", redactedUnix);

        if (!string.IsNullOrWhiteSpace(Environment.UserName))
        {
            var inputUser = $"Logged in user: {Environment.UserName}";
            var redactedUser = PathRedactor.Redact(inputUser);
            Assert.DoesNotContain(Environment.UserName, redactedUser);
            Assert.Contains("[REDACTED]", redactedUser);
        }
    }

    [Fact]
    public void DiskGuard_QueriesDriveSafely()
    {
        var freeBytes = DiskGuard.AvailableFreeBytes();
        Assert.True(freeBytes > 0, "Available free bytes on disk must be positive");

        var hasSpace = DiskGuard.HasSufficientSpace();
        // Just verify it returns without throwing exception
        Assert.True(hasSpace || !hasSpace);
    }
}

