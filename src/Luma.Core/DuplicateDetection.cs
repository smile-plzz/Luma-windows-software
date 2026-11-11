using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Luma.Core;

public sealed record DuplicateGroup(long FileLength, string Hash, IReadOnlyList<CachedMedia> Items)
{
    public string FormattedSize => FileLength switch
    {
        >= 1024 * 1024 * 1024 => $"{FileLength / (1024.0 * 1024 * 1024):N2} GB",
        >= 1024 * 1024 => $"{FileLength / (1024.0 * 1024):N1} MB",
        _ => $"{FileLength / 1024.0:N0} KB"
    };
}

public sealed partial class MediaCache
{
    public async Task<IReadOnlyList<DuplicateGroup>> FindDuplicatesAsync(string? sourceId = null, CancellationToken ct = default)
    {
        await using var db = await OpenConfiguredConnectionAsync(ct);
        bool hasExclusions = await HasExclusionsAsync(db, ct: ct);

        // Single joined query avoids up to 200 N+1 round-trips for candidate lengths.
        var candidatesByLength = new Dictionary<long, List<(CachedMedia Media, string FullPath)>>();
        var resolvedRoots = new Dictionary<string, string?>(StringComparer.Ordinal);
        await using (var cmd = db.CreateCommand())
        {
            var sourcePred = sourceId is null ? "" : " AND m.source_id=$s";
            var exclPred = hasExclusions ? " AND " + AllowedMediaSql : "";
            cmd.CommandText = $"""
                WITH dup_lengths AS (
                    SELECT length FROM media m
                    WHERE length > 1024{sourcePred}{exclPred}
                    GROUP BY length HAVING COUNT(*) > 1
                    ORDER BY length DESC LIMIT 200
                )
                SELECT m.source_id, m.relative_path, m.length, m.modified_ticks, m.kind
                FROM media m
                JOIN dup_lengths d ON d.length = m.length
                WHERE m.length > 1024{sourcePred}{exclPred}
                ORDER BY m.length DESC, m.source_id, m.relative_path
                """;
            if (sourceId is not null) cmd.Parameters.AddWithValue("$s", sourceId);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sId = reader.GetString(0);
                var rPath = reader.GetString(1);
                if (!resolvedRoots.TryGetValue(sId, out var root))
                    resolvedRoots[sId] = root = sourceResolver?.ResolveRoot(sId);
                if (root is null) continue;

                var len = reader.GetInt64(2);
                var fullPath = SourcePaths.Combine(root, rPath);
                var media = new CachedMedia(sId, rPath, len, reader.GetInt64(3), reader.GetString(4), true);
                if (!candidatesByLength.TryGetValue(len, out var list))
                {
                    list = new List<(CachedMedia, string)>();
                    candidatesByLength[len] = list;
                }
                list.Add((media, fullPath));
            }
        }

        var workItems = new List<(long Length, CachedMedia Media, string FullPath)>();
        foreach (var (len, items) in candidatesByLength)
        {
            if (items.Count > 1)
            {
                foreach (var (media, fullPath) in items)
                    workItems.Add((len, media, fullPath));
            }
        }
        if (workItems.Count == 0) return Array.Empty<DuplicateGroup>();

        // Hash connected candidate files with bounded concurrency and shared per-bucket clustering.
        var hashedByLength = new System.Collections.Concurrent.ConcurrentDictionary<long, System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<CachedMedia>>>();
        int parallelism = Math.Clamp(Environment.ProcessorCount - 1, 2, 4);
        await Parallel.ForEachAsync(
            workItems,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct },
            async (entry, token) =>
            {
                var hash = await ComputeFastHashAsync(entry.FullPath, entry.Length, token);
                if (hash is null) return;
                var lenMap = hashedByLength.GetOrAdd(entry.Length, _ => new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<CachedMedia>>(StringComparer.Ordinal));
                var bag = lenMap.GetOrAdd(hash, _ => new System.Collections.Concurrent.ConcurrentBag<CachedMedia>());
                bag.Add(entry.Media);
            });

        var groups = new List<DuplicateGroup>();
        foreach (var (len, byHash) in hashedByLength)
        {
            foreach (var (hash, matchingItems) in byHash)
            {
                if (matchingItems.Count > 1)
                {
                    groups.Add(new DuplicateGroup(
                        len,
                        hash,
                        matchingItems.OrderBy(m => m.SourceId, StringComparer.Ordinal).ThenBy(m => m.RelativePath, StringComparer.Ordinal).ToList()));
                }
            }
        }

        return groups.OrderByDescending(g => g.FileLength * g.Items.Count).ToArray();
    }

    private static async Task<string?> ComputeFastHashAsync(string path, long length, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);

            // For small files (<= 4 MB), hash entire file
            if (length <= 4 * 1024 * 1024)
            {
                var fullHash = await SHA256.HashDataAsync(stream, ct);
                return ToLowerHex(fullHash);
            }

            // For larger files, sample head (64 KB) and tail (64 KB)
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
            try
            {
                int readHead = await stream.ReadAsync(buffer.AsMemory(0, 64 * 1024), ct);
                int totalRead = readHead;
                if (length > 128 * 1024)
                {
                    stream.Seek(-64 * 1024, SeekOrigin.End);
                    int readTail = await stream.ReadAsync(buffer.AsMemory(readHead, 64 * 1024), ct);
                    totalRead += readTail;
                }
                else
                {
                    int rest = await stream.ReadAsync(buffer.AsMemory(readHead, 64 * 1024), ct);
                    totalRead += rest;
                }
                return HashSample(buffer, totalRead, length);
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            return null;
        }
    }

    private static string HashSample(byte[] buffer, int count, long totalLength)
    {
        Span<byte> sampleHash = stackalloc byte[32];
        SHA256.HashData(buffer.AsSpan(0, count), sampleHash);
        return $"{totalLength}_{ToLowerHex(sampleHash)}";
    }
}

