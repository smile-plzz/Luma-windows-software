using System.Text.Json;
using Luma.Core;
using Luma.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Luma.App;

public sealed partial class MainWindow
{
    private async Task LoadAlbums()
    {
        var id = SelectedAlbum; var albums = await catalog.AlbumsAsync(); var r = ready; ready = false;
        AlbumsBox.ItemsSource = albums; AlbumsBox.SelectedItem = albums.FirstOrDefault(a => a.Id == id); ready = r;
        UpdateSidebarCounts(albumsCount: albums.Count);
    }

    private async Task LoadSmartCollections()
    {
        await catalog.SeedDefaultSmartCollectionsAsync();
        var activeId = (SmartCollectionsBox.SelectedItem as SmartCollection)?.Id;
        var list = await catalog.SmartCollectionsAsync();
        var r = ready; ready = false;
        SmartCollectionsBox.ItemsSource = list;
        SmartCollectionsBox.SelectedItem = list.FirstOrDefault(c => c.Id == activeId);
        ready = r;
        UpdateSidebarCounts(collectionsCount: list.Count);
    }

    private async void SmartCollectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || restoringPlace) return;
        if (SmartCollectionsBox.SelectedItem is not SmartCollection sc) return;
        PushNavigationHistory();
        ready = false;
        try
        {
            SearchBox.Text = sc.Search ?? "";
            Navigation.SelectedIndex = sc.Kind switch
            {
                "photo" => 1,
                "video" => 2,
                _ => sc.Favorite == true ? 3 : 0
            };
            filterOrientation = sc.Orientation;
            OrientationFilterBox.SelectedIndex = sc.Orientation switch { "landscape" => 1, "portrait" => 2, "square" => 3, _ => 0 };
            filterUntaggedOnly = sc.UntaggedOnly;
            UntaggedFilterToggle.IsChecked = sc.UntaggedOnly;
            filterMinLength = sc.MinLength;
            MinSizeFilterBox.SelectedIndex = sc.MinLength switch
            {
                >= 1024L * 1024 * 1024 => 4,
                >= 100L * 1024 * 1024 => 3,
                >= 25L * 1024 * 1024 => 2,
                >= 5L * 1024 * 1024 => 1,
                _ => 0
            };
            filterHasGpsOnly = sc.HasGpsOnly;
            HasGpsFilterToggle.IsChecked = sc.HasGpsOnly;
            Heading.Text = sc.Name;
            ClearDateRange();
        }
        finally { ready = true; }
        await Guard(Refresh);
    }

    private async void SaveCurrentAsSmartCollection(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var name = await Prompt("Save Smart Collection", "Saves your current search, type, orientation, size, and GPS filters.", "");
        if (string.IsNullOrWhiteSpace(name)) return;
        var kind = Navigation.SelectedItem?.ToString() switch { "Photos" => "photo", "Videos" => "video", _ => null };
        bool? fav = Navigation.SelectedItem?.ToString() == "Favorites" ? true : null;
        var id = await catalog.SaveSmartCollectionAsync(
            name,
            search: string.IsNullOrWhiteSpace(SearchBox.Text) ? null : SearchBox.Text.Trim(),
            kind: kind,
            favorite: fav,
            orientation: filterOrientation,
            untaggedOnly: filterUntaggedOnly,
            minLength: filterMinLength,
            hasGpsOnly: filterHasGpsOnly);
        await LoadSmartCollections();
        SmartCollectionsBox.SelectedItem = ((IReadOnlyList<SmartCollection>)SmartCollectionsBox.ItemsSource).FirstOrDefault(c => c.Id == id);
        if (!settings.SmartCollectionsExpanded)
        {
            settings.SmartCollectionsExpanded = true;
            ApplyCollapsibleState();
            settings.Save();
        }
        Notify($"Saved Smart Collection '{name}'.");
    }, true);

    private async void DeleteSmartCollection(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (SmartCollectionsBox.SelectedItem is not SmartCollection sc) return;
        if (!await Confirm("Delete Smart Collection?", $"Remove saved collection '{sc.Name}'? Originals and catalog media remain untouched.", "Delete")) return;
        await catalog.DeleteSmartCollectionAsync(sc.Id);
        await LoadSmartCollections();
        await Refresh();
    }, true);

    private async void ClearSmartCollection(object sender, RoutedEventArgs e)
    {
        SmartCollectionsBox.SelectedItem = null;
        await Guard(Refresh);
    }

    private async void AlbumChanged(object sender, SelectionChangedEventArgs e)
    { if (!ready || restoringPlace) return; PushNavigationHistory(); Heading.Text = IsMemories ? "On this day" : (AlbumsBox.SelectedItem as Album)?.Name ?? "Library"; ClearDateRange(); await Guard(Refresh); }

    private async void LeaveAlbum(object sender, RoutedEventArgs e) { AlbumsBox.SelectedItem = null; await Guard(Refresh); }

    private async void NewAlbum(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var name = await Prompt("New album", "Albums organize media without moving original files.", ""); if (name is null) return;
        var id = await catalog.SaveAlbumAsync(name); await LoadAlbums(); AlbumsBox.SelectedItem = ((IReadOnlyList<Album>)AlbumsBox.ItemsSource).First(a => a.Id == id);
    }, true);

    private async void RenameAlbum(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (AlbumsBox.SelectedItem is not Album album) return;
        var name = await Prompt("Rename album", "Album name", album.Name); if (name is null) return;
        await catalog.SaveAlbumAsync(name, album.Id); await LoadAlbums(); Heading.Text = name;
    }, true);

    private async void DeleteAlbum(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (AlbumsBox.SelectedItem is not Album album) return;
        if (!await Confirm("Delete album?", "Only the album is removed. Original files, tags and favorites stay intact.", "Delete album")) return;
        await catalog.DeleteAlbumAsync(album.Id); await LoadAlbums(); await Refresh();
    }, true);

    private async void AddToAlbum(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var items = GetSelectedCards().Select(c => c.Item.Media).ToArray(); if (items.Length == 0) return;
        var albums = await catalog.AlbumsAsync();
        if (albums.Count == 0) { Notify("Create an album in the sidebar first."); return; }
        var picker = new ComboBox { ItemsSource = albums, SelectedIndex = 0, MinWidth = 280 };
        var dialog = new ContentDialog { RequestedTheme = Root.ActualTheme, XamlRoot = Root.XamlRoot, Title = $"Add {items.Length} items to album", Content = picker, PrimaryButtonText = "Add", CloseButtonText = "Cancel" };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        await catalog.SetAlbumItemsAsync(((Album)picker.SelectedItem).Id, items, true); await LoadAlbums(); Notify("Added to album. Originals stayed in place.");
    }, true);

    private async void RemoveFromAlbum(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (SelectedAlbum is not string id) return;
        await catalog.SetAlbumItemsAsync(id, GetSelectedCards().Select(c => c.Item.Media).ToArray(), false);
        await LoadAlbums(); await Refresh();
    }, true);

    private async Task RefreshMetadataFor(string? source)
    {
        scanning = true; workClock.Restart(); workTimer.Start(); scanCancellation = new(); Busy.IsActive = true; CancelButton.Visibility = Visibility.Visible;
        try
        {
            ImportFeedback("metadata", "Reading dates · Preparing", "Counting changed files. Saved media remains available.");
            var details = MetadataFeedback(source ?? "All sources", "Metadata");
            var count = await Task.Run(() => catalog.EnrichAsync(new WindowsMetadataReader(), source, scanCancellation.Token, details: details));
            if (count > 0) InvalidateMapCache(resetBackfillCheck: true);
            Status.Text = $"Date reading complete · {count:N0} updates saved";
            Notify($"Metadata refreshed for {count:N0} changed files. Unsupported dates use modification time.");
        }
        finally { scanning = false; EndImportFeedback(); workClock.Stop(); workTimer.Stop(); WorkElapsed.Text = ""; Busy.IsActive = false; CancelButton.Visibility = Visibility.Collapsed; if (ready) await Refresh(); }
    }

    private void StartDrag(object sender, DragItemsStartingEventArgs e)
    {
        var media = e.Items.Cast<MediaCard>().Select(c => c.Item.Media).ToArray();
        e.Data.SetData("Luma.FileSelection.v1", JsonSerializer.Serialize(media));
        e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        e.Data.SetDataProvider(StandardDataFormats.StorageItems, async request =>
        {
            var deferral = request.GetDeferral();
            try
            {
                var items = new List<IStorageItem>();
                foreach (var item in media) items.Add(await StorageFile.GetFileFromPathAsync(Resolve(item)));
                request.SetData(items);
            }
            catch (Exception ex) { DispatcherQueue.TryEnqueue(() => Notify(ex.Message, true)); }
            finally { deferral.Complete(); }
        });
    }

    private void AlbumDragOver(object sender, DragEventArgs e)
    {
        if (SelectedAlbum is not null && e.DataView.Contains("Luma.FileSelection.v1"))
        { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "Add to selected album · originals stay in place"; e.Handled = true; }
    }

    private async void AlbumDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            await Guard(async () =>
            {
                if (SelectedAlbum is not string id || !e.DataView.Contains("Luma.FileSelection.v1")) return;
                var media = JsonSerializer.Deserialize<CachedMedia[]>((string)await e.DataView.GetDataAsync("Luma.FileSelection.v1")) ?? [];
                await catalog.SetAlbumItemsAsync(id, media, true); await LoadAlbums(); await Refresh();
            }, true);
        }
        finally { deferral.Complete(); }
    }

    private void FolderDragOver(object sender, DragEventArgs e)
    {
        if (SelectedSource is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var move = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Shift).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        e.AcceptedOperation = move ? DataPackageOperation.Move : DataPackageOperation.Copy;
        e.DragUIOverride.Caption = move ? "Move to folder · confirmation required" : "Copy to folder · hold Shift to move"; e.Handled = true;
    }

    private async void FolderDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            await Guard(async () =>
            {
                if (SelectedSource is not string id) return;
                var root = resolver.ResolveRoot(id) ?? throw new IOException("Destination drive is offline.");
                var element = e.OriginalSource as FrameworkElement;
                while (element is not null && element.DataContext is not CachedFolder) element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element) as FrameworkElement;
                var folder = (element?.DataContext as CachedFolder)?.RelativePath ?? FolderFilter.Text.Trim();
                var destination = folder.Length == 0 ? root : SourcePaths.Combine(root, folder);
                await TransferData(e.DataView, destination, e.AcceptedOperation == DataPackageOperation.Move);
            }, true);
        }
        finally { deferral.Complete(); }
    }

    private void RecordRecentDestination(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        settings.RecentDestinations.RemoveAll(d => string.Equals(d, folder, StringComparison.OrdinalIgnoreCase));
        settings.RecentDestinations.Insert(0, folder);
        if (settings.RecentDestinations.Count > 6) settings.RecentDestinations.RemoveRange(6, settings.RecentDestinations.Count - 6);
        try { settings.Save(); } catch { }
    }

    private void QuickMoveMenu_Opening(object? sender, object e)
    {
        QuickMoveMenu.Items.Clear();
        var validRecents = settings.RecentDestinations.Where(Directory.Exists).ToArray();
        if (validRecents.Length > 0)
        {
            foreach (var dest in validRecents)
            {
                var label = Path.GetFileName(dest.TrimEnd(Path.DirectorySeparatorChar, '/'));
                if (string.IsNullOrEmpty(label)) label = dest;
                var item = new MenuFlyoutItem { Text = $"Move to '{label}'", Tag = dest };
                ToolTipService.SetToolTip(item, dest);
                item.Click += async (s, _) =>
                {
                    if (s is FrameworkElement { Tag: string targetFolder })
                    {
                        await Guard(async () =>
                        {
                            var media = GetSelectedCards().Select(c => c.Item.Media).ToArray();
                            var paths = await Task.Run(() => media.Select(Resolve).ToArray());
                            await TransferPaths(paths, media, targetFolder, true);
                        }, true);
                    }
                };
                QuickMoveMenu.Items.Add(item);
            }
            QuickMoveMenu.Items.Add(new MenuFlyoutSeparator());
        }
        var browseItem = new MenuFlyoutItem { Text = "Choose folder…" };
        browseItem.Click += MoveToFolder;
        QuickMoveMenu.Items.Add(browseItem);
    }

    private async Task<string?> PickQuickDestinationOrBrowseAsync(string title)
    {
        var validRecents = settings.RecentDestinations.Where(Directory.Exists).ToArray();
        if (validRecents.Length == 0) return await PickFolder();

        var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 220 };
        foreach (var d in validRecents) list.Items.Add(d);
        list.SelectedIndex = 0;

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(Description("Choose a recent destination or browse for another folder:"));
        panel.Children.Add(list);
        bool browseOther = false;
        var dialog = new ContentDialog { RequestedTheme = Root.ActualTheme, XamlRoot = Root.XamlRoot, Title = title, Content = panel, PrimaryButtonText = "Use selected", SecondaryButtonText = "Browse…", CloseButtonText = "Cancel" };
        dialog.SecondaryButtonClick += (_, _) => { browseOther = true; };
        var res = await ShowDialogAsync(dialog);
        if (browseOther) return await PickFolder();
        if (res != ContentDialogResult.Primary) return null;
        return list.SelectedItem as string;
    }

    private async void CopyToFolder(object sender, RoutedEventArgs e) => await Guard(() => TransferSelected(false), true);
    private async void MoveToFolder(object sender, RoutedEventArgs e) => await Guard(() => TransferSelected(true), true);
    private async Task TransferSelected(bool move)
    {
        var destination = await PickFolder(); if (destination is null) return;
        var media = GetSelectedCards().Select(c => c.Item.Media).ToArray();
        var paths = await Task.Run(() => media.Select(Resolve).ToArray());
        await TransferPaths(paths, media, destination, move);
    }

    private async Task TransferData(DataPackageView data, string destination, bool move, bool clearClipboard = false)
    {
        var items = await data.GetStorageItemsAsync();
        if (items.Any(i => i is not StorageFile)) throw new NotSupportedException("Transfer files only. Add folders as sources.");
        CachedMedia[] media = [];
        if (data.Contains("Luma.FileSelection.v1")) media = JsonSerializer.Deserialize<CachedMedia[]>((string)await data.GetDataAsync("Luma.FileSelection.v1")) ?? [];
        var paths = media.Length > 0 ? media.Select(Resolve).ToArray() : items.Select(i => i.Path).ToArray();
        if (await TransferPaths(paths, media, destination, move))
        { data.ReportOperationCompleted(move ? DataPackageOperation.Move : DataPackageOperation.Copy); if (move && clearClipboard) { Clipboard.Clear(); pendingCutIds.Clear(); foreach (var card in cards) card.IsCut = false; } }
    }

    private async Task<bool> TransferPaths(string[] paths, CachedMedia[] media, string destination, bool move)
    {
        if (paths.Length == 0) return false;
        var conflictPicker = new ComboBox
        {
            Header = "If a filename already exists at destination",
            ItemsSource = new[] { "Skip existing files (recommended)", "Auto-rename incoming (e.g. photo (2).jpg)" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var confirmPanel = new StackPanel { Spacing = 12, MinWidth = 380 };
        confirmPanel.Children.Add(Description($"{paths.Length:N0} file(s) -> {destination}"));
        confirmPanel.Children.Add(conflictPicker);
        var confirmDialog = new ContentDialog
        {
            RequestedTheme = Root.ActualTheme,
            XamlRoot = Root.XamlRoot,
            Title = move ? "Move selected originals?" : "Copy selected originals?",
            Content = confirmPanel,
            PrimaryButtonText = move ? "Move files" : "Copy files",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close
        };
        if (await ShowDialogAsync(confirmDialog) != ContentDialogResult.Primary) return false;

        var conflictMode = conflictPicker.SelectedIndex == 1 ? TransferConflictMode.Rename : TransferConflictMode.Skip;
        RecordRecentDestination(destination);

        var destinationIdentity = resolver.Register(destination);
        var roots = await catalog.SourcesAsync();
        string targetId = destinationIdentity.Id, targetRoot = destination;
        foreach (var source in roots.OrderByDescending(s => s.RootPath.Length))
        {
            var current = resolver.ResolveRoot(source.Id); if (current is null) continue;
            var prefix = Path.TrimEndingDirectorySeparator(current) + Path.DirectorySeparatorChar;
            if (destination.Equals(current, StringComparison.OrdinalIgnoreCase) || destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { targetId = source.Id; targetRoot = current; break; }
        }
        if (!roots.Any(s => s.Id == targetId)) await Task.Run(() => catalog.ScanAsync(targetId, targetRoot));
        scanCancellation = new(); CancelButton.Visibility = Visibility.Visible; Busy.IsActive = true;
        var reports = new List<TransferResult>(); bool catalogFailure = false;
        try
        {
            for (int index = 0; index < paths.Length; index++)
            {
                if (scanCancellation.IsCancellationRequested) break;
                var path = paths[index]; var descriptor = media.Length == paths.Length ? media[index] : null;
                var original = new FileInfo(path);
                if (!original.Exists)
                {
                    reports.Add(new TransferResult(path, Path.Combine(destination, Path.GetFileName(path)), false, false, "Original file is no longer present on disk."));
                    continue;
                }
                var length = original.Length; var ticks = original.LastWriteTimeUtc.Ticks;
                var target = Path.Combine(destination, Path.GetFileName(path));
                bool Valid() => resolver.ResolveRoot(destinationIdentity.Id) is not null && (descriptor is null ? new FileInfo(path) is var f && f.Exists && f.Length == length && f.LastWriteTimeUtc.Ticks == ticks : resolver.ResolveRoot(descriptor.SourceId) is string r && SourcePaths.Combine(r, descriptor.RelativePath).Equals(path, StringComparison.OrdinalIgnoreCase) && MediaCache.VersionMatches(path, descriptor));
                Status.Text = $"{(move ? "Moving" : "Copying")} {index + 1:N0} / {paths.Length:N0}";
                var result = await Task.Run(() => FileTransfer.RunAsync(path, target, move, Valid, scanCancellation.Token, conflictMode)); reports.Add(result);
                if (result.Moved && descriptor is not null)
                {
                    try
                    {
                        var info = new FileInfo(result.Destination);
                        await catalog.RelocateAsync(descriptor, descriptor with { SourceId = targetId, RelativePath = Path.GetRelativePath(targetRoot, result.Destination), Length = info.Length, ModifiedTicks = info.LastWriteTimeUtc.Ticks });
                    }
                    catch (Exception ex) { catalogFailure = true; reports[^1] = result with { Error = "File moved; catalog reconciliation failed: " + ex.Message }; }
                }
            }
            var log = Path.Combine(AppSettings.LocalRoot, "last-transfer.json");
            await File.WriteAllTextAsync(log, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
            if (!catalogFailure)
            {
                await Task.Run(() => catalog.ScanAsync(targetId, targetRoot));
                InvalidateMapCache(resetBackfillCheck: true);
                await LoadSources(); await LoadAlbums(); await Refresh();
            }
            var errors = reports.Where(r => r.Error is not null).ToArray();
            Notify($"{reports.Count(r => move ? r.Moved : r.Copied):N0} / {paths.Length:N0} completed. " + (errors.Length > 0 ? errors[0].Error + " Details: " + log : "Original organization is retained for in-app moves."), errors.Length > 0);
            return reports.Count == paths.Length && errors.Length == 0;
        }
        finally
        {
            try { await File.WriteAllTextAsync(Path.Combine(AppSettings.LocalRoot, "last-transfer.json"), JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true })); }
            catch (IOException) { Notify("The transfer report could not be saved. Check available disk space.", true); }
            Busy.IsActive = false; CancelButton.Visibility = Visibility.Collapsed;
        }
    }

    private async void ReviewLastTransfer(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var log = Path.Combine(AppSettings.LocalRoot, "last-transfer.json");
        if (!File.Exists(log)) { Notify("No recent transfer report found."); return; }
        var reports = JsonSerializer.Deserialize<TransferResult[]>(await File.ReadAllTextAsync(log)) ?? [];
        if (reports.Length == 0) { Notify("Last transfer report is empty."); return; }

        var movedItems = reports.Where(r => r.Moved && r.Error is null && File.Exists(r.Destination) && !File.Exists(r.Source)).ToArray();
        var copiedCount = reports.Count(r => r.Copied && !r.Moved && r.Error is null);
        var errorCount = reports.Count(r => r.Error is not null);

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(Description($"Moved: {reports.Count(r => r.Moved):N0} · Copied: {copiedCount:N0} · Skipped/Errors: {errorCount:N0}\nEligible to undo (move back): {movedItems.Length:N0}"));
        var previewList = new ListView { MaxHeight = 200, SelectionMode = ListViewSelectionMode.None };
        foreach (var r in reports.Take(40))
            previewList.Items.Add($"{(r.Moved ? "[Moved]" : r.Copied ? "[Copied]" : "[Skipped]")} {Path.GetFileName(r.Source)} -> {Path.GetDirectoryName(r.Destination)} {(r.Error is null ? "" : $"({r.Error})")}");
        panel.Children.Add(previewList);

        var dialog = new ContentDialog
        {
            RequestedTheme = Root.ActualTheme,
            XamlRoot = Root.XamlRoot,
            Title = "Last transfer summary",
            Content = panel,
            PrimaryButtonText = movedItems.Length > 0 ? $"Undo {movedItems.Length} move(s)" : "",
            IsPrimaryButtonEnabled = movedItems.Length > 0,
            CloseButtonText = "Close"
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary || movedItems.Length == 0) return;

        int undone = 0, skippedOffline = 0, relocationErrors = 0;
        var roots = await catalog.SourcesAsync();
        (string SourceId, string RelativePath)? MatchRegisteredRoot(string fullPath)
        {
            foreach (var src in roots)
            {
                var rPath = resolver.ResolveRoot(src.Id);
                if (rPath is null) continue;
                var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rPath)) + Path.DirectorySeparatorChar;
                var normalizedFile = Path.GetFullPath(fullPath);
                if (normalizedFile.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
                    return (src.Id, Path.GetRelativePath(normalizedRoot, normalizedFile));
            }
            return null;
        }

        var remaining = reports.ToList();
        foreach (var item in movedItems)
        {
            var srcRoot = Path.GetPathRoot(item.Source);
            if (string.IsNullOrWhiteSpace(srcRoot) || !Directory.Exists(srcRoot))
            {
                skippedOffline++;
                continue;
            }
            var destMatch = MatchRegisteredRoot(item.Destination);
            var origMatch = MatchRegisteredRoot(item.Source);
            var catalogEntry = destMatch is { } dm ? await catalog.GetItemAsync(dm.SourceId, dm.RelativePath) : null;

            var destDir = Path.GetDirectoryName(item.Source)!;
            Directory.CreateDirectory(destDir);
            var rev = await Task.Run(() => FileTransfer.RunAsync(item.Destination, item.Source, true, () => File.Exists(item.Destination), CancellationToken.None));
            if (rev.Moved)
            {
                undone++;
                remaining.Remove(item);
                if (catalogEntry is not null && origMatch is { } om)
                {
                    try
                    {
                        var info = new FileInfo(item.Source);
                        await catalog.RelocateAsync(catalogEntry.Media, catalogEntry.Media with
                        {
                            SourceId = om.SourceId,
                            RelativePath = om.RelativePath,
                            Length = info.Length,
                            ModifiedTicks = info.LastWriteTimeUtc.Ticks,
                            IsAvailable = true
                        });
                    }
                    catch { relocationErrors++; }
                }
                else
                {
                    foreach (var src in roots)
                    {
                        var rPath = resolver.ResolveRoot(src.Id);
                        if (rPath is not null && (item.Source.StartsWith(rPath, StringComparison.OrdinalIgnoreCase) || item.Destination.StartsWith(rPath, StringComparison.OrdinalIgnoreCase)))
                            await Task.Run(() => catalog.ScanAsync(src.Id, rPath));
                    }
                }
            }
        }
        try { await File.WriteAllTextAsync(log, JsonSerializer.Serialize(remaining, new JsonSerializerOptions { WriteIndented = true })); } catch { }
        InvalidateMapCache(resetBackfillCheck: false);
        await LoadSources(); await LoadAlbums(); await Refresh();
        var undoNote = $"Reversed {undone:N0} of {movedItems.Length:N0} moved file(s) with saved tags and albums preserved.";
        if (skippedOffline > 0) undoNote += $" {skippedOffline:N0} skipped because destination drive is disconnected.";
        if (relocationErrors > 0) undoNote += $" {relocationErrors:N0} catalog link(s) could not be updated; refresh those sources.";
        Notify(undoNote, skippedOffline > 0 || relocationErrors > 0);
    }, true);
}
