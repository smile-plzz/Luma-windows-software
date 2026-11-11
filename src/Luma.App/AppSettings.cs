using System.Text.Json;

namespace Luma.App;

public sealed class AppSettings
{
    public bool RememberPlace { get; set; } = true;
    public BrowsePlace? Place { get; set; }
    public int SortIndex { get; set; } = 4;
    public bool Descending { get; set; } = true;
    public int GroupIndex { get; set; } = 2;
    public int ThumbnailSize { get; set; } = 190;
    public bool ShowNames { get; set; }
    public bool ShowDetails { get; set; }
    public bool ShowExactTime { get; set; }
    public bool FitImages { get; set; }
    public bool Compact { get; set; }
    public bool CommonOnly { get; set; }
    public int FilterDefaultsVersion { get; set; } = 1;
    public bool IncludeDescendants { get; set; } = true;
    public int CacheGiB { get; set; } = 2;
    public string Theme { get; set; } = "Default";
    public bool RestoredFromCorruptedFile { get; set; }

    public bool SidebarCollapsed { get; set; }
    public bool HeaderCollapsed { get; set; }
    public bool ViewsExpanded { get; set; } = true;
    public bool AlbumsExpanded { get; set; } = true;
    public bool SmartCollectionsExpanded { get; set; }
    public bool SourcesExpanded { get; set; } = true;

    public string LayoutMode { get; set; } = "Uniform";
    public bool StackSimilar { get; set; }
    public bool FavoritesFirst { get; set; }
    public List<string> RecentDestinations { get; set; } = new();
    public List<string> RecentSearches { get; set; } = new();

    public static bool IsSmokeTest => Environment.GetCommandLineArgs().Contains("--smoke-test");

    private static string SmokeSession
    {
        get
        {
            var value = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--smoke-session="))?["--smoke-session=".Length..];
            return Guid.TryParse(value, out var id) ? id.ToString("N") : Environment.ProcessId.ToString();
        }
    }

    public static string LocalRoot => IsSmokeTest ? Path.Combine(Path.GetTempPath(), "Luma-smoke-" + SmokeSession)
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Luma");

    public static AppSettings Load()
    {
        var settingsPath = Path.Combine(LocalRoot, "settings.json");
        if (!File.Exists(settingsPath)) return new();

        try
        {
            var json = File.ReadAllText(settingsPath);
            return FromJson(json);
        }
        catch (JsonException ex)
        {
            // Preserve corrupted file for user diagnostics rather than overwriting silently
            try
            {
                var corruptPath = Path.Combine(LocalRoot, "settings.json.corrupt");
                File.Copy(settingsPath, corruptPath, true);
                File.AppendAllText(Path.Combine(LocalRoot, "last-error.txt"), $"[{DateTime.UtcNow:O}] Corrupt settings preserved to settings.json.corrupt: {ex.Message}{Environment.NewLine}");
            }
            catch { }

            var fallback = new AppSettings { RestoredFromCorruptedFile = true };
            return fallback;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    public static AppSettings FromJson(string json)
    {
        var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new();
        using var document = JsonDocument.Parse(json);
        // Older versions enabled this filter by default. Reveal supported files once
        // on upgrade; subsequent explicit choices remain persistent.
        if (document.RootElement.ValueKind == JsonValueKind.Object && !document.RootElement.TryGetProperty(nameof(FilterDefaultsVersion), out _))
            settings.CommonOnly = false;
        settings.FilterDefaultsVersion = 1;
        settings.RecentDestinations ??= new();
        settings.RecentSearches ??= new();
        if (settings.LayoutMode is not ("Uniform" or "Adaptive" or "Moments"))
            settings.LayoutMode = "Uniform";
        return settings;
    }

    public void Save()
    {
        Directory.CreateDirectory(LocalRoot);
        var path = Path.Combine(LocalRoot, "settings.json");
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tempPath, path, true);
    }
}

public sealed record BrowsePlace(string? SourceId = null, string Folder = "", string Search = "", string? AlbumId = null,
    int Navigation = 0, string? Month = null, string? MemoryDay = null, string? AnchorSource = null, string? AnchorPath = null, double Inset = 0);
