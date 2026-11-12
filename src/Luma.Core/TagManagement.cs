using Microsoft.Data.Sqlite;

namespace Luma.Core;

public sealed record TagCount(string Tag, int Count)
{
    public override string ToString() => $"{Tag} ({Count})";
}

public sealed partial class MediaCache
{
    public async Task<IReadOnlyList<TagCount>> GetTagsWithCountsAsync(CancellationToken ct = default)
    {
        await using var db = CreateConnection();
        await db.OpenAsync(ct);
        await using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT tags FROM annotations WHERE tags != '' AND tags IS NOT NULL";

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var raw = reader.GetString(0);
            var parts = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                counts[part] = counts.GetValueOrDefault(part, 0) + 1;
            }
        }

        return counts
            .Select(kv => new TagCount(kv.Key, kv.Value))
            .OrderByDescending(tc => tc.Count)
            .ThenBy(tc => tc.Tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task RenameTagAsync(string oldTag, string newTag, CancellationToken ct = default)
    {
        oldTag = oldTag.Trim();
        newTag = newTag.Trim();
        if (string.IsNullOrWhiteSpace(oldTag)) return;
        if (string.Equals(oldTag, newTag, StringComparison.OrdinalIgnoreCase)) return;

        await using var db = await OpenConfiguredConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);

        var itemsToUpdate = new List<(string SourceId, string RelativePath, string NewTags)>();
        await using (var selectCmd = db.CreateCommand())
        {
            selectCmd.Transaction = tx;
            selectCmd.CommandText = "SELECT source_id, relative_path, tags FROM annotations WHERE tags != '' AND tags IS NOT NULL";
            await using var reader = await selectCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sId = reader.GetString(0);
                var rPath = reader.GetString(1);
                var raw = reader.GetString(2);

                var parts = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(p => string.Equals(p, oldTag, StringComparison.OrdinalIgnoreCase) ? newTag : p)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var updated = string.Join(", ", parts);
                if (updated != raw)
                {
                    itemsToUpdate.Add((sId, rPath, updated));
                }
            }
        }

        if (itemsToUpdate.Count > 0)
        {
            await using var updateCmd = db.CreateCommand();
            updateCmd.Transaction = tx;
            updateCmd.CommandText = "UPDATE annotations SET tags=$tags WHERE source_id=$source AND relative_path=$path";
            var pTags = updateCmd.Parameters.Add("$tags", SqliteType.Text);
            var pSource = updateCmd.Parameters.Add("$source", SqliteType.Text);
            var pPath = updateCmd.Parameters.Add("$path", SqliteType.Text);
            updateCmd.Prepare();
            foreach (var (sId, rPath, updatedTags) in itemsToUpdate)
            {
                pTags.Value = updatedTags;
                pSource.Value = sId;
                pPath.Value = rPath;
                await updateCmd.ExecuteNonQueryAsync(ct);
            }
        }

        await tx.CommitAsync(ct);
    }

    public Task DeleteTagAsync(string tag, CancellationToken ct = default) => RenameTagAsync(tag, "", ct);

    public async Task BulkTagAsync(IEnumerable<CachedMedia> media, IEnumerable<string> addTags, IEnumerable<string> removeTags, CancellationToken ct = default)
    {
        var toAdd = addTags.Select(t => t.Trim()).Where(t => t.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toRemove = removeTags.Select(t => t.Trim()).Where(t => t.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (toAdd.Count == 0 && toRemove.Count == 0) return;

        await using var db = await OpenConfiguredConnectionAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);

        await using var getCmd = db.CreateCommand();
        getCmd.Transaction = tx;
        getCmd.CommandText = "SELECT favorite, tags FROM annotations WHERE source_id=$s AND relative_path=$p";
        var gSource = getCmd.Parameters.Add("$s", SqliteType.Text);
        var gPath = getCmd.Parameters.Add("$p", SqliteType.Text);
        getCmd.Prepare();

        await using var setCmd = db.CreateCommand();
        setCmd.Transaction = tx;
        setCmd.CommandText = """
            INSERT INTO annotations(source_id, relative_path, favorite, tags)
            VALUES($s, $p, $f, $t)
            ON CONFLICT(source_id, relative_path) DO UPDATE SET tags=$t;
            """;
        var sSource = setCmd.Parameters.Add("$s", SqliteType.Text);
        var sPath = setCmd.Parameters.Add("$p", SqliteType.Text);
        var sFav = setCmd.Parameters.Add("$f", SqliteType.Integer);
        var sTags = setCmd.Parameters.Add("$t", SqliteType.Text);
        setCmd.Prepare();

        char[] separators = [',', ';'];
        foreach (var item in media)
        {
            string currentTags = "";
            bool favorite = false;
            gSource.Value = item.SourceId;
            gPath.Value = item.RelativePath;
            await using (var reader = await getCmd.ExecuteReaderAsync(ct))
            {
                if (await reader.ReadAsync(ct))
                {
                    favorite = reader.GetInt32(0) == 1;
                    currentTags = reader.GetString(1);
                }
            }

            var parts = currentTags.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(t => !toRemove.Contains(t))
                .Concat(toAdd)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var newTags = string.Join(", ", parts);

            sSource.Value = item.SourceId;
            sPath.Value = item.RelativePath;
            sFav.Value = favorite ? 1 : 0;
            sTags.Value = newTags;
            await setCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }
}

