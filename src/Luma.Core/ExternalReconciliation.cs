using Microsoft.Data.Sqlite;

namespace Luma.Core;

public sealed record ReconciledMove(string SourceId, string OldRelativePath, string NewRelativePath, long Length);

public sealed record ReconciliationReport(string SourceId, IReadOnlyList<ReconciledMove> ReconciledMoves)
{
    public int Count => ReconciledMoves.Count;
}

public sealed partial class MediaCache
{
    public async Task<ReconciliationReport> ReconcileExternalMovesAsync(string sourceId, CancellationToken ct = default)
    {
        var root = sourceResolver?.ResolveRoot(sourceId);
        if (root is null || !Directory.Exists(root))
        {
            return new ReconciliationReport(sourceId, Array.Empty<ReconciledMove>());
        }

        var exclusions = await ExclusionsAsync(ct);

        // 1. Collect catalog entries and find items missing on disk in one pass
        var missingInCatalog = new List<CachedMedia>();
        var catalogPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var db = CreateConnection())
        {
            await db.OpenAsync(ct);
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT relative_path, length, modified_ticks, kind FROM media WHERE source_id=$s";
            cmd.Parameters.AddWithValue("$s", sourceId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var relPath = reader.GetString(0);
                catalogPaths.Add(relPath);
                var fullPath = SourcePaths.Combine(root, relPath);
                if (!File.Exists(fullPath))
                {
                    missingInCatalog.Add(new CachedMedia(sourceId, relPath, reader.GetInt64(1), reader.GetInt64(2), reader.GetString(3), false));
                }
            }
        }

        if (missingInCatalog.Count == 0)
        {
            return new ReconciliationReport(sourceId, Array.Empty<ReconciledMove>());
        }

        // 2. Discover untracked files currently on disk while honoring active exclusions
        var diskFiles = new List<(string RelativePath, long Length, long ModifiedTicks, string Kind)>();
        try
        {
            var dirInfo = new DirectoryInfo(root);
            foreach (var file in dirInfo.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(root, file.FullName);
                if (exclusions.Any(e => e.Matches(sourceId, rel))) continue;
                if (!catalogPaths.Contains(rel) && Extensions.Contains(file.Extension))
                {
                    diskFiles.Add((rel, file.Length, file.LastWriteTimeUtc.Ticks, Videos.Contains(file.Extension) ? "video" : "photo"));
                }
            }
        }
        catch { }

        if (diskFiles.Count == 0)
        {
            return new ReconciliationReport(sourceId, Array.Empty<ReconciledMove>());
        }

        // 3. Match missing catalog entries with untracked disk files by size and modified timestamp
        var reconciled = new List<ReconciledMove>();
        var remainingDiskFiles = diskFiles.ToList();

        foreach (var missing in missingInCatalog)
        {
            ct.ThrowIfCancellationRequested();
            // Look for unique match on length and modified timestamp (within 2 seconds tolerance for filesystem differences)
            var matches = remainingDiskFiles.Where(d => d.Length == missing.Length && Math.Abs(d.ModifiedTicks - missing.ModifiedTicks) < 20_000_000L).ToArray();

            // If multiple match, prefer same filename
            (string RelativePath, long Length, long ModifiedTicks, string Kind) match;
            if (matches.Length == 1)
            {
                match = matches[0];
            }
            else if (matches.Length > 1)
            {
                var sameName = matches.FirstOrDefault(m => string.Equals(Path.GetFileName(m.RelativePath), Path.GetFileName(missing.RelativePath), StringComparison.OrdinalIgnoreCase));
                if (sameName.RelativePath is not null) match = sameName;
                else continue;
            }
            else
            {
                continue;
            }

            remainingDiskFiles.Remove(match);
            var targetMedia = missing with
            {
                RelativePath = match.RelativePath,
                Length = match.Length,
                ModifiedTicks = match.ModifiedTicks,
                Kind = match.Kind,
                IsAvailable = true
            };

            await RelocateAsync(missing, targetMedia, ct);
            reconciled.Add(new ReconciledMove(sourceId, missing.RelativePath, match.RelativePath, match.Length));
        }

        return new ReconciliationReport(sourceId, reconciled);
    }
}

