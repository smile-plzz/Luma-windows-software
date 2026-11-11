using Luma.Core;
using Windows.Storage;

namespace Luma.Windows;

public sealed class WindowsMetadataReader : IMetadataReader
{
    private static (double? Lat, double? Lon) ValidCoords(double? lat, double? lon)
    {
        if (lat is not double la || lon is not double lo) return (null, null);
        if (!double.IsFinite(la) || !double.IsFinite(lo)) return (null, null);
        if (la < -90 || la > 90 || lo < -180 || lo > 180) return (null, null);
        if (Math.Abs(la) < 1e-6 && Math.Abs(lo) < 1e-6) return (null, null);
        return (Math.Round(la, 6), Math.Round(lo, 6));
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
                try
                {
                    var props = await file.Properties.RetrievePropertiesAsync(new[]
                    {
                        "System.GPS.LatitudeDecimal",
                        "System.GPS.LongitudeDecimal"
                    }).AsTask(ct);
                    if (props.TryGetValue("System.GPS.LatitudeDecimal", out var latObj) && latObj is not null &&
                        props.TryGetValue("System.GPS.LongitudeDecimal", out var lonObj) && lonObj is not null)
                    {
                        (vLat, vLon) = ValidCoords(Convert.ToDouble(latObj), Convert.ToDouble(lonObj));
                    }
                }
                catch { }
                // Encoding dates are not capture dates. Keep modification fallback for videos.
                return new(null, "Video: modification date fallback", (int)video.Width, (int)video.Height, video.Duration.TotalSeconds, (int)video.Orientation, vLat, vLon);
            }

            var photo = await file.Properties.GetImagePropertiesAsync().AsTask(ct);
            long? taken = photo.DateTaken.Year >= 1900 && photo.DateTaken <= DateTimeOffset.UtcNow.AddDays(2) ? photo.DateTaken.UtcTicks : null;
            var (pLat, pLon) = ValidCoords(photo.Latitude, photo.Longitude);

            string? cameraModel = null;
            int? iso = null;
            double? fNumber = null, exposureSec = null, focalLength = null;
            try
            {
                var props = await file.Properties.RetrievePropertiesAsync(new[]
                {
                    "System.Photo.CameraModel",
                    "System.Photo.ISOSpeed",
                    "System.Photo.FNumber",
                    "System.Photo.ExposureTime",
                    "System.Photo.FocalLength"
                }).AsTask(ct);
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
            }
            catch { }

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
                focalLength);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or UnauthorizedAccessException or ArgumentException)
        { return new(null, "Metadata unavailable; modification date fallback"); }
    }
}
