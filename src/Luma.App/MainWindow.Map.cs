using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Luma.Core;
using Luma.Windows;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Windows.Storage.Streams;
using Windows.UI;

using Line = Microsoft.UI.Xaml.Shapes.Line;
using Polygon = Microsoft.UI.Xaml.Shapes.Polygon;
using Polyline = Microsoft.UI.Xaml.Shapes.Polyline;
using WinPoint = global::Windows.Foundation.Point;

namespace Luma.App;

public sealed record OfflineGeoCluster(
    string Title,
    string Subtitle,
    double CenterLat,
    double CenterLon,
    double MinLat,
    double MaxLat,
    double MinLon,
    double MaxLon,
    int Count = 0);

public sealed record MapDrawerMediaRow(
    CachedMedia Media,
    string Title,
    string Subtitle,
    string LocationName,
    bool Favorite);

public sealed record MapFilterOption(string Label, string? Value)
{
    public override string ToString() => Label;
}

public sealed class MapStripItem : INotifyPropertyChanged
{
    private BitmapImage? thumbnail;
    public required GeoMarker Marker { get; init; }
    public required string Label { get; init; }
    public required string Tooltip { get; init; }
    public BitmapImage? Thumbnail
    {
        get => thumbnail;
        set
        {
            if (ReferenceEquals(thumbnail, value)) return;
            thumbnail = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class MainWindow
{
    private bool mapOnlineConsentGranted;
    private WebView2? mapWebView;
    private bool mapWebReady;
    private IReadOnlyList<GeoMarker> currentMapMarkers = Array.Empty<GeoMarker>();
    private (double Lat, double Lon)? pendingMapFocus;

    private bool mapFiltersInitializing;
    private string? mapSelectedKind;
    private string mapViewMode = "clusters";
    private string mapDrawerTab = "clusters";
    private bool mapPinMode;
    private CachedMedia[] pendingPinMedia = Array.Empty<CachedMedia>();

    private double offlineCenterLat = 20.0;
    private double offlineCenterLon = 0.0;
    private double offlineZoom = 1.6;
    private bool offlineDragging;
    private double offlineDragDistance;
    private WinPoint offlineLastPoint;

    private CancellationTokenSource? mapBackfillCts;
    private bool mapBackfillRunning;
    private CancellationTokenSource? mapStripThumbCts;
    private readonly ObservableCollection<MapStripItem> mapStripItems = new();

    private async void OpenMapView(object sender, RoutedEventArgs e)
    {
        pendingPinMedia = Array.Empty<CachedMedia>();
        mapPinMode = false;
        MapPinModeToggle.IsChecked = false;
        await Guard(() => OpenMapAsync(null));
    }

    private async void ShowSelectedOnMap(object sender, RoutedEventArgs e)
    {
        var card = PrimarySelectedCard() ?? contextCard;
        if (card?.Item.Metadata is { Latitude: double lat, Longitude: double lon })
        {
            pendingPinMedia = Array.Empty<CachedMedia>();
            mapPinMode = false;
            MapPinModeToggle.IsChecked = false;
            await Guard(() => OpenMapAsync((lat, lon)));
        }
    }

    private async void PreviewShowOnMap(object sender, RoutedEventArgs e)
    {
        if (previewingCard?.Item.Metadata is { Latitude: double lat, Longitude: double lon })
        {
            DoClosePreview();
            await Guard(() => OpenMapAsync((lat, lon)));
        }
    }

    private async void PinSelectedOnMap(object sender, RoutedEventArgs e)
    {
        var targets = GetSelectedCards().Select(c => c.Item.Media).ToArray();
        if (targets.Length == 0 && contextCard is not null) targets = [contextCard.Item.Media];
        if (targets.Length == 0)
        {
            Notify("Select one or more photos or videos first.");
            return;
        }
        await OpenMapForPinningAsync(targets);
    }

    private async Task OpenMapForPinningAsync(CachedMedia[] targets)
    {
        pendingPinMedia = targets;
        await OpenMapAsync(null);
        mapPinMode = true;
        MapPinModeToggle.IsChecked = true;
        UpdatePinModeVisuals();
    }

    private async Task OpenMapAsync((double Lat, double Lon)? focusCoords)
    {
        pendingMapFocus = focusCoords;
        MapOverlay.Visibility = Visibility.Visible;
        MapFilmstripList.ItemsSource = mapStripItems;

        await PopulateMapFilterDropdownsAsync();
        await RefreshMapDataAsync(fitViewport: !focusCoords.HasValue);

        if (focusCoords is { } focus)
        {
            offlineCenterLat = focus.Lat;
            offlineCenterLon = focus.Lon;
            offlineZoom = Math.Max(offlineZoom, 8.0);
            RenderOfflineCanvas();
        }

        if (mapOnlineConsentGranted)
        {
            await EnsureAndPushOnlineMapAsync();
        }
        else
        {
            MapOfflinePanel.Visibility = Visibility.Visible;
            MapWebHost.Visibility = Visibility.Collapsed;
            MapToggleModeButton.Visibility = Visibility.Visible;
        }

        _ = StartConnectedMetadataBackfillIfNeededAsync(forceRestart: false);
    }

    private async Task PopulateMapFilterDropdownsAsync()
    {
        mapFiltersInitializing = true;
        try
        {
            var baseScope = (activeQuery ?? CurrentQuery()) with { HasGpsOnly = false, MapBounds = null, Search = "" };
            var buckets = await Task.Run(() => catalog.MonthBucketsAsync(baseScope));
            var years = buckets
                .Select(b => b.Month.Length >= 4 ? b.Month[..4] : "")
                .Where(y => y.Length == 4)
                .Distinct()
                .OrderByDescending(y => y)
                .ToArray();

            var prevYear = (MapYearFilter.SelectedItem as MapFilterOption)?.Value;
            var yearItems = new List<MapFilterOption> { new("All years", null) };
            yearItems.AddRange(years.Select(y => new MapFilterOption(y, y)));
            MapYearFilter.ItemsSource = yearItems;
            MapYearFilter.SelectedIndex = prevYear is not null
                ? Math.Max(0, yearItems.FindIndex(o => o.Value == prevYear))
                : 0;

            var sources = await Task.Run(() => catalog.SourcesAsync());
            var labels = await Task.Run(() => catalog.SourceLabelsAsync());
            var prevSource = (MapSourceFilter.SelectedItem as MapFilterOption)?.Value ?? SelectedSource;
            var sourceItems = new List<MapFilterOption> { new("All sources", null) };
            foreach (var s in sources)
            {
                var label = labels.GetValueOrDefault(s.Id, Path.GetFileName(s.RootPath.TrimEnd('\\')));
                if (string.IsNullOrWhiteSpace(label)) label = s.RootPath;
                if (!s.IsOnline) label += " (offline)";
                sourceItems.Add(new(label, s.Id));
            }
            MapSourceFilter.ItemsSource = sourceItems;
            MapSourceFilter.SelectedIndex = prevSource is not null
                ? Math.Max(0, sourceItems.FindIndex(o => o.Value == prevSource))
                : 0;
        }
        finally
        {
            mapFiltersInitializing = false;
        }
    }

    private LibraryQuery BuildMapFilterQuery()
    {
        var baseQuery = (activeQuery ?? CurrentQuery()) with
        {
            MapBounds = null,
            HasGpsOnly = false,
            FromTicks = null,
            BeforeTicks = null,
            MemoriesOn = null,
            Folder = ""
        };

        if (mapSelectedKind is not null) baseQuery = baseQuery with { Kind = mapSelectedKind };
        if (MapFavFilter.IsChecked == true) baseQuery = baseQuery with { FavoritesOnly = true };

        if (MapYearFilter.SelectedItem is MapFilterOption { Value: string yr } && int.TryParse(yr, out var yNum))
        {
            var start = new DateTime(yNum, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks;
            var end = new DateTime(yNum, 12, 31, 23, 59, 59, DateTimeKind.Local).ToUniversalTime().Ticks;
            baseQuery = baseQuery with { FromTicks = start, BeforeTicks = end };
        }

        if (MapSourceFilter.SelectedItem is MapFilterOption { Value: string sid })
        {
            baseQuery = baseQuery with { SourceId = sid };
        }

        string? originFilter = MapOriginFilter.SelectedIndex switch
        {
            1 => "file",
            2 => "manual",
            _ => null
        };
        if (originFilter is not null) baseQuery = baseQuery with { LocationOriginFilter = originFilter };

        var mapSearch = MapSearchBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(mapSearch))
        {
            baseQuery = baseQuery with
            {
                Search = string.IsNullOrWhiteSpace(baseQuery.Search) ? mapSearch : $"{baseQuery.Search} {mapSearch}"
            };
        }

        return baseQuery;
    }

    private async Task RefreshMapDataAsync(bool fitViewport = true)
    {
        var query = BuildMapFilterQuery();
        var markersTask = Task.Run(() => catalog.QueryGeotaggedMarkersAsync(query, 25000));
        var summaryTask = Task.Run(() => catalog.QueryMapSummaryAsync(query));
        var placeTask = Task.Run(() => catalog.QueryPlaceTaggedWithoutCoordsAsync(query, 300));
        var unlocatedTask = Task.Run(() => catalog.QueryUnlocatedItemsAsync(query, 150));

        await Task.WhenAll(markersTask, summaryTask, placeTask, unlocatedTask);

        currentMapMarkers = markersTask.Result;
        var summary = summaryTask.Result;
        var placeTagged = placeTask.Result;
        var unlocated = unlocatedTask.Result;

        MapSummaryText.Text = summary.PlaceTagOnlyCount > 0
            ? $"{summary.GeotaggedCount:N0} mapped · {summary.PlaceTagOnlyCount:N0} place-tagged"
            : $"{summary.GeotaggedCount:N0} mapped";

        MapTabClusters.Content = $"Clusters ({currentMapMarkers.Count:N0})";
        MapTabPlaceTags.Content = $"Place tags ({summary.PlaceTagOnlyCount:N0})";
        MapTabUnlocated.Content = $"Unlocated ({summary.UnlocatedCount:N0})";

        PopulateOfflineGeoClusters(placeTagged, unlocated);

        if (fitViewport) FitOfflineViewport();
        else RenderOfflineCanvas();

        if (mapOnlineConsentGranted && mapWebReady)
        {
            PushMarkersToWebView();
        }
        else
        {
            var visible = GetMarkersInOfflineViewport().Take(24).ToArray();
            _ = RefreshMapFilmstripAsync(visible);
        }
    }

    private void PopulateOfflineGeoClusters(
        IReadOnlyList<PlaceTaggedItem> placeTagged,
        IReadOnlyList<LibraryItem> unlocated)
    {
        if (currentMapMarkers.Count == 0)
        {
            MapOfflineGroupsList.ItemsSource = Array.Empty<OfflineGeoCluster>();
        }
        else
        {
            var clusters = currentMapMarkers
                .GroupBy(m => (LatCell: Math.Round(m.Latitude * 50) / 50.0, LonCell: Math.Round(m.Longitude * 50) / 50.0))
                .OrderByDescending(g => g.Count())
                .Take(100)
                .Select(g =>
                {
                    var items = g.ToArray();
                    var avgLat = items.Average(x => x.Latitude);
                    var avgLon = items.Average(x => x.Longitude);
                    var minLat = items.Min(x => x.Latitude) - 0.01;
                    var maxLat = items.Max(x => x.Latitude) + 0.01;
                    var minLon = items.Min(x => x.Longitude) - 0.01;
                    var maxLon = items.Max(x => x.Longitude) + 0.01;
                    var ns = avgLat >= 0 ? "N" : "S";
                    var ew = avgLon >= 0 ? "E" : "W";
                    var sample = items.OrderByDescending(x => x.DateTicks).First();
                    var place = items.Select(x => x.LocationName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
                    var dateText = new DateTime(sample.DateTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy");
                    var title = string.IsNullOrWhiteSpace(place)
                        ? $"{Math.Abs(avgLat):0.000}° {ns}, {Math.Abs(avgLon):0.000}° {ew} · {items.Length:N0}"
                        : $"{place} · {items.Length:N0}";
                    return new OfflineGeoCluster(
                        title,
                        $"Latest: {sample.Name} ({dateText})",
                        avgLat, avgLon, minLat, maxLat, minLon, maxLon, items.Length);
                })
                .ToArray();
            MapOfflineGroupsList.ItemsSource = clusters;
        }

        MapPlaceTagsList.ItemsSource = placeTagged.Select(p => new MapDrawerMediaRow(
            p.Media,
            Path.GetFileName(p.Media.RelativePath),
            new DateTime(p.DateTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy HH:mm"),
            p.LocationName,
            p.Favorite)).ToArray();

        MapUnlocatedList.ItemsSource = unlocated.Select(u =>
        {
            var ticks = u.Metadata?.TakenTicks ?? u.Media.ModifiedTicks;
            var metaNote = u.Metadata?.CameraModel ?? u.Media.Kind;
            return new MapDrawerMediaRow(
                u.Media,
                Path.GetFileName(u.Media.RelativePath),
                $"{metaNote} · {new DateTime(ticks, DateTimeKind.Utc).ToLocalTime():dd MMM yyyy}",
                u.Metadata?.LocationName ?? "",
                u.Favorite);
        }).ToArray();

        UpdateDrawerEmptyState();
    }

    private void UpdateDrawerEmptyState()
    {
        MapOfflineGroupsList.Visibility = mapDrawerTab == "clusters" ? Visibility.Visible : Visibility.Collapsed;
        MapPlaceTagsList.Visibility = mapDrawerTab == "placeTags" ? Visibility.Visible : Visibility.Collapsed;
        MapUnlocatedList.Visibility = mapDrawerTab == "unlocated" ? Visibility.Visible : Visibility.Collapsed;

        int activeCount = mapDrawerTab switch
        {
            "clusters" => (MapOfflineGroupsList.ItemsSource as Array)?.Length ?? 0,
            "placeTags" => (MapPlaceTagsList.ItemsSource as Array)?.Length ?? 0,
            _ => (MapUnlocatedList.ItemsSource as Array)?.Length ?? 0
        };
        MapDrawerEmpty.Visibility = activeCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        MapDrawerEmpty.Text = mapDrawerTab switch
        {
            "clusters" => "No mapped items match these filters yet. Try Scan GPS or Pin mode to place items on the map.",
            "placeTags" => "No unlocated files with place tags match these filters.",
            _ => "All matching files already have map coordinates."
        };
    }

    private async Task StartConnectedMetadataBackfillIfNeededAsync(bool forceRestart)
    {
        if (scanning || (mapBackfillRunning && !forceRestart)) return;
        if (forceRestart)
        {
            mapBackfillCts?.Cancel();
        }

        var sources = await Task.Run(() => catalog.SourcesAsync());
        var onlineIds = sources.Where(s => s.IsOnline).Select(s => s.Id).ToArray();
        if (onlineIds.Length == 0) return;

        var pendingTotal = await Task.Run(() => catalog.CountPendingMetadataBackfillAsync(null, onlineIds));
        if (pendingTotal <= 0) return;

        mapBackfillCts?.Dispose();
        mapBackfillCts = new CancellationTokenSource();
        var ct = mapBackfillCts.Token;
        mapBackfillRunning = true;

        MapBackfillPill.Visibility = Visibility.Visible;
        MapBackfillRing.IsActive = true;
        MapBackfillText.Text = $"Scanning files for GPS (0 / {pendingTotal:N0})";

        try
        {
            long processedOffset = 0;
            int totalUpdated = 0;
            var refreshThrottle = Stopwatch.StartNew();
            foreach (var sid in onlineIds)
            {
                ct.ThrowIfCancellationRequested();
                long sourceCount = 0;
                var details = new Progress<MetadataProgress>(p =>
                {
                    if (p.Total > 0) sourceCount = p.Total;
                    var done = processedOffset + p.Checked;
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        MapBackfillText.Text = $"Scanning GPS ({Math.Min(done, pendingTotal):N0} / {pendingTotal:N0})";
                    });
                });
                var progress = new Progress<int>(savedCount =>
                {
                    if (refreshThrottle.ElapsedMilliseconds >= 2000)
                    {
                        refreshThrottle.Restart();
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (MapOverlay.Visibility == Visibility.Visible)
                                _ = RefreshMapDataAsync(fitViewport: currentMapMarkers.Count == 0);
                        });
                    }
                });

                totalUpdated += await Task.Run(() => catalog.EnrichAsync(new WindowsMetadataReader(), sid, ct, progress, details), ct);
                processedOffset += sourceCount;
            }

            if (MapOverlay.Visibility == Visibility.Visible)
            {
                await RefreshMapDataAsync(fitViewport: currentMapMarkers.Count == 0);
            }
            if (totalUpdated > 0)
            {
                Status.Text = $"Indexed location & EXIF metadata for {totalUpdated:N0} file(s)";
            }
        }
        catch (OperationCanceledException)
        {
            Notify("GPS scan paused. Partial results were saved.");
        }
        catch (Exception ex)
        {
            Notify($"GPS scan error: {ex.Message}", true);
        }
        finally
        {
            mapBackfillRunning = false;
            MapBackfillRing.IsActive = false;
            MapBackfillPill.Visibility = Visibility.Collapsed;
        }
    }

    private void PauseMapBackfill(object sender, RoutedEventArgs e)
    {
        mapBackfillCts?.Cancel();
    }

    private async void TriggerMapBackfill(object sender, RoutedEventArgs e)
    {
        await StartConnectedMetadataBackfillIfNeededAsync(forceRestart: true);
    }

    private void MapPinModeToggled(object sender, RoutedEventArgs e)
    {
        if (MapPinModeToggle is null || OfflinePinHint is null) return;
        mapPinMode = MapPinModeToggle.IsChecked == true;
        if (!mapPinMode) pendingPinMedia = Array.Empty<CachedMedia>();
        UpdatePinModeVisuals();
    }

    private void UpdatePinModeVisuals()
    {
        if (OfflinePinHint is null || OfflinePinHintText is null) return;
        var activeTargets = GetPinTargets();
        var hint = activeTargets.Length > 0
            ? $"Click map to pin {activeTargets.Length:N0} selected item(s) cleanly in catalog"
            : "Click map to enter coordinates, or select an item from Unlocated / Gallery";
        OfflinePinHintText.Text = hint;
        OfflinePinHint.Visibility = mapPinMode ? Visibility.Visible : Visibility.Collapsed;

        if (mapWebReady && mapWebView?.CoreWebView2 is not null)
        {
            mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setPinMode", enabled = mapPinMode }));
        }
    }

    private CachedMedia[] GetPinTargets()
    {
        if (pendingPinMedia.Length > 0) return pendingPinMedia;
        return GetSelectedCards().Select(c => c.Item.Media).ToArray();
    }

    private async Task HandlePickedCoordinateAsync(double lat, double lon)
    {
        var targets = GetPinTargets();
        if (targets.Length == 0)
        {
            mapPinMode = false;
            MapPinModeToggle.IsChecked = false;
            UpdatePinModeVisuals();
            Notify("Select items in the gallery or click 'Pin on map…' beside any item in Unlocated first.");
            return;
        }

        await ShowManualPinDialogAsync(targets, lat, lon, "");
    }

    private async Task ShowManualPinDialogAsync(CachedMedia[] targets, double? defaultLat, double? defaultLon, string defaultPlace)
    {
        var coordBox = new TextBox
        {
            Header = "Latitude, Longitude (e.g. 48.8566, 2.3522)",
            Text = defaultLat.HasValue && defaultLon.HasValue
                ? $"{defaultLat.Value.ToString("0.00000", CultureInfo.InvariantCulture)}, {defaultLon.Value.ToString("0.00000", CultureInfo.InvariantCulture)}"
                : "",
            PlaceholderText = "48.8566, 2.3522"
        };
        var placeBox = new TextBox
        {
            Header = "Optional place label",
            Text = defaultPlace,
            PlaceholderText = "Paris, France"
        };
        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Assigns coordinates to {targets.Length:N0} item(s) inside Luma's local catalog only. Original files are never modified.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.75
        });
        panel.Children.Add(coordBox);
        panel.Children.Add(placeBox);

        var dialog = new ContentDialog
        {
            RequestedTheme = Root.ActualTheme,
            XamlRoot = Root.XamlRoot,
            Title = "Pin location in catalog",
            Content = panel,
            PrimaryButtonText = "Save pin",
            SecondaryButtonText = "Clear pin",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;

        if (result == ContentDialogResult.Secondary)
        {
            await catalog.AssignLocationAsync(targets, null, null, null);
            pendingPinMedia = Array.Empty<CachedMedia>();
            mapPinMode = false;
            MapPinModeToggle.IsChecked = false;
            UpdatePinModeVisuals();
            await RefreshMapDataAsync(fitViewport: false);
            if (ready) await Refresh();
            Notify("Catalog location pin cleared.");
            return;
        }

        var parts = coordBox.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        double? parsedLat = null, parsedLon = null;
        if (parts.Length >= 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var la) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lo))
        {
            (parsedLat, parsedLon) = MinimalGpsExtractor.NormalizeCoords(la, lo);
        }

        if (parts.Length > 0 && (parsedLat is null || parsedLon is null) && string.IsNullOrWhiteSpace(placeBox.Text))
        {
            Notify("Enter valid latitude, longitude coordinates or a place label.", true);
            return;
        }

        await catalog.AssignLocationAsync(targets, parsedLat, parsedLon, placeBox.Text);
        pendingPinMedia = Array.Empty<CachedMedia>();
        mapPinMode = false;
        MapPinModeToggle.IsChecked = false;
        UpdatePinModeVisuals();
        if (parsedLat.HasValue && parsedLon.HasValue)
        {
            offlineCenterLat = parsedLat.Value;
            offlineCenterLon = parsedLon.Value;
            pendingMapFocus = (parsedLat.Value, parsedLon.Value);
        }
        await RefreshMapDataAsync(fitViewport: false);
        if (ready) await Refresh();
        Notify($"Saved catalog location for {targets.Length:N0} item(s).");
    }

    private void MapChromeToggled(object sender, RoutedEventArgs e)
    {
        if (MapFilmstripBar is null || MapDrawerPanel is null || MapStripToggle is null || MapDrawerToggle is null) return;
        MapFilmstripBar.Visibility = MapStripToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        MapDrawerPanel.Visibility = MapDrawerToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void MapKindFilterClicked(object sender, RoutedEventArgs e)
    {
        if (MapKindAll is null || MapKindPhotos is null || MapKindVideos is null) return;
        if (ReferenceEquals(sender, MapKindAll)) mapSelectedKind = null;
        else if (ReferenceEquals(sender, MapKindPhotos)) mapSelectedKind = "photo";
        else if (ReferenceEquals(sender, MapKindVideos)) mapSelectedKind = "video";

        MapKindAll.IsChecked = mapSelectedKind is null;
        MapKindPhotos.IsChecked = mapSelectedKind == "photo";
        MapKindVideos.IsChecked = mapSelectedKind == "video";
        if (MapOverlay?.Visibility == Visibility.Visible) await RefreshMapDataAsync(fitViewport: true);
    }

    private async void MapFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || mapFiltersInitializing || MapOverlay is null || MapOverlay.Visibility != Visibility.Visible) return;
        await RefreshMapDataAsync(fitViewport: true);
    }

    private async void MapComboFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || mapFiltersInitializing || MapOverlay is null || MapOverlay.Visibility != Visibility.Visible) return;
        await RefreshMapDataAsync(fitViewport: true);
    }

    private async void MapSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || mapFiltersInitializing || MapOverlay is null || MapOverlay.Visibility != Visibility.Visible) return;
        await RefreshMapDataAsync(fitViewport: false);
    }

    private async void MapResetFiltersClicked(object sender, RoutedEventArgs e)
    {
        mapFiltersInitializing = true;
        try
        {
            mapSelectedKind = null;
            MapKindAll.IsChecked = true;
            MapKindPhotos.IsChecked = false;
            MapKindVideos.IsChecked = false;
            MapFavFilter.IsChecked = false;
            MapYearFilter.SelectedIndex = 0;
            MapSourceFilter.SelectedIndex = 0;
            MapOriginFilter.SelectedIndex = 0;
            MapSearchBox.Text = "";
        }
        finally
        {
            mapFiltersInitializing = false;
        }
        await RefreshMapDataAsync(fitViewport: true);
    }

    private void MapViewModeClicked(object sender, RoutedEventArgs e)
    {
        if (MapViewClustersBtn is null || MapViewPinsBtn is null || MapViewTrailBtn is null) return;
        if (ReferenceEquals(sender, MapViewClustersBtn)) mapViewMode = "clusters";
        else if (ReferenceEquals(sender, MapViewPinsBtn)) mapViewMode = "pins";
        else if (ReferenceEquals(sender, MapViewTrailBtn)) mapViewMode = "trail";

        MapViewClustersBtn.IsChecked = mapViewMode == "clusters";
        MapViewPinsBtn.IsChecked = mapViewMode == "pins";
        MapViewTrailBtn.IsChecked = mapViewMode == "trail";

        RenderOfflineCanvas();
        if (mapWebReady && mapWebView?.CoreWebView2 is not null)
        {
            mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setViewMode", viewMode = mapViewMode }));
        }
    }

    private void MapDrawerTabClicked(object sender, RoutedEventArgs e)
    {
        if (MapTabClusters is null || MapTabPlaceTags is null || MapTabUnlocated is null) return;
        if (ReferenceEquals(sender, MapTabClusters)) mapDrawerTab = "clusters";
        else if (ReferenceEquals(sender, MapTabPlaceTags)) mapDrawerTab = "placeTags";
        else if (ReferenceEquals(sender, MapTabUnlocated)) mapDrawerTab = "unlocated";

        MapTabClusters.IsChecked = mapDrawerTab == "clusters";
        MapTabPlaceTags.IsChecked = mapDrawerTab == "placeTags";
        MapTabUnlocated.IsChecked = mapDrawerTab == "unlocated";
        UpdateDrawerEmptyState();
    }

    private void DismissMapConsentBanner(object sender, RoutedEventArgs e)
    {
        MapConsentCard.Visibility = Visibility.Collapsed;
    }

    // ---- Offline Interactive Canvas Rendering & Interaction ----

    private void FitOfflineViewport()
    {
        if (currentMapMarkers.Count == 0)
        {
            offlineCenterLat = 20.0;
            offlineCenterLon = 0.0;
            offlineZoom = 1.6;
        }
        else
        {
            var minLat = currentMapMarkers.Min(m => m.Latitude);
            var maxLat = currentMapMarkers.Max(m => m.Latitude);
            var minLon = currentMapMarkers.Min(m => m.Longitude);
            var maxLon = currentMapMarkers.Max(m => m.Longitude);
            offlineCenterLat = (minLat + maxLat) / 2.0;
            offlineCenterLon = (minLon + maxLon) / 2.0;
            var span = Math.Max(0.05, Math.Max(maxLat - minLat, maxLon - minLon));
            offlineZoom = span > 120 ? 1.5
                : span > 45 ? 2.5
                : span > 15 ? 4.0
                : span > 4 ? 6.5
                : span > 0.8 ? 10.0
                : 13.0;
        }
        RenderOfflineCanvas();
    }

    private (double Scale, double OriginX, double OriginY) GetOfflineProjection(double w, double h)
    {
        var worldSize = 256.0 * offlineZoom;
        var cx = OfflineWorldMap.LonToNormX(offlineCenterLon) * worldSize;
        var cy = OfflineWorldMap.LatToNormY(offlineCenterLat) * worldSize;
        return (worldSize, cx - w / 2.0, cy - h / 2.0);
    }

    private GeoBounds GetOfflineViewportBounds()
    {
        var w = Math.Max(320, OfflineMapCanvas.ActualWidth);
        var h = Math.Max(200, OfflineMapCanvas.ActualHeight);
        var (scale, ox, oy) = GetOfflineProjection(w, h);
        var minLon = OfflineWorldMap.NormXToLon(ox / scale);
        var maxLon = OfflineWorldMap.NormXToLon((ox + w) / scale);
        var maxLat = OfflineWorldMap.NormYToLat(oy / scale);
        var minLat = OfflineWorldMap.NormYToLat((oy + h) / scale);
        return new GeoBounds(minLat, maxLat, minLon, maxLon);
    }

    private IEnumerable<GeoMarker> GetMarkersInOfflineViewport()
    {
        var b = GetOfflineViewportBounds();
        return currentMapMarkers.Where(m => m.Latitude >= b.MinLat && m.Latitude <= b.MaxLat && m.Longitude >= b.MinLon && m.Longitude <= b.MaxLon);
    }

    private void RenderOfflineCanvas()
    {
        if (MapOverlay.Visibility != Visibility.Visible || MapOfflinePanel.Visibility != Visibility.Visible) return;
        var w = OfflineMapCanvas.ActualWidth;
        var h = OfflineMapCanvas.ActualHeight;
        if (w < 20 || h < 20) return;

        OfflineMapCanvas.Children.Clear();
        var isLight = Root.ActualTheme == ElementTheme.Light;
        var gridColor = isLight ? Color.FromArgb(24, 0, 0, 0) : Color.FromArgb(26, 255, 255, 255);
        var landFill = isLight ? Color.FromArgb(46, 80, 90, 100) : Color.FromArgb(40, 210, 220, 235);
        var landStroke = isLight ? Color.FromArgb(90, 70, 80, 90) : Color.FromArgb(75, 200, 210, 225);
        var gridBrush = new SolidColorBrush(gridColor);
        var landFillBrush = new SolidColorBrush(landFill);
        var landStrokeBrush = new SolidColorBrush(landStroke);

        var (scale, ox, oy) = GetOfflineProjection(w, h);

        // Subtle graticule lines
        for (int lat = -60; lat <= 60; lat += 30)
        {
            var py = OfflineWorldMap.LatToNormY(lat) * scale - oy;
            if (py < 0 || py > h) continue;
            OfflineMapCanvas.Children.Add(new Line { X1 = 0, Y1 = py, X2 = w, Y2 = py, Stroke = gridBrush, StrokeThickness = 1 });
        }
        for (int lon = -135; lon <= 135; lon += 45)
        {
            var px = OfflineWorldMap.LonToNormX(lon) * scale - ox;
            if (px < 0 || px > w) continue;
            OfflineMapCanvas.Children.Add(new Line { X1 = px, Y1 = 0, X2 = px, Y2 = h, Stroke = gridBrush, StrokeThickness = 1 });
        }

        // Vector landmasses
        foreach (var polyPts in OfflineWorldMap.Landmasses)
        {
            var poly = new Polygon
            {
                Fill = landFillBrush,
                Stroke = landStrokeBrush,
                StrokeThickness = 1
            };
            foreach (var (lat, lon) in polyPts)
            {
                var px = OfflineWorldMap.LonToNormX(lon) * scale - ox;
                var py = OfflineWorldMap.LatToNormY(lat) * scale - oy;
                poly.Points.Add(new WinPoint(px, py));
            }
            OfflineMapCanvas.Children.Add(poly);
        }

        // Timeline trail
        if (mapViewMode == "trail" && currentMapMarkers.Count > 1)
        {
            var trail = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(175, 59, 130, 246)),
                StrokeThickness = 2,
                StrokeDashArray = [5, 4]
            };
            foreach (var m in currentMapMarkers.OrderBy(x => x.DateTicks))
            {
                var px = OfflineWorldMap.LonToNormX(m.Longitude) * scale - ox;
                var py = OfflineWorldMap.LatToNormY(m.Latitude) * scale - oy;
                trail.Points.Add(new WinPoint(px, py));
            }
            OfflineMapCanvas.Children.Add(trail);
        }

        // Spatial clustering for markers
        double cellPx = mapViewMode == "pins" ? 10 : (mapViewMode == "trail" ? 14 : 38);
        var cells = new Dictionary<(int gx, int gy), List<GeoMarker>>();
        var visible = new List<GeoMarker>();

        foreach (var m in currentMapMarkers)
        {
            var px = OfflineWorldMap.LonToNormX(m.Longitude) * scale - ox;
            var py = OfflineWorldMap.LatToNormY(m.Latitude) * scale - oy;
            if (px < -30 || px > w + 30 || py < -30 || py > h + 30) continue;
            visible.Add(m);
            var cell = ((int)Math.Floor(px / cellPx), (int)Math.Floor(py / cellPx));
            if (!cells.TryGetValue(cell, out var list)) cells[cell] = list = new List<GeoMarker>();
            list.Add(m);
        }

        var pinBorder = new SolidColorBrush(Colors.White);
        var photoBrush = new SolidColorBrush(Color.FromArgb(235, 37, 99, 235));
        var videoBrush = new SolidColorBrush(Color.FromArgb(235, 16, 185, 129));
        var favBrush = new SolidColorBrush(Color.FromArgb(240, 224, 145, 36));

        foreach (var list in cells.Values)
        {
            var avgLat = list.Average(x => x.Latitude);
            var avgLon = list.Average(x => x.Longitude);
            var px = OfflineWorldMap.LonToNormX(avgLon) * scale - ox;
            var py = OfflineWorldMap.LatToNormY(avgLat) * scale - oy;
            var hasFav = list.Any(x => x.Favorite);

            if (list.Count == 1)
            {
                var m = list[0];
                var dot = new Border
                {
                    Width = 14,
                    Height = 14,
                    CornerRadius = new CornerRadius(7),
                    Background = hasFav ? favBrush : (m.Kind == "video" ? videoBrush : photoBrush),
                    BorderBrush = pinBorder,
                    BorderThickness = new Thickness(1.5)
                };
                ToolTipService.SetToolTip(dot, string.IsNullOrWhiteSpace(m.LocationName) ? m.Name : $"{m.Name}\n{m.LocationName}");
                dot.Tapped += async (_, args) =>
                {
                    if (mapPinMode) return;
                    args.Handled = true;
                    var item = await Task.Run(() => catalog.GetItemAsync(m.SourceId, m.RelativePath));
                    if (item is not null)
                    {
                        MapOverlay.Visibility = Visibility.Collapsed;
                        OpenPreviewFor(new MediaCard(item, settings));
                    }
                };
                Canvas.SetLeft(dot, px - 7);
                Canvas.SetTop(dot, py - 7);
                OfflineMapCanvas.Children.Add(dot);
            }
            else
            {
                var size = list.Count >= 50 ? 34 : 28;
                var pill = new Border
                {
                    MinWidth = size,
                    Height = size,
                    Padding = new Thickness(6, 0, 6, 0),
                    CornerRadius = new CornerRadius(size / 2.0),
                    Background = hasFav ? favBrush : photoBrush,
                    BorderBrush = pinBorder,
                    BorderThickness = new Thickness(1.5),
                    Child = new TextBlock
                    {
                        Text = list.Count > 999 ? $"{Math.Round(list.Count / 1000.0)}k" : list.Count.ToString(),
                        Foreground = pinBorder,
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                };
                ToolTipService.SetToolTip(pill, $"{list.Count:N0} items around {avgLat:0.00}, {avgLon:0.00}. Click to zoom in.");
                pill.Tapped += (_, args) =>
                {
                    if (mapPinMode) return;
                    args.Handled = true;
                    offlineCenterLat = avgLat;
                    offlineCenterLon = avgLon;
                    offlineZoom = Math.Min(32.0, offlineZoom * 2.2);
                    RenderOfflineCanvas();
                };
                Canvas.SetLeft(pill, px - size / 2.0);
                Canvas.SetTop(pill, py - size / 2.0);
                OfflineMapCanvas.Children.Add(pill);
            }
        }

        _ = RefreshMapFilmstripAsync(visible.Take(24).ToArray());
    }

    private void OfflineCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderOfflineCanvas();

    private void OfflineCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var pt = e.GetCurrentPoint(OfflineMapCanvas).Position;
        offlineDragging = true;
        offlineDragDistance = 0;
        offlineLastPoint = pt;
        OfflineMapCanvas.CapturePointer(e.Pointer);
    }

    private void OfflineCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!offlineDragging) return;
        var pt = e.GetCurrentPoint(OfflineMapCanvas).Position;
        var dx = pt.X - offlineLastPoint.X;
        var dy = pt.Y - offlineLastPoint.Y;
        offlineDragDistance += Math.Abs(dx) + Math.Abs(dy);
        offlineLastPoint = pt;

        var worldSize = 256.0 * offlineZoom;
        var nx = Math.Clamp(OfflineWorldMap.LonToNormX(offlineCenterLon) - (dx / worldSize), 0.0, 1.0);
        var ny = Math.Clamp(OfflineWorldMap.LatToNormY(offlineCenterLat) - (dy / worldSize), 0.02, 0.98);
        offlineCenterLon = OfflineWorldMap.NormXToLon(nx);
        offlineCenterLat = OfflineWorldMap.NormYToLat(ny);
        RenderOfflineCanvas();
    }

    private async void OfflineCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!offlineDragging) return;
        offlineDragging = false;
        OfflineMapCanvas.ReleasePointerCapture(e.Pointer);

        if (mapPinMode && offlineDragDistance < 6)
        {
            var pt = e.GetCurrentPoint(OfflineMapCanvas).Position;
            var (scale, ox, oy) = GetOfflineProjection(OfflineMapCanvas.ActualWidth, OfflineMapCanvas.ActualHeight);
            var lon = OfflineWorldMap.NormXToLon((pt.X + ox) / scale);
            var lat = OfflineWorldMap.NormYToLat((pt.Y + oy) / scale);
            await HandlePickedCoordinateAsync(lat, lon);
        }
    }

    private void OfflineCanvas_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(OfflineMapCanvas).Properties.MouseWheelDelta;
        offlineZoom = Math.Clamp(delta > 0 ? offlineZoom * 1.28 : offlineZoom / 1.28, 1.0, 64.0);
        RenderOfflineCanvas();
    }

    private void OfflineZoomInClicked(object sender, RoutedEventArgs e)
    {
        offlineZoom = Math.Clamp(offlineZoom * 1.6, 1.0, 64.0);
        RenderOfflineCanvas();
    }

    private void OfflineZoomOutClicked(object sender, RoutedEventArgs e)
    {
        offlineZoom = Math.Clamp(offlineZoom / 1.6, 1.0, 64.0);
        RenderOfflineCanvas();
    }

    private void OfflineFitClicked(object sender, RoutedEventArgs e) => FitOfflineViewport();

    private async void OfflineFilterBoundsClicked(object sender, RoutedEventArgs e)
    {
        activeMapBounds = GetOfflineViewportBounds();
        MapOverlay.Visibility = Visibility.Collapsed;
        await Guard(Refresh);
    }

    // ---- Filmstrip Viewport Loader ----

    private async Task RefreshMapFilmstripAsync(IReadOnlyList<GeoMarker> visible)
    {
        if (MapStripToggle.IsChecked != true) return;
        mapStripThumbCts?.Cancel();
        mapStripThumbCts?.Dispose();
        mapStripThumbCts = new CancellationTokenSource();
        var ct = mapStripThumbCts.Token;

        MapFilmstripHeader.Text = visible.Count == 0
            ? "Visible in map area (0)"
            : $"Visible in map area ({visible.Count:N0} shown)";

        mapStripItems.Clear();
        foreach (var m in visible)
        {
            var t = new DateTime(m.DateTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM HH:mm");
            var label = string.IsNullOrWhiteSpace(m.LocationName) ? m.Name : $"{m.Name} · {m.LocationName}";
            mapStripItems.Add(new MapStripItem
            {
                Marker = m,
                Label = label,
                Tooltip = $"{m.Name}\n{t}\n{m.Latitude:0.0000}, {m.Longitude:0.0000}"
            });
        }

        if (thumbnails is null) return;
        foreach (var card in mapStripItems.ToArray())
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var libItem = await Task.Run(() => catalog.GetItemAsync(card.Marker.SourceId, card.Marker.RelativePath, ct), ct);
                if (libItem is null) continue;
                var bytes = await UseCache(() => thumbnails.GetAsync(libItem.Media, 192, ct));
                if (bytes is null || ct.IsCancellationRequested) continue;

                using var ms = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(ms.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(bytes);
                    await writer.StoreAsync();
                    await writer.FlushAsync();
                }
                ms.Seek(0);
                var bmp = new BitmapImage();
                await bmp.SetSourceAsync(ms);
                card.Thumbnail = bmp;
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    private async void MapFilmstripItemClicked(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not MapStripItem card) return;
        var m = card.Marker;
        var item = await Task.Run(() => catalog.GetItemAsync(m.SourceId, m.RelativePath));
        if (item is not null)
        {
            MapOverlay.Visibility = Visibility.Collapsed;
            OpenPreviewFor(new MediaCard(item, settings));
        }
    }

    // ---- Online WebView2 Map ----

    private async void GrantMapOnlineConsent(object sender, RoutedEventArgs e)
    {
        mapOnlineConsentGranted = true;
        await EnsureAndPushOnlineMapAsync();
    }

    private async Task EnsureAndPushOnlineMapAsync()
    {
        try
        {
            MapOfflinePanel.Visibility = Visibility.Collapsed;
            MapWebHost.Visibility = Visibility.Visible;
            MapToggleModeButton.Visibility = Visibility.Collapsed;

            if (mapWebView is null)
            {
                mapWebView = new WebView2();
                MapWebHost.Children.Clear();
                MapWebHost.Children.Add(mapWebView);

                var webDataFolder = Path.Combine(AppSettings.LocalRoot, "webview2");
                Directory.CreateDirectory(webDataFolder);
                var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, webDataFolder, null);
                await mapWebView.EnsureCoreWebView2Async(env);

                mapWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                mapWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                var mapFolder = Path.Combine(AppContext.BaseDirectory, "Assets", "Map");
                mapWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "luma.local",
                    mapFolder,
                    CoreWebView2HostResourceAccessKind.Allow);
                mapWebView.WebMessageReceived += MapWebView_WebMessageReceived;
                var tcs = new TaskCompletionSource();
                void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    mapWebView.CoreWebView2.NavigationCompleted -= OnCompleted;
                    tcs.TrySetResult();
                }
                mapWebView.CoreWebView2.NavigationCompleted += OnCompleted;
                mapWebView.Source = new Uri("https://luma.local/map.html");
                await tcs.Task;
                mapWebReady = true;
            }

            PushMarkersToWebView();
        }
        catch (Exception ex)
        {
            mapOnlineConsentGranted = false;
            MapWebHost.Visibility = Visibility.Collapsed;
            MapOfflinePanel.Visibility = Visibility.Visible;
            MapToggleModeButton.Visibility = Visibility.Visible;
            Notify($"Online map view could not start ({ex.Message}). Showing offline coordinate canvas.", true);
        }
    }

    private void PushMarkersToWebView()
    {
        if (!mapWebReady || mapWebView?.CoreWebView2 is null) return;
        var payload = new
        {
            type = "setMarkers",
            theme = Root.ActualTheme == ElementTheme.Light ? "light" : "dark",
            viewMode = mapViewMode,
            markers = currentMapMarkers.Select(m => new
            {
                sourceId = m.SourceId,
                relativePath = m.RelativePath,
                latitude = m.Latitude,
                longitude = m.Longitude,
                name = m.Name,
                kind = m.Kind,
                favorite = m.Favorite,
                dateTicks = m.DateTicks,
                locationName = m.LocationName,
                origin = m.LocationOrigin,
                cameraModel = m.CameraModel,
                dateText = new DateTime(m.DateTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
            })
        };
        mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
        mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setPinMode", enabled = mapPinMode }));

        if (pendingMapFocus is { } focus)
        {
            mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "focusPin", lat = focus.Lat, lon = focus.Lon }));
            pendingMapFocus = null;
        }
    }

    private async void MapWebView_WebMessageReceived(WebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeProp)) return;
            var msgType = typeProp.GetString();

            if (msgType == "requestThumb")
            {
                var sourceId = root.GetProperty("sourceId").GetString() ?? "";
                var relPath = root.GetProperty("relativePath").GetString() ?? "";
                var match = await Task.Run(() => catalog.GetItemAsync(sourceId, relPath));
                if (match is not null && thumbnails is not null)
                {
                    var bytes = await UseCache(() => thumbnails.GetAsync(match.Media, 256, CancellationToken.None));
                    if (bytes is not null)
                    {
                        var dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
                        sender.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                        {
                            type = "thumb",
                            sourceId,
                            relativePath = relPath,
                            dataUri
                        }));
                    }
                }
            }
            else if (msgType == "openPreview")
            {
                var sourceId = root.GetProperty("sourceId").GetString() ?? "";
                var relPath = root.GetProperty("relativePath").GetString() ?? "";
                var card = cards.FirstOrDefault(c => c.Item.Media.SourceId == sourceId && c.Item.Media.RelativePath == relPath);
                if (card is null)
                {
                    var match = await Task.Run(() => catalog.GetItemAsync(sourceId, relPath));
                    if (match is not null) card = new MediaCard(match, settings);
                }
                if (card is not null)
                {
                    MapOverlay.Visibility = Visibility.Collapsed;
                    OpenPreviewFor(card);
                }
            }
            else if (msgType == "filterBounds")
            {
                var minLat = root.GetProperty("minLat").GetDouble();
                var maxLat = root.GetProperty("maxLat").GetDouble();
                var minLon = root.GetProperty("minLon").GetDouble();
                var maxLon = root.GetProperty("maxLon").GetDouble();
                activeMapBounds = new GeoBounds(minLat, maxLat, minLon, maxLon);
                MapOverlay.Visibility = Visibility.Collapsed;
                await Guard(Refresh);
            }
            else if (msgType == "pickedCoord")
            {
                var lat = root.GetProperty("lat").GetDouble();
                var lon = root.GetProperty("lon").GetDouble();
                await HandlePickedCoordinateAsync(lat, lon);
            }
            else if (msgType == "viewportChanged" && root.TryGetProperty("visibleKeys", out var keysProp))
            {
                var keySet = new HashSet<(string SourceId, string RelativePath)>();
                foreach (var entry in keysProp.EnumerateArray())
                {
                    var sid = entry.GetProperty("sourceId").GetString() ?? "";
                    var rp = entry.GetProperty("relativePath").GetString() ?? "";
                    keySet.Add((sid, rp));
                }
                var vis = currentMapMarkers.Where(m => keySet.Contains((m.SourceId, m.RelativePath))).Take(24).ToArray();
                await RefreshMapFilmstripAsync(vis);
            }
        }
        catch { }
    }

    // ---- Drawer Actions ----

    private void MapOfflineGroupZoomClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OfflineGeoCluster cluster } && cluster.Count > 0)
        {
            offlineCenterLat = cluster.CenterLat;
            offlineCenterLon = cluster.CenterLon;
            offlineZoom = 11.0;
            RenderOfflineCanvas();
            if (mapWebReady && mapWebView?.CoreWebView2 is not null)
            {
                mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "focusPin", lat = cluster.CenterLat, lon = cluster.CenterLon }));
            }
        }
    }

    private async void MapOfflineGroupFilterClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OfflineGeoCluster cluster } && cluster.MinLat != cluster.MaxLat)
        {
            activeMapBounds = new GeoBounds(cluster.MinLat, cluster.MaxLat, cluster.MinLon, cluster.MaxLon);
            MapOverlay.Visibility = Visibility.Collapsed;
            await Guard(Refresh);
        }
    }

    private void MapOfflineGroupBrowserClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OfflineGeoCluster cluster } && (cluster.CenterLat != 0 || cluster.CenterLon != 0))
        {
            var latStr = cluster.CenterLat.ToString("0.00000", CultureInfo.InvariantCulture);
            var lonStr = cluster.CenterLon.ToString("0.00000", CultureInfo.InvariantCulture);
            var url = $"https://www.openstreetmap.org/?mlat={latStr}&mlon={lonStr}#map=14/{latStr}/{lonStr}";
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }
    }

    private async void MapDrawerItemPreviewClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MapDrawerMediaRow row }) return;
        var item = await Task.Run(() => catalog.GetItemAsync(row.Media.SourceId, row.Media.RelativePath));
        if (item is not null)
        {
            MapOverlay.Visibility = Visibility.Collapsed;
            OpenPreviewFor(new MediaCard(item, settings));
        }
    }

    private async void MapDrawerItemPinClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: MapDrawerMediaRow row }) return;
        await ShowManualPinDialogAsync([row.Media], null, null, row.LocationName);
    }

    private void CloseMapView(object sender, RoutedEventArgs e)
    {
        mapBackfillCts?.Cancel();
        mapStripThumbCts?.Cancel();
        mapPinMode = false;
        MapPinModeToggle.IsChecked = false;
        MapOverlay.Visibility = Visibility.Collapsed;
    }
}
