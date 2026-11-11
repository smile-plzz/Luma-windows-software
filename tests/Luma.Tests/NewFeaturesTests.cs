using Luma.Core;
using Xunit;

namespace Luma.Tests;

public class NewFeaturesTests : IDisposable
{
    private readonly string tempDir;
    private readonly string catalogPath;
    private readonly MediaCache catalog;

    public NewFeaturesTests()
    {
        tempDir = Path.Combine(Path.GetTempPath(), "luma-feat-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        catalogPath = Path.Combine(tempDir, "catalog.db");
        catalog = new MediaCache(catalogPath);
        catalog.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(tempDir, true); } catch { }
    }

    [Fact]
    public async Task BackupAndRestore_PreservesCatalogAndRecoversData()
    {
        // 1. Insert media and annotations
        var sourceFolder = Path.Combine(tempDir, "Source");
        Directory.CreateDirectory(sourceFolder);
        File.WriteAllText(Path.Combine(sourceFolder, "photo1.jpg"), "dummy");
        await catalog.ScanAsync("src-1", sourceFolder);
        await catalog.AnnotateAsync(new CachedMedia("src-1", "photo1.jpg", 5, DateTime.UtcNow.Ticks, "photo", true), true, "vacation, nature");

        // 2. Create backup
        var backupFile = Path.Combine(tempDir, "test-backup.db");
        await catalog.CreateBackupAsync(backupFile);
        Assert.True(File.Exists(backupFile));

        // 3. Mutate catalog
        await catalog.AnnotateAsync(new CachedMedia("src-1", "photo1.jpg", 5, DateTime.UtcNow.Ticks, "photo", true), false, "changed");
        var modifiedTags = (await catalog.GetTagsWithCountsAsync()).Select(t => t.Tag).ToArray();
        Assert.Contains("changed", modifiedTags);

        // 4. Restore from backup
        var restored = await catalog.RestoreBackupAsync(backupFile);
        Assert.True(restored);

        var restoredTags = (await catalog.GetTagsWithCountsAsync()).Select(t => t.Tag).ToArray();
        Assert.Contains("vacation", restoredTags);
        Assert.Contains("nature", restoredTags);
        Assert.DoesNotContain("changed", restoredTags);
    }

    [Fact]
    public async Task TagManager_RenameMergeAndDelete_WorkCorrectly()
    {
        var media1 = new CachedMedia("s1", "a.jpg", 100, 1000, "photo", true);
        var media2 = new CachedMedia("s1", "b.jpg", 100, 1000, "photo", true);
        await catalog.AnnotateAsync(media1, false, "travel, summer");
        await catalog.AnnotateAsync(media2, false, "summer, beach");

        var tags = await catalog.GetTagsWithCountsAsync();
        Assert.Equal(3, tags.Count);
        var summerTag = tags.Single(t => t.Tag == "summer");
        Assert.Equal(2, summerTag.Count);

        // Rename summer -> holiday
        await catalog.RenameTagAsync("summer", "holiday");
        var afterRename = await catalog.GetTagsWithCountsAsync();
        Assert.DoesNotContain(afterRename, t => t.Tag == "summer");
        Assert.Contains(afterRename, t => t.Tag == "holiday" && t.Count == 2);

        // Delete beach
        await catalog.DeleteTagAsync("beach");
        var afterDelete = await catalog.GetTagsWithCountsAsync();
        Assert.DoesNotContain(afterDelete, t => t.Tag == "beach");

        // Merge holiday into travel
        await catalog.RenameTagAsync("holiday", "travel");
        var afterMerge = await catalog.GetTagsWithCountsAsync();
        Assert.Single(afterMerge);
        Assert.Equal("travel", afterMerge[0].Tag);
        Assert.Equal(2, afterMerge[0].Count);
    }

    [Fact]
    public async Task SearchOperators_ParseAndFilterByTagExtAndYear()
    {
        var sourceDir = Path.Combine(tempDir, "Media");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllText(Path.Combine(sourceDir, "mountain.jpg"), "12345");
        File.WriteAllText(Path.Combine(sourceDir, "ocean.png"), "12345");
        File.WriteAllText(Path.Combine(sourceDir, "clip.mp4"), "12345");

        await catalog.ScanAsync("src-s", sourceDir);

        var mediaJpg = new CachedMedia("src-s", "mountain.jpg", 5, new DateTime(2023, 6, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, "photo", true);
        var mediaPng = new CachedMedia("src-s", "ocean.png", 5, new DateTime(2024, 7, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, "photo", true);
        var mediaMp4 = new CachedMedia("src-s", "clip.mp4", 5, new DateTime(2025, 8, 1, 0, 0, 0, DateTimeKind.Utc).Ticks, "video", true);

        await catalog.AnnotateAsync(mediaJpg, false, "nature, scenic");
        await catalog.AnnotateAsync(mediaPng, false, "water, scenic");
        await catalog.AnnotateAsync(mediaMp4, false, "nature, video");

        // Search ext:png
        var extResult = await catalog.QueryAsync(new LibraryQuery(Search: "ext:png"));
        Assert.Single(extResult.Items);
        Assert.Equal("ocean.png", extResult.Items[0].Media.RelativePath);

        // Search tag:water
        var tagResult = await catalog.QueryAsync(new LibraryQuery(Search: "tag:water"));
        Assert.Single(tagResult.Items);
        Assert.Equal("ocean.png", tagResult.Items[0].Media.RelativePath);

        // Search tag:scenic
        var scenicResult = await catalog.QueryAsync(new LibraryQuery(Search: "tag:scenic"));
        Assert.Equal(2, scenicResult.Items.Count);

        // Search text + ext
        var combinedResult = await catalog.QueryAsync(new LibraryQuery(Search: "mountain ext:jpg"));
        Assert.Single(combinedResult.Items);
        Assert.Equal("mountain.jpg", combinedResult.Items[0].Media.RelativePath);
    }

    [Fact]
    public async Task SmartCollections_SaveListAndDelete_BehaveIdempotently()
    {
        var id = await catalog.SaveSmartCollectionAsync("2024 Favorites", search: "year:2024", favorite: true);
        Assert.False(string.IsNullOrWhiteSpace(id));

        var collections = await catalog.SmartCollectionsAsync();
        Assert.Single(collections);
        Assert.Equal("2024 Favorites", collections[0].Name);
        Assert.Equal("year:2024", collections[0].Search);
        Assert.True(collections[0].Favorite);

        // Update name
        await catalog.SaveSmartCollectionAsync("2024 Starred", search: "year:2024", favorite: true, id: id);
        var updated = await catalog.SmartCollectionsAsync();
        Assert.Single(updated);
        Assert.Equal("2024 Starred", updated[0].Name);

        // Delete
        await catalog.DeleteSmartCollectionAsync(id);
        var empty = await catalog.SmartCollectionsAsync();
        Assert.Empty(empty);
    }

    [Fact]
    public void PhotoStackBuilder_GroupsRawJpegPairsAndConsecutiveBursts()
    {
        var baseTime = new DateTime(2026, 6, 15, 14, 0, 0, DateTimeKind.Utc);
        var items = new[]
        {
            new LibraryItem(new CachedMedia("s1", "IMG_001.CR3", 20_000_000, baseTime.Ticks, "photo", true), false, "", "1", new MediaMetadata(baseTime.Ticks)),
            new LibraryItem(new CachedMedia("s1", "IMG_001.JPG", 4_000_000, baseTime.Ticks, "photo", true), false, "", "2", new MediaMetadata(baseTime.Ticks)),
            new LibraryItem(new CachedMedia("s1", "BURST_1.JPG", 3_000_000, baseTime.AddMinutes(5).Ticks, "photo", true), false, "", "3", new MediaMetadata(baseTime.AddMinutes(5).Ticks)),
            new LibraryItem(new CachedMedia("s1", "BURST_2.JPG", 3_100_000, baseTime.AddMinutes(5).AddSeconds(10).Ticks, "photo", true), true, "", "4", new MediaMetadata(baseTime.AddMinutes(5).AddSeconds(10).Ticks)),
            new LibraryItem(new CachedMedia("s1", "LATER.JPG", 3_200_000, baseTime.AddHours(2).Ticks, "photo", true), false, "", "5", new MediaMetadata(baseTime.AddHours(2).Ticks)),
        };

        var stacks = PhotoStackBuilder.Build(items, allowBurstClustering: true, burstSeconds: 30);
        Assert.Equal(3, stacks.Count);

        // First stack: RAW+JPEG pair picks JPG cover
        Assert.True(stacks[0].IsStack);
        Assert.Equal("IMG_001.JPG", stacks[0].Cover.Media.RelativePath);
        Assert.Equal(2, stacks[0].Items.Count);

        // Second stack: Burst picks Favorited frame as cover
        Assert.True(stacks[1].IsStack);
        Assert.Equal("BURST_2.JPG", stacks[1].Cover.Media.RelativePath);
        Assert.Equal(2, stacks[1].Items.Count);

        // Third item: standalone
        Assert.False(stacks[2].IsStack);
        Assert.Equal("LATER.JPG", stacks[2].Cover.Media.RelativePath);
    }

    [Fact]
    public void BatchRenameFormatter_AppliesTokensAndLocksExtension()
    {
        var utc = new DateTime(2025, 8, 14, 9, 30, 15, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        var item = new LibraryItem(
            new CachedMedia("s1", "Trips/DCIM_001.JPG", 1024, utc.Ticks, "photo", true),
            false, "", "1", new MediaMetadata(utc.Ticks));

        var renamed = BatchRenameFormatter.FormatName("{date}_{time}_{orig}_{seq}", item, 7, 120);
        Assert.Equal($"{local:yyyy-MM-dd}_{local:HHmmss}_DCIM_001_007.JPG", renamed);

        var sanitized = BatchRenameFormatter.FormatName("bad:name*{orig}", item, 1, 5);
        Assert.Equal("bad_name_DCIM_001.JPG", sanitized);
    }

    [Fact]
    public async Task FileTransfer_ConflictModeRename_GeneratesUniqueFilenameWithoutOverwriting()
    {
        var srcDir = Path.Combine(tempDir, "Src");
        var dstDir = Path.Combine(tempDir, "Dst");
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(dstDir);

        var srcFile = Path.Combine(srcDir, "pic.jpg");
        var existingDst = Path.Combine(dstDir, "pic.jpg");
        File.WriteAllText(srcFile, "incoming-content");
        File.WriteAllText(existingDst, "original-dst-content");

        var result = await FileTransfer.RunAsync(srcFile, Path.Combine(dstDir, "pic.jpg"), move: false, () => true, CancellationToken.None, TransferConflictMode.Rename);
        Assert.True(result.Copied);
        Assert.Null(result.Error);
        Assert.Equal(Path.Combine(dstDir, "pic (2).jpg"), result.Destination);
        Assert.Equal("original-dst-content", File.ReadAllText(existingDst));
        Assert.Equal("incoming-content", File.ReadAllText(result.Destination));
    }

    [Fact]
    public async Task ExtendedFiltersAndGeotaggedQueries_WorkAcrossOrientationSizeUntaggedAndMapBounds()
    {
        var sourceDir = Path.Combine(tempDir, "GeoMedia");
        Directory.CreateDirectory(sourceDir);
        File.WriteAllBytes(Path.Combine(sourceDir, "paris.jpg"), new byte[12000]);
        File.WriteAllBytes(Path.Combine(sourceDir, "tokyo.png"), new byte[300]);
        File.WriteAllBytes(Path.Combine(sourceDir, "notes.bmp"), new byte[500]);

        await catalog.ScanAsync("geo", sourceDir);

        var paris = (await catalog.QueryAsync(new LibraryQuery(Search: "paris"))).Items[0].Media;
        var tokyo = (await catalog.QueryAsync(new LibraryQuery(Search: "tokyo"))).Items[0].Media;
        var notes = (await catalog.QueryAsync(new LibraryQuery(Search: "notes"))).Items[0].Media;

        await catalog.SaveMetadataAsync(paris, new MediaMetadata(
            TakenTicks: new DateTime(2024, 5, 10, 12, 0, 0, DateTimeKind.Utc).Ticks,
            DateOrigin: "exif", Width: 4000, Height: 3000,
            Latitude: 48.8566, Longitude: 2.3522, CameraModel: "Canon R6", Iso: 200));
        await catalog.SaveMetadataAsync(tokyo, new MediaMetadata(
            TakenTicks: new DateTime(2024, 8, 20, 18, 0, 0, DateTimeKind.Utc).Ticks,
            DateOrigin: "exif", Width: 1080, Height: 1920,
            Latitude: 35.6762, Longitude: 139.6503));
        await catalog.SaveMetadataAsync(notes, new MediaMetadata(Width: 800, Height: 800));

        await catalog.AnnotateAsync(paris, true, "europe, trip");
        await catalog.AnnotateAsync(notes, false, "temp");

        // Search operators: size:>5kb
        var big = await catalog.QueryAsync(new LibraryQuery(Search: "size:>5kb"));
        Assert.Single(big.Items);
        Assert.Equal("paris.jpg", big.Items[0].Media.RelativePath);

        // Search operators: is:portrait
        var portrait = await catalog.QueryAsync(new LibraryQuery(Search: "is:portrait"));
        Assert.Single(portrait.Items);
        Assert.Equal("tokyo.png", portrait.Items[0].Media.RelativePath);

        // Search operators: is:untagged
        var untagged = await catalog.QueryAsync(new LibraryQuery(Search: "is:untagged"));
        Assert.Single(untagged.Items);
        Assert.Equal("tokyo.png", untagged.Items[0].Media.RelativePath);

        // Search operators: negation -ext:bmp -tag:europe
        var filtered = await catalog.QueryAsync(new LibraryQuery(Search: "-ext:bmp -tag:europe"));
        Assert.Single(filtered.Items);
        Assert.Equal("tokyo.png", filtered.Items[0].Media.RelativePath);

        // Map markers & bounding box filter
        var markers = await catalog.QueryGeotaggedMarkersAsync(new LibraryQuery());
        Assert.Equal(2, markers.Count);

        var parisBox = await catalog.QueryAsync(new LibraryQuery(MapBounds: new GeoBounds(48.0, 49.5, 1.5, 3.0)));
        Assert.Single(parisBox.Items);
        Assert.Equal("paris.jpg", parisBox.Items[0].Media.RelativePath);
        Assert.Equal("Canon R6", parisBox.Items[0].Metadata?.CameraModel);
        Assert.Equal(200, parisBox.Items[0].Metadata?.Iso);
    }

    [Fact]
    public async Task SeedDefaultSmartCollections_IsIdempotent()
    {
        await catalog.SeedDefaultSmartCollectionsAsync();
        await catalog.SeedDefaultSmartCollectionsAsync();
        var seeded = await catalog.SmartCollectionsAsync();
        Assert.Equal(4, seeded.Count);
    }

    private sealed class MapResolver(Dictionary<string, string> roots) : ISourceResolver
    {
        public string? ResolveRoot(string sourceId) => roots.GetValueOrDefault(sourceId);
    }

    private sealed class StubGpsReader : IMetadataReader
    {
        public int Calls { get; private set; }
        public Task<MediaMetadata> ReadAsync(string path, string kind, CancellationToken ct)
        {
            Calls++;
            var extracted = MinimalGpsExtractor.ExtractFromFile(path);
            return Task.FromResult(new MediaMetadata(
                null,
                "Modification date fallback",
                1200, 900, 0, 1,
                extracted.Latitude,
                extracted.Longitude,
                LocationName: extracted.LocationName,
                LocationOrigin: extracted.Origin));
        }
    }

    private static byte[] BuildSyntheticJpegWithCoarseExifGps(uint latDeg, uint latMin, uint lonDeg, uint lonMin)
    {
        using var tiff = new MemoryStream();
        using var bw = new BinaryWriter(tiff);
        bw.Write(new byte[] { (byte)'I', (byte)'I', 42, 0 });
        bw.Write((uint)8); // IFD0 offset

        // IFD0: 1 entry (0x8825 GPSInfo pointer)
        bw.Write((ushort)1);
        bw.Write((ushort)0x8825); bw.Write((ushort)4); bw.Write((uint)1); bw.Write((uint)(8 + 2 + 12 + 4)); // offset 26
        bw.Write((uint)0); // next IFD

        // GPS IFD at 26: 2 entries (0x0002 Lat, 0x0004 Lon) — intentionally omits Ref tags (1 & 3)
        bw.Write((ushort)2);
        var latRatOffset = (uint)(26 + 2 + 24 + 4); // 56
        var lonRatOffset = latRatOffset + 24;        // 80
        bw.Write((ushort)0x0002); bw.Write((ushort)5); bw.Write((uint)3); bw.Write(latRatOffset);
        bw.Write((ushort)0x0004); bw.Write((ushort)5); bw.Write((uint)3); bw.Write(lonRatOffset);
        bw.Write((uint)0);

        // Lat rational [deg, min, 0]
        bw.Write(latDeg); bw.Write((uint)1);
        bw.Write(latMin); bw.Write((uint)1);
        bw.Write((uint)0); bw.Write((uint)1);

        // Lon rational [deg, min, 0]
        bw.Write(lonDeg); bw.Write((uint)1);
        bw.Write(lonMin); bw.Write((uint)1);
        bw.Write((uint)0); bw.Write((uint)1);

        var tiffBytes = tiff.ToArray();
        using var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8, 0xFF, 0xE1]);
        var segLen = (ushort)(2 + 6 + tiffBytes.Length);
        jpeg.Write([(byte)(segLen >> 8), (byte)(segLen & 0xFF)]);
        jpeg.Write("Exif\0\0"u8);
        jpeg.Write(tiffBytes);
        jpeg.Write([0xFF, 0xDA, 0xFF, 0xD9]);
        return jpeg.ToArray();
    }

    private static byte[] BuildSyntheticMp4WithIso6709(string isoString)
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

        var isoBytes = System.Text.Encoding.UTF8.GetBytes(isoString);
        var xyzPayload = new byte[4 + isoBytes.Length];
        xyzPayload[0] = (byte)(isoBytes.Length >> 8);
        xyzPayload[1] = (byte)(isoBytes.Length & 0xFF);
        Buffer.BlockCopy(isoBytes, 0, xyzPayload, 4, isoBytes.Length);

        var xyzBox = Box("\u00A9xyz", xyzPayload);
        var udtaBox = Box("udta", xyzBox);
        var moovBox = Box("moov", udtaBox);
        var ftypBox = Box("ftyp", "isom\0\0\u0002\0isommp41"u8.ToArray());

        var combined = new byte[ftypBox.Length + moovBox.Length];
        Buffer.BlockCopy(ftypBox, 0, combined, 0, ftypBox.Length);
        Buffer.BlockCopy(moovBox, 0, combined, ftypBox.Length, moovBox.Length);
        return combined;
    }

    [Fact]
    public void MinimalGpsExtractor_ExtractsCoarseJpegExifSidecarXmpAndMp4Iso6709()
    {
        var dir = Path.Combine(tempDir, "Extractors");
        Directory.CreateDirectory(dir);

        // 1. Minimal JPEG EXIF without N/E Ref tags (23 deg 48 min = 23.8, 90 deg 24 min = 90.4)
        var jpgPath = Path.Combine(dir, "coarse.jpg");
        File.WriteAllBytes(jpgPath, BuildSyntheticJpegWithCoarseExifGps(23, 48, 90, 24));
        var jpgLoc = MinimalGpsExtractor.ExtractFromFile(jpgPath);
        Assert.True(jpgLoc.HasCoordinates);
        Assert.InRange(jpgLoc.Latitude!.Value, 23.79, 23.81);
        Assert.InRange(jpgLoc.Longitude!.Value, 90.39, 90.41);

        // 2. Photo with adjacent .xmp sidecar containing DMS coords + City/Country
        var rawPath = Path.Combine(dir, "frame.dng");
        File.WriteAllBytes(rawPath, [1, 2, 3, 4]);
        File.WriteAllText(rawPath + ".xmp", """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:exif="http://ns.adobe.com/exif/1.0/" xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
                  exif:GPSLatitude="35,40.572N" exif:GPSLongitude="139,39.018E"
                  photoshop:City="Tokyo" photoshop:Country="Japan" />
              </rdf:RDF>
            </x:xmpmeta>
            """);
        var xmpLoc = MinimalGpsExtractor.ExtractFromFile(rawPath);
        Assert.True(xmpLoc.HasCoordinates);
        Assert.InRange(xmpLoc.Latitude!.Value, 35.67, 35.68);
        Assert.InRange(xmpLoc.Longitude!.Value, 139.65, 139.66);
        Assert.Equal("Tokyo, Japan", xmpLoc.LocationName);
        Assert.Equal("sidecar", xmpLoc.Origin);

        // 3. MP4 video with moov -> udta -> ©xyz ISO-6709 string
        var mp4Path = Path.Combine(dir, "clip.mp4");
        File.WriteAllBytes(mp4Path, BuildSyntheticMp4WithIso6709("+48.8566+002.3522+035.0/"));
        var mp4Loc = MinimalGpsExtractor.ExtractFromFile(mp4Path);
        Assert.True(mp4Loc.HasCoordinates);
        Assert.InRange(mp4Loc.Latitude!.Value, 48.85, 48.86);
        Assert.InRange(mp4Loc.Longitude!.Value, 2.35, 2.36);
        Assert.Equal("video-iso6709", mp4Loc.Origin);
    }

    [Fact]
    public async Task MapBackfillManualPinsAndLocationQueries_WorkTogetherSafely()
    {
        var dir = Path.Combine(tempDir, "MapBackfill");
        Directory.CreateDirectory(Path.Combine(dir, "100%_trips"));
        var clipPath = Path.Combine(dir, "100%_trips", "video_1.mp4");
        var photoPath = Path.Combine(dir, "manual.jpg");
        var tagOnlyPath = Path.Combine(dir, "place_only.dng");

        File.WriteAllBytes(clipPath, BuildSyntheticMp4WithIso6709("+35.6762+139.6503/"));
        File.WriteAllBytes(photoPath, BuildSyntheticJpegWithCoarseExifGps(23, 48, 90, 24));
        File.WriteAllBytes(tagOnlyPath, [1, 2, 3, 4]);
        File.WriteAllText(tagOnlyPath + ".xmp", """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:photoshop="http://ns.adobe.com/photoshop/1.0/"
                  photoshop:City="Kyoto" photoshop:Country="Japan" />
              </rdf:RDF>
            </x:xmpmeta>
            """);

        var resolver = new MapResolver(new Dictionary<string, string> { ["s1"] = dir });
        var scopedCatalog = new MediaCache(catalogPath, resolver);
        await scopedCatalog.ScanAsync("s1", dir);

        var clip = (await scopedCatalog.GetItemAsync("s1", Path.Combine("100%_trips", "video_1.mp4")))!;
        var manual = (await scopedCatalog.GetItemAsync("s1", "manual.jpg"))!;
        Assert.NotNull(clip);
        Assert.NotNull(manual);

        // Simulate legacy v0.6.0 metadata rows (meta_version = 0, no GPS yet) and manual catalog pin
        var existingTaken = new DateTime(2024, 3, 1, 10, 0, 0, DateTimeKind.Utc).Ticks;
        await scopedCatalog.SaveMetadataAsync(clip.Media, new MediaMetadata(existingTaken, "legacy", 1920, 1080, MetaVersion: 0));
        await scopedCatalog.AssignLocationAsync([manual.Media], 40.7128, -74.0060, "New York, USA");

        Assert.Equal(3, await scopedCatalog.CountPendingMetadataBackfillAsync("s1", ["s1"]));

        var stubReader = new StubGpsReader();
        var updated = await scopedCatalog.EnrichAsync(stubReader, "s1");
        Assert.Equal(3, updated);
        Assert.Equal(0, await scopedCatalog.EnrichAsync(stubReader, "s1"));
        Assert.Equal(0, await scopedCatalog.CountPendingMetadataBackfillAsync("s1", ["s1"]));

        // Verify legacy video got coordinates while keeping its existing taken_ticks, and manual pin survived unchanged
        var clipAfter = (await scopedCatalog.GetItemAsync("s1", Path.Combine("100%_trips", "video_1.mp4")))!;
        Assert.InRange(clipAfter.Metadata!.Latitude!.Value, 35.67, 35.68);
        Assert.Equal(existingTaken, clipAfter.Metadata.TakenTicks);

        var manualAfter = (await scopedCatalog.GetItemAsync("s1", "manual.jpg"))!;
        Assert.Equal("manual", manualAfter.Metadata!.LocationOrigin);
        Assert.InRange(manualAfter.Metadata.Latitude!.Value, 40.71, 40.72);
        Assert.Equal("New York, USA", manualAfter.Metadata.LocationName);

        // Verify map filters, place-tagged unlocated list, summary counts, and location: search token
        var summary = await scopedCatalog.QueryMapSummaryAsync(new LibraryQuery());
        Assert.Equal(2, summary.GeotaggedCount);
        Assert.Equal(1, summary.ManualPinCount);
        Assert.Equal(1, summary.PlaceTagOnlyCount);
        Assert.Equal(1, summary.UnlocatedCount);

        var placeOnly = await scopedCatalog.QueryPlaceTaggedWithoutCoordsAsync(new LibraryQuery());
        Assert.Single(placeOnly);
        Assert.Equal("Kyoto, Japan", placeOnly[0].LocationName);

        var manualMarkers = await scopedCatalog.QueryGeotaggedMarkersAsync(new LibraryQuery(LocationOriginFilter: "manual"));
        Assert.Single(manualMarkers);
        Assert.Equal("manual.jpg", manualMarkers[0].Name);

        var kyotoSearch = await scopedCatalog.QueryAsync(new LibraryQuery(Search: "location:Kyoto"));
        Assert.Single(kyotoSearch.Items);
        Assert.Equal("place_only.dng", kyotoSearch.Items[0].Media.RelativePath);
    }

    [Fact]
    public async Task LocateAsyncMatchesQueryOrderingAcrossAllSortsAndFavoritesFirst()
    {
        var dir = Path.Combine(tempDir, "LocateSorts");
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var files = new[]
        {
            ("a.jpg", 2048, new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc), new DateTime(2024, 4, 1, 10, 0, 0, DateTimeKind.Utc), 4000, 3000, 0.0, true, "trip"),
            ("b.png", 2048, new DateTime(2024, 5, 1, 10, 0, 0, DateTimeKind.Utc), new DateTime(2024, 4, 2, 10, 0, 0, DateTimeKind.Utc), 1920, 1080, 0.0, false, ""),
            ("sub\\c.mp4", 8192, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc), 3840, 2160, 14.5, true, "clip"),
            ("d.webp", 1024, new DateTime(2024, 3, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2024, 3, 1, 8, 0, 0, DateTimeKind.Utc), 1000, 1000, 0.0, false, "trip"),
            ("e.jpg", 4096, new DateTime(2024, 5, 15, 9, 0, 0, DateTimeKind.Utc), new DateTime(2024, 5, 15, 9, 0, 0, DateTimeKind.Utc), 800, 1200, 0.0, false, "")
        };

        foreach (var (rel, len, mod, _, _, _, _, _, _) in files)
        {
            var full = Path.Combine(dir, rel);
            File.WriteAllBytes(full, new byte[len]);
            File.SetLastWriteTimeUtc(full, mod);
        }

        await catalog.ScanAsync("s-loc", dir);
        foreach (var (rel, _, _, taken, w, h, dur, fav, tags) in files)
        {
            var item = (await catalog.GetItemAsync("s-loc", rel))!;
            await catalog.SaveMetadataAsync(item.Media, new MediaMetadata(taken.Ticks, "test", w, h, dur, 1));
            await catalog.AnnotateAsync(item.Media, fav, tags);
        }

        foreach (var sort in Enum.GetValues<MediaSort>())
        {
            foreach (var desc in new[] { false, true })
            {
                foreach (var favFirst in new[] { false, true })
                {
                    var q = new LibraryQuery(SourceId: "s-loc", Sort: sort, Descending: desc, FavoritesFirst: favFirst);
                    var page = await catalog.QueryAsync(q);
                    Assert.Equal(files.Length, page.Items.Count);
                    for (int i = 0; i < page.Items.Count; i++)
                    {
                        var pos = await catalog.LocateAsync(q, page.Items[i].Media.SourceId, page.Items[i].Media.RelativePath);
                        Assert.Equal(i, pos);
                    }
                }
            }
        }

        var filteredQ = new LibraryQuery(SourceId: "s-loc", Search: "tag:trip", Sort: MediaSort.Captured, Descending: true);
        var filteredPage = await catalog.QueryAsync(filteredQ);
        Assert.Equal(2, filteredPage.Items.Count);
        Assert.Equal(1, await catalog.LocateAsync(filteredQ, filteredPage.Items[1].Media.SourceId, filteredPage.Items[1].Media.RelativePath));
        Assert.Null(await catalog.LocateAsync(filteredQ, "s-loc", "b.png"));
    }

    [Fact]
    public async Task MonthBucketsAsyncFastPathMatchesFallbackAcrossManyMonthsAndFilters()
    {
        var dbPath = Path.Combine(tempDir, "months.db");
        var monthCatalog = new MediaCache(dbPath);
        await monthCatalog.InitializeAsync();

        // Populate 130 distinct months (>120 fast-path threshold) plus 1 pre-1970 row.
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
        {
            await db.OpenAsync();
            await using var tx = db.BeginTransaction();
            await using var cmd = db.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO sources VALUES('s1','C:\\\\test','2026-01-01T00:00:00Z');";
            await cmd.ExecuteNonQueryAsync();

            var ins = db.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT INTO media VALUES('s1',$p,1000,$t,'photo')";
            var pP = ins.Parameters.Add("$p", Microsoft.Data.Sqlite.SqliteType.Text);
            var pT = ins.Parameters.Add("$t", Microsoft.Data.Sqlite.SqliteType.Integer);
            ins.Prepare();

            var anchor = new DateTime(2025, 1, 15, 12, 0, 0, DateTimeKind.Utc);
            for (int i = 0; i < 130; i++)
            {
                pP.Value = $"m{i:000}.jpg";
                pT.Value = anchor.AddMonths(-i).Ticks;
                await ins.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        // Fast-path fallback (>120 months) returns all 130 months accurately
        var allMonths = await monthCatalog.MonthBucketsAsync(new LibraryQuery(Sort: MediaSort.Captured));
        Assert.Equal(130, allMonths.Count);
        Assert.Equal("2025-01", allMonths[0].Month);

        // With source filter on a small fast-path compatible DB (3 months) -> verify exact equality with fallback
        var smallDbPath = Path.Combine(tempDir, "months-small.db");
        var smallCat = new MediaCache(smallDbPath);
        await smallCat.InitializeAsync();
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = smallDbPath }.ToString()))
        {
            await db.OpenAsync();
            await using var cmd = db.CreateCommand();
            var d1 = new DateTime(2025, 3, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;
            var d2 = new DateTime(2025, 2, 10, 12, 0, 0, DateTimeKind.Utc).Ticks;
            cmd.CommandText = $"""
                INSERT INTO sources VALUES('s1','C:\\\\test','2026-01-01T00:00:00Z');
                INSERT INTO media VALUES('s1','a.jpg',100,{d1},'photo');
                INSERT INTO media VALUES('s1','b.jpg',100,{d1 + 10000000},'photo');
                INSERT INTO media VALUES('s1','c.png',100,{d2},'photo');
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        var fastBuckets = await smallCat.MonthBucketsAsync(new LibraryQuery(Sort: MediaSort.Captured));
        Assert.Equal(2, fastBuckets.Count);
        Assert.Equal("2025-03", fastBuckets[0].Month);
        Assert.Equal(2, fastBuckets[0].Count);
        Assert.Equal("2025-02", fastBuckets[1].Month);
        Assert.Equal(1, fastBuckets[1].Count);

        await smallCat.SetExclusionAsync(new LibraryExclusion("type", "", ".png"), true);
        var exclBuckets = await smallCat.MonthBucketsAsync(new LibraryQuery(Sort: MediaSort.Captured));
        Assert.Single(exclBuckets);
        Assert.Equal(2, exclBuckets[0].Count);
    }

    [Fact]
    public async Task ThumbnailKeyAndCacheOptimization_PreserveExactHashAndFastLruEviction()
    {
        var longPath = new string('x', 600) + "/📷_photo.jpg";
        var media = new CachedMedia("vol-😀", longPath, 123456789L, 638712864000000000L, "photo", true);
        var expectedRaw = System.Text.Encoding.UTF8.GetBytes($"{media.SourceId}\n{media.RelativePath}\n{media.Length}\n{media.ModifiedTicks}");
        var expectedMediaKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expectedRaw)).ToLowerInvariant();
        Assert.Equal(expectedMediaKey, MediaCache.ThumbnailKey(media));

        var expectedThumbKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"luma-thumbnail-v1:512:{expectedMediaKey}"))).ToLowerInvariant();
        Assert.Equal(expectedThumbKey, ThumbnailCache.Key(media, 512));

        var cacheDir = Path.Combine(tempDir, "thumb-opt");
        using var thumbCache = new ThumbnailCache(cacheDir, maxBytes: 10);
        var k1 = ThumbnailCache.Key(new("s", "1.jpg", 4, 1, "photo", true));
        var k2 = ThumbnailCache.Key(new("s", "2.jpg", 4, 2, "photo", true));
        var k3 = ThumbnailCache.Key(new("s", "3.jpg", 4, 3, "photo", true));
        await thumbCache.StoreAsync(k1, new byte[4]);
        await thumbCache.StoreAsync(k2, new byte[4]);
        // Rapid read within same tick must still keep k1 alive and evict k2 when storing k3.
        Assert.NotNull(await thumbCache.ReadAsync(k1));
        await thumbCache.StoreAsync(k3, new byte[4]);
        Assert.NotNull(await thumbCache.ReadAsync(k1));
        Assert.Null(await thumbCache.ReadAsync(k2));
        Assert.NotNull(await thumbCache.ReadAsync(k3));
    }

    [Fact]
    public async Task DuplicateDetectionBulkTagAndMinimalGpsNonContainerSkip_WorkCorrectly()
    {
        var dir = Path.Combine(tempDir, "DupTags");
        Directory.CreateDirectory(dir);
        var sameBytes = new byte[2048];
        Random.Shared.NextBytes(sameBytes);
        var diffBytes = (byte[])sameBytes.Clone();
        diffBytes[0] ^= 0xFF;

        File.WriteAllBytes(Path.Combine(dir, "dup1.jpg"), sameBytes);
        File.WriteAllBytes(Path.Combine(dir, "dup2.jpg"), sameBytes);
        File.WriteAllBytes(Path.Combine(dir, "same_size_diff.jpg"), diffBytes);
        File.WriteAllBytes(Path.Combine(dir, "tiny_ignored.jpg"), new byte[512]);
        File.WriteAllBytes(Path.Combine(dir, "pic.bmp"), [0x42, 0x4D, 0, 0]);
        File.WriteAllText(Path.Combine(dir, "pic.bmp.xmp"), """
            <x:xmpmeta xmlns:x="adobe:ns:meta/">
              <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">
                <rdf:Description xmlns:exif="http://ns.adobe.com/exif/1.0/" exif:GPSLatitude="12,30.0N" exif:GPSLongitude="77,30.0E" />
              </rdf:RDF>
            </x:xmpmeta>
            """);

        var resolver = new MapResolver(new Dictionary<string, string> { ["s-dup"] = dir });
        var scopedCatalog = new MediaCache(catalogPath, resolver);
        await scopedCatalog.ScanAsync("s-dup", dir);

        var dups = await scopedCatalog.FindDuplicatesAsync("s-dup");
        var grp = Assert.Single(dups);
        Assert.Equal(2, grp.Items.Count);
        Assert.Equal(2048, grp.FileLength);

        var allMedia = await scopedCatalog.ListAsync("s-dup");
        await scopedCatalog.BulkTagAsync(allMedia, ["vacation", "2025"], []);
        await scopedCatalog.RenameTagAsync("vacation", "holiday");
        var tags = await scopedCatalog.GetTagsWithCountsAsync();
        Assert.Contains(tags, t => t.Tag == "holiday" && t.Count == allMedia.Count);
        Assert.DoesNotContain(tags, t => t.Tag == "vacation");

        var bmpGps = MinimalGpsExtractor.ExtractFromFile(Path.Combine(dir, "pic.bmp"));
        Assert.True(bmpGps.HasCoordinates);
        Assert.InRange(bmpGps.Latitude!.Value, 12.49, 12.51);
        Assert.Equal("sidecar", bmpGps.Origin);
    }
}
