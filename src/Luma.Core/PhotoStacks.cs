using System.Globalization;

namespace Luma.Core;

public sealed record PhotoStackGroup(LibraryItem Cover, IReadOnlyList<LibraryItem> Items, string Kind)
{
    public bool IsStack => Items.Count > 1;
}

public static class PhotoStackBuilder
{
    private static readonly HashSet<string> EverydayPhotoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".heic", ".heif", ".webp" };

    private static readonly HashSet<string> RawPhotoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".tif", ".tiff" };

    private static long EffectiveTicks(LibraryItem item) => item.Metadata?.TakenTicks ?? item.Media.ModifiedTicks;

    public static bool AreSameStemPair(CachedMedia a, CachedMedia b)
    {
        if (!string.Equals(a.SourceId, b.SourceId, StringComparison.OrdinalIgnoreCase)) return false;
        if (a.Kind != "photo" || b.Kind != "photo") return false;
        var dirA = Path.GetDirectoryName(a.RelativePath) ?? "";
        var dirB = Path.GetDirectoryName(b.RelativePath) ?? "";
        if (!string.Equals(dirA, dirB, StringComparison.OrdinalIgnoreCase)) return false;
        var stemA = Path.GetFileNameWithoutExtension(a.RelativePath);
        var stemB = Path.GetFileNameWithoutExtension(b.RelativePath);
        if (!string.Equals(stemA, stemB, StringComparison.OrdinalIgnoreCase)) return false;
        var extA = Path.GetExtension(a.RelativePath);
        var extB = Path.GetExtension(b.RelativePath);
        if (string.Equals(extA, extB, StringComparison.OrdinalIgnoreCase)) return false;
        return (EverydayPhotoExt.Contains(extA) && RawPhotoExt.Contains(extB)) ||
               (EverydayPhotoExt.Contains(extB) && RawPhotoExt.Contains(extA));
    }

    public static IReadOnlyList<PhotoStackGroup> Build(
        IReadOnlyList<LibraryItem> items,
        bool allowBurstClustering = true,
        int burstSeconds = 30,
        int maxStackSize = 25)
    {
        if (items.Count == 0) return Array.Empty<PhotoStackGroup>();

        var result = new List<PhotoStackGroup>(items.Count);
        var burstWindowTicks = TimeSpan.FromSeconds(Math.Max(1, burstSeconds)).Ticks;

        // First pass: merge adjacent or nearby same-stem RAW+JPEG pairs regardless of exact position
        var consumed = new bool[items.Count];
        var stemIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < items.Count; i++)
        {
            var m = items[i].Media;
            var key = $"{m.SourceId}\n{Path.GetDirectoryName(m.RelativePath)}\n{Path.GetFileNameWithoutExtension(m.RelativePath)}";
            if (stemIndex.TryGetValue(key, out int existingIdx) && AreSameStemPair(items[existingIdx].Media, m))
            {
                // Group pair at first occurrence index if not already merged
                continue;
            }
            stemIndex[key] = i;
        }

        // Build contiguous stacks cleanly preserving sort order
        var currentGroup = new List<LibraryItem>();
        string currentKind = "Single";

        void Flush()
        {
            if (currentGroup.Count == 0) return;
            var cover = SelectCover(currentGroup);
            result.Add(new PhotoStackGroup(cover, currentGroup.ToArray(), currentGroup.Count > 1 ? currentKind : "Single"));
            currentGroup.Clear();
            currentKind = "Single";
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (consumed[i]) continue;
            var current = items[i];

            if (currentGroup.Count == 0)
            {
                currentGroup.Add(current);
                consumed[i] = true;

                // Look ahead for a same-stem RAW+JPEG partner nearby (within 5 items)
                for (int j = i + 1; j < Math.Min(items.Count, i + 6); j++)
                {
                    if (!consumed[j] && AreSameStemPair(current.Media, items[j].Media))
                    {
                        currentGroup.Add(items[j]);
                        consumed[j] = true;
                        currentKind = "RAW+JPEG";
                    }
                }
                continue;
            }

            var anchor = currentGroup[^1];
            bool isPair = AreSameStemPair(anchor.Media, current.Media);
            bool isBurst = allowBurstClustering
                && current.Media.Kind == "photo"
                && anchor.Media.Kind == "photo"
                && currentGroup.Count < maxStackSize
                && Math.Abs(EffectiveTicks(current) - EffectiveTicks(anchor)) <= burstWindowTicks;

            if (isPair || isBurst)
            {
                currentGroup.Add(current);
                consumed[i] = true;
                currentKind = isPair && currentKind == "Single" ? "RAW+JPEG" : "Burst";
            }
            else
            {
                Flush();
                currentGroup.Add(current);
                consumed[i] = true;

                for (int j = i + 1; j < Math.Min(items.Count, i + 6); j++)
                {
                    if (!consumed[j] && AreSameStemPair(current.Media, items[j].Media))
                    {
                        currentGroup.Add(items[j]);
                        consumed[j] = true;
                        currentKind = "RAW+JPEG";
                    }
                }
            }
        }

        Flush();
        return result;
    }

    private static LibraryItem SelectCover(List<LibraryItem> items)
    {
        if (items.Count == 1) return items[0];
        // Prefer favorited items first, then everyday photo format (JPEG/PNG/HEIC) for crisp decode
        return items
            .OrderByDescending(i => i.Favorite)
            .ThenByDescending(i => EverydayPhotoExt.Contains(Path.GetExtension(i.Media.RelativePath)))
            .First();
    }
}

public static class BatchRenameFormatter
{
    public static string FormatName(string pattern, LibraryItem item, int sequence1Based, int totalCount)
    {
        var ext = Path.GetExtension(item.Media.RelativePath);
        var origStem = Path.GetFileNameWithoutExtension(item.Media.RelativePath);
        var ticks = item.Metadata?.TakenTicks ?? item.Media.ModifiedTicks;
        var local = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime();

        var padWidth = Math.Max(3, totalCount.ToString(CultureInfo.InvariantCulture).Length);
        var seqStr = sequence1Based.ToString(CultureInfo.InvariantCulture).PadLeft(padWidth, '0');

        var stem = string.IsNullOrWhiteSpace(pattern) ? "{date}_{orig}" : pattern.Trim();
        stem = stem
            .Replace("{date}", local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{time}", local.ToString("HHmmss", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{year}", local.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{month}", local.ToString("MM", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{day}", local.ToString("dd", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{orig}", origStem, StringComparison.OrdinalIgnoreCase)
            .Replace("{seq}", seqStr, StringComparison.OrdinalIgnoreCase);

        foreach (var bad in Path.GetInvalidFileNameChars())
            stem = stem.Replace(bad, '_');

        stem = stem.Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(stem)) stem = $"{origStem}_{seqStr}";
        return stem + ext;
    }
}

