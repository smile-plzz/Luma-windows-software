using System.ComponentModel;
using System.Collections.ObjectModel;
using Luma.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Luma.App;

public sealed class MediaCard : INotifyPropertyChanged
{
    private readonly AppSettings settings;
    private readonly DateOnly? memoryDay;
    private bool discarded;

    public MediaCard(LibraryItem item, AppSettings settings, DateOnly? memoryDay = null, IReadOnlyList<LibraryItem>? stackItems = null, string stackKind = "Single")
    {
        Item = item;
        this.settings = settings;
        this.memoryDay = memoryDay;
        StackItems = stackItems is { Count: > 1 } ? stackItems : [item];
        StackKind = stackKind;
        adaptiveWidth = settings.ThumbnailSize;
        adaptiveHeight = settings.ThumbnailSize * 0.75;
    }

    public LibraryItem Item { get; private set; }
    public IReadOnlyList<LibraryItem> StackItems { get; }
    public string StackKind { get; }
    public bool IsStack => StackItems.Count > 1;
    public bool IsExpandedStack { get; set; }
    public Visibility StackVisibility => IsStack ? Visibility.Visible : Visibility.Collapsed;
    public string StackBadgeText => IsStack ? $"{StackItems.Count}" : "";

    public void SetAvailability(bool online)
    {
        if (Item.Media.IsAvailable == online) return;
        Item = Item with { Media = Item.Media with { IsAvailable = online } };
        Changed(nameof(Item)); Changed(nameof(State)); Changed(nameof(StatusVisibility)); Changed(nameof(TooltipText));
    }

    public void UpdateItem(LibraryItem updated)
    {
        Item = updated;
        Changed(nameof(Item));
        Changed(nameof(State));
        Changed(nameof(StatusVisibility));
        Changed(nameof(FavoriteGlyph));
        Changed(nameof(TooltipText));
    }

    public void Discard()
    {
        discarded = true;
        Realized = false;
        Thumbnail = null;
        ThumbnailRequest?.Cancel();
        ThumbnailRequest = null;
        Changed(nameof(Name));
        Changed(nameof(TooltipText));
    }

    public string Name => discarded ? "" : Path.GetFileName(Item.Media.RelativePath);

    // Prefer capture date when available unless explicitly sorting by Date Modified
    public DateTime Date => new DateTime(
        settings.SortIndex is 0 or 1
            ? Item.Media.ModifiedTicks
            : Item.Metadata?.TakenTicks ?? Item.Media.ModifiedTicks,
        DateTimeKind.Utc).ToLocalTime();

    public string ExactDateText => Date.ToString("yyyy-MM-dd HH:mm:ss");

    public string Group
    {
        get
        {
            if (memoryDay is not null)
                return $"{Date:yyyy} · {memoryDay.Value.Year - Date.Year} {(memoryDay.Value.Year - Date.Year == 1 ? "year" : "years")} ago";
            return settings.GroupIndex switch
            {
                1 => Date.ToString("yyyy"),
                3 => Date.ToString("dddd, d MMMM yyyy"),
                4 => $"{Date:dddd, d MMMM yyyy} · {SessionLabel(Date.Hour)}",
                _ => Date.ToString("MMMM yyyy")
            };
        }
    }

    private static string SessionLabel(int hour) => hour switch
    {
        < 6 => "Night",
        < 12 => "Morning",
        < 17 => "Afternoon",
        < 21 => "Evening",
        _ => "Night"
    };

    public string Caption => settings.ShowExactTime || settings.GroupIndex >= 3
        ? $"{Date:dd MMM yyyy · HH:mm:ss} · {Item.Media.Length / 1024d:N0} KB"
        : $"{Date:dd MMM yyyy} · {Item.Media.Length / 1024d:N0} KB";

    public string TooltipText
    {
        get
        {
            var meta = Item.Metadata;
            var lines = new List<string> { $"{Name} ({Item.Media.Length / 1024d:N0} KB)" };
            var timeKind = meta?.TakenTicks is not null && settings.SortIndex is not (0 or 1) ? "Taken" : "Modified";
            lines.Add($"{timeKind}: {ExactDateText}");
            if (meta is not null)
            {
                var specs = new List<string>();
                if (meta.Width > 0 && meta.Height > 0) specs.Add($"{meta.Width} x {meta.Height}");
                if (meta.DurationSeconds > 0) specs.Add(TimeSpan.FromSeconds(meta.DurationSeconds).ToString(@"mm\:ss"));
                if (!string.IsNullOrWhiteSpace(meta.CameraModel)) specs.Add(meta.CameraModel);
                if (meta.FNumber is double fn) specs.Add($"f/{fn:0.#}");
                if (meta.Iso is int iso) specs.Add($"ISO {iso}");
                if (!string.IsNullOrWhiteSpace(meta.LocationName)) specs.Add(meta.LocationName);
                if (specs.Count > 0) lines.Add(string.Join(" · ", specs));
                if (meta.Latitude is double lat && meta.Longitude is double lon)
                    lines.Add($"GPS: {lat:0.0000}, {lon:0.0000}" + (meta.LocationOrigin == "manual" ? " (Manual pin)" : ""));
            }
            if (!string.IsNullOrWhiteSpace(Item.Tags)) lines.Add($"Tags: {Item.Tags}");
            if (IsStack) lines.Add($"Stack of {StackItems.Count} ({StackKind}): actions apply to the cover photo until expanded");
            if (!Item.Media.IsAvailable) lines.Add("Offline preview");
            return string.Join("\n", lines);
        }
    }

    public string State => (Item.Favorite ? "★  " : "") + (Item.Media.Kind == "video" ? "VIDEO  " : "") + (Item.Media.IsAvailable ? "" : "Offline");
    public Visibility StatusVisibility => string.IsNullOrWhiteSpace(State) ? Visibility.Collapsed : Visibility.Visible;
    public string FavoriteGlyph => Item.Favorite ? "★" : "☆";

    public double TileWidth => settings.ThumbnailSize;
    public double TileHeight => settings.ThumbnailSize * 0.75;

    private double adaptiveWidth;
    private double adaptiveHeight;
    public double AdaptiveWidth { get => adaptiveWidth; set { adaptiveWidth = value; Changed(nameof(AdaptiveWidth)); } }
    public double AdaptiveHeight { get => adaptiveHeight; set { adaptiveHeight = value; Changed(nameof(AdaptiveHeight)); } }

    public double AspectRatio
    {
        get
        {
            if (Item.Metadata is { Width: > 0, Height: > 0 } m)
                return Math.Clamp((double)m.Width / m.Height, 0.55, 2.4);
            if (DecodedWidth > 0 && DecodedHeight > 0)
                return Math.Clamp((double)DecodedWidth / DecodedHeight, 0.55, 2.4);
            return 4.0 / 3.0;
        }
    }

    public int DecodedWidth { get; set; }
    public int DecodedHeight { get; set; }

    public Thickness Spacing => new(settings.Compact ? 2 : 5);
    public Visibility Labels => settings.ShowNames ? Visibility.Visible : Visibility.Collapsed;
    public Visibility Details => settings.ShowDetails ? Visibility.Visible : Visibility.Collapsed;
    public Stretch Fit => settings.LayoutMode != "Uniform" || settings.FitImages ? Stretch.Uniform : Stretch.UniformToFill;

    private bool isCut;
    public bool IsCut { get => isCut; set { isCut = value; Changed(nameof(CutOpacity)); } }
    public double CutOpacity => IsCut ? 0.45 : 1;

    private bool isHovered;
    public bool IsHovered { get => isHovered; set { isHovered = value; Changed(nameof(QuickActionVisibility)); } }
    public Visibility QuickActionVisibility => isHovered ? Visibility.Visible : Visibility.Collapsed;

    private bool isSelectedAdaptive;
    public bool IsSelectedAdaptive { get => isSelectedAdaptive; set { isSelectedAdaptive = value; Changed(nameof(AdaptiveSelectionThickness)); } }
    public Thickness AdaptiveSelectionThickness => isSelectedAdaptive ? new Thickness(2.5) : new Thickness(0);

    public Visibility PlaceholderVisibility => Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
    public bool Realized { get; set; }
    public CancellationTokenSource? ThumbnailRequest { get; set; }
    public BitmapImage? Thumbnail { get => thumbnail; set { thumbnail = value; Changed(nameof(Thumbnail)); Changed(nameof(PlaceholderVisibility)); } }
    private BitmapImage? thumbnail;

    public void UpdateView()
    {
        foreach (var name in new[]
        {
            nameof(TileWidth), nameof(TileHeight), nameof(Spacing),
            nameof(Labels), nameof(Details), nameof(Fit), nameof(Caption), nameof(TooltipText)
        }) Changed(name);
    }

    private void Changed(string name) => PropertyChanged?.Invoke(this, new(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class MediaGroup(string key) : ObservableCollection<MediaCard> { public string Key { get; } = key; }
public sealed record SourceChoice(string? Id, string Label) { public override string ToString() => Label; }
public sealed record FolderBreadcrumb(string Label, string RelativePath);
