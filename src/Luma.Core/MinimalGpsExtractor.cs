using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Luma.Core;

/// <summary>Location data recovered from media file headers, video atoms, or sidecar XMP files.</summary>
public sealed record ExtractedLocation(
    double? Latitude = null,
    double? Longitude = null,
    string? LocationName = null,
    string? Origin = null)
{
    public bool HasCoordinates => Latitude.HasValue && Longitude.HasValue;
    public bool HasAnyLocation => HasCoordinates || !string.IsNullOrWhiteSpace(LocationName);
}

/// <summary>
/// Read-only, bounded binary/XML metadata reader capable of extracting minimal GPS coordinates
/// and place tags from JPEG, TIFF, DNG, RAW, PNG, WebP, HEIC/AVIF, MP4/MOV (ISO-6709), and .xmp sidecars.
/// </summary>
public static partial class MinimalGpsExtractor
{
    private const int MaxMetadataBufferBytes = 4 * 1024 * 1024;
    private const int MaxXmpFileBytes = 1024 * 1024;
    private const int MaxIfdEntries = 512;

    public static (double? Lat, double? Lon) NormalizeCoords(double? lat, double? lon)
    {
        if (lat is not double la || lon is not double lo) return (null, null);
        if (!double.IsFinite(la) || !double.IsFinite(lo)) return (null, null);
        if (la < -90 || la > 90 || lo < -180 || lo > 180) return (null, null);
        if (Math.Abs(la) < 1e-6 && Math.Abs(lo) < 1e-6) return (null, null);
        return (Math.Round(la, 6), Math.Round(lo, 6));
    }

    public static double? ConvertDmsToDecimal(IReadOnlyList<double> parts, string? hemisphereRef)
    {
        if (parts.Count == 0) return null;
        double deg = parts[0];
        double min = parts.Count > 1 ? parts[1] : 0;
        double sec = parts.Count > 2 ? parts[2] : 0;
        if (!double.IsFinite(deg) || !double.IsFinite(min) || !double.IsFinite(sec)) return null;

        var sign = deg < 0 ? -1.0 : 1.0;
        var val = Math.Abs(deg) + (Math.Abs(min) / 60.0) + (Math.Abs(sec) / 3600.0);
        if (!string.IsNullOrWhiteSpace(hemisphereRef))
        {
            var c = char.ToUpperInvariant(hemisphereRef.Trim()[0]);
            if (c is 'S' or 'W') sign = -1.0;
            else if (c is 'N' or 'E') sign = sign < 0 ? -1.0 : 1.0;
        }
        return val * sign;
    }

    public static ExtractedLocation ExtractFromFile(string path)
    {
        ExtractedLocation best = new();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return best;

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".bmp" or ".gif" or ".ico" or ".svg" or ".wmv" or ".avi" or ".mkv" or ".webm" or ".flv" or ".ts" or ".mts" or ".m2ts"))
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                best = ExtractFromStream(fs, ext);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // Keep going so an adjacent .xmp sidecar can still supply metadata cleanly.
            }
        }

        // Check adjacent XMP sidecars (<file>.ext.xmp and <file>.xmp) when coords or place name are still missing.
        if (!best.HasCoordinates || string.IsNullOrWhiteSpace(best.LocationName))
        {
            foreach (var sidecar in new[] { path + ".xmp", Path.ChangeExtension(path, ".xmp") })
            {
                if (string.IsNullOrWhiteSpace(sidecar) || sidecar.Equals(path, StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(sidecar)) continue;
                try
                {
                    var len = new FileInfo(sidecar).Length;
                    if (len is <= 0 or > MaxXmpFileBytes) continue;
                    var xmpBytes = File.ReadAllBytes(sidecar);
                    var parsed = ParseXmpBytes(xmpBytes, "sidecar");
                    best = Merge(best, parsed);
                    if (best.HasCoordinates && !string.IsNullOrWhiteSpace(best.LocationName)) break;
                }
                catch { }
            }
        }

        return best;
    }

    public static ExtractedLocation ExtractFromStream(Stream stream, string? extensionHint = null)
    {
        if (!stream.CanRead || !stream.CanSeek) return new();

        Span<byte> head = stackalloc byte[16];
        stream.Position = 0;
        var read = stream.Read(head);
        if (read < 8) return new();
        stream.Position = 0;

        ExtractedLocation result = new();
        if (head[0] == 0xFF && head[1] == 0xD8)
        {
            result = ExtractFromJpeg(stream);
        }
        else if (head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47)
        {
            result = ExtractFromPng(stream);
        }
        else if (head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' &&
                 read >= 12 && head[8] == 'W' && head[9] == 'E' && head[10] == 'B' && head[11] == 'P')
        {
            result = ExtractFromWebP(stream);
        }
        else if (read >= 12 && head[4] == 'f' && head[5] == 't' && head[6] == 'y' && head[7] == 'p')
        {
            result = ExtractFromIsoBmff(stream);
        }
        else if ((head[0] == 'I' && head[1] == 'I' && head[2] == 0x2A && head[3] == 0x00) ||
                 (head[0] == 'M' && head[1] == 'M' && head[2] == 0x00 && head[3] == 0x2A) ||
                 extensionHint is ".dng" or ".cr2" or ".nef" or ".arw" or ".orf" or ".rw2" or ".tif" or ".tiff")
        {
            result = ExtractFromTiffHeaderStream(stream);
        }

        return result;
    }

    private static ExtractedLocation Merge(ExtractedLocation primary, ExtractedLocation fallback)
    {
        var lat = primary.Latitude ?? fallback.Latitude;
        var lon = primary.Longitude ?? fallback.Longitude;
        var (nLat, nLon) = NormalizeCoords(lat, lon);
        var locName = !string.IsNullOrWhiteSpace(primary.LocationName) ? primary.LocationName : fallback.LocationName;
        var origin = primary.HasCoordinates ? primary.Origin
            : fallback.HasCoordinates ? fallback.Origin
            : primary.Origin ?? fallback.Origin;
        return new ExtractedLocation(nLat, nLon, locName, origin);
    }

    private static ExtractedLocation ExtractFromJpeg(Stream stream)
    {
        var acc = new ExtractedLocation();
        var len = stream.Length;
        stream.Position = 2;
        Span<byte> lenBytes = stackalloc byte[2];

        while (stream.Position + 2 <= len)
        {
            var b0 = stream.ReadByte();
            if (b0 < 0) break;
            if (b0 != 0xFF) continue;

            var marker = stream.ReadByte();
            while (marker == 0xFF) marker = stream.ReadByte();
            if (marker < 0 || marker == 0xD9 || marker == 0xDA) break; // EOI or SOS
            if (marker is 0x00 or 0x01 or (>= 0xD0 and <= 0xD8)) continue;

            if (stream.Read(lenBytes) < 2) break;
            var segLen = BinaryPrimitives.ReadUInt16BigEndian(lenBytes);
            if (segLen < 2) break;

            var payloadLen = segLen - 2;
            var nextPos = stream.Position + payloadLen;
            if (nextPos > len) break;

            if (marker == 0xE1 && payloadLen > 6 && payloadLen <= MaxMetadataBufferBytes)
            {
                var buf = new byte[payloadLen];
                if (ReadExact(stream, buf) == payloadLen)
                {
                    if (buf.AsSpan().StartsWith("Exif\0\0"u8))
                    {
                        acc = Merge(acc, ParseTiffGps(buf.AsSpan(6), "exif"));
                    }
                    else if (buf.AsSpan().StartsWith("http://ns.adobe.com/xap/1.0/\0"u8))
                    {
                        acc = Merge(acc, ParseXmpBytes(buf.AsSpan(29), "xmp"));
                    }
                }
            }

            stream.Position = nextPos;
            if (acc.HasCoordinates && !string.IsNullOrWhiteSpace(acc.LocationName)) break;
        }
        return acc;
    }

    private static ExtractedLocation ExtractFromPng(Stream stream)
    {
        var acc = new ExtractedLocation();
        var len = stream.Length;
        stream.Position = 8;
        Span<byte> header = stackalloc byte[8];

        for (var chunkIndex = 0; chunkIndex < 4096 && stream.Position + 8 <= len; chunkIndex++)
        {
            if (ReadExact(stream, header) < 8) break;
            var chunkLen = BinaryPrimitives.ReadUInt32BigEndian(header[..4]);
            var type = Encoding.ASCII.GetString(header[4..8]);
            var nextPos = stream.Position + chunkLen + 4; // payload + 4-byte CRC
            if (nextPos > len) break;

            if (type == "eXIf" && chunkLen > 8 && chunkLen <= MaxMetadataBufferBytes)
            {
                var buf = new byte[chunkLen];
                if (ReadExact(stream, buf) == chunkLen)
                {
                    var span = buf.AsSpan();
                    if (span.StartsWith("Exif\0\0"u8)) span = span[6..];
                    acc = Merge(acc, ParseTiffGps(span, "exif"));
                }
            }
            else if (type is "tEXt" or "iTXt" or "zTXt" && chunkLen is > 16 and <= MaxMetadataBufferBytes)
            {
                var buf = new byte[chunkLen];
                if (ReadExact(stream, buf) == chunkLen)
                {
                    var xmpBytes = TryExtractPngXmpChunk(type, buf);
                    if (!xmpBytes.IsEmpty) acc = Merge(acc, ParseXmpBytes(xmpBytes, "xmp"));
                }
            }
            else if (type == "IEND")
            {
                break;
            }

            stream.Position = nextPos;
            if (acc.HasCoordinates && !string.IsNullOrWhiteSpace(acc.LocationName)) break;
        }
        return acc;
    }

    private static ReadOnlySpan<byte> TryExtractPngXmpChunk(string type, byte[] buf)
    {
        var nul = Array.IndexOf(buf, (byte)0);
        if (nul <= 0) return default;
        var keyword = Encoding.Latin1.GetString(buf, 0, nul);
        if (!keyword.Equals("XML:com.adobe.xmp", StringComparison.OrdinalIgnoreCase)) return default;

        if (type == "tEXt")
        {
            return nul + 1 < buf.Length ? buf.AsSpan(nul + 1) : default;
        }
        if (type == "zTXt" && nul + 2 < buf.Length && buf[nul + 1] == 0)
        {
            return InflateBounded(buf.AsSpan(nul + 2));
        }
        if (type == "iTXt" && nul + 5 < buf.Length)
        {
            var compressionFlag = buf[nul + 1];
            var idx = nul + 3;
            var langNul = Array.IndexOf(buf, (byte)0, idx);
            if (langNul < 0 || langNul + 1 >= buf.Length) return default;
            var transNul = Array.IndexOf(buf, (byte)0, langNul + 1);
            if (transNul < 0 || transNul + 1 >= buf.Length) return default;
            var data = buf.AsSpan(transNul + 1);
            return compressionFlag == 1 ? InflateBounded(data) : data;
        }
        return default;
    }

    private static byte[]? InflateBounded(ReadOnlySpan<byte> compressed)
    {
        try
        {
            using var ms = new MemoryStream(compressed.ToArray());
            using var z = new ZLibStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            var tmp = new byte[8192];
            int total = 0, read;
            while ((read = z.Read(tmp, 0, tmp.Length)) > 0)
            {
                total += read;
                if (total > MaxXmpFileBytes) return null;
                outMs.Write(tmp, 0, read);
            }
            return outMs.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static ExtractedLocation ExtractFromWebP(Stream stream)
    {
        var acc = new ExtractedLocation();
        var len = stream.Length;
        stream.Position = 12;
        Span<byte> header = stackalloc byte[8];

        while (stream.Position + 8 <= len)
        {
            if (ReadExact(stream, header) < 8) break;
            var fourcc = Encoding.ASCII.GetString(header[..4]);
            var chunkLen = BinaryPrimitives.ReadUInt32LittleEndian(header[4..8]);
            var padded = (long)chunkLen + (chunkLen & 1);
            var nextPos = stream.Position + padded;
            if (nextPos > len) break;

            if (fourcc == "EXIF" && chunkLen > 8 && chunkLen <= MaxMetadataBufferBytes)
            {
                var buf = new byte[chunkLen];
                if (ReadExact(stream, buf) == chunkLen)
                {
                    var span = buf.AsSpan();
                    if (span.StartsWith("Exif\0\0"u8)) span = span[6..];
                    acc = Merge(acc, ParseTiffGps(span, "exif"));
                }
            }
            else if (fourcc == "XMP " && chunkLen > 16 && chunkLen <= MaxMetadataBufferBytes)
            {
                var buf = new byte[chunkLen];
                if (ReadExact(stream, buf) == chunkLen) acc = Merge(acc, ParseXmpBytes(buf, "xmp"));
            }

            stream.Position = nextPos;
            if (acc.HasCoordinates && !string.IsNullOrWhiteSpace(acc.LocationName)) break;
        }
        return acc;
    }

    private static ExtractedLocation ExtractFromTiffHeaderStream(Stream stream)
    {
        stream.Position = 0;
        var len = (int)Math.Min(stream.Length, MaxMetadataBufferBytes);
        if (len < 8) return new();
        var buf = new byte[len];
        var read = ReadExact(stream, buf);
        return ParseTiffGps(buf.AsSpan(0, read), "exif");
    }

    private static ExtractedLocation ExtractFromIsoBmff(Stream stream)
    {
        var acc = new ExtractedLocation();
        WalkIsoBoxes(stream, 0, stream.Length, ref acc, depth: 0);
        return acc;
    }

    private static void WalkIsoBoxes(Stream stream, long start, long end, ref ExtractedLocation acc, int depth)
    {
        if (depth > 8 || start >= end) return;
        Span<byte> hdr = stackalloc byte[16];
        long pos = start;

        while (pos + 8 <= end)
        {
            stream.Position = pos;
            if (ReadExact(stream, hdr[..8]) < 8) break;
            ulong boxSize = BinaryPrimitives.ReadUInt32BigEndian(hdr[..4]);
            var typeBytes = hdr[4..8];
            long headerLen = 8;

            if (boxSize == 1)
            {
                if (pos + 16 > end || ReadExact(stream, hdr[8..16]) < 8) break;
                boxSize = BinaryPrimitives.ReadUInt64BigEndian(hdr[8..16]);
                headerLen = 16;
            }
            else if (boxSize == 0)
            {
                boxSize = (ulong)(end - pos);
            }

            if (boxSize < (ulong)headerLen) break;
            long boxEnd = pos + (long)Math.Min(boxSize, (ulong)(end - pos));
            long childStart = pos + headerLen;

            var type = Encoding.Latin1.GetString(typeBytes);
            if (type is "moov" or "trak" or "udta" or "edts")
            {
                WalkIsoBoxes(stream, childStart, boxEnd, ref acc, depth + 1);
            }
            else if (type == "meta")
            {
                var metaStart = ResolveMetaChildOffset(stream, childStart, boxEnd);
                var metaLen = boxEnd - metaStart;
                if (metaLen > 8 && metaLen <= MaxMetadataBufferBytes)
                {
                    var metaBuf = new byte[metaLen];
                    stream.Position = metaStart;
                    if (ReadExact(stream, metaBuf) == metaLen)
                    {
                        acc = Merge(acc, ParseIsoMetaBox(metaBuf, stream));
                    }
                }
            }
            else if (type == "\u00A9xyz")
            {
                var size = boxEnd - childStart;
                if (size is > 4 and <= 1024)
                {
                    var buf = new byte[size];
                    stream.Position = childStart;
                    if (ReadExact(stream, buf) == size)
                    {
                        var text = DecodeAtomString(buf);
                        if (TryParseIso6709(text, out var lat, out var lon))
                            acc = Merge(acc, new ExtractedLocation(lat, lon, null, "video-iso6709"));
                    }
                }
            }
            else if (type is "XMP_" or "uuid")
            {
                var size = boxEnd - childStart;
                if (size is > 32 and <= MaxMetadataBufferBytes)
                {
                    var buf = new byte[size];
                    stream.Position = childStart;
                    if (ReadExact(stream, buf) == size)
                    {
                        if (type == "XMP_")
                            acc = Merge(acc, ParseXmpBytes(buf, "xmp"));
                        else if (buf.Length > 16 && IsXmpUuid(buf.AsSpan(0, 16)))
                            acc = Merge(acc, ParseXmpBytes(buf.AsSpan(16), "xmp"));
                    }
                }
            }

            if (acc.HasCoordinates && !string.IsNullOrWhiteSpace(acc.LocationName)) break;
            pos = boxEnd;
        }
    }

    private static bool IsXmpUuid(ReadOnlySpan<byte> uuid) =>
        uuid.SequenceEqual(new byte[] { 0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8, 0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC });

    private static long ResolveMetaChildOffset(Stream stream, long childStart, long boxEnd)
    {
        if (childStart + 12 > boxEnd) return childStart;
        Span<byte> peek = stackalloc byte[12];
        stream.Position = childStart;
        if (ReadExact(stream, peek) < 12) return childStart;
        var direct = Encoding.Latin1.GetString(peek[4..8]);
        if (direct is "hdlr" or "keys" or "ilst" or "iinf" or "iloc" or "idat") return childStart;
        var shifted = Encoding.Latin1.GetString(peek[8..12]);
        if (shifted is "hdlr" or "keys" or "ilst" or "iinf" or "iloc" or "idat") return childStart + 4;
        return childStart + 4;
    }

    private static ExtractedLocation ParseIsoMetaBox(byte[] metaBuf, Stream fileStream)
    {
        var acc = new ExtractedLocation();
        var keys = new List<string>();
        ReadOnlySpan<byte> ilstSpan = default;
        var exifItemIds = new HashSet<uint>();
        var xmpItemIds = new HashSet<uint>();
        var itemLocations = new Dictionary<uint, (ushort Method, long Offset, long Length)>();
        ReadOnlySpan<byte> idatSpan = default;

        int pos = 0;
        while (pos + 8 <= metaBuf.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(metaBuf.AsSpan(pos, 4));
            if (size < 8 || pos + size > metaBuf.Length) break;
            var type = Encoding.Latin1.GetString(metaBuf, pos + 4, 4);
            var body = metaBuf.AsSpan(pos + 8, (int)size - 8);

            if (type == "keys" && body.Length >= 8)
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(body[4..8]);
                int kp = 8;
                for (uint i = 0; i < count && kp + 8 <= body.Length; i++)
                {
                    var kSize = (int)BinaryPrimitives.ReadUInt32BigEndian(body.Slice(kp, 4));
                    if (kSize < 8 || kp + kSize > body.Length) break;
                    keys.Add(Encoding.UTF8.GetString(body.Slice(kp + 8, kSize - 8)));
                    kp += kSize;
                }
            }
            else if (type == "ilst")
            {
                ilstSpan = body;
            }
            else if (type == "idat")
            {
                idatSpan = body;
            }
            else if (type == "iinf")
            {
                ParseHeifIinf(body, exifItemIds, xmpItemIds);
            }
            else if (type == "iloc")
            {
                ParseHeifIloc(body, itemLocations);
            }

            pos += (int)size;
        }

        if (!ilstSpan.IsEmpty && keys.Count > 0)
        {
            acc = Merge(acc, ParseQuickTimeIlst(ilstSpan, keys));
        }

        // HEIC/HEIF Exif & XMP extents referenced from meta -> iloc
        foreach (var id in exifItemIds)
        {
            if (itemLocations.TryGetValue(id, out var loc))
            {
                var raw = ReadHeifExtent(loc, idatSpan, fileStream);
                if (raw is { Length: > 10 })
                {
                    var tiffOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(0, 4));
                    var start = 4 + tiffOffset;
                    if (start < raw.Length)
                    {
                        var span = raw.AsSpan(start);
                        if (span.StartsWith("Exif\0\0"u8)) span = span[6..];
                        acc = Merge(acc, ParseTiffGps(span, "exif"));
                    }
                    else
                    {
                        var idx = raw.AsSpan().IndexOf("Exif\0\0"u8);
                        if (idx >= 0 && idx + 6 < raw.Length)
                            acc = Merge(acc, ParseTiffGps(raw.AsSpan(idx + 6), "exif"));
                    }
                }
            }
        }

        foreach (var id in xmpItemIds)
        {
            if (itemLocations.TryGetValue(id, out var loc))
            {
                var raw = ReadHeifExtent(loc, idatSpan, fileStream);
                if (raw is { Length: > 16 }) acc = Merge(acc, ParseXmpBytes(raw, "xmp"));
            }
        }

        return acc;
    }

    private static ExtractedLocation ParseQuickTimeIlst(ReadOnlySpan<byte> ilst, List<string> keys)
    {
        var acc = new ExtractedLocation();
        int p = 0;
        while (p + 8 <= ilst.Length)
        {
            var entrySize = (int)BinaryPrimitives.ReadUInt32BigEndian(ilst.Slice(p, 4));
            if (entrySize < 8 || p + entrySize > ilst.Length) break;
            var keyIdx = BinaryPrimitives.ReadUInt32BigEndian(ilst.Slice(p + 4, 4));
            if (keyIdx >= 1 && keyIdx <= keys.Count)
            {
                var keyName = keys[(int)keyIdx - 1];
                var valueStr = ExtractIlstDataUtf8(ilst.Slice(p + 8, entrySize - 8));
                if (!string.IsNullOrWhiteSpace(valueStr))
                {
                    if (keyName.Contains("ISO6709", StringComparison.OrdinalIgnoreCase) &&
                        TryParseIso6709(valueStr, out var lat, out var lon))
                    {
                        acc = Merge(acc, new ExtractedLocation(lat, lon, null, "video-iso6709"));
                    }
                    else if (keyName.Contains("location.name", StringComparison.OrdinalIgnoreCase))
                    {
                        acc = Merge(acc, new ExtractedLocation(null, null, valueStr.Trim(), "video-iso6709"));
                    }
                }
            }
            p += entrySize;
        }
        return acc;
    }

    private static string? ExtractIlstDataUtf8(ReadOnlySpan<byte> entryBody)
    {
        int p = 0;
        while (p + 8 <= entryBody.Length)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(entryBody.Slice(p, 4));
            if (s < 8 || p + s > entryBody.Length) break;
            var t = Encoding.Latin1.GetString(entryBody.Slice(p + 4, 4));
            if (t == "data" && s > 16)
            {
                return Encoding.UTF8.GetString(entryBody.Slice(p + 16, s - 16)).TrimEnd('\0');
            }
            p += s;
        }
        return null;
    }

    private static void ParseHeifIinf(ReadOnlySpan<byte> body, HashSet<uint> exifIds, HashSet<uint> xmpIds)
    {
        if (body.Length < 8) return;
        var version = body[0];
        var countSize = version == 0 ? 2 : 4;
        if (4 + countSize > body.Length) return;
        uint count = version == 0
            ? BinaryPrimitives.ReadUInt16BigEndian(body.Slice(4, 2))
            : BinaryPrimitives.ReadUInt32BigEndian(body.Slice(4, 4));
        int p = 4 + countSize;

        for (uint i = 0; i < count && p + 8 <= body.Length; i++)
        {
            var s = (int)BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p, 4));
            if (s < 16 || p + s > body.Length) break;
            var boxType = Encoding.Latin1.GetString(body.Slice(p + 4, 4));
            if (boxType == "infe")
            {
                var infeVer = body[p + 8];
                if (infeVer is 2 or 3 && p + 16 <= body.Length)
                {
                    int off = p + 12;
                    uint itemId = infeVer == 2
                        ? BinaryPrimitives.ReadUInt16BigEndian(body.Slice(off, 2))
                        : BinaryPrimitives.ReadUInt32BigEndian(body.Slice(off, 4));
                    off += (infeVer == 2 ? 2 : 4) + 2; // + protection index
                    if (off + 4 <= p + s)
                    {
                        var itemType = Encoding.Latin1.GetString(body.Slice(off, 4));
                        if (itemType == "Exif")
                        {
                            exifIds.Add(itemId);
                        }
                        else if (itemType == "mime")
                        {
                            var mimeStr = Encoding.Latin1.GetString(body.Slice(off + 4, p + s - (off + 4)));
                            if (mimeStr.Contains("rdf+xml", StringComparison.OrdinalIgnoreCase) ||
                                mimeStr.Contains("xmp", StringComparison.OrdinalIgnoreCase))
                            {
                                xmpIds.Add(itemId);
                            }
                        }
                    }
                }
            }
            p += s;
        }
    }

    private static void ParseHeifIloc(ReadOnlySpan<byte> body, Dictionary<uint, (ushort Method, long Offset, long Length)> map)
    {
        if (body.Length < 8) return;
        var version = body[0];
        var offsetSize = body[4] >> 4;
        var lengthSize = body[4] & 0xF;
        var baseOffsetSize = body[5] >> 4;
        var indexSize = version is 1 or 2 ? (body[5] & 0xF) : 0;

        int p = 6;
        uint itemCount;
        if (version < 2)
        {
            if (p + 2 > body.Length) return;
            itemCount = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2));
            p += 2;
        }
        else
        {
            if (p + 4 > body.Length) return;
            itemCount = BinaryPrimitives.ReadUInt32BigEndian(body.Slice(p, 4));
            p += 4;
        }

        for (uint i = 0; i < itemCount && p < body.Length; i++)
        {
            uint itemId = version < 2 ? ReadUIntN(body, ref p, 2) : ReadUIntN(body, ref p, 4);
            ushort method = 0;
            if (version is 1 or 2)
            {
                if (p + 2 > body.Length) break;
                method = (ushort)(BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2)) & 0x0F);
                p += 2;
            }
            if (p + 2 > body.Length) break;
            p += 2; // data_reference_index
            var baseOffset = ReadULongN(body, ref p, baseOffsetSize);
            if (p + 2 > body.Length) break;
            var extentCount = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(p, 2));
            p += 2;

            for (int e = 0; e < extentCount; e++)
            {
                if (version is 1 or 2 && indexSize > 0) _ = ReadULongN(body, ref p, indexSize);
                var extOffset = ReadULongN(body, ref p, offsetSize);
                var extLen = ReadULongN(body, ref p, lengthSize);
                if (e == 0 && extLen is > 0 and <= MaxMetadataBufferBytes)
                {
                    map[itemId] = (method, (long)(baseOffset + extOffset), (long)extLen);
                }
            }
        }
    }

    private static byte[]? ReadHeifExtent((ushort Method, long Offset, long Length) loc, ReadOnlySpan<byte> idat, Stream stream)
    {
        if (loc.Length <= 0 || loc.Length > MaxMetadataBufferBytes) return null;
        if (loc.Method == 1)
        {
            if (loc.Offset < 0 || loc.Offset + loc.Length > idat.Length) return null;
            return idat.Slice((int)loc.Offset, (int)loc.Length).ToArray();
        }
        if (loc.Method == 0 && loc.Offset >= 0 && loc.Offset + loc.Length <= stream.Length)
        {
            var buf = new byte[loc.Length];
            stream.Position = loc.Offset;
            return ReadExact(stream, buf) == loc.Length ? buf : null;
        }
        return null;
    }

    private static uint ReadUIntN(ReadOnlySpan<byte> span, ref int p, int size) =>
        (uint)ReadULongN(span, ref p, size);

    private static ulong ReadULongN(ReadOnlySpan<byte> span, ref int p, int size)
    {
        if (size <= 0 || p + size > span.Length) return 0;
        ulong val = 0;
        for (int i = 0; i < size; i++) val = (val << 8) | span[p + i];
        p += size;
        return val;
    }

    private static string DecodeAtomString(byte[] raw)
    {
        if (raw.Length >= 4)
        {
            var len = BinaryPrimitives.ReadUInt16BigEndian(raw.AsSpan(0, 2));
            if (len > 0 && len <= raw.Length - 4)
                return Encoding.UTF8.GetString(raw, 4, len);
        }
        return Encoding.UTF8.GetString(raw).TrimEnd('\0');
    }

    public static bool TryParseIso6709(string? text, out double lat, out double lon)
    {
        lat = 0;
        lon = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var match = Iso6709Regex().Match(text.Trim());
        if (!match.Success) return false;
        if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLat)) return false;
        if (!double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedLon)) return false;
        var (nLat, nLon) = NormalizeCoords(parsedLat, parsedLon);
        if (nLat is null || nLon is null) return false;
        lat = nLat.Value;
        lon = nLon.Value;
        return true;
    }

    private static ushort ReadU16(ReadOnlySpan<byte> tiff, int o, bool le) =>
        le ? BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(o, 2)) : BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(o, 2));

    private static uint ReadU32(ReadOnlySpan<byte> tiff, int o, bool le) =>
        le ? BinaryPrimitives.ReadUInt32LittleEndian(tiff.Slice(o, 4)) : BinaryPrimitives.ReadUInt32BigEndian(tiff.Slice(o, 4));

    private static int ReadI32(ReadOnlySpan<byte> tiff, int o, bool le) =>
        le ? BinaryPrimitives.ReadInt32LittleEndian(tiff.Slice(o, 4)) : BinaryPrimitives.ReadInt32BigEndian(tiff.Slice(o, 4));

    public static ExtractedLocation ParseTiffGps(ReadOnlySpan<byte> tiff, string origin)
    {
        if (tiff.Length < 8) return new();
        bool littleEndian;
        if (tiff[0] == 'I' && tiff[1] == 'I' && BinaryPrimitives.ReadUInt16LittleEndian(tiff.Slice(2, 2)) == 42)
            littleEndian = true;
        else if (tiff[0] == 'M' && tiff[1] == 'M' && BinaryPrimitives.ReadUInt16BigEndian(tiff.Slice(2, 2)) == 42)
            littleEndian = false;
        else
            return new();

        var acc = new ExtractedLocation();
        uint gpsIfdOffset = 0;
        int ifdOffset = (int)ReadU32(tiff, 4, littleEndian);

        for (int ifdDepth = 0; ifdDepth < 4 && ifdOffset >= 8 && ifdOffset + 2 <= tiff.Length; ifdDepth++)
        {
            int count = Math.Min(MaxIfdEntries, (int)ReadU16(tiff, ifdOffset, littleEndian));
            int p = ifdOffset + 2;
            for (int i = 0; i < count && p + 12 <= tiff.Length; i++, p += 12)
            {
                ushort tag = ReadU16(tiff, p, littleEndian);
                ushort type = ReadU16(tiff, p + 2, littleEndian);
                uint valCount = ReadU32(tiff, p + 4, littleEndian);
                uint valOrOffset = ReadU32(tiff, p + 8, littleEndian);

                if (tag == 0x8825 && valOrOffset > 0 && valOrOffset < tiff.Length)
                {
                    gpsIfdOffset = valOrOffset;
                }
                else if (tag == 0x02BC && valCount > 16 && valCount <= MaxMetadataBufferBytes && valOrOffset + valCount <= tiff.Length)
                {
                    acc = Merge(acc, ParseXmpBytes(tiff.Slice((int)valOrOffset, (int)valCount), "xmp"));
                }
            }

            if (gpsIfdOffset > 0) break;
            if (p + 4 > tiff.Length) break;
            ifdOffset = (int)ReadU32(tiff, p, littleEndian);
        }

        if (gpsIfdOffset > 0 && gpsIfdOffset + 2 <= tiff.Length)
        {
            string? latRef = null, lonRef = null, areaName = null;
            double? latVal = null, lonVal = null;

            int count = Math.Min(MaxIfdEntries, (int)ReadU16(tiff, (int)gpsIfdOffset, littleEndian));
            int p = (int)gpsIfdOffset + 2;
            for (int i = 0; i < count && p + 12 <= tiff.Length; i++, p += 12)
            {
                ushort tag = ReadU16(tiff, p, littleEndian);
                ushort type = ReadU16(tiff, p + 2, littleEndian);
                uint valCount = ReadU32(tiff, p + 4, littleEndian);
                uint valOrOffset = ReadU32(tiff, p + 8, littleEndian);

                if (tag == 0x0001) latRef = ReadIfdAscii(tiff, type, valCount, p + 8, valOrOffset);
                else if (tag == 0x0003) lonRef = ReadIfdAscii(tiff, type, valCount, p + 8, valOrOffset);
                else if (tag == 0x0002) latVal = ReadGpsCoordinate(tiff, littleEndian, type, valCount, p + 8, valOrOffset, latRef);
                else if (tag == 0x0004) lonVal = ReadGpsCoordinate(tiff, littleEndian, type, valCount, p + 8, valOrOffset, lonRef);
                else if (tag == 0x001B) areaName = ReadExifEncodedText(tiff, valCount, p + 8, valOrOffset, littleEndian);
            }

            // Re-apply hemisphere sign if Ref appeared after Coordinate tag in the IFD.
            if (latVal.HasValue && !string.IsNullOrWhiteSpace(latRef) && char.ToUpperInvariant(latRef.Trim()[0]) == 'S' && latVal.Value > 0)
                latVal = -latVal.Value;
            if (lonVal.HasValue && !string.IsNullOrWhiteSpace(lonRef) && char.ToUpperInvariant(lonRef.Trim()[0]) == 'W' && lonVal.Value > 0)
                lonVal = -lonVal.Value;

            var (nLat, nLon) = NormalizeCoords(latVal, lonVal);
            acc = Merge(acc, new ExtractedLocation(nLat, nLon, CleanPlaceText(areaName), nLat.HasValue ? origin : null));
        }

        return acc;
    }

    private static string? ReadIfdAscii(ReadOnlySpan<byte> tiff, ushort type, uint count, int inlineOffset, uint valueOffset)
    {
        if (type != 2 || count == 0 || count > 512) return null;
        if (count <= 4)
            return Encoding.Latin1.GetString(tiff.Slice(inlineOffset, (int)count)).TrimEnd('\0', ' ');
        if (valueOffset + count <= tiff.Length)
            return Encoding.Latin1.GetString(tiff.Slice((int)valueOffset, (int)count)).TrimEnd('\0', ' ');
        return null;
    }

    private static double? ReadGpsCoordinate(
        ReadOnlySpan<byte> tiff,
        bool littleEndian,
        ushort type,
        uint count,
        int inlineOffset,
        uint valueOffset,
        string? hemisphereRef)
    {
        if (type == 2) // ASCII decimal or DMS string
        {
            var str = ReadIfdAscii(tiff, type, count, inlineOffset, valueOffset);
            return TryParseFlexibleCoordString(str, hemisphereRef);
        }

        if (type is 5 or 10 && count is >= 1 and <= 4) // RATIONAL / SRATIONAL
        {
            int offset = (int)valueOffset;
            if (offset < 0 || offset + count * 8 > tiff.Length) return null;
            var parts = new List<double>((int)count);
            for (int idx = 0; idx < count; idx++)
            {
                var p = offset + idx * 8;
                double num = type == 10 ? ReadI32(tiff, p, littleEndian) : ReadU32(tiff, p, littleEndian);
                double den = type == 10 ? ReadI32(tiff, p + 4, littleEndian) : ReadU32(tiff, p + 4, littleEndian);
                parts.Add(Math.Abs(den) > 1e-9 ? num / den : 0);
            }
            return ConvertDmsToDecimal(parts, hemisphereRef);
        }

        if (type == 11 && count == 1 && inlineOffset + 4 <= tiff.Length) // FLOAT
        {
            var f = littleEndian
                ? BinaryPrimitives.ReadSingleLittleEndian(tiff.Slice(inlineOffset, 4))
                : BinaryPrimitives.ReadSingleBigEndian(tiff.Slice(inlineOffset, 4));
            return ConvertDmsToDecimal([f], hemisphereRef);
        }

        if (type == 12 && count == 1 && valueOffset + 8 <= tiff.Length) // DOUBLE
        {
            var d = littleEndian
                ? BinaryPrimitives.ReadDoubleLittleEndian(tiff.Slice((int)valueOffset, 8))
                : BinaryPrimitives.ReadDoubleBigEndian(tiff.Slice((int)valueOffset, 8));
            return ConvertDmsToDecimal([d], hemisphereRef);
        }

        return null;
    }

    private static string? ReadExifEncodedText(ReadOnlySpan<byte> tiff, uint count, int inlineOffset, uint valueOffset, bool littleEndian)
    {
        if (count == 0 || count > 2048) return null;
        ReadOnlySpan<byte> raw = count <= 4
            ? tiff.Slice(inlineOffset, (int)count)
            : (valueOffset + count <= tiff.Length ? tiff.Slice((int)valueOffset, (int)count) : default);
        if (raw.IsEmpty) return null;

        if (raw.Length >= 8)
        {
            if (raw.StartsWith("UNICODE\0"u8))
                return (littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(raw[8..]).Trim('\0', ' ');
            if (raw.StartsWith("ASCII\0\0\0"u8))
                return Encoding.UTF8.GetString(raw[8..]).Trim('\0', ' ');
        }
        return Encoding.UTF8.GetString(raw).Trim('\0', ' ');
    }

    public static ExtractedLocation ParseXmpBytes(ReadOnlySpan<byte> xmpBytes, string origin)
    {
        if (xmpBytes.IsEmpty) return new();
        var text = Encoding.UTF8.GetString(xmpBytes).Trim('\0', ' ', '\t', '\r', '\n');
        var start = text.IndexOf('<');
        var end = text.LastIndexOf('>');
        if (start < 0 || end <= start) return new();
        var slice = text[start..(end + 1)];

        try
        {
            var doc = XDocument.Parse(slice, LoadOptions.None);
            string? FindXmpValue(string localName)
            {
                foreach (var el in doc.Descendants())
                {
                    if (el.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase) && !el.HasElements && !string.IsNullOrWhiteSpace(el.Value))
                        return el.Value.Trim();
                    var bagVal = el.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase)
                        ? el.Descendants().FirstOrDefault(d => !d.HasElements && !string.IsNullOrWhiteSpace(d.Value))?.Value.Trim()
                        : null;
                    if (!string.IsNullOrWhiteSpace(bagVal)) return bagVal;
                    foreach (var attr in el.Attributes())
                    {
                        if (attr.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(attr.Value))
                            return attr.Value.Trim();
                    }
                }
                return null;
            }

            var latStr = FindXmpValue("GPSLatitude");
            var lonStr = FindXmpValue("GPSLongitude");
            var (nLat, nLon) = NormalizeCoords(
                TryParseFlexibleCoordString(latStr, FindXmpValue("GPSLatitudeRef")),
                TryParseFlexibleCoordString(lonStr, FindXmpValue("GPSLongitudeRef")));

            var placeParts = new[]
            {
                CleanPlaceText(FindXmpValue("Location")),
                CleanPlaceText(FindXmpValue("City")),
                CleanPlaceText(FindXmpValue("State")),
                CleanPlaceText(FindXmpValue("Country")) ?? CleanPlaceText(FindXmpValue("CountryName"))
            }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

            var placeName = placeParts.Length > 0 ? string.Join(", ", placeParts) : null;
            return new ExtractedLocation(nLat, nLon, placeName, nLat.HasValue || placeName is not null ? origin : null);
        }
        catch
        {
            return new();
        }
    }

    public static double? TryParseFlexibleCoordString(string? raw, string? refHint = null)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        char? trailingRef = null;
        var last = char.ToUpperInvariant(s[^1]);
        if (last is 'N' or 'S' or 'E' or 'W')
        {
            trailingRef = last;
            s = s[..^1].Trim();
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var simple))
            return ConvertDmsToDecimal([simple], trailingRef?.ToString() ?? refHint);

        // Handle rational strings like "48/1 51/1 2376/100"
        if (s.Contains('/'))
        {
            var ratTokens = s.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            var ratParts = new List<double>(3);
            foreach (var tok in ratTokens)
            {
                var slash = tok.IndexOf('/');
                if (slash > 0 && slash < tok.Length - 1 &&
                    double.TryParse(tok[..slash], NumberStyles.Float, CultureInfo.InvariantCulture, out var num) &&
                    double.TryParse(tok[(slash + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var den) &&
                    Math.Abs(den) > 1e-9)
                {
                    ratParts.Add(num / den);
                }
                else if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                {
                    ratParts.Add(d);
                }
            }
            if (ratParts.Count is >= 1 and <= 3)
                return ConvertDmsToDecimal(ratParts, trailingRef?.ToString() ?? refHint);
        }

        // Comma-separated XMP DMS ("48,51.396" or "48,51,23.76") or "37 deg 46' 29.64\""
        var nums = NumberRegex().Matches(s)
            .Select(m => double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (double?)d : null)
            .Where(d => d.HasValue)
            .Select(d => d!.Value)
            .ToArray();
        if (nums.Length is >= 1 and <= 3)
            return ConvertDmsToDecimal(nums, trailingRef?.ToString() ?? refHint);

        return null;
    }

    private static string? CleanPlaceText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim();
        return cleaned.Length > 120 ? cleaned[..120].Trim() : cleaned;
    }

    private static int ReadExact(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            var r = stream.Read(buffer[total..]);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }

    [GeneratedRegex(@"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)")]
    private static partial Regex Iso6709Regex();

    [GeneratedRegex(@"[+-]?\d+(?:\.\d+)?")]
    private static partial Regex NumberRegex();
}
