using Luma.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Luma.App;

public sealed partial class MainWindow
{
    private CachedFolder? contextFolder;

    // Register every cache request, including canceled page loads, before a window can close.
    private async Task<T> UseCache<T>(Func<Task<T>> action)
    {
        var task = Task.Run(action);
        lock (cacheTasks) cacheTasks.Add(task);
        try { return await task; }
        finally { lock (cacheTasks) cacheTasks.Remove(task); }
    }

    private OfflinePreparation Preparation => new(catalog, cache!, thumbnails!);

    private async Task LoadFolders(CancellationToken ct)
    {
        var source = SelectedSource; var parent = FolderFilter.Text.Trim();
        IReadOnlyList<CachedFolder> folders = source is null ? Array.Empty<CachedFolder>() : await Task.Run(() => catalog.FoldersAsync(source, parent, ct), ct);
        ct.ThrowIfCancellationRequested();
        FolderNavigation.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        FolderList.ItemsSource = folders;
        FolderHint.Text = source is null ? "Select a source to browse its folders." :
            folders.Count == 0 ? "No indexed subfolders here." : "Subfolders · includes nested media";
        UpdateFolderBreadcrumbs(source, parent);
    }

    private void UpdateFolderBreadcrumbs(string? source, string folderPath)
    {
        if (source is null)
        {
            FolderBreadcrumbs.ItemsSource = Array.Empty<FolderBreadcrumb>();
            return;
        }

        var crumbs = new List<FolderBreadcrumb> { new("Root", "") };
        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            var parts = folderPath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var cumulative = "";
            foreach (var part in parts)
            {
                cumulative = cumulative.Length == 0 ? part : Path.Combine(cumulative, part);
                crumbs.Add(new("> " + part, cumulative));
            }
        }
        FolderBreadcrumbs.ItemsSource = crumbs;
    }

    private void BreadcrumbClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string relPath })
        {
            PushNavigationHistory();
            FolderFilter.Text = relPath;
        }
    }

    private void FolderSelected(object sender, SelectionChangedEventArgs e)
    {
        if (FolderList.SelectedItem is CachedFolder folder)
        {
            PushNavigationHistory();
            FolderFilter.Text = folder.RelativePath;
        }
    }

    private void ParentFolder(object sender, RoutedEventArgs e)
    {
        PushNavigationHistory();
        FolderFilter.Text = Path.GetDirectoryName(FolderFilter.Text.Trim().TrimEnd(Path.DirectorySeparatorChar)) ?? "";
    }

    private void FolderRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not CachedFolder)
            element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        contextFolder = element?.DataContext as CachedFolder;
    }

    private void RevealSelectedFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedSource is not string src || contextFolder is null) return;
        var root = resolver.ResolveRoot(src);
        if (root is null) { Notify("This drive is offline. Reconnect it to open in Explorer.", true); return; }
        var full = SourcePaths.Combine(root, contextFolder.RelativePath);
        if (Directory.Exists(full)) ShellFiles.OpenInDefaultApp(full);
        else Notify("Folder not found on disk. Refresh source to reconcile.", true);
    }

    private async void ExcludeSelectedFolder(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (SelectedSource is not string src || contextFolder is null) return;
        if (!await Confirm("Exclude folder?", $"Hide '{contextFolder.RelativePath}' and its subfolders from Luma?", "Exclude")) return;
        await Task.Run(() => catalog.SetExclusionAsync(new LibraryExclusion("folder", src, contextFolder.RelativePath), true));
        await Refresh();
        Notify($"Excluded folder '{contextFolder.Name}'. Restore anytime in Manage exclusions.");
    }, true);
}
