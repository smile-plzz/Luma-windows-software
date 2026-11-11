using Luma.Windows;
using Luma.Core;
using Xunit;

namespace Luma.Windows.Tests;

public sealed class WindowsTests
{
    [Fact]
    public async Task GeneratesAndCachesBitmapThroughVolumeIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "Luma-bitmap-আলো-" + Guid.NewGuid());
        var cacheRoot = Path.Combine(Path.GetTempPath(), "Luma-cache-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "sample.bmp");
            // A valid 256x256 uncompressed 24-bit BMP; no external fixture or codec package.
            using (var writer = new BinaryWriter(File.Create(path)))
            {
                writer.Write((ushort)0x4D42); writer.Write(54 + 256 * 256 * 3);
                writer.Write(0); writer.Write(54); writer.Write(40); writer.Write(256); writer.Write(256);
                writer.Write((ushort)1); writer.Write((ushort)24); writer.Write(0); writer.Write(256 * 256 * 3);
                writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                writer.Write(Enumerable.Repeat((byte)120, 256 * 256 * 3).ToArray());
            }
            var resolver = new WindowsSourceResolver();
            var source = resolver.Register(root);
            var info = new FileInfo(path);
            var media = new CachedMedia(source.Id, "sample.bmp", info.Length, info.LastWriteTimeUtc.Ticks, "photo", true);
            using var cache = new ThumbnailCache(cacheRoot);
            var service = new ThumbnailService(cache, resolver, new WindowsThumbnailGenerator());
            Assert.NotNull(await service.GetAsync(media));
            var metadata = await new WindowsMetadataReader().ReadAsync(path,"photo",CancellationToken.None);
            Assert.Equal(256,metadata.Width); Assert.Equal(256,metadata.Height);
            Assert.Null(metadata.TakenTicks);
            File.Delete(path);
            Assert.NotNull(await service.GetAsync(media));
        }
        finally
        {
            Directory.Delete(root, true);
            if (Directory.Exists(cacheRoot)) Directory.Delete(cacheRoot, true);
        }
    }

    [Theory]
    [InlineData("jpg")]
    [InlineData("png")]
    public async Task EverydayImageFormatsProducePreviewAndDimensions(string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "Luma-format-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(root);
            var file = await folder.CreateFileAsync("sample." + extension);
            using (var stream = await file.OpenAsync(global::Windows.Storage.FileAccessMode.ReadWrite))
            {
                var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(extension == "jpg" ?
                    global::Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId : global::Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, 96, 64, 96, 96,
                    Enumerable.Repeat((byte)180, 96 * 64 * 4).ToArray());
                await encoder.FlushAsync();
            }
            var metadata = await new WindowsMetadataReader().ReadAsync(file.Path, "photo", CancellationToken.None);
            Assert.Equal(96, metadata.Width); Assert.Equal(64, metadata.Height);
            var preview = await new WindowsThumbnailGenerator().GenerateAsync(file.Path, 128, CancellationToken.None);
            Assert.NotNull(preview); Assert.NotEmpty(preview!);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void IdentityIsStableAndFoldersOnSameVolumeAreDistinct()
    {
        var root = Path.Combine(Path.GetTempPath(), "Luma-win-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "other"));
        try
        {
            var resolver = new WindowsSourceResolver();
            var source = resolver.Register(root);
            Assert.Equal(source.Id, resolver.Register(root + Path.DirectorySeparatorChar).Id);
            Assert.NotEqual(source.Id, resolver.Register(Path.Combine(root, "other")).Id);
            var resolved = resolver.ResolveRoot(source.Id);
            Assert.NotNull(resolved);
            File.WriteAllText(Path.Combine(root, "probe.txt"), "same volume");
            Assert.Equal("same volume", File.ReadAllText(Path.Combine(resolved!, "probe.txt")));
            Assert.Null(resolver.ResolveRoot("windows-volume-v1:invalid"));
            var encoded = source.Id["windows-volume-v1:".Length..];
            var identity = System.Text.Json.Nodes.JsonNode.Parse(Convert.FromBase64String(encoded))!;
            identity["Serial"] = identity["Serial"]!.GetValue<uint>() ^ 1u;
            var differentVolume = "windows-volume-v1:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(identity.ToJsonString()));
            Assert.Null(resolver.ResolveRoot(differentVolume));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task WindowsMetadataReader_ExtractsGpsAndCameraModelSafely()
    {
        var root = Path.Combine(Path.GetTempPath(), "Luma-gps-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var folder = await global::Windows.Storage.StorageFolder.GetFolderFromPathAsync(root);
            var file = await folder.CreateFileAsync("geotagged.jpg");
            using (var stream = await file.OpenAsync(global::Windows.Storage.FileAccessMode.ReadWrite))
            {
                var encoder = await global::Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(global::Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId, stream);
                encoder.SetPixelData(global::Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    global::Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, 64, 48, 96, 96,
                    Enumerable.Repeat((byte)200, 64 * 48 * 4).ToArray());

                static ulong Rational(uint num, uint den) => ((ulong)den << 32) | num;
                var gpsLat = new[] { Rational(48, 1), Rational(51, 1), Rational(2376, 100) };
                var gpsLon = new[] { Rational(2, 1), Rational(21, 1), Rational(792, 100) };
                await encoder.BitmapProperties.SetPropertiesAsync(new Dictionary<string, global::Windows.Graphics.Imaging.BitmapTypedValue>
                {
                    ["/app1/ifd/{ushort=272}"] = new("Luma Test Cam", global::Windows.Foundation.PropertyType.String),
                    ["/app1/ifd/gps/{ushort=1}"] = new("N", global::Windows.Foundation.PropertyType.String),
                    ["/app1/ifd/gps/{ushort=2}"] = new(gpsLat, global::Windows.Foundation.PropertyType.UInt64Array),
                    ["/app1/ifd/gps/{ushort=3}"] = new("E", global::Windows.Foundation.PropertyType.String),
                    ["/app1/ifd/gps/{ushort=4}"] = new(gpsLon, global::Windows.Foundation.PropertyType.UInt64Array)
                });
                await encoder.FlushAsync();
            }

            var metadata = await new WindowsMetadataReader().ReadAsync(file.Path, "photo", CancellationToken.None);
            Assert.Equal("Luma Test Cam", metadata.CameraModel);
            Assert.NotNull(metadata.Latitude);
            Assert.NotNull(metadata.Longitude);
            Assert.InRange(metadata.Latitude!.Value, 48.85, 48.86);
            Assert.InRange(metadata.Longitude!.Value, 2.35, 2.36);
        }
        finally { Directory.Delete(root, true); }
    }
}
