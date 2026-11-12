using Microsoft.Data.Sqlite;

namespace Luma.Core;

public sealed record SourceOpenResult(bool UsedSavedCatalog, long MediaCount);

public sealed partial class MediaCache
{
    // Reopening a known identity is catalog-only, even when the drive is offline.
    // Only an unknown source needs discovery; ScanAsync remains the explicit refresh path.
    public async Task<SourceOpenResult> OpenSourceAsync(string sourceId, string root,
        CancellationToken ct = default, IProgress<int>? progress = null, IProgress<DiscoveryProgress>? details = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) throw new ArgumentException("Source ID required", nameof(sourceId));
        await using (var db = CreateConnection())
        {
            await db.OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT (SELECT COUNT(*) FROM media m WHERE source_id=$id AND " + AllowedMediaSql + ") FROM sources WHERE id=$id AND NOT EXISTS(SELECT 1 FROM scan_jobs WHERE source_id=$id)";
            cmd.Parameters.AddWithValue("$id", sourceId);
            var saved = await cmd.ExecuteScalarAsync(ct);
            if (saved is not null) return new(true, Convert.ToInt64(saved));
        }
        return new(false, await ScanAsync(sourceId, root, ct, progress, details));
    }
}
