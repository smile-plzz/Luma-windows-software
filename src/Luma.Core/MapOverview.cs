using System.Globalization;

namespace Luma.Core;

public sealed record VisitedPlaceSummary(
    string Title,
    string Subtitle,
    int Count,
    int PhotoCount,
    int VideoCount,
    int FavoriteCount,
    double CenterLat,
    double CenterLon,
    double MinLat,
    double MaxLat,
    double MinLon,
    double MaxLon,
    long EarliestDateTicks,
    long LatestDateTicks,
    GeoMarker Representative,
    bool HasNamedPlace);

public sealed record MapHeatCell(
    double CenterLat,
    double CenterLon,
    double Weight,
    int Count,
    int FavoriteCount,
    string? TopLocationName,
    GeoMarker SampleMarker);

public static class MapOverviewBuilder
{
    public static IReadOnlyList<VisitedPlaceSummary> BuildVisitedPlaces(
        IReadOnlyList<GeoMarker> markers,
        int maxPlaces = 120)
    {
        if (markers.Count == 0 || maxPlaces <= 0) return Array.Empty<VisitedPlaceSummary>();

        var namedBuckets = new Dictionary<string, List<List<GeoMarker>>>(StringComparer.OrdinalIgnoreCase);
        var unnamedMarkers = new List<GeoMarker>();

        foreach (var m in markers)
        {
            if (string.IsNullOrWhiteSpace(m.LocationName))
            {
                unnamedMarkers.Add(m);
                continue;
            }

            var name = m.LocationName.Trim();
            if (!namedBuckets.TryGetValue(name, out var clusters))
            {
                clusters = [];
                namedBuckets[name] = clusters;
            }

            // Split identically named places on far continents (~1.2 degrees apart).
            List<GeoMarker>? target = null;
            foreach (var list in clusters)
            {
                var anchor = list[0];
                if (Math.Abs(anchor.Latitude - m.Latitude) <= 1.2 && Math.Abs(anchor.Longitude - m.Longitude) <= 1.2)
                {
                    target = list;
                    break;
                }
            }
            if (target is null)
            {
                target = new List<GeoMarker>();
                clusters.Add(target);
            }
            target.Add(m);
        }

        var workingGroups = new List<(string? Name, List<GeoMarker> Items)>();
        var namedCentroids = new List<(double Lat, double Lon, List<GeoMarker> Items)>();
        foreach (var (name, clusters) in namedBuckets)
        {
            foreach (var list in clusters)
            {
                workingGroups.Add((name, list));
                namedCentroids.Add((list.Average(x => x.Latitude), list.Average(x => x.Longitude), list));
            }
        }

        // Merge nearby unnamed markers (~12 km) into matching named regions, otherwise group into ~22 km regional cells.
        var unnamedGrid = new Dictionary<(int LatCell, int LonCell), List<GeoMarker>>();
        foreach (var m in unnamedMarkers)
        {
            List<GeoMarker>? nearestNamed = null;
            double bestDistSq = 0.11 * 0.11;
            foreach (var nc in namedCentroids)
            {
                var dLat = nc.Lat - m.Latitude;
                var dLon = nc.Lon - m.Longitude;
                var distSq = dLat * dLat + dLon * dLon;
                if (distSq <= bestDistSq)
                {
                    bestDistSq = distSq;
                    nearestNamed = nc.Items;
                }
            }

            if (nearestNamed is not null)
            {
                nearestNamed.Add(m);
                continue;
            }

            var latCell = (int)Math.Round(m.Latitude / 0.20);
            var cosLat = Math.Max(0.25, Math.Cos(m.Latitude * Math.PI / 180.0));
            var lonStep = 0.20 / cosLat;
            var lonCell = (int)Math.Round(m.Longitude / lonStep);
            var key = (latCell, lonCell);
            if (!unnamedGrid.TryGetValue(key, out var list))
                unnamedGrid[key] = list = new List<GeoMarker>();
            list.Add(m);
        }

        foreach (var list in unnamedGrid.Values)
            workingGroups.Add((null, list));

        var results = new List<VisitedPlaceSummary>(workingGroups.Count);
        foreach (var (name, items) in workingGroups)
        {
            double sumLat = 0, sumLon = 0;
            double minLat = 90, maxLat = -90, minLon = 180, maxLon = -180;
            int photos = 0, videos = 0, favs = 0;
            long minTicks = long.MaxValue, maxTicks = long.MinValue;
            GeoMarker? bestRep = null;

            foreach (var x in items)
            {
                sumLat += x.Latitude;
                sumLon += x.Longitude;
                if (x.Latitude < minLat) minLat = x.Latitude;
                if (x.Latitude > maxLat) maxLat = x.Latitude;
                if (x.Longitude < minLon) minLon = x.Longitude;
                if (x.Longitude > maxLon) maxLon = x.Longitude;
                if (string.Equals(x.Kind, "video", StringComparison.OrdinalIgnoreCase)) videos++;
                else photos++;
                if (x.Favorite) favs++;
                if (x.DateTicks < minTicks) minTicks = x.DateTicks;
                if (x.DateTicks > maxTicks) maxTicks = x.DateTicks;

                if (bestRep is null
                    || (x.Favorite && !bestRep.Favorite)
                    || (x.Favorite == bestRep.Favorite && x.DateTicks > bestRep.DateTicks))
                {
                    bestRep = x;
                }
            }

            var avgLat = sumLat / items.Count;
            var avgLon = sumLon / items.Count;
            var title = !string.IsNullOrWhiteSpace(name)
                ? name!
                : FormatApproxCoord(avgLat, avgLon);

            var countParts = new List<string>(2);
            if (photos > 0) countParts.Add(photos == 1 ? "1 photo" : $"{photos:N0} photos");
            if (videos > 0) countParts.Add(videos == 1 ? "1 video" : $"{videos:N0} videos");
            var dateSpan = FormatDateSpan(minTicks, maxTicks);
            var subtitle = dateSpan.Length > 0
                ? $"{string.Join(" · ", countParts)} · {dateSpan}"
                : string.Join(" · ", countParts);

            results.Add(new VisitedPlaceSummary(
                Title: title,
                Subtitle: subtitle,
                Count: items.Count,
                PhotoCount: photos,
                VideoCount: videos,
                FavoriteCount: favs,
                CenterLat: avgLat,
                CenterLon: avgLon,
                MinLat: Math.Max(-85.0, minLat - 0.015),
                MaxLat: Math.Min(85.0, maxLat + 0.015),
                MinLon: Math.Max(-180.0, minLon - 0.015),
                MaxLon: Math.Min(180.0, maxLon + 0.015),
                EarliestDateTicks: minTicks,
                LatestDateTicks: maxTicks,
                Representative: bestRep!,
                HasNamedPlace: !string.IsNullOrWhiteSpace(name)));
        }

        return results
            .OrderByDescending(p => p.Count)
            .ThenByDescending(p => p.FavoriteCount)
            .ThenByDescending(p => p.LatestDateTicks)
            .Take(maxPlaces)
            .ToArray();
    }

    public static IReadOnlyList<MapHeatCell> BuildHeatCells(
        IReadOnlyList<GeoMarker> markers,
        double cellDegrees = 0.25)
    {
        if (markers.Count == 0) return Array.Empty<MapHeatCell>();
        cellDegrees = Math.Clamp(cellDegrees, 0.002, 20.0);

        var grid = new Dictionary<(int LatCell, int LonCell), List<GeoMarker>>();
        foreach (var m in markers)
        {
            var key = ((int)Math.Floor(m.Latitude / cellDegrees), (int)Math.Floor(m.Longitude / cellDegrees));
            if (!grid.TryGetValue(key, out var list))
                grid[key] = list = new List<GeoMarker>();
            list.Add(m);
        }

        var cells = new List<MapHeatCell>(grid.Count);
        foreach (var list in grid.Values)
        {
            double sumLat = 0, sumLon = 0;
            int favCount = 0;
            GeoMarker sample = list[0];
            var placeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var m in list)
            {
                sumLat += m.Latitude;
                sumLon += m.Longitude;
                if (m.Favorite) favCount++;
                if (m.Favorite && !sample.Favorite || (m.Favorite == sample.Favorite && m.DateTicks > sample.DateTicks))
                    sample = m;
                if (!string.IsNullOrWhiteSpace(m.LocationName))
                {
                    var trimmed = m.LocationName.Trim();
                    placeCounts[trimmed] = placeCounts.GetValueOrDefault(trimmed) + 1;
                }
            }

            string? topPlace = null;
            int topPlaceCount = 0;
            foreach (var (place, cnt) in placeCounts)
            {
                if (cnt > topPlaceCount)
                {
                    topPlaceCount = cnt;
                    topPlace = place;
                }
            }

            // Log-compressed intensity keeps mega-clusters from washing out secondary trip hotspots.
            var weight = 1.0 + Math.Log2(list.Count) + Math.Min(1.5, favCount * 0.2);
            cells.Add(new MapHeatCell(
                sumLat / list.Count,
                sumLon / list.Count,
                weight,
                list.Count,
                favCount,
                topPlace,
                sample));
        }

        return cells.OrderByDescending(c => c.Weight).ToArray();
    }

    private static string FormatApproxCoord(double lat, double lon)
    {
        var ns = lat >= 0 ? "N" : "S";
        var ew = lon >= 0 ? "E" : "W";
        return string.Create(CultureInfo.InvariantCulture, $"{Math.Abs(lat):0.00}° {ns}, {Math.Abs(lon):0.00}° {ew}");
    }

    private static string FormatDateSpan(long minTicks, long maxTicks)
    {
        if (minTicks <= 0 || maxTicks < minTicks || minTicks > DateTime.MaxValue.Ticks || maxTicks > DateTime.MaxValue.Ticks)
            return "";
        var d0 = new DateTime(minTicks, DateTimeKind.Utc).ToLocalTime();
        var d1 = new DateTime(maxTicks, DateTimeKind.Utc).ToLocalTime();
        if (d0.Year == d1.Year && d0.Month == d1.Month && d0.Day == d1.Day)
            return d1.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);
        if (d0.Year == d1.Year && d0.Month == d1.Month)
            return d1.ToString("MMM yyyy", CultureInfo.InvariantCulture);
        return $"{d0.ToString("MMM yyyy", CultureInfo.InvariantCulture)} - {d1.ToString("MMM yyyy", CultureInfo.InvariantCulture)}";
    }
}
