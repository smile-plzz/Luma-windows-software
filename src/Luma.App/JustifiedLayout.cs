using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;

namespace Luma.App;

public sealed class GalleryRow
{
    public string? GroupHeader { get; init; }
    public Visibility HeaderVisibility => string.IsNullOrEmpty(GroupHeader) ? Visibility.Collapsed : Visibility.Visible;
    public IReadOnlyList<MediaCard> Items { get; init; } = Array.Empty<MediaCard>();
}

public static class JustifiedRowBuilder
{
    public static IReadOnlyList<GalleryRow> Build(
        IReadOnlyList<MediaCard> cards,
        double viewportWidth,
        double baseSize,
        bool compact,
        bool grouped,
        bool relevantSizing)
    {
        if (cards.Count == 0) return Array.Empty<GalleryRow>();

        var rows = new List<GalleryRow>();
        var widthBudget = Math.Max(360, viewportWidth - 20);
        var gap = compact ? 4.0 : 10.0;
        var targetHeight = Math.Clamp(baseSize * 0.92, 110, 310);

        var currentItems = new List<MediaCard>();
        double aspectSum = 0;
        string? pendingHeader = grouped ? cards[0].Group : null;
        string? lastGroup = grouped ? cards[0].Group : null;

        void Flush(bool justify)
        {
            if (currentItems.Count == 0) return;
            var totalGaps = Math.Max(0, currentItems.Count - 1) * gap;
            double rowHeight = targetHeight;

            if (justify && aspectSum > 0)
            {
                rowHeight = (widthBudget - totalGaps) / aspectSum;
                rowHeight = Math.Clamp(rowHeight, targetHeight * 0.65, targetHeight * 1.45);
            }
            else if (relevantSizing && currentItems.Any(c => c.Item.Favorite || c.IsStack) && currentItems.Count <= 3)
            {
                rowHeight = Math.Min(targetHeight * 1.25, 320);
            }

            // Compute exact widths while clamping within remaining budget
            double used = 0;
            for (int i = 0; i < currentItems.Count; i++)
            {
                var c = currentItems[i];
                var weight = relevantSizing && (c.Item.Favorite || c.IsStack) ? 1.28 : 1.0;
                var w = Math.Max(64, Math.Floor(c.AspectRatio * weight * rowHeight));
                if (justify && i == currentItems.Count - 1)
                {
                    var remaining = Math.Floor(widthBudget - totalGaps - used);
                    if (remaining > 60 && Math.Abs(remaining - w) < 40) w = remaining;
                }
                c.AdaptiveWidth = w;
                c.AdaptiveHeight = Math.Floor(rowHeight);
                used += w;
            }

            rows.Add(new GalleryRow
            {
                GroupHeader = pendingHeader,
                Items = currentItems.ToArray()
            });
            currentItems.Clear();
            aspectSum = 0;
            pendingHeader = null;
        }

        foreach (var card in cards)
        {
            if (grouped && !string.Equals(card.Group, lastGroup, StringComparison.Ordinal))
            {
                Flush(justify: false);
                lastGroup = card.Group;
                pendingHeader = card.Group;
            }

            var weight = relevantSizing && (card.Item.Favorite || card.IsStack) ? 1.28 : 1.0;
            var effectiveAspect = card.AspectRatio * weight;
            currentItems.Add(card);
            aspectSum += effectiveAspect;

            var totalGaps = Math.Max(0, currentItems.Count - 1) * gap;
            var estimatedWidth = aspectSum * targetHeight + totalGaps;

            if (estimatedWidth >= widthBudget || currentItems.Count >= 6)
            {
                Flush(justify: true);
            }
        }

        Flush(justify: false);
        return rows;
    }
}

