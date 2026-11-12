using System.Collections.Concurrent;

namespace Luma.Core;

/// <summary>Implementations must verify identity, never infer it from a previously used drive letter.</summary>
public interface ISourceResolver
{
    string? ResolveRoot(string sourceId);
}

public interface IThumbnailGenerator
{
    Task<byte[]?> GenerateAsync(string path, int pixels, CancellationToken ct);
}

public sealed class ThumbnailService(ThumbnailCache cache, ISourceResolver sources, IThumbnailGenerator generator)
{
    // Bounded multi-worker decoder pool tuned to CPU core count for parallel throughput
    private readonly SemaphoreSlim generationGate = new(Math.Clamp(Environment.ProcessorCount - 1, 2, 6));
    private readonly ConcurrentDictionary<string, bool> missingCodecs = new(StringComparer.OrdinalIgnoreCase);

    public bool IsCodecMissing(string extension) => missingCodecs.ContainsKey(extension);
    public void ResetCodecCache() => missingCodecs.Clear();

    public Task<byte[]?> TryReadCachedAsync(CachedMedia media, int pixels = 256, CancellationToken ct = default) =>
        cache.ReadAsync(ThumbnailCache.Key(media, pixels), ct);

    public async Task<byte[]?> GetAsync(CachedMedia media, int pixels = 256, CancellationToken ct = default, bool allowEviction = true, bool forceRefresh = false)
    {
        var ext = Path.GetExtension(media.RelativePath);
        if (missingCodecs.ContainsKey(ext)) return null;

        var key = ThumbnailCache.Key(media, pixels);
        var cached = forceRefresh ? null : await cache.ReadAsync(key, ct);
        if (cached is not null) return cached; // Offline browsing never probes the removable drive on a hit.

        await generationGate.WaitAsync(ct);
        try
        {
            cached = forceRefresh ? null : await cache.ReadAsync(key, ct);
            if (cached is not null) return cached;

            var root = sources.ResolveRoot(media.SourceId);
            if (root is null) return null;
            var path = SourcePaths.Combine(root, media.RelativePath);

            try
            {
                if (!Matches(path, media)) return null;
                var bytes = await generator.GenerateAsync(path, pixels, ct);
                // An unplug, overwrite, or mount change during generation must not poison the cache.
                if (bytes is null || bytes.Length == 0 || bytes.Length > cache.MaxEntryBytes || sources.ResolveRoot(media.SourceId) != root || !Matches(path, media))
                {
                    if (bytes is null && (ext.Equals(".heic", StringComparison.OrdinalIgnoreCase) ||
                                          ext.Equals(".heif", StringComparison.OrdinalIgnoreCase) ||
                                          ext.Equals(".cr2", StringComparison.OrdinalIgnoreCase) ||
                                          ext.Equals(".cr3", StringComparison.OrdinalIgnoreCase) ||
                                          ext.Equals(".nef", StringComparison.OrdinalIgnoreCase) ||
                                          ext.Equals(".arw", StringComparison.OrdinalIgnoreCase)))
                    {
                        missingCodecs.TryAdd(ext, true);
                    }
                    return null;
                }

                if (allowEviction) await cache.StoreAsync(key, bytes, ct);
                else if (!await cache.TryStoreWithoutEvictionAsync(key, bytes, ct)) return null;
                return bytes;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }
        finally { generationGate.Release(); }
    }

    private static bool Matches(string path, CachedMedia media)
    {
        var file = new FileInfo(path);
        return file.Exists && file.Length == media.Length && file.LastWriteTimeUtc.Ticks == media.ModifiedTicks;
    }
}

public static class SourcePaths
{
    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    private static readonly char[] InvalidWindowsFileNameChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static bool IsInvalidWindowsFileNameChar(char ch) =>
        ch < ' ' || Array.IndexOf(InvalidWindowsFileNameChars, ch) >= 0;

    public static string SanitizeWindowsFileNameStem(string stem)
    {
        if (string.IsNullOrEmpty(stem)) return "";
        return string.Create(stem.Length, stem, static (span, value) =>
        {
            for (int i = 0; i < value.Length; i++)
                span[i] = IsInvalidWindowsFileNameChar(value[i]) ? '_' : value[i];
        });
    }

    public static bool ValidateWindowsFileName(string? name, out string error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Enter a valid Windows filename.";
            return false;
        }
        if (name.Any(IsInvalidWindowsFileNameChar) || name.EndsWith('.') || name.EndsWith(' '))
        {
            error = "Enter a valid Windows filename without invalid characters or trailing dots or spaces.";
            return false;
        }
        var stem = Path.GetFileNameWithoutExtension(name);
        if (WindowsReservedNames.Contains(name) || WindowsReservedNames.Contains(stem))
        {
            error = $"\"{stem.ToUpperInvariant()}\" is a reserved Windows device name. Choose a different filename.";
            return false;
        }
        error = "";
        return true;
    }

    public static string Combine(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new ArgumentException("A relative media path is required.", nameof(relativePath));
        var prefix = Path.GetFullPath(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(prefix, relativePath));
        if (!result.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Path escapes the registered source.", nameof(relativePath));
        return result;
    }
}
