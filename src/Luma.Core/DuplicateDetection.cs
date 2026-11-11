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
        await using var db = CreateConnection();
        await db.OpenAsync(ct);

        // 1. Find candidate lengths with multiple items
        var candidateLengths = new List<long>();
        await using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT length FROM media m WHERE ($s IS NULL OR m.source_id=$s) AND length > 1024 AND " + AllowedMediaSql + " GROUP BY length HAVING COUNT(*) > 1 ORDER BY length DESC LIMIT 200";
            cmd.Parameters.AddWithValue("$s", (object?)sourceId ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                candidateLengths.Add(reader.GetInt64(0));
            }
        }

        if (candidateLengths.Count == 0) return Array.Empty<DuplicateGroup>();

        // 2. Fetch candidates
        var candidatesByLength = new Dictionary<long, List<CachedMedia>>();
        foreach (var len in candidateLengths)
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT source_id, relative_path, length, modified_ticks, kind FROM media m WHERE ($s IS NULL OR m.source_id=$s) AND length=$len AND " + AllowedMediaSql;
            cmd.Parameters.AddWithValue("$s", (object?)sourceId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$len", len);
            var list = new List<CachedMedia>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sId = reader.GetString(0);
                var rPath = reader.GetString(1);
                var root = sourceResolver?.ResolveRoot(sId);
                var available = root is not null && File.Exists(SourcePaths.Combine(root, rPath));
                list.Add(new CachedMedia(sId, rPath, reader.GetInt64(2), reader.GetInt64(3), reader.GetString(4), available));
            }
            if (list.Count > 1)
            {
                candidatesByLength[len] = list;
            }
        }

        // 3. Hash candidates on connected sources
        var groups = new List<DuplicateGroup>();
        foreach (var (len, items) in candidatesByLength)
        {
            ct.ThrowIfCancellationRequested();
            var hashed = new Dictionary<string, List<CachedMedia>>(StringComparer.Ordinal);

            foreach (var item in items)
            {
                var root = sourceResolver?.ResolveRoot(item.SourceId);
                if (root is null) continue;
                var fullPath = SourcePaths.Combine(root, item.RelativePath);
                if (!File.Exists(fullPath)) continue;

                var hash = await ComputeFastHashAsync(fullPath, len, ct);
                if (hash is null) continue;

                if (!hashed.TryGetValue(hash, out var groupList))
                {
                    groupList = new List<CachedMedia>();
                    hashed[hash] = groupList;
                }
                groupList.Add(item);
            }

            foreach (var (hash, matchingItems) in hashed)
            {
                if (matchingItems.Count > 1)
                {
                    groups.Add(new DuplicateGroup(len, hash, matchingItems));
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
            using var sha = SHA256.Create();

            // For small files (< 4 MB), hash entire file
            if (length <= 4 * 1024 * 1024)
            {
                var fullHash = await sha.ComputeHashAsync(stream, ct);
                return Convert.ToHexString(fullHash).ToLowerInvariant();
            }

            // For larger files, sample head (64 KB) and tail (64 KB)
            var buffer = new byte[128 * 1024];
            int readHead = await stream.ReadAsync(buffer.AsMemory(0, 64 * 1024), ct);
            if (length > 128 * 1024)
            {
                stream.Seek(-64 * 1024, SeekOrigin.End);
                int readTail = await stream.ReadAsync(buffer.AsMemory(64 * 1024, 64 * 1024), ct);
            }
            var sampleHash = sha.ComputeHash(buffer, 0, Math.Min((int)length, buffer.Length));
            return $"{length}_{Convert.ToHexString(sampleHash).ToLowerInvariant()}";
        }
        catch
        {
            return null;
        }
    }
}
