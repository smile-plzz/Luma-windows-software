using System.Runtime.InteropServices;
using Luma.Core;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Luma.Windows;

/// <summary>Uses installed Windows codecs; unsupported media returns a placeholder result (null).</summary>
public sealed class WindowsThumbnailGenerator : IThumbnailGenerator
{
    public async Task<byte[]?> GenerateAsync(string path, int pixels, CancellationToken ct)
    {
        if (pixels is < 32 or > 1024) throw new ArgumentOutOfRangeException(nameof(pixels));
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
            // Shell thumbnail handlers are optional (notably on Windows Server). WIC image decoding is a reliable fallback.
            using (var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)pixels,
                ThumbnailOptions.UseCurrentScale).AsTask(ct))
            {
                if (thumbnail is not null && thumbnail.Type == ThumbnailType.Image && thumbnail.Size > 0)
                    return await ReadBytes(thumbnail, ct);
            }

            using var input = await file.OpenReadAsync().AsTask(ct);
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask(ct);
            // Fuzzing & hostile file guard: 100 megapixel ceiling and zero dimension check
            if ((long)decoder.PixelWidth * decoder.PixelHeight > 100_000_000 || decoder.PixelWidth == 0 || decoder.PixelHeight == 0) return null;

            var scale = Math.Min(1d, (double)pixels / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant
            };

            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                transform, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);

            using var output = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask(ct);
            encoder.SetSoftwareBitmap(bitmap);
            await encoder.FlushAsync().AsTask(ct);
            return await ReadBytes(output, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Corrupt, truncated, hostile, or unsupported codecs return null safely without crashing the host process.
            return null;
        }
    }

    private static async Task<byte[]?> ReadBytes(IRandomAccessStream stream, CancellationToken ct)
    {
        if (stream.Size == 0 || stream.Size > 16 * 1024 * 1024) return null;
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        var size = checked((uint)stream.Size);
        if (await reader.LoadAsync(size).AsTask(ct) != size) return null;
        var bytes = new byte[checked((int)size)];
        reader.ReadBytes(bytes);
        return bytes;
    }
}
