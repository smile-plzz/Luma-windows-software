using System.Security.Cryptography;
using System.Text;

namespace Luma.Core;

/// <summary>One owner per cache directory. Store on the internal disk; dispose after all requests finish.</summary>
public sealed class ThumbnailCache : IDisposable
{
    private readonly string directory;
    private readonly long maxBytes;
    private readonly int maxEntryBytes;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly FileStream ownership;
    private sealed record Entry(string Path, long Length, DateTime Accessed, DateTime DiskTimestamp);
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SortedSet<Entry> lru = new(Comparer<Entry>.Create((a, b) =>
    {
        var time = a.Accessed.CompareTo(b.Accessed);
        return time != 0 ? time : StringComparer.Ordinal.Compare(a.Path, b.Path);
    }));
    private long retainedBytes;
    private long lastLruTicks;

    public ThumbnailCache(string directory, long maxBytes = 2L * 1024 * 1024 * 1024,
        int maxEntryBytes = 16 * 1024 * 1024)
    {
        if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxEntryBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxEntryBytes));
        this.directory = Path.GetFullPath(directory);
        this.maxBytes = maxBytes;
        this.maxEntryBytes = (int)Math.Min(maxBytes, maxEntryBytes);
        Directory.CreateDirectory(this.directory);
        ownership = new FileStream(Path.Combine(this.directory, "cache.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        try
        {
            // Exclusive ownership makes crash recovery safe: no other writer has an active temporary file.
            foreach (var file in Directory.EnumerateFiles(this.directory, "*.tmp")) File.Delete(file);
            foreach (var file in new DirectoryInfo(this.directory).EnumerateFiles("*.thumb"))
                Track(file.FullName, file.Length, file.LastWriteTimeUtc, file.LastWriteTimeUtc);
            Trim();
        }
        catch { ownership.Dispose(); throw; }
    }

    public int MaxEntryBytes => maxEntryBytes;

    public static string Key(CachedMedia media, int pixels = 256)
    {
        if (pixels is < 32 or > 1024) throw new ArgumentOutOfRangeException(nameof(pixels));
        var mediaKey = MediaCache.ThumbnailKey(media);
        Span<byte> buf = stackalloc byte[128];
        Encoding.ASCII.GetBytes("luma-thumbnail-v1:", buf);
        int written = 18;
        System.Buffers.Text.Utf8Formatter.TryFormat(pixels, buf[written..], out var pxBytes);
        written += pxBytes;
        buf[written++] = (byte)':';
        written += Encoding.ASCII.GetBytes(mediaKey, buf[written..]);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(buf[..written], hash);
        return MediaCache.ToLowerHex(hash);
    }

    private DateTime NextLruTime(DateTime utcNow)
    {
        long ticks = Math.Max(utcNow.Ticks, lastLruTicks + 1);
        lastLruTicks = ticks;
        return new DateTime(ticks, DateTimeKind.Utc);
    }

    public async Task<byte[]?> ReadAsync(string key, CancellationToken ct = default)
    {
        var path = EntryPath(key);
        await gate.WaitAsync(ct);
        try
        {
            byte[] bytes;
            bool deleteCorrupt = false;
            try
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                long length = fs.Length;
                if (length <= 0 || length > maxEntryBytes)
                {
                    deleteCorrupt = true;
                    bytes = Array.Empty<byte>();
                }
                else
                {
                    bytes = new byte[length];
                    await fs.ReadExactlyAsync(bytes, ct);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                Forget(path);
                return null;
            }

            if (deleteCorrupt)
            {
                File.Delete(path);
                Forget(path);
                return null;
            }

            var now = DateTime.UtcNow;
            var diskTs = entries.TryGetValue(path, out var previous) ? previous.DiskTimestamp : DateTime.MinValue;
            // Throttle NTFS MFT write-time updates on warm hits while always advancing in-memory LRU immediately.
            if ((now - diskTs) >= TimeSpan.FromMinutes(15))
            {
                File.SetLastWriteTimeUtc(path, now);
                diskTs = now;
            }
            Track(path, bytes.Length, NextLruTime(now), diskTs);
            return bytes;
        }
        finally { gate.Release(); }
    }

    public async Task StoreAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        => await WriteAsync(key, bytes, true, ct);

    // Bulk preparation must not evict thumbnails belonging to a disconnected source.
    public Task<bool> TryStoreWithoutEvictionAsync(string key, ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        => WriteAsync(key, bytes, false, ct);

    private async Task<bool> WriteAsync(string key, ReadOnlyMemory<byte> bytes, bool evict, CancellationToken ct)
    {
        var path = EntryPath(key);
        if (bytes.Length == 0 || bytes.Length > maxEntryBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes), "Thumbnail exceeds the entry or cache budget.");
        await gate.WaitAsync(ct);
        var temporary = Path.Combine(directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            var previousLength = entries.TryGetValue(path, out var previous) ? previous.Length : 0;
            if (!evict && retainedBytes - previousLength + bytes.Length > maxBytes) return false;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            var accessed = DateTime.UtcNow;
            File.SetLastWriteTimeUtc(path, accessed);
            Track(path, bytes.Length, NextLruTime(accessed), accessed);
            Trim();
            return true;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }

    public async Task<ThumbnailCacheSnapshot> SnapshotAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var entry in entries.Values.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                var file = new FileInfo(entry.Path);
                if (!file.Exists) Forget(entry.Path);
                else if (file.Length == 0 || file.Length > maxEntryBytes) { File.Delete(entry.Path); Forget(entry.Path); }
                else if (file.Length != entry.Length) Track(entry.Path, file.Length, NextLruTime(file.LastWriteTimeUtc), file.LastWriteTimeUtc);
            }
            var set = new HashSet<string>(entries.Count, StringComparer.Ordinal);
            foreach (var keyPath in entries.Keys)
            {
                var name = Path.GetFileNameWithoutExtension(keyPath);
                if (name is not null) set.Add(name);
            }
            return new(set, retainedBytes, maxBytes);
        }
        finally { gate.Release(); }
    }

    public async Task<CacheRemoval> InspectRemovalAsync(IReadOnlySet<string> keys, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            long count = 0, bytes = 0;
            foreach (var kv in entries)
            {
                var name = Path.GetFileNameWithoutExtension(kv.Key);
                if (name is not null && keys.Contains(name))
                {
                    count++;
                    bytes += kv.Value.Length;
                }
            }
            return new(count, bytes);
        }
        finally { gate.Release(); }
    }

    public async Task<CacheRemoval> RemoveAsync(IReadOnlySet<string> keys, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var targets = new List<Entry>();
            foreach (var entry in entries.Values)
            {
                var name = Path.GetFileNameWithoutExtension(entry.Path);
                if (name is not null && keys.Contains(name)) targets.Add(entry);
            }
            long count = 0, bytes = 0;
            foreach (var entry in targets)
            {
                ct.ThrowIfCancellationRequested();
                File.Delete(entry.Path); Forget(entry.Path); count++; bytes += entry.Length;
            }
            return new(count, bytes);
        }
        finally { gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var path in entries.Keys.ToArray())
            {
                ct.ThrowIfCancellationRequested();
                File.Delete(path); Forget(path);
            }
        }
        finally { gate.Release(); }
    }

    private string EntryPath(string key)
    {
        if (key.Length != 64)
            throw new ArgumentException("Expected a SHA-256 cache key.", nameof(key));
        bool needsLower = false;
        for (int i = 0; i < key.Length; i++)
        {
            var c = key[i];
            if (!char.IsAsciiHexDigit(c))
                throw new ArgumentException("Expected a SHA-256 cache key.", nameof(key));
            if (char.IsAsciiLetterUpper(c)) needsLower = true;
        }
        return Path.Combine(directory, (needsLower ? key.ToLowerInvariant() : key) + ".thumb");
    }

    private void Forget(string path)
    {
        if (!entries.Remove(path, out var previous)) return;
        lru.Remove(previous);
        retainedBytes -= previous.Length;
    }

    private void Track(string path, long length, DateTime accessed, DateTime diskTimestamp)
    {
        Forget(path);
        if (accessed.Ticks > lastLruTicks) lastLruTicks = accessed.Ticks;
        var entry = new Entry(path, length, accessed, diskTimestamp);
        entries.Add(path, entry);
        lru.Add(entry);
        retainedBytes += length;
    }

    private void Trim()
    {
        while (retainedBytes > maxBytes && lru.Count > 0)
        {
            var oldest = lru.Min!;
            File.Delete(oldest.Path);
            Forget(oldest.Path);
        }
    }

    public void Dispose() { ownership.Dispose(); gate.Dispose(); }
}

public sealed record ThumbnailCacheSnapshot(IReadOnlySet<string> Keys, long Bytes, long LimitBytes);

public sealed record CacheRemoval(long Count,long Bytes);
