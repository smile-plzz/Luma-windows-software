using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Luma.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Luma.App;

public sealed record OfflineGeoCluster(
    string Title,
    string Subtitle,
    double CenterLat,
    double CenterLon,
    double MinLat,
    double MaxLat,
    double MinLon,
    double MaxLon);

public sealed partial class MainWindow
{
    private bool mapOnlineConsentGranted;
    private WebView2? mapWebView;
    private bool mapWebReady;
    private IReadOnlyList<GeoMarker> currentMapMarkers = Array.Empty<GeoMarker>();
    private (double Lat, double Lon)? pendingMapFocus;

    private async void OpenMapView(object sender, RoutedEventArgs e) => await Guard(() => OpenMapAsync(null));
    private async void ShowSelectedOnMap(object sender, RoutedEventArgs e)
    {
        var card = PrimarySelectedCard() ?? contextCard;
        if (card?.Item.Metadata is { Latitude: double lat, Longitude: double lon })
            await Guard(() => OpenMapAsync((lat, lon)));
    }
    private async void PreviewShowOnMap(object sender, RoutedEventArgs e)
    {
        if (previewingCard?.Item.Metadata is { Latitude: double lat, Longitude: double lon })
        {
            DoClosePreview();
            await Guard(() => OpenMapAsync((lat, lon)));
        }
    }

    private async Task OpenMapAsync((double Lat, double Lon)? focusCoords)
    {
        pendingMapFocus = focusCoords;
        MapOverlay.Visibility = Visibility.Visible;
        var baseQuery = (activeQuery ?? CurrentQuery()) with { MapBounds = null, HasGpsOnly = true };
        currentMapMarkers = await Task.Run(() => catalog.QueryGeotaggedMarkersAsync(baseQuery, 10000));
        MapSummaryText.Text = $"{currentMapMarkers.Count:N0} geotagged item(s)";

        PopulateOfflineGeoClusters();

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
    }

    private void PopulateOfflineGeoClusters()
    {
        if (currentMapMarkers.Count == 0)
        {
            MapOfflineGroupsList.ItemsSource = new[]
            {
                new OfflineGeoCluster(
                    "No geotagged photos or videos in this view",
                    "Run Manage sources -> More -> Refresh metadata on connected drives to index GPS EXIF coordinates.",
                    0, 0, 0, 0, 0, 0)
            };
            return;
        }

        // Group coordinates into ~1-2 km cells (0.02 deg precision) purely offline
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
                var dateText = new DateTime(sample.DateTicks, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy");
                return new OfflineGeoCluster(
                    $"{Math.Abs(avgLat):0.000}° {ns}, {Math.Abs(avgLon):0.000}° {ew}  ·  {items.Length:N0} item(s)",
                    $"Latest: {sample.Name} ({dateText})",
                    avgLat, avgLon, minLat, maxLat, minLon, maxLon);
            })
            .ToArray();

        MapOfflineGroupsList.ItemsSource = clusters;
    }

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
                await mapWebView.EnsureCoreWebView2Async();
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

            if (mapWebReady && mapWebView?.CoreWebView2 is not null)
            {
                var payload = new
                {
                    type = "setMarkers",
                    markers = currentMapMarkers.Select(m => new
                    {
                        sourceId = m.SourceId,
                        relativePath = m.RelativePath,
                        latitude = m.Latitude,
                        longitude = m.Longitude,
                        name = m.Name,
                        favorite = m.Favorite,
                        dateText = new DateTime(m.DateTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                    })
                };
                mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));

                if (pendingMapFocus is { } focus)
                {
                    mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "focusPin", lat = focus.Lat, lon = focus.Lon }));
                    pendingMapFocus = null;
                }
            }
        }
        catch (Exception ex)
        {
            mapOnlineConsentGranted = false;
            MapWebHost.Visibility = Visibility.Collapsed;
            MapOfflinePanel.Visibility = Visibility.Visible;
            MapToggleModeButton.Visibility = Visibility.Visible;
            Notify($"Online map view could not start ({ex.Message}). Showing offline coordinate groups.", true);
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
                var page = await Task.Run(() => catalog.QueryAsync(new LibraryQuery(SourceId: sourceId, Folder: relPath, Limit: 5)));
                var match = page.Items.FirstOrDefault(i => i.Media.SourceId == sourceId && i.Media.RelativePath == relPath);
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
                    var page = await Task.Run(() => catalog.QueryAsync(new LibraryQuery(SourceId: sourceId, Search: Path.GetFileName(relPath), Limit: 20)));
                    var match = page.Items.FirstOrDefault(i => i.Media.SourceId == sourceId && i.Media.RelativePath == relPath);
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
        }
        catch { }
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

    private void CloseMapView(object sender, RoutedEventArgs e)
    {
        MapOverlay.Visibility = Visibility.Collapsed;
    }
}

