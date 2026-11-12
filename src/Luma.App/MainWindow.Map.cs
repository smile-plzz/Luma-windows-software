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

using Ellipse = Microsoft.UI.Xaml.Shapes.Ellipse;
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
    private enum MapViewportMode { Preserve, PreserveIfVisible, FitAll, RestoreOrFit }

    private bool mapOnlineConsentGranted;
    private bool mapSettingsAppliedOnce;
    private WebView2? mapWebView;
    private bool mapWebReady;
    private IReadOnlyList<GeoMarker> currentMapMarkers = Array.Empty<GeoMarker>();
    private IReadOnlyList<VisitedPlaceSummary> currentVisitedPlaces = Array.Empty<VisitedPlaceSummary>();
    private (double Lat, double Lon)? pendingMapFocus;

    private bool mapFiltersInitializing;
    private string? mapSelectedKind;
    private string mapViewMode = "heat";
    private string mapDrawerTab = "places";
    private bool mapPinMode;
    private CachedMedia[] pendingPinMedia = Array.Empty<CachedMedia>();

    // Unified Web Mercator coordinates & zoom level [1.2 .. 18.0] shared 1:1 between Offline Canvas and map.html.
    private double offlineCenterLat = 20.0;
    private double offlineCenterLon = 0.0;
    private double offlineZoom = 2.0;
    private bool offlineDragging;
    private double offlineDragDistance;
    private WinPoint offlineLastPoint;

    private CancellationTokenSource? mapBackfillCts;
    private bool mapBackfillRunning;
    private CancellationTokenSource? mapStripThumbCts;
    private CancellationTokenSource? mapSearchDebounceCts;
    private readonly ObservableCollection<MapStripItem> mapStripItems = new();

    private long catalogGeneration = 1;
    private long mapBackfillCleanGeneration = -1;
    private long mapCacheGeneration = -1;
    private LibraryQuery? mapCacheQuery;
    private IReadOnlyList<GeoMarker>? mapCacheMarkers;
    private MapLocationSummary? mapCacheSummary;
    private IReadOnlyList<PlaceTaggedItem>? mapCachePlaceTagged;
    private IReadOnlyList<LibraryItem>? mapCacheUnlocated;
    private IReadOnlyList<VisitedPlaceSummary>? mapCacheVisitedPlaces;
    private OfflineGeoCluster[]? mapCacheSpotClusters;
    private long mapDropdownCacheGeneration = -1;
    private LibraryQuery? mapDropdownCacheScope;
    private List<MapFilterOption>? mapCacheYearItems;
    private List<MapFilterOption>? mapCacheSourceItems;

    private void InvalidateMapCache(bool resetBackfillCheck = true)
    {
        catalogGeneration++;
        mapCacheGeneration = -1;
        mapDropdownCacheGeneration = -1;
        if (resetBackfillCheck) mapBackfillCleanGeneration = -1;
    }

    private Task OpenMediaExternallyAsync(string sourceId, string relativePath) => Guard(async () =>
    {
        var item = await Task.Run(() => catalog.GetItemAsync(sourceId, relativePath));
        if (item is null)
        {
            Notify("This catalog entry is no longer available. Refresh the source to update the library.", true);
            return;
        }
        ShellFiles.OpenInDefaultApp(Resolve(item.Media));
    });

    private void SaveMapPreferencesToSettings()
    {
        settings.MapViewMode = mapViewMode;
        settings.MapCenterLat = Math.Clamp(offlineCenterLat, -85.0, 85.0);
        settings.MapCenterLon = Math.Clamp(offlineCenterLon, -180.0, 180.0);
        settings.MapZoom = Math.Clamp(offlineZoom, 1.0, 18.0);
        if (MapDrawerToggle is not null) settings.MapShowLocations = MapDrawerToggle.IsChecked == true;
        if (MapStripToggle is not null) settings.MapShowFilmstrip = MapStripToggle.IsChecked == true;
        settings.MapKind = mapSelectedKind;
        if (MapFavFilter is not null) settings.MapFavoritesOnly = MapFavFilter.IsChecked == true;
        if (MapMatchGalleryToggle is not null) settings.MapMatchGalleryFilters = MapMatchGalleryToggle.IsChecked == true;
        if (MapYearFilter?.SelectedItem is MapFilterOption yOpt) settings.MapYear = yOpt.Value;
        if (MapSourceFilter?.SelectedItem is MapFilterOption sOpt) settings.MapSourceId = sOpt.Value;
        if (MapOriginFilter is not null) settings.MapOriginIndex = Math.Clamp(MapOriginFilter.SelectedIndex, 0, 2);
        if (MapSearchBox is not null) settings.MapSearch = MapSearchBox.Text.Trim();
    }

    private void ApplySavedMapSettingsToUi()
    {
        if (mapSettingsAppliedOnce) return;
        mapSettingsAppliedOnce = true;

        mapFiltersInitializing = true;
        try
        {
            mapViewMode = settings.MapViewMode is ("heat" or "clusters" or "places" or "pins" or "trail")
                ? settings.MapViewMode
                : "heat";
            mapSelectedKind = settings.MapKind;

            if (settings.MapHasSavedViewport)
            {
                offlineCenterLat = Math.Clamp(settings.MapCenterLat, -85.0, 85.0);
                offlineCenterLon = Math.Clamp(settings.MapCenterLon, -180.0, 180.0);
                offlineZoom = Math.Clamp(settings.MapZoom, 1.2, 18.0);
            }

            if (MapDrawerToggle is not null) MapDrawerToggle.IsChecked = settings.MapShowLocations;
            if (MapStripToggle is not null) MapStripToggle.IsChecked = settings.MapShowFilmstrip;
            if (MapFavFilter is not null) MapFavFilter.IsChecked = settings.MapFavoritesOnly;
            if (MapMatchGalleryToggle is not null) MapMatchGalleryToggle.IsChecked = settings.MapMatchGalleryFilters;
            if (MapOriginFilter is not null) MapOriginFilter.SelectedIndex = Math.Clamp(settings.MapOriginIndex, 0, 2);
            if (MapSearchBox is not null) MapSearchBox.Text = settings.MapSearch ?? "";
            if (MapKindAll is not null && MapKindPhotos is not null && MapKindVideos is not null)
            {
                MapKindAll.IsChecked = mapSelectedKind is null;
                MapKindPhotos.IsChecked = mapSelectedKind == "photo";
                MapKindVideos.IsChecked = mapSelectedKind == "video";
            }

            UpdateViewModeButtons();
            ApplyMapChromeVisibility();
        }
        finally
        {
            mapFiltersInitializing = false;
        }
    }

    private void UpdateViewModeButtons()
    {
        if (MapViewHeatBtn is not null) MapViewHeatBtn.IsChecked = mapViewMode == "heat";
        if (MapViewClustersBtn is not null) MapViewClustersBtn.IsChecked = mapViewMode == "clusters";
        if (MapViewPlacesBtn is not null) MapViewPlacesBtn.IsChecked = mapViewMode == "places";
        if (MapViewPinsBtn is not null) MapViewPinsBtn.IsChecked = mapViewMode == "pins";
        if (MapViewTrailBtn is not null) MapViewTrailBtn.IsChecked = mapViewMode == "trail";
        if (OfflineHeatLegend is not null)
            OfflineHeatLegend.Visibility = mapViewMode == "heat" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyMapChromeVisibility()
    {
        if (MapDrawerPanel is not null && MapDrawerToggle is not null)
            MapDrawerPanel.Visibility = MapDrawerToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (MapFilmstripBar is not null && MapStripToggle is not null)
            MapFilmstripBar.Visibility = MapStripToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (MapConsentCard is not null)
            MapConsentCard.Visibility = !mapOnlineConsentGranted && !settings.MapConsentNoticeDismissed
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void UpdateMapModeHeaderButton()
    {
        if (MapToggleModeButton is null) return;
        MapToggleModeButton.Visibility = Visibility.Visible;
        if (MapWebHost.Visibility == Visibility.Visible && mapOnlineConsentGranted)
            MapToggleModeButton.Content = "Offline canvas";
        else if (mapOnlineConsentGranted)
            MapToggleModeButton.Content = "Online map";
        else
            MapToggleModeButton.Content = "Load online map (this session)";
    }

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
        ApplySavedMapSettingsToUi();
        pendingMapFocus = focusCoords;
        MapOverlay.Visibility = Visibility.Visible;
        MapFilmstripList.ItemsSource = mapStripItems;
        ApplyMapChromeVisibility();

        await PopulateMapFilterDropdownsAsync();

        if (focusCoords is { } focus)
        {
            offlineCenterLat = Math.Clamp(focus.Lat, -85.0, 85.0);
            offlineCenterLon = Math.Clamp(focus.Lon, -180.0, 180.0);
            offlineZoom = Math.Max(offlineZoom, 13.0);
            settings.MapHasSavedViewport = true;
            await RefreshMapDataAsync(MapViewportMode.Preserve);
        }
        else
        {
            await RefreshMapDataAsync(MapViewportMode.RestoreOrFit);
        }

        if (mapOnlineConsentGranted)
        {
            await EnsureAndPushOnlineMapAsync(preserveViewport: true);
        }
        else
        {
            MapOfflinePanel.Visibility = Visibility.Visible;
            MapWebHost.Visibility = Visibility.Collapsed;
            UpdateMapModeHeaderButton();
        }

        _ = StartConnectedMetadataBackfillIfNeededAsync(forceRestart: false);
    }

    private async Task PopulateMapFilterDropdownsAsync()
    {
        mapFiltersInitializing = true;
        try
        {
            var baseScope = MapMatchGalleryToggle.IsChecked == true
                ? (activeQuery ?? CurrentQuery()) with { HasGpsOnly = false, MapBounds = null, Search = "" }
                : new LibraryQuery();

            List<MapFilterOption> yearItems;
            List<MapFilterOption> sourceItems;
            if (mapDropdownCacheGeneration == catalogGeneration && mapDropdownCacheScope == baseScope &&
                mapCacheYearItems is not null && mapCacheSourceItems is not null)
            {
                yearItems = mapCacheYearItems;
                sourceItems = mapCacheSourceItems;
            }
            else
            {
                var buckets = await Task.Run(() => catalog.MonthBucketsAsync(baseScope));
                var years = buckets
                    .Select(b => b.Month.Length >= 4 ? b.Month[..4] : "")
                    .Where(y => y.Length == 4)
                    .Distinct()
                    .OrderByDescending(y => y)
                    .ToArray();

                yearItems = new List<MapFilterOption> { new("All years", null) };
                yearItems.AddRange(years.Select(y => new MapFilterOption(y, y)));

                var sources = await Task.Run(() => catalog.SourcesAsync());
                var labels = await Task.Run(() => catalog.SourceLabelsAsync());
                sourceItems = new List<MapFilterOption> { new("All sources", null) };
                foreach (var s in sources)
                {
                    var label = labels.GetValueOrDefault(s.Id, Path.GetFileName(s.RootPath.TrimEnd('\\')));
                    if (string.IsNullOrWhiteSpace(label)) label = s.RootPath;
                    if (!s.IsOnline) label += " (offline)";
                    sourceItems.Add(new(label, s.Id));
                }

                mapDropdownCacheGeneration = catalogGeneration;
                mapDropdownCacheScope = baseScope;
                mapCacheYearItems = yearItems;
                mapCacheSourceItems = sourceItems;
            }

            var prevYear = (MapYearFilter.SelectedItem as MapFilterOption)?.Value ?? settings.MapYear;
            MapYearFilter.ItemsSource = yearItems;
            MapYearFilter.SelectedIndex = prevYear is not null
                ? Math.Max(0, yearItems.FindIndex(o => o.Value == prevYear))
                : 0;

            var prevSource = (MapSourceFilter.SelectedItem as MapFilterOption)?.Value ?? settings.MapSourceId;
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

    private LibraryQuery BuildMapFilterQuery(out int activeFilterCount)
    {
        activeFilterCount = 0;
        LibraryQuery baseQuery;
        if (MapMatchGalleryToggle?.IsChecked == true)
        {
            baseQuery = (activeQuery ?? CurrentQuery()) with
            {
                MapBounds = null,
                HasGpsOnly = false,
                FromTicks = null,
                BeforeTicks = null,
                MemoriesOn = null,
                Folder = ""
            };
            activeFilterCount++;
        }
        else
        {
            baseQuery = new LibraryQuery();
        }

        if (mapSelectedKind is not null)
        {
            baseQuery = baseQuery with { Kind = mapSelectedKind };
            activeFilterCount++;
        }
        if (MapFavFilter?.IsChecked == true)
        {
            baseQuery = baseQuery with { FavoritesOnly = true };
            activeFilterCount++;
        }

        if (MapYearFilter?.SelectedItem is MapFilterOption { Value: string yr } && int.TryParse(yr, out var yNum))
        {
            var start = new DateTime(yNum, 1, 1, 0, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks;
            var end = new DateTime(yNum, 12, 31, 23, 59, 59, DateTimeKind.Local).ToUniversalTime().Ticks;
            baseQuery = baseQuery with { FromTicks = start, BeforeTicks = end };
            activeFilterCount++;
        }

        if (MapSourceFilter?.SelectedItem is MapFilterOption { Value: string sid })
        {
            baseQuery = baseQuery with { SourceId = sid };
            activeFilterCount++;
        }

        string? originFilter = MapOriginFilter?.SelectedIndex switch
        {
            1 => "file",
            2 => "manual",
            _ => null
        };
        if (originFilter is not null)
        {
            baseQuery = baseQuery with { LocationOriginFilter = originFilter };
            activeFilterCount++;
        }

        var mapSearch = MapSearchBox?.Text.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(mapSearch))
        {
            baseQuery = baseQuery with
            {
                Search = string.IsNullOrWhiteSpace(baseQuery.Search) ? mapSearch : $"{baseQuery.Search} {mapSearch}"
            };
            activeFilterCount++;
        }

        return baseQuery;
    }

    private void UpdateActiveFilterChip(int activeCount)
    {
        if (MapActiveFilterChip is null) return;
        if (activeCount <= 0)
        {
            MapActiveFilterChip.Visibility = Visibility.Collapsed;
        }
        else
        {
            MapActiveFilterChip.Content = activeCount == 1 ? "1 filter ✕" : $"{activeCount} filters ✕";
            MapActiveFilterChip.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshMapDataAsync(MapViewportMode viewportMode = MapViewportMode.Preserve)
    {
        var query = BuildMapFilterQuery(out int activeFilterCount);
        UpdateActiveFilterChip(activeFilterCount);

        MapLocationSummary summary;
        IReadOnlyList<PlaceTaggedItem> placeTagged;
        IReadOnlyList<LibraryItem> unlocated;
        OfflineGeoCluster[] spotClusters;

        if (mapCacheGeneration == catalogGeneration &&
            mapCacheQuery == query &&
            mapCacheMarkers is not null &&
            mapCacheSummary is not null &&
            mapCachePlaceTagged is not null &&
            mapCacheUnlocated is not null &&
            mapCacheVisitedPlaces is not null &&
            mapCacheSpotClusters is not null)
        {
            currentMapMarkers = mapCacheMarkers;
            summary = mapCacheSummary;
            placeTagged = mapCachePlaceTagged;
            unlocated = mapCacheUnlocated;
            currentVisitedPlaces = mapCacheVisitedPlaces;
            spotClusters = PopulateOfflineDrawerLists(placeTagged, unlocated);
        }
        else
        {
            var markersTask = Task.Run(() => catalog.QueryGeotaggedMarkersAsync(query, 25000));
            var summaryTask = Task.Run(() => catalog.QueryMapSummaryAsync(query));
            var placeTask = Task.Run(() => catalog.QueryPlaceTaggedWithoutCoordsAsync(query, 300));
            var unlocatedTask = Task.Run(() => catalog.QueryUnlocatedItemsAsync(query, 150));

            await Task.WhenAll(markersTask, summaryTask, placeTask, unlocatedTask);

            currentMapMarkers = markersTask.Result;
            summary = summaryTask.Result;
            placeTagged = placeTask.Result;
            unlocated = unlocatedTask.Result;

            currentVisitedPlaces = await Task.Run(() => MapOverviewBuilder.BuildVisitedPlaces(currentMapMarkers, 120));
            spotClusters = PopulateOfflineDrawerLists(placeTagged, unlocated);

            mapCacheGeneration = catalogGeneration;
            mapCacheQuery = query;
            mapCacheMarkers = currentMapMarkers;
            mapCacheSummary = summary;
            mapCachePlaceTagged = placeTagged;
            mapCacheUnlocated = unlocated;
            mapCacheVisitedPlaces = currentVisitedPlaces;
            mapCacheSpotClusters = spotClusters;
        }

        if (currentVisitedPlaces.Count > 0)
        {
            MapSummaryText.Text = summary.PlaceTagOnlyCount > 0
                ? $"{summary.GeotaggedCount:N0} mapped · {currentVisitedPlaces.Count:N0} places · {summary.PlaceTagOnlyCount:N0} tagged"
                : $"{summary.GeotaggedCount:N0} mapped · {currentVisitedPlaces.Count:N0} {(currentVisitedPlaces.Count == 1 ? "place" : "places")}";
        }
        else
        {
            MapSummaryText.Text = summary.PlaceTagOnlyCount > 0
                ? $"{summary.GeotaggedCount:N0} mapped · {summary.PlaceTagOnlyCount:N0} place-tagged"
                : $"{summary.GeotaggedCount:N0} mapped";
        }

        MapTabPlaces.Content = $"Places ({currentVisitedPlaces.Count:N0})";
        MapTabClusters.Content = $"Spots ({spotClusters.Length:N0})";
        MapTabPlaceTags.Content = $"Tags ({summary.PlaceTagOnlyCount:N0})";
        MapTabUnlocated.Content = $"Unlocated ({summary.UnlocatedCount:N0})";

        bool shouldFit = viewportMode switch
        {
            MapViewportMode.FitAll => true,
            MapViewportMode.RestoreOrFit => !settings.MapHasSavedViewport,
            MapViewportMode.PreserveIfVisible => currentMapMarkers.Count > 0 && !GetMarkersInOfflineViewport().Any(),
            _ => false
        };

        if (shouldFit) FitOfflineViewport();
        else RenderOfflineCanvas();

        if (mapOnlineConsentGranted && mapWebReady && MapWebHost.Visibility == Visibility.Visible)
        {
            PushMarkersToWebView(preserveViewport: !shouldFit);
        }
        else if (MapStripToggle?.IsChecked == true)
        {
            var visible = GetMarkersInOfflineViewport().Take(24).ToArray();
            _ = RefreshMapFilmstripAsync(visible);
        }
    }

    private OfflineGeoCluster[] PopulateOfflineDrawerLists(
        IReadOnlyList<PlaceTaggedItem> placeTagged,
        IReadOnlyList<LibraryItem> unlocated)
    {
        MapPlacesList.ItemsSource = currentVisitedPlaces;

        OfflineGeoCluster[] clusters;
        if (currentMapMarkers.Count == 0)
        {
            clusters = Array.Empty<OfflineGeoCluster>();
        }
        else
        {
            clusters = currentMapMarkers
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
        }
        MapOfflineGroupsList.ItemsSource = clusters;

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
        return clusters;
    }

    private void UpdateDrawerEmptyState()
    {
        if (MapPlacesList is null || MapOfflineGroupsList is null || MapPlaceTagsList is null || MapUnlocatedList is null) return;
        MapPlacesList.Visibility = mapDrawerTab == "places" ? Visibility.Visible : Visibility.Collapsed;
        MapOfflineGroupsList.Visibility = mapDrawerTab == "clusters" ? Visibility.Visible : Visibility.Collapsed;
        MapPlaceTagsList.Visibility = mapDrawerTab == "placeTags" ? Visibility.Visible : Visibility.Collapsed;
        MapUnlocatedList.Visibility = mapDrawerTab == "unlocated" ? Visibility.Visible : Visibility.Collapsed;

        int activeCount = mapDrawerTab switch
        {
            "places" => currentVisitedPlaces.Count,
            "clusters" => (MapOfflineGroupsList.ItemsSource as Array)?.Length ?? 0,
            "placeTags" => (MapPlaceTagsList.ItemsSource as Array)?.Length ?? 0,
            _ => (MapUnlocatedList.ItemsSource as Array)?.Length ?? 0
        };
        MapDrawerEmpty.Visibility = activeCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        MapDrawerEmpty.Text = mapDrawerTab switch
        {
            "places" => "No visited places match these filters yet. Click Scan GPS or Pin mode to place items on the map.",
            "clusters" => "No coordinate spots match these filters yet.",
            "placeTags" => "No unlocated files with place tags match these filters.",
            _ => "All matching files already have map coordinates."
        };
    }

    private async Task StartConnectedMetadataBackfillIfNeededAsync(bool forceRestart)
    {
        if (scanning || (mapBackfillRunning && !forceRestart)) return;
        if (!forceRestart && mapBackfillCleanGeneration == catalogGeneration) return;
        if (forceRestart)
        {
            mapBackfillCts?.Cancel();
        }

        var sources = await Task.Run(() => catalog.SourcesAsync());
        var onlineIds = sources.Where(s => s.IsOnline).Select(s => s.Id).ToArray();
        if (onlineIds.Length == 0)
        {
            if (forceRestart)
                Notify("No connected sources to check. Saved map locations remain cached offline.");
            return;
        }

        var pendingTotal = await Task.Run(() => catalog.CountPendingMetadataBackfillAsync(null, onlineIds));
        if (pendingTotal <= 0)
        {
            mapBackfillCleanGeneration = catalogGeneration;
            if (forceRestart)
                Notify($"Map metadata cache is up to date across {onlineIds.Length:N0} connected source(s). New or changed files update automatically.");
            return;
        }

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
                        if (!isClosing) MapBackfillText.Text = $"Scanning GPS ({Math.Min(done, pendingTotal):N0} / {pendingTotal:N0})";
                    });
                });
                var progress = new Progress<int>(savedCount =>
                {
                    if (refreshThrottle.ElapsedMilliseconds >= 2000)
                    {
                        refreshThrottle.Restart();
                        DispatcherQueue.TryEnqueue(() =>
                        {
                            if (!isClosing && MapOverlay.Visibility == Visibility.Visible)
                            {
                                var mode = currentMapMarkers.Count == 0 && !settings.MapHasSavedViewport
                                    ? MapViewportMode.FitAll
                                    : MapViewportMode.Preserve;
                                _ = RefreshMapDataAsync(mode);
                            }
                        });
                    }
                });

                totalUpdated += await Task.Run(() => catalog.EnrichAsync(new WindowsMetadataReader(), sid, ct, progress, details), ct);
                processedOffset += sourceCount;
            }

            if (totalUpdated > 0)
                InvalidateMapCache(resetBackfillCheck: false);
            mapBackfillCleanGeneration = catalogGeneration;

            if (MapOverlay.Visibility == Visibility.Visible)
            {
                var mode = currentMapMarkers.Count == 0 && !settings.MapHasSavedViewport
                    ? MapViewportMode.FitAll
                    : MapViewportMode.Preserve;
                await RefreshMapDataAsync(mode);
            }
            if (totalUpdated > 0)
            {
                Status.Text = $"Indexed location & EXIF metadata for {totalUpdated:N0} file(s)";
                Notify($"Cached GPS & location metadata for {totalUpdated:N0} file(s). Future visits reuse this cache.");
            }
            else if (forceRestart)
            {
                Notify("Checked pending files; map metadata cache is now up to date.");
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

    private void PauseMapBackfill(object sender, RoutedEventArgs e) => mapBackfillCts?.Cancel();

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

        var result = await ShowDialogAsync(dialog);
        if (result == ContentDialogResult.None) return;

        if (result == ContentDialogResult.Secondary)
        {
            await catalog.AssignLocationAsync(targets, null, null, null);
            InvalidateMapCache(resetBackfillCheck: false);
            pendingPinMedia = Array.Empty<CachedMedia>();
            mapPinMode = false;
            MapPinModeToggle.IsChecked = false;
            UpdatePinModeVisuals();
            await RefreshMapDataAsync(MapViewportMode.Preserve);
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
        InvalidateMapCache(resetBackfillCheck: false);
        pendingPinMedia = Array.Empty<CachedMedia>();
        mapPinMode = false;
        MapPinModeToggle.IsChecked = false;
        UpdatePinModeVisuals();
        if (parsedLat.HasValue && parsedLon.HasValue)
        {
            offlineCenterLat = parsedLat.Value;
            offlineCenterLon = parsedLon.Value;
            settings.MapHasSavedViewport = true;
            pendingMapFocus = (parsedLat.Value, parsedLon.Value);
        }
        await RefreshMapDataAsync(MapViewportMode.Preserve);
        if (ready) await Refresh();
        Notify($"Saved catalog location for {targets.Length:N0} item(s).");
    }

    private void MapChromeToggled(object sender, RoutedEventArgs e)
    {
        if (mapFiltersInitializing || MapFilmstripBar is null || MapDrawerPanel is null || MapStripToggle is null || MapDrawerToggle is null) return;
        ApplyMapChromeVisibility();
        SaveMapPreferencesToSettings();
        if (MapStripToggle.IsChecked == true)
        {
            if (mapOnlineConsentGranted && mapWebReady && MapWebHost.Visibility == Visibility.Visible)
                PushMarkersToWebView(preserveViewport: true);
            else
                _ = RefreshMapFilmstripAsync(GetMarkersInOfflineViewport().Take(24).ToArray());
        }
    }

    private void MapSummaryPillClicked(object sender, RoutedEventArgs e)
    {
        if (MapDrawerToggle is null) return;
        MapDrawerToggle.IsChecked = !(MapDrawerToggle.IsChecked == true);
        if (MapDrawerToggle.IsChecked == true)
        {
            mapDrawerTab = "places";
            UpdateDrawerTabButtons();
            UpdateDrawerEmptyState();
        }
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
        SaveMapPreferencesToSettings();
        if (MapOverlay?.Visibility == Visibility.Visible) await RefreshMapDataAsync(MapViewportMode.PreserveIfVisible);
    }

    private async void MapFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || mapFiltersInitializing || MapOverlay is null || MapOverlay.Visibility != Visibility.Visible) return;
        if (ReferenceEquals(sender, MapMatchGalleryToggle))
            await PopulateMapFilterDropdownsAsync();
        SaveMapPreferencesToSettings();
        await RefreshMapDataAsync(MapViewportMode.PreserveIfVisible);
    }

    private async void MapComboFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || mapFiltersInitializing || MapOverlay is null || MapOverlay.Visibility != Visibility.Visible) return;
        SaveMapPreferencesToSettings();
        await RefreshMapDataAsync(MapViewportMode.PreserveIfVisible);
    }

    private async void MapSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || mapFiltersInitializing || MapOverlay is null || MapOverlay.Visibility != Visibility.Visible) return;
        mapSearchDebounceCts?.Cancel();
        mapSearchDebounceCts?.Dispose();
        var cts = mapSearchDebounceCts = new CancellationTokenSource();
        try
        {
            await Task.Delay(180, cts.Token);
            SaveMapPreferencesToSettings();
            await RefreshMapDataAsync(MapViewportMode.PreserveIfVisible);
        }
        catch (OperationCanceledException) { }
    }

    private async void MapResetFiltersClicked(object sender, RoutedEventArgs e)
    {
        mapFiltersInitializing = true;
        try
        {
            mapSelectedKind = null;
            if (MapKindAll is not null) MapKindAll.IsChecked = true;
            if (MapKindPhotos is not null) MapKindPhotos.IsChecked = false;
            if (MapKindVideos is not null) MapKindVideos.IsChecked = false;
            if (MapFavFilter is not null) MapFavFilter.IsChecked = false;
            if (MapMatchGalleryToggle is not null) MapMatchGalleryToggle.IsChecked = false;
            if (MapYearFilter is not null) MapYearFilter.SelectedIndex = 0;
            if (MapSourceFilter is not null) MapSourceFilter.SelectedIndex = 0;
            if (MapOriginFilter is not null) MapOriginFilter.SelectedIndex = 0;
            if (MapSearchBox is not null) MapSearchBox.Text = "";
        }
        finally
        {
            mapFiltersInitializing = false;
        }
        await PopulateMapFilterDropdownsAsync();
        SaveMapPreferencesToSettings();
        await RefreshMapDataAsync(MapViewportMode.FitAll);
    }

    private void MapViewModeClicked(object sender, RoutedEventArgs e)
    {
        if (MapViewHeatBtn is null || MapViewClustersBtn is null || MapViewPlacesBtn is null || MapViewPinsBtn is null || MapViewTrailBtn is null) return;
        if (ReferenceEquals(sender, MapViewHeatBtn)) mapViewMode = "heat";
        else if (ReferenceEquals(sender, MapViewClustersBtn)) mapViewMode = "clusters";
        else if (ReferenceEquals(sender, MapViewPlacesBtn)) mapViewMode = "places";
        else if (ReferenceEquals(sender, MapViewPinsBtn)) mapViewMode = "pins";
        else if (ReferenceEquals(sender, MapViewTrailBtn)) mapViewMode = "trail";

        UpdateViewModeButtons();
        SaveMapPreferencesToSettings();

        RenderOfflineCanvas();
        if (mapWebReady && mapWebView?.CoreWebView2 is not null)
        {
            mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setViewMode", viewMode = mapViewMode }));
        }
    }

    private void UpdateDrawerTabButtons()
    {
        if (MapTabPlaces is not null) MapTabPlaces.IsChecked = mapDrawerTab == "places";
        if (MapTabClusters is not null) MapTabClusters.IsChecked = mapDrawerTab == "clusters";
        if (MapTabPlaceTags is not null) MapTabPlaceTags.IsChecked = mapDrawerTab == "placeTags";
        if (MapTabUnlocated is not null) MapTabUnlocated.IsChecked = mapDrawerTab == "unlocated";
    }

    private void MapDrawerTabClicked(object sender, RoutedEventArgs e)
    {
        if (MapTabPlaces is null || MapTabClusters is null || MapTabPlaceTags is null || MapTabUnlocated is null) return;
        if (ReferenceEquals(sender, MapTabPlaces)) mapDrawerTab = "places";
        else if (ReferenceEquals(sender, MapTabClusters)) mapDrawerTab = "clusters";
        else if (ReferenceEquals(sender, MapTabPlaceTags)) mapDrawerTab = "placeTags";
        else if (ReferenceEquals(sender, MapTabUnlocated)) mapDrawerTab = "unlocated";

        UpdateDrawerTabButtons();
        UpdateDrawerEmptyState();
    }

    private void DismissMapConsentBanner(object sender, RoutedEventArgs e)
    {
        settings.MapConsentNoticeDismissed = true;
        MapConsentCard.Visibility = Visibility.Collapsed;
        try { settings.Save(); } catch { }
    }

    // ---- Offline Interactive Canvas Rendering & Unified Mercator Projection ----

    private void PersistOfflineViewport()
    {
        settings.MapHasSavedViewport = true;
        settings.MapCenterLat = Math.Clamp(offlineCenterLat, -85.0, 85.0);
        settings.MapCenterLon = Math.Clamp(offlineCenterLon, -180.0, 180.0);
        settings.MapZoom = Math.Clamp(offlineZoom, 1.0, 18.0);
    }

    private void FocusOfflineBounds(double minLat, double maxLat, double minLon, double maxLon)
    {
        offlineCenterLat = Math.Clamp((minLat + maxLat) / 2.0, -85.0, 85.0);
        offlineCenterLon = Math.Clamp((minLon + maxLon) / 2.0, -180.0, 180.0);
        var span = Math.Max(0.01, Math.Max(maxLat - minLat, maxLon - minLon));
        offlineZoom = span > 120 ? 1.8
            : span > 45 ? 3.0
            : span > 15 ? 4.8
            : span > 4 ? 7.0
            : span > 0.8 ? 10.5
            : span > 0.15 ? 12.2
            : 13.5;
        PersistOfflineViewport();
        RenderOfflineCanvas();
        if (mapWebReady && mapWebView?.CoreWebView2 is not null && MapWebHost.Visibility == Visibility.Visible)
        {
            mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new
            {
                type = "fitBounds",
                minLat, maxLat, minLon, maxLon
            }));
        }
    }

    private void FitOfflineViewport()
    {
        if (currentMapMarkers.Count == 0)
        {
            offlineCenterLat = 20.0;
            offlineCenterLon = 0.0;
            offlineZoom = 2.0;
            RenderOfflineCanvas();
            return;
        }

        double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
        foreach (var m in currentMapMarkers)
        {
            if (m.Latitude < minLat) minLat = m.Latitude;
            if (m.Latitude > maxLat) maxLat = m.Latitude;
            if (m.Longitude < minLon) minLon = m.Longitude;
            if (m.Longitude > maxLon) maxLon = m.Longitude;
        }
        FocusOfflineBounds(minLat, maxLat, minLon, maxLon);
    }

    private (double Scale, double OriginX, double OriginY) GetOfflineProjection(double w, double h)
    {
        var worldSize = 256.0 * Math.Pow(2.0, Math.Clamp(offlineZoom, 1.0, 18.0));
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
        var gridColor = isLight ? Color.FromArgb(20, 0, 0, 0) : Color.FromArgb(22, 255, 255, 255);
        var landFill = isLight ? Color.FromArgb(42, 85, 95, 110) : Color.FromArgb(36, 200, 212, 232);
        var landStroke = isLight ? Color.FromArgb(85, 70, 80, 95) : Color.FromArgb(68, 195, 210, 230);
        var gridBrush = new SolidColorBrush(gridColor);
        var landFillBrush = new SolidColorBrush(landFill);
        var landStrokeBrush = new SolidColorBrush(landStroke);

        var (scale, ox, oy) = GetOfflineProjection(w, h);

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

        if (mapViewMode == "trail" && currentMapMarkers.Count > 1)
        {
            var ordered = currentMapMarkers.OrderBy(x => x.DateTicks).ToArray();
            var glowTrail = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(72, 59, 130, 246)),
                StrokeThickness = 6
            };
            var trail = new Polyline
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 59, 130, 246)),
                StrokeThickness = 2.2,
                StrokeDashArray = [5, 4]
            };
            foreach (var m in ordered)
            {
                var px = OfflineWorldMap.LonToNormX(m.Longitude) * scale - ox;
                var py = OfflineWorldMap.LatToNormY(m.Latitude) * scale - oy;
                var wp = new WinPoint(px, py);
                glowTrail.Points.Add(wp);
                trail.Points.Add(wp);
            }
            OfflineMapCanvas.Children.Add(glowTrail);
            OfflineMapCanvas.Children.Add(trail);
        }

        var visible = new List<GeoMarker>();
        foreach (var m in currentMapMarkers)
        {
            var px = OfflineWorldMap.LonToNormX(m.Longitude) * scale - ox;
            var py = OfflineWorldMap.LatToNormY(m.Latitude) * scale - oy;
            if (px < -40 || px > w + 40 || py < -40 || py > h + 40) continue;
            visible.Add(m);
        }

        if (mapViewMode == "heat")
        {
            RenderOfflineHeatLayer(visible, scale, ox, oy);
        }
        else if (mapViewMode == "places")
        {
            RenderOfflinePlacesLayer(w, h, scale, ox, oy);
        }
        else
        {
            RenderOfflineMarkerLayer(visible, scale, ox, oy);
        }

        if (OfflineMapModeBadge is not null)
        {
            OfflineMapModeBadge.Text = currentVisitedPlaces.Count > 0
                ? $"Offline · {visible.Count:N0} in view · {currentVisitedPlaces.Count:N0} places"
                : $"Offline · {visible.Count:N0} in view";
        }

        if (MapStripToggle?.IsChecked == true)
            _ = RefreshMapFilmstripAsync(visible.Take(24).ToArray());
    }

    private void RenderOfflineHeatLayer(List<GeoMarker> visible, double scale, double ox, double oy)
    {
        if (visible.Count == 0) return;

        double cellDeg = Math.Clamp(10.0 / Math.Pow(2.0, offlineZoom), 0.008, 4.0);
        var heatCells = MapOverviewBuilder.BuildHeatCells(visible, cellDeg);
        double maxWeight = Math.Max(1.0, heatCells.Max(c => c.Weight));

        // 1. Concentric translucent thermal glow circles
        foreach (var cell in heatCells.Take(300))
        {
            var px = OfflineWorldMap.LonToNormX(cell.CenterLon) * scale - ox;
            var py = OfflineWorldMap.LatToNormY(cell.CenterLat) * scale - oy;
            var ratio = Math.Clamp(cell.Weight / maxWeight, 0.15, 1.0);
            var radius = 20.0 + ratio * 34.0;

            Color core = ratio > 0.78 ? Color.FromArgb(185, 239, 68, 68)
                : ratio > 0.52 ? Color.FromArgb(170, 245, 158, 11)
                : ratio > 0.30 ? Color.FromArgb(155, 16, 185, 129)
                : Color.FromArgb(140, 56, 189, 248);
            Color mid = ratio > 0.52 ? Color.FromArgb(85, 245, 158, 11) : Color.FromArgb(75, 56, 189, 248);

            var grad = new RadialGradientBrush
            {
                Center = new WinPoint(0.5, 0.5),
                GradientOrigin = new WinPoint(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5
            };
            grad.GradientStops.Add(new GradientStop { Color = core, Offset = 0.0 });
            grad.GradientStops.Add(new GradientStop { Color = mid, Offset = 0.45 });
            grad.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, mid.R, mid.G, mid.B), Offset = 1.0 });

            var glow = new Ellipse
            {
                Width = radius * 2.0,
                Height = radius * 2.0,
                Fill = grad,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(glow, px - radius);
            Canvas.SetTop(glow, py - radius);
            OfflineMapCanvas.Children.Add(glow);
        }

        // 2. Crisp clickable core hotspots & top place pills
        var badgeBg = new SolidColorBrush(Color.FromArgb(208, 22, 24, 30));
        var badgeBorder = new SolidColorBrush(Color.FromArgb(110, 255, 255, 255));
        var whiteBrush = new SolidColorBrush(Colors.White);
        var coreBrush = new SolidColorBrush(Color.FromArgb(240, 245, 158, 11));

        foreach (var cell in heatCells.Take(120))
        {
            var px = OfflineWorldMap.LonToNormX(cell.CenterLon) * scale - ox;
            var py = OfflineWorldMap.LatToNormY(cell.CenterLat) * scale - oy;

            FrameworkElement hotspot;
            if (cell.Count >= 3)
            {
                var text = !string.IsNullOrWhiteSpace(cell.TopLocationName) && offlineZoom >= 5.0
                    ? $"{cell.TopLocationName.Split(',')[0]} · {cell.Count:N0}"
                    : cell.Count.ToString(CultureInfo.InvariantCulture);
                hotspot = new Border
                {
                    Background = badgeBg,
                    BorderBrush = badgeBorder,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(999),
                    Padding = new Thickness(7, 2, 7, 2),
                    Child = new TextBlock { Text = text, Foreground = whiteBrush, FontSize = 11, FontWeight = FontWeights.SemiBold }
                };
            }
            else
            {
                hotspot = new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(5),
                    Background = whiteBrush,
                    BorderBrush = coreBrush,
                    BorderThickness = new Thickness(2)
                };
            }

            ToolTipService.SetToolTip(hotspot, string.IsNullOrWhiteSpace(cell.TopLocationName)
                ? $"{cell.Count:N0} item(s) around {cell.CenterLat:0.00}, {cell.CenterLon:0.00}"
                : $"{cell.TopLocationName} · {cell.Count:N0} item(s)");

            hotspot.Tapped += async (_, args) =>
            {
                if (mapPinMode) return;
                args.Handled = true;
                if (cell.Count > 1 && offlineZoom < 12.0)
                {
                    offlineCenterLat = cell.CenterLat;
                    offlineCenterLon = cell.CenterLon;
                    offlineZoom = Math.Min(16.0, offlineZoom + 2.2);
                    PersistOfflineViewport();
                    RenderOfflineCanvas();
                }
                else
                {
                    await OpenMediaExternallyAsync(cell.SampleMarker.SourceId, cell.SampleMarker.RelativePath);
                }
            };

            hotspot.Measure(new global::Windows.Foundation.Size(200, 40));
            Canvas.SetLeft(hotspot, px - hotspot.DesiredSize.Width / 2.0);
            Canvas.SetTop(hotspot, py - hotspot.DesiredSize.Height / 2.0);
            OfflineMapCanvas.Children.Add(hotspot);
        }
    }

    private void RenderOfflinePlacesLayer(double w, double h, double scale, double ox, double oy)
    {
        var isLight = Root.ActualTheme == ElementTheme.Light;
        var pillBg = new SolidColorBrush(isLight ? Color.FromArgb(236, 250, 249, 246) : Color.FromArgb(230, 24, 25, 30));
        var pillFg = new SolidColorBrush(isLight ? Color.FromArgb(255, 24, 24, 27) : Color.FromArgb(255, 244, 244, 245));
        var borderNormal = new SolidColorBrush(isLight ? Color.FromArgb(45, 0, 0, 0) : Color.FromArgb(60, 255, 255, 255));
        var borderFav = new SolidColorBrush(Color.FromArgb(180, 224, 145, 36));
        var countBlue = new SolidColorBrush(Color.FromArgb(255, 59, 130, 246));
        var countGold = new SolidColorBrush(Color.FromArgb(255, 224, 145, 36));
        var whiteBrush = new SolidColorBrush(Colors.White);

        var placed = new List<(double X, double Y, double W)>();
        foreach (var place in currentVisitedPlaces)
        {
            var px = OfflineWorldMap.LonToNormX(place.CenterLon) * scale - ox;
            var py = OfflineWorldMap.LatToNormY(place.CenterLat) * scale - oy;
            if (px < -100 || px > w + 100 || py < -30 || py > h + 30) continue;

            var estW = Math.Min(210.0, 58.0 + place.Title.Length * 6.5);
            if (placed.Any(b => Math.Abs(px - b.X) < (estW + b.W) * 0.48 && Math.Abs(py - b.Y) < 28.0))
                continue;
            placed.Add((px, py, estW));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            row.Children.Add(new TextBlock
            {
                Text = place.Title,
                Foreground = pillFg,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                MaxWidth = 148,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new Border
            {
                Background = place.FavoriteCount > 0 ? countGold : countBlue,
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(6, 1, 6, 1),
                Child = new TextBlock
                {
                    Text = place.Count > 999 ? $"{Math.Round(place.Count / 1000.0)}k" : place.Count.ToString(),
                    Foreground = whiteBrush,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold
                }
            });

            var pill = new Border
            {
                Background = pillBg,
                BorderBrush = place.FavoriteCount > 0 ? borderFav : borderNormal,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(10, 4, 8, 4),
                Child = row
            };
            ToolTipService.SetToolTip(pill, $"{place.Title}\n{place.Subtitle}");
            pill.Tapped += async (_, args) =>
            {
                if (mapPinMode) return;
                args.Handled = true;
                if (offlineZoom < 10.5)
                {
                    FocusOfflineBounds(place.MinLat, place.MaxLat, place.MinLon, place.MaxLon);
                }
                else
                {
                    await OpenMediaExternallyAsync(place.Representative.SourceId, place.Representative.RelativePath);
                }
            };

            pill.Measure(new global::Windows.Foundation.Size(240, 40));
            Canvas.SetLeft(pill, px - pill.DesiredSize.Width / 2.0);
            Canvas.SetTop(pill, py - pill.DesiredSize.Height / 2.0);
            OfflineMapCanvas.Children.Add(pill);
        }
    }

    private void RenderOfflineMarkerLayer(List<GeoMarker> visible, double scale, double ox, double oy)
    {
        double cellPx = mapViewMode == "pins" ? 10 : (mapViewMode == "trail" ? 14 : 38);
        var cells = new Dictionary<(int gx, int gy), List<GeoMarker>>();
        foreach (var m in visible)
        {
            var px = OfflineWorldMap.LonToNormX(m.Longitude) * scale - ox;
            var py = OfflineWorldMap.LatToNormY(m.Latitude) * scale - oy;
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
                    await OpenMediaExternallyAsync(m.SourceId, m.RelativePath);
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
                    offlineZoom = Math.Min(16.0, offlineZoom + 2.0);
                    PersistOfflineViewport();
                    RenderOfflineCanvas();
                };
                Canvas.SetLeft(pill, px - size / 2.0);
                Canvas.SetTop(pill, py - size / 2.0);
                OfflineMapCanvas.Children.Add(pill);
            }
        }
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

        var worldSize = 256.0 * Math.Pow(2.0, Math.Clamp(offlineZoom, 1.0, 18.0));
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
        PersistOfflineViewport();

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
        var props = e.GetCurrentPoint(OfflineMapCanvas);
        var delta = props.Properties.MouseWheelDelta;
        var w = Math.Max(320, OfflineMapCanvas.ActualWidth);
        var h = Math.Max(200, OfflineMapCanvas.ActualHeight);
        var pt = props.Position;

        var (oldScale, oldOx, oldOy) = GetOfflineProjection(w, h);
        var focusLon = OfflineWorldMap.NormXToLon((oldOx + pt.X) / oldScale);
        var focusLat = OfflineWorldMap.NormYToLat((oldOy + pt.Y) / oldScale);

        offlineZoom = Math.Clamp(delta > 0 ? offlineZoom + 0.65 : offlineZoom - 0.65, 1.2, 18.0);
        var newScale = 256.0 * Math.Pow(2.0, offlineZoom);
        var newFocusX = OfflineWorldMap.LonToNormX(focusLon) * newScale;
        var newFocusY = OfflineWorldMap.LatToNormY(focusLat) * newScale;
        var newCx = newFocusX - (pt.X - w / 2.0);
        var newCy = newFocusY - (pt.Y - h / 2.0);
        offlineCenterLon = OfflineWorldMap.NormXToLon(newCx / newScale);
        offlineCenterLat = OfflineWorldMap.NormYToLat(newCy / newScale);
        PersistOfflineViewport();
        RenderOfflineCanvas();
    }

    private void OfflineZoomInClicked(object sender, RoutedEventArgs e)
    {
        offlineZoom = Math.Clamp(offlineZoom + 1.0, 1.2, 18.0);
        PersistOfflineViewport();
        RenderOfflineCanvas();
    }

    private void OfflineZoomOutClicked(object sender, RoutedEventArgs e)
    {
        offlineZoom = Math.Clamp(offlineZoom - 1.0, 1.2, 18.0);
        PersistOfflineViewport();
        RenderOfflineCanvas();
    }

    private void OfflineFitClicked(object sender, RoutedEventArgs e) => FitOfflineViewport();

    private async void OfflineFilterBoundsClicked(object sender, RoutedEventArgs e)
    {
        activeMapBounds = GetOfflineViewportBounds();
        PersistOfflineViewport();
        SaveMapPreferencesToSettings();
        MapOverlay.Visibility = Visibility.Collapsed;
        await Guard(Refresh);
    }

    // ---- Filmstrip Viewport Loader ----

    private async Task RefreshMapFilmstripAsync(IReadOnlyList<GeoMarker> visible)
    {
        if (MapStripToggle?.IsChecked != true) return;
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
                Tooltip = $"{m.Name}\n{t}\n{m.Latitude:0.0000}, {m.Longitude:0.0000}\nClick to open in default app"
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
        await OpenMediaExternallyAsync(card.Marker.SourceId, card.Marker.RelativePath);
    }

    // ---- Online WebView2 Map ----

    private async void ToggleMapModeClicked(object sender, RoutedEventArgs e)
    {
        if (!mapOnlineConsentGranted || MapWebHost.Visibility != Visibility.Visible)
        {
            mapOnlineConsentGranted = true;
            await EnsureAndPushOnlineMapAsync(preserveViewport: true);
        }
        else
        {
            MapWebHost.Visibility = Visibility.Collapsed;
            MapOfflinePanel.Visibility = Visibility.Visible;
            UpdateMapModeHeaderButton();
            RenderOfflineCanvas();
        }
    }

    private async void GrantMapOnlineConsent(object sender, RoutedEventArgs e)
    {
        mapOnlineConsentGranted = true;
        await EnsureAndPushOnlineMapAsync(preserveViewport: true);
    }

    private async Task EnsureAndPushOnlineMapAsync(bool preserveViewport = true)
    {
        try
        {
            MapOfflinePanel.Visibility = Visibility.Collapsed;
            MapWebHost.Visibility = Visibility.Visible;
            UpdateMapModeHeaderButton();

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

            PushMarkersToWebView(preserveViewport: preserveViewport);
        }
        catch (Exception ex)
        {
            mapOnlineConsentGranted = false;
            MapWebHost.Visibility = Visibility.Collapsed;
            MapOfflinePanel.Visibility = Visibility.Visible;
            UpdateMapModeHeaderButton();
            Notify($"Online map view could not start ({ex.Message}). Showing offline coordinate canvas.", true);
        }
    }

    private void PushMarkersToWebView(bool preserveViewport = true)
    {
        if (!mapWebReady || mapWebView?.CoreWebView2 is null) return;
        var payload = new
        {
            type = "setMarkers",
            theme = Root.ActualTheme == ElementTheme.Light ? "light" : "dark",
            style = string.IsNullOrWhiteSpace(settings.MapStyle) ? null : settings.MapStyle,
            viewMode = mapViewMode,
            preserveViewport = preserveViewport || settings.MapHasSavedViewport,
            centerLat = offlineCenterLat,
            centerLon = offlineCenterLon,
            zoom = offlineZoom,
            places = currentVisitedPlaces.Select(p => new
            {
                title = p.Title,
                subtitle = p.Subtitle,
                count = p.Count,
                favoriteCount = p.FavoriteCount,
                centerLat = p.CenterLat,
                centerLon = p.CenterLon,
                minLat = p.MinLat,
                maxLat = p.MaxLat,
                minLon = p.MinLon,
                maxLon = p.MaxLon,
                representative = new
                {
                    sourceId = p.Representative.SourceId,
                    relativePath = p.Representative.RelativePath,
                    latitude = p.Representative.Latitude,
                    longitude = p.Representative.Longitude,
                    name = p.Representative.Name,
                    kind = p.Representative.Kind,
                    favorite = p.Representative.Favorite,
                    dateTicks = p.Representative.DateTicks,
                    locationName = p.Representative.LocationName,
                    origin = p.Representative.LocationOrigin,
                    cameraModel = p.Representative.CameraModel,
                    dateText = new DateTime(p.Representative.DateTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                }
            }),
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
                        var isPng = bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
                        var dataUri = $"data:{(isPng ? "image/png" : "image/jpeg")};base64," + Convert.ToBase64String(bytes);
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
            else if (msgType is "openFile" or "openPreview")
            {
                var sourceId = root.GetProperty("sourceId").GetString() ?? "";
                var relPath = root.GetProperty("relativePath").GetString() ?? "";
                await OpenMediaExternallyAsync(sourceId, relPath);
            }
            else if (msgType == "filterBounds")
            {
                var minLat = root.GetProperty("minLat").GetDouble();
                var maxLat = root.GetProperty("maxLat").GetDouble();
                var minLon = root.GetProperty("minLon").GetDouble();
                var maxLon = root.GetProperty("maxLon").GetDouble();
                activeMapBounds = new GeoBounds(minLat, maxLat, minLon, maxLon);
                PersistOfflineViewport();
                SaveMapPreferencesToSettings();
                MapOverlay.Visibility = Visibility.Collapsed;
                await Guard(Refresh);
            }
            else if (msgType == "pickedCoord")
            {
                var lat = root.GetProperty("lat").GetDouble();
                var lon = root.GetProperty("lon").GetDouble();
                await HandlePickedCoordinateAsync(lat, lon);
            }
            else if (msgType == "styleChanged" && root.TryGetProperty("style", out var stProp))
            {
                settings.MapStyle = stProp.GetString() ?? "";
            }
            else if (msgType == "escapePressed")
            {
                if (mapPinMode)
                {
                    mapPinMode = false;
                    MapPinModeToggle.IsChecked = false;
                    UpdatePinModeVisuals();
                }
                else
                {
                    CloseMapView(this, new RoutedEventArgs());
                }
            }
            else if (msgType == "viewportChanged")
            {
                if (root.TryGetProperty("centerLat", out var latP) &&
                    root.TryGetProperty("centerLon", out var lonP) &&
                    root.TryGetProperty("zoom", out var zoomP))
                {
                    offlineCenterLat = Math.Clamp(latP.GetDouble(), -85.0, 85.0);
                    offlineCenterLon = Math.Clamp(lonP.GetDouble(), -180.0, 180.0);
                    offlineZoom = Math.Clamp(zoomP.GetDouble(), 1.2, 18.0);
                    PersistOfflineViewport();
                }

                if (MapStripToggle?.IsChecked == true && root.TryGetProperty("visibleKeys", out var keysProp))
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
        }
        catch { }
    }

    // ---- Drawer Actions ----

    private void MapPlaceFocusClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VisitedPlaceSummary place })
        {
            FocusOfflineBounds(place.MinLat, place.MaxLat, place.MinLon, place.MaxLon);
        }
    }

    private async void MapPlaceFilterClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VisitedPlaceSummary place })
        {
            activeMapBounds = new GeoBounds(place.MinLat, place.MaxLat, place.MinLon, place.MaxLon);
            PersistOfflineViewport();
            SaveMapPreferencesToSettings();
            MapOverlay.Visibility = Visibility.Collapsed;
            await Guard(Refresh);
        }
    }

    private async void MapPlaceOpenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: VisitedPlaceSummary place })
            await OpenMediaExternallyAsync(place.Representative.SourceId, place.Representative.RelativePath);
    }

    private void MapOfflineGroupZoomClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OfflineGeoCluster cluster } && cluster.Count > 0)
        {
            FocusOfflineBounds(cluster.MinLat, cluster.MaxLat, cluster.MinLon, cluster.MaxLon);
        }
    }

    private async void MapOfflineGroupFilterClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: OfflineGeoCluster cluster } && cluster.MinLat != cluster.MaxLat)
        {
            activeMapBounds = new GeoBounds(cluster.MinLat, cluster.MaxLat, cluster.MinLon, cluster.MaxLon);
            PersistOfflineViewport();
            SaveMapPreferencesToSettings();
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

    private async void MapDrawerItemOpenClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: MapDrawerMediaRow row })
            await OpenMediaExternallyAsync(row.Media.SourceId, row.Media.RelativePath);
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
        mapSearchDebounceCts?.Cancel();
        SaveMapPreferencesToSettings();
        try { settings.Save(); } catch { }
        mapPinMode = false;
        MapPinModeToggle.IsChecked = false;
        if (MapNotice is not null) MapNotice.IsOpen = false;
        MapOverlay.Visibility = Visibility.Collapsed;
    }
}
