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

    [Fact]
    public async Task WindowsMetadataReader_FallsBackToQuickTimeIso6709AndSidecarXmp()
    {
        var root = Path.Combine(Path.GetTempPath(), "Luma-fallback-gps-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            static byte[] Box(string type, byte[] payload)
            {
                var buf = new byte[8 + payload.Length];
                var size = (uint)buf.Length;
                buf[0] = (byte)(size >> 24);
                buf[1] = (byte)(size >> 16);
                buf[2] = (byte)(size >> 8);
                buf[3] = (byte)size;
                System.Text.Encoding.Latin1.GetBytes(type, 0, 4, buf, 4);
                Buffer.BlockCopy(payload, 0, buf, 8, payload.Length);
                return buf;
            }

            var isoBytes = System.Text.Encoding.UTF8.GetBytes("+37.7749-122.4194+010.500/");
            var xyzPayload = new byte[4 + isoBytes.Length];
            xyzPayload[0] = (byte)(isoBytes.Length >> 8);
            xyzPayload[1] = (byte)(isoBytes.Length & 0xFF);
            Buffer.BlockCopy(isoBytes, 0, xyzPayload, 4, isoBytes.Length);
            var moov = Box("moov", Box("udta", Box("\u00A9xyz", xyzPayload)));
            var ftyp = Box("ftyp", "isom\0\0\u0002\0isommp41"u8.ToArray());
            var mp4 = new byte[ftyp.Length + moov.Length];
            Buffer.BlockCopy(ftyp, 0, mp4, 0, ftyp.Length);
            Buffer.BlockCopy(moov, 0, mp4, ftyp.Length, moov.Length);

            var mp4Path = Path.Combine(root, "phone.mp4");
            File.WriteAllBytes(mp4Path, mp4);

            var videoMeta = await new WindowsMetadataReader().ReadAsync(mp4Path, "video", CancellationToken.None);
            Assert.NotNull(videoMeta.Latitude);
            Assert.NotNull(videoMeta.Longitude);
            Assert.InRange(videoMeta.Latitude!.Value, 37.77, 37.78);
            Assert.InRange(videoMeta.Longitude!.Value, -122.42, -122.41);
            Assert.Equal("video-iso6709", videoMeta.LocationOrigin);

            // Photo with empty image body + sidecar .xmp
            var dngPath = Path.Combine(root, "shot.dng");
            File.WriteAllBytes(dngPath, [1, 2, 3, 4]);
            File.WriteAllText(dngPath + ".xmp", """
                <x:xmpmeta xmlns:x="adobe:ns:meta/">
                  <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                    <rdf:Description xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
                      exif:GPSLatitude="51.5074" exif:GPSLongitude="-0.1278"
                      photoshop:City="London" photoshop:Country="UK" />
                  </rdf:RDF>
                </x:xmpmeta>
                """);
            var photoMeta = await new WindowsMetadataReader().ReadAsync(dngPath, "photo", CancellationToken.None);
            Assert.InRange(photoMeta.Latitude!.Value, 51.50, 51.51);
            Assert.InRange(photoMeta.Longitude!.Value, -0.13, -0.12);
            Assert.Equal("London, UK", photoMeta.LocationName);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task BanglaUnicodeAndExtendedLongPath_ScanMetadataAndThumbnailWorkSafely()
    {
        var root = Path.Combine(Path.GetTempPath(), "Luma-long-" + Guid.NewGuid());
        var deepSegment = Path.Combine(
            "বাংলা_সংস্কৃতি_অ্যালবাম",
            new string('a', 90),
            new string('b', 90),
            new string('c', 90));
        var longFolder = Path.Combine(root, deepSegment);
        Directory.CreateDirectory(@"\\?\" + longFolder);
        try
        {
            var bmpName = "ঢাকা_ছবি_🎉.bmp";
            var fullExtended = @"\\?\" + Path.Combine(longFolder, bmpName);
            await using (var writer = new BinaryWriter(File.Create(fullExtended)))
            {
                writer.Write((ushort)0x4D42); writer.Write(54 + 64 * 64 * 3); writer.Write(0); writer.Write(54);
                writer.Write(40); writer.Write(64); writer.Write(64); writer.Write((ushort)1); writer.Write((ushort)24);
                writer.Write(0); writer.Write(64 * 64 * 3); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
                for (int i = 0; i < 64 * 64 * 3; i++) writer.Write((byte)140);
            }

            Assert.True(Path.Combine(longFolder, bmpName).Length > 260, "Path must exceed legacy MAX_PATH 260 limit.");
            var resolver = new WindowsSourceResolver();
            var reg = resolver.Register(root);
            var catalog = new MediaCache(Path.Combine(root, "catalog.db"), resolver);
            await catalog.InitializeAsync();
            var scanned = await catalog.ScanAsync(reg.Id, reg.RootPath);
            Assert.Equal(1, scanned);

            var query = await catalog.QueryAsync(new LibraryQuery(SourceId: reg.Id, Search: "ঢাকা"));
            Assert.Equal(1, query.Total);
        }
        finally
        {
            try { Directory.Delete(@"\\?\" + root, true); } catch { }
        }
    }

    [Fact]
    public async Task CorruptAndZeroByteFiles_DoNotThrowUnhandledExceptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "Luma-corrupt-media-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var emptyJpg = Path.Combine(root, "empty.jpg");
            var junkPng = Path.Combine(root, "corrupt.png");
            await File.WriteAllBytesAsync(emptyJpg, Array.Empty<byte>());
            await File.WriteAllBytesAsync(junkPng, [0x01, 0xFF, 0x00, 0x42, 0x99]);

            var reader = new WindowsMetadataReader();
            var emptyMeta = await reader.ReadAsync(emptyJpg, "photo", CancellationToken.None);
            var junkMeta = await reader.ReadAsync(junkPng, "photo", CancellationToken.None);
            Assert.NotNull(emptyMeta);
            Assert.NotNull(junkMeta);

            var thumbGen = new WindowsThumbnailGenerator();
            _ = await thumbGen.GenerateAsync(emptyJpg, 256, CancellationToken.None);
            _ = await thumbGen.GenerateAsync(junkPng, 256, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
