using Luma.Core;
using Windows.Storage;

namespace Luma.Windows;

public sealed class WindowsMetadataReader : IMetadataReader
{
    private static readonly string[] VideoPropertyKeys =
    [
        "System.GPS.LatitudeDecimal",
        "System.GPS.LongitudeDecimal",
        "System.GPS.Latitude",
        "System.GPS.LatitudeRef",
        "System.GPS.Longitude",
        "System.GPS.LongitudeRef",
        "System.GPS.AreaInformation"
    ];

    private static readonly string[] PhotoPropertyKeys =
    [
        "System.Photo.CameraModel",
        "System.Photo.ISOSpeed",
        "System.Photo.FNumber",
        "System.Photo.ExposureTime",
        "System.Photo.FocalLength",
        "System.GPS.LatitudeDecimal",
        "System.GPS.LongitudeDecimal",
        "System.GPS.Latitude",
        "System.GPS.LatitudeRef",
        "System.GPS.Longitude",
        "System.GPS.LongitudeRef",
        "System.GPS.AreaInformation"
    ];

    private static (double? Lat, double? Lon, string? AreaName) ParseShellGpsProperties(IDictionary<string, object> props)
    {
        double? lat = null, lon = null;
        if (props.TryGetValue("System.GPS.LatitudeDecimal", out var latDec) && latDec is not null &&
            props.TryGetValue("System.GPS.LongitudeDecimal", out var lonDec) && lonDec is not null)
        {
            (lat, lon) = MinimalGpsExtractor.NormalizeCoords(Convert.ToDouble(latDec), Convert.ToDouble(lonDec));
        }

        if ((lat is null || lon is null) &&
            props.TryGetValue("System.GPS.Latitude", out var latArr) && latArr is not null &&
            props.TryGetValue("System.GPS.Longitude", out var lonArr) && lonArr is not null)
        {
            var latRef = props.TryGetValue("System.GPS.LatitudeRef", out var lr) ? lr?.ToString() : null;
            var lonRef = props.TryGetValue("System.GPS.LongitudeRef", out var loR) ? loR?.ToString() : null;
            var la = ConvertShellArrayToDecimal(latArr, latRef);
            var lo = ConvertShellArrayToDecimal(lonArr, lonRef);
            (lat, lon) = MinimalGpsExtractor.NormalizeCoords(la, lo);
        }

        string? area = null;
        if (props.TryGetValue("System.GPS.AreaInformation", out var areaObj) && areaObj is string areaStr && !string.IsNullOrWhiteSpace(areaStr))
            area = areaStr.Trim();

        return (lat, lon, area);
    }

    private static double? ConvertShellArrayToDecimal(object value, string? hemisphereRef)
    {
        if (value is double d) return MinimalGpsExtractor.ConvertDmsToDecimal([d], hemisphereRef);
        if (value is string s) return MinimalGpsExtractor.TryParseFlexibleCoordString(s, hemisphereRef);
        if (value is System.Collections.IEnumerable seq)
        {
            var nums = new List<double>(3);
            foreach (var item in seq)
            {
                if (item is null) continue;
                nums.Add(Convert.ToDouble(item));
                if (nums.Count == 3) break;
            }
            return MinimalGpsExtractor.ConvertDmsToDecimal(nums, hemisphereRef);
        }
        return null;
    }

    public async Task<MediaMetadata> ReadAsync(string path, string kind, CancellationToken ct)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            if (kind == "video")
            {
                var video = await file.Properties.GetVideoPropertiesAsync().AsTask(ct);
                double? vLat = null, vLon = null;
                string? areaName = null, locOrigin = null;
                try
                {
                    var props = await file.Properties.RetrievePropertiesAsync(VideoPropertyKeys).AsTask(ct);
                    var shell = ParseShellGpsProperties(props);
                    vLat = shell.Lat;
                    vLon = shell.Lon;
                    areaName = shell.AreaName;
                    if (vLat.HasValue) locOrigin = "windows-shell";
                }
                catch { }

                if (vLat is null || vLon is null || string.IsNullOrWhiteSpace(areaName))
                {
                    var extracted = MinimalGpsExtractor.ExtractFromFile(path);
                    if (vLat is null && extracted.HasCoordinates)
                    {
                        vLat = extracted.Latitude;
                        vLon = extracted.Longitude;
                        locOrigin = extracted.Origin;
                    }
                    areaName ??= extracted.LocationName;
                    locOrigin ??= extracted.Origin;
                }

                // Encoding dates are not capture dates. Keep modification fallback for videos.
                return new(
                    null,
                    "Video: modification date fallback",
                    (int)video.Width,
                    (int)video.Height,
                    video.Duration.TotalSeconds,
                    (int)video.Orientation,
                    vLat,
                    vLon,
                    LocationName: areaName,
                    LocationOrigin: locOrigin,
                    MetaVersion: MediaCache.CurrentMetadataVersion);
            }

            var photo = await file.Properties.GetImagePropertiesAsync().AsTask(ct);
            long? taken = photo.DateTaken.Year >= 1900 && photo.DateTaken <= DateTimeOffset.UtcNow.AddDays(2) ? photo.DateTaken.UtcTicks : null;
            var (pLat, pLon) = MinimalGpsExtractor.NormalizeCoords(photo.Latitude, photo.Longitude);
            string? pOrigin = pLat.HasValue ? "exif" : null;
            string? pArea = null;

            string? cameraModel = null;
            int? iso = null;
            double? fNumber = null, exposureSec = null, focalLength = null;
            try
            {
                var props = await file.Properties.RetrievePropertiesAsync(PhotoPropertyKeys).AsTask(ct);
                if (props.TryGetValue("System.Photo.CameraModel", out var camObj) && camObj is string cam && !string.IsNullOrWhiteSpace(cam))
                    cameraModel = cam.Trim();
                if (props.TryGetValue("System.Photo.ISOSpeed", out var isoObj) && isoObj is not null)
                    iso = Convert.ToInt32(isoObj);
                if (props.TryGetValue("System.Photo.FNumber", out var fObj) && fObj is not null && Convert.ToDouble(fObj) > 0)
                    fNumber = Math.Round(Convert.ToDouble(fObj), 2);
                if (props.TryGetValue("System.Photo.ExposureTime", out var expObj) && expObj is not null && Convert.ToDouble(expObj) > 0)
                    exposureSec = Convert.ToDouble(expObj);
                if (props.TryGetValue("System.Photo.FocalLength", out var focObj) && focObj is not null && Convert.ToDouble(focObj) > 0)
                    focalLength = Math.Round(Convert.ToDouble(focObj), 1);

                var shellGps = ParseShellGpsProperties(props);
                if ((pLat is null || pLon is null) && shellGps.Lat.HasValue && shellGps.Lon.HasValue)
                {
                    pLat = shellGps.Lat;
                    pLon = shellGps.Lon;
                    pOrigin = "windows-shell";
                }
                pArea = shellGps.AreaName;
            }
            catch { }

            if (pLat is null || pLon is null || string.IsNullOrWhiteSpace(pArea))
            {
                var extracted = MinimalGpsExtractor.ExtractFromFile(path);
                if ((pLat is null || pLon is null) && extracted.HasCoordinates)
                {
                    pLat = extracted.Latitude;
                    pLon = extracted.Longitude;
                    pOrigin = extracted.Origin;
                }
                pArea ??= extracted.LocationName;
                pOrigin ??= extracted.Origin;
            }

            return new(
                taken,
                taken is null ? "Modification date fallback" : "Windows date taken; original timezone not verified",
                (int)photo.Width,
                (int)photo.Height,
                0,
                (int)photo.Orientation,
                pLat,
                pLon,
                cameraModel,
                iso,
                fNumber,
                exposureSec,
                focalLength,
                pArea,
                pOrigin,
                MediaCache.CurrentMetadataVersion);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Even when a codec or WinRT property handler is missing (e.g. RAW/DNG/HEIC), recover EXIF/XMP/sidecar GPS read-only.
            var fallback = MinimalGpsExtractor.ExtractFromFile(path);
            return new(
                null,
                "Metadata unavailable; modification date fallback",
                Latitude: fallback.Latitude,
                Longitude: fallback.Longitude,
                LocationName: fallback.LocationName,
                LocationOrigin: fallback.Origin,
                MetaVersion: MediaCache.CurrentMetadataVersion);
        }
    }
}
