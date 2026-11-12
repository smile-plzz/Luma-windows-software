using System.Diagnostics;
using System.Text.Json;
using Luma.Core;
using Luma.Windows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace Luma.App;

public sealed partial class MainWindow : Window
{
    private readonly WindowsSourceResolver resolver = new();
    private readonly MediaCache catalog;
    private readonly AppSettings settings = AppSettings.Load();
    private ThumbnailCache? cache;
    private ThumbnailService? thumbnails;
    private CancellationTokenSource? queryCancellation, scanCancellation, searchCancellation;
    private readonly HashSet<Task> cacheTasks = new();
    private bool isClosing;

    private Task[] SnapshotCacheTasks()
    {
        lock (cacheTasks) return cacheTasks.ToArray();
    }
    private readonly HashSet<string> pendingCutIds = new();
    private CancellationTokenSource? preparationCancellation;
    private bool preparing, clearing;
    private bool ready, scanning, operation, clipboardBusy;
    private string sourceSignature = "";
    private bool checkingSources;

    private long total;
    private readonly Stopwatch workClock = new();
    private readonly DispatcherTimer workTimer = new() { Interval=TimeSpan.FromSeconds(1) };
    private readonly System.Collections.ObjectModel.ObservableCollection<MediaCard> cards = new();
    private readonly DispatcherTimer availabilityTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private nint Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
    private string? SelectedSource => (Sources.SelectedItem as SourceChoice)?.Id;
    private MediaCard? Selected => MediaGrid.SelectedItem as MediaCard;

    public MainWindow()
    {
        catalog = new(Path.Combine(AppSettings.LocalRoot, "catalog.db"), resolver);
        InitializeComponent();
        try
        {
            if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
                SystemBackdrop = new MicaBackdrop();
        }
        catch { }
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory,"Assets","Luma.ico"));
        var work = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id,
            Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min(1280, work.Width - 32); var height = Math.Min(850, work.Height - 32);
        AppWindow.MoveAndResize(new global::Windows.Graphics.RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        Closed += OnClosed;
        AppWindow.Closing += (_, _) => OnClosed(this, null);
        placeTimer.Tick+=(_,_)=>SavePlace();
        workTimer.Tick+=(_,_)=>WorkElapsed.Text=workClock.IsRunning ? workClock.Elapsed.ToString(@"hh\:mm\:ss") : "";
        Activated += async (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && ready && !scanning && !operation)
                await Guard(LoadSources);
        };
        availabilityTimer.Tick += async (_, _) => { if (ready && !scanning && !operation) await Guard(async () => await LoadSources()); };
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ready) return;
        await Guard(async () =>
        {
            if (AppSettings.IsSmokeTest && !File.Exists(Path.Combine(AppSettings.LocalRoot,"settings.json"))) { settings.ThumbnailSize=140; settings.ShowNames=true; }
            ApplyTheme();
            if (settings.RestoredFromCorruptedFile)
            {
                Notify("Corrupted settings were safely reset to defaults; previous file preserved as settings.json.corrupt.", true);
            }
            if (!DiskGuard.HasSufficientSpace())
            {
                Notify("Low disk space on system drive (< 500 MB). Thumbnail generation is paused to safeguard your disk.", true);
            }
            var healthy = await Task.Run(() => catalog.QuickCheckAsync());
            if (!healthy)
            {
                var outcome = await Task.Run(() => catalog.RepairCatalogDetailedAsync());
                var message = outcome switch
                {
                    CatalogRepairOutcome.RestoredFromBackup => "Catalog corruption was detected and restored from backup. Originals are untouched.",
                    CatalogRepairOutcome.ReinitializedEmpty => "Catalog corruption was detected and no healthy backup was found. Created a clean catalog without touching originals.",
                    _ => "Catalog repair could not finish automatically. Check disk space or restore a manual backup from Settings."
                };
                Notify(message, true);
            }
            else
            {
                await Task.Run(() => catalog.InitializeAsync());
            }
            cache = await Task.Run(() => new ThumbnailCache(Path.Combine(AppSettings.LocalRoot, "thumbnails"), Math.Clamp(settings.CacheGiB, 1, 32) * 1024L * 1024 * 1024));
            thumbnails = new(cache, resolver, new WindowsThumbnailGenerator());
            if (AppSettings.IsSmokeTest) await SeedSmokeFixtures();
            await LoadSources(); await LoadAlbums(); await LoadSmartCollections(); ApplyViewSettings(); settings.Save(); availabilityTimer.Start(); await RestorePlace();
        });
    }
    private async Task SeedSmokeFixtures()
    {
        if(File.Exists(Path.Combine(AppSettings.LocalRoot,"smoke-seeded")))return;
        var folder = Path.Combine(AppSettings.LocalRoot, "Fixture originals"); Directory.CreateDirectory(folder);
        Directory.CreateDirectory(Path.Combine(folder, "Trips"));
        for (int index = 0; index < 260; index++)
        {
            using var writer = new BinaryWriter(File.Create(Path.Combine(index == 7 ? Path.Combine(folder, "Trips") : folder, $"fixture{index:000}.bmp")));
            writer.Write((ushort)0x4D42); writer.Write(54 + 256 * 256 * 3); writer.Write(0); writer.Write(54);
            writer.Write(40); writer.Write(256); writer.Write(256); writer.Write((ushort)1); writer.Write((ushort)24);
            writer.Write(0); writer.Write(256 * 256 * 3); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            for (int y = 0; y < 256; y++) for (int x = 0; x < 256; x++)
            { writer.Write((byte)(80 + index * 15)); writer.Write((byte)y); writer.Write((byte)x); }
            writer.Dispose();
            File.SetLastWriteTimeUtc(Path.Combine(index == 7 ? Path.Combine(folder,"Trips") : folder,$"fixture{index:000}.bmp"),new DateTime(2026,9,30,12,0,0,DateTimeKind.Utc).AddDays(-index));
        }
        var source = resolver.Register(folder);
        await Task.Run(() => catalog.ScanAsync(source.Id, folder));
        await Task.Run(() => catalog.EnrichAsync(new WindowsMetadataReader(),source.Id));
        var memory=(await catalog.QueryAsync(new(SourceId:source.Id,Search:"fixture003.bmp"))).Items.Single();
        await catalog.SaveMetadataAsync(memory.Media,new(DateTime.Now.Date.AddYears(-1).AddHours(12).ToUniversalTime().Ticks,"Smoke capture date"));
        File.WriteAllText(Path.Combine(AppSettings.LocalRoot,"smoke-seeded"),"1");
    }

    private void OnClosed(object? sender, WindowEventArgs? args)
    {
        if (isClosing) return;
        isClosing = true;
        SavePlace();
        placeTimer.Stop();
        ready = false;
        availabilityTimer.Stop();
        workTimer.Stop();
        scanCancellation?.Cancel();
        queryCancellation?.Cancel();
        searchCancellation?.Cancel();
        preparationCancellation?.Cancel();
        previewCancellation?.Cancel();
        mapBackfillCts?.Cancel();
        try { Task.WaitAll(SnapshotCacheTasks(), TimeSpan.FromSeconds(2)); } catch (Exception) { /* Pending work must finish before releasing cache ownership. */ }
        cache?.Dispose();
        App.Current.Exit();
        Environment.Exit(0);
    }
    private async Task Guard(Func<Task> action, bool exclusive = false)
    {
        if (exclusive && operation) { Notify("Work is already running. Browse freely, or open Activity to pause it before making this change."); return; }
        if (exclusive) operation = true;
        try { await action(); }
        catch (OperationCanceledException) { Status.Text = "Paused · saved library intact"; }
        catch (Exception ex)
        {
            var redacted = PathRedactor.Redact(ex.ToString());
            try { Directory.CreateDirectory(AppSettings.LocalRoot); File.WriteAllText(Path.Combine(AppSettings.LocalRoot, "last-error.txt"), DateTime.Now + "\n" + redacted); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Notify(string.IsNullOrWhiteSpace(ex.Message) ? "This action could not finish. Try again; diagnostic details are saved in Luma’s local data folder (last-error.txt)." : ex.Message, true);
            Status.Text = "Action could not be completed.";
        }
        finally { if (exclusive) operation = false; }
    }
    private void Notify(string text, bool error = false)
    { recentActivity.Enqueue($"{DateTime.Now:t} · {text}"); while(recentActivity.Count>20)recentActivity.Dequeue(); Notice.Message = text; Notice.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Informational; Notice.IsOpen = true; }
    private async Task LoadSources()
    {
        if (checkingSources) return;
        checkingSources = true;
        try
        {
            var selected = SelectedSource;
            var all = await Task.Run(() => catalog.SourcesAsync());
            var labels = await catalog.SourceLabelsAsync();
            var signature = string.Join(";", all.Select(s => s.Id + s.IsOnline + s.RootPath + (labels.TryGetValue(s.Id,out var label) ? label : "")));
            var changed = signature != sourceSignature; sourceSignature = signature;
            if(!changed && Sources.ItemsSource is not null) return;
            var choices = new List<SourceChoice> { new(null, "All sources") };
            choices.AddRange(all.Select(s => new SourceChoice(s.Id, $"{(s.IsOnline ? "●" : "○")} {(labels.TryGetValue(s.Id, out var label) && label.Length > 0 ? label : s.RootPath)}")));
            suppressFilterEvents++;
            try
            {
                Sources.ItemsSource = choices;
                Sources.SelectedItem = choices.FirstOrDefault(s => s.Id == selected) ?? choices[0];
            }
            finally
            {
                suppressFilterEvents--;
            }
            UpdateSidebarCounts(sourcesCount: all.Count);
            if (ready && changed)
            {
                var online = all.Where(s => s.IsOnline).Select(s => s.Id).ToHashSet();
                foreach (var card in cards) card.SetAvailability(online.Contains(card.Item.Media.SourceId));
                RefreshSelectionDetails();
                // Preserve the collection, scroll and selection; only retry visible missing previews.
                var retry = cards.Where(c => c.Realized && c.Thumbnail is null && c.Item.Media.IsAvailable).ToArray();
                foreach (var card in retry) _ = LoadThumbnail(card);
                if (!scanning) Notify("Drive status updated. Your saved library is ready.");
            }
        }
        finally { checkingSources = false; }
    }
    private async Task<string?> PickFolder()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    private async void AddSource(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!ready) return; var folder = await PickFolder(); if (folder is null) return;
        var source = await Task.Run(() => resolver.Register(folder)); await Scan(new[] { new SourceInfo(source.Id, source.RootPath, DateTime.UtcNow, true) }, reuseExisting: true);
    }, true);
    private async void RefreshDriveStatus(object sender, RoutedEventArgs e) => await Guard(LoadSources, true);
    private async void ResumeIndexing(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var pending=(await catalog.IndexingCheckpointsAsync()).Select(c=>c.SourceId).ToHashSet();
        var sources=await Task.Run(()=>catalog.SourcesAsync());
        var connected=sources.Where(s=>pending.Contains(s.Id) && s.IsOnline).ToArray();
        if(connected.Length==0) { Notify("Reconnect a drive with paused indexing to resume."); return; }
        await Scan(connected);
    },true);
    private Task RescanCurrent() => RefreshSources(SelectedSource);
    private async Task Scan(IEnumerable<SourceInfo> sources, bool reuseExisting = false)
    {
        scanning = true; workClock.Restart(); workTimer.Start(); scanCancellation = new(); Busy.IsActive = true; CancelButton.Visibility = Visibility.Visible;
        AddSourceButton.IsEnabled = false;
        bool published = false;
        try
        {
            long count = 0;
            var batch=sources.ToArray(); int sourceNumber=0;
            var scannedIds = new List<string>();
            foreach (var source in batch)
            {
                sourceNumber++;
                var checkpoint=(await catalog.IndexingCheckpointsAsync()).FirstOrDefault(c=>c.SourceId==source.Id);
                ImportFeedback("discovery",$"Step 1 of 2 · Finding media · Source {sourceNumber} / {batch.Length}",source.RootPath+"\nTotal files are not known until discovery finishes. Existing media remains available. Pause saves your checkpoint.");
                var details=new Progress<DiscoveryProgress>(p=>ImportFeedback("discovery",
                    $"Step 1 of 2 · Finding media · {p.SavedFiles:N0} saved · Source {sourceNumber} / {batch.Length}",
                    $"{source.RootPath}\n{p.Folder}\n{p.ExaminedFiles:N0} files examined this pass · {p.CompletedFolders:N0} folders completed · {p.SkippedFolders:N0} inaccessible.\nTotal is unknown until discovery finishes. Pause saves a checkpoint."));
                var opened = reuseExisting
                    ? await Task.Run(() => catalog.OpenSourceAsync(source.Id, source.RootPath, scanCancellation.Token, details: details))
                    : new SourceOpenResult(false, await Task.Run(() => catalog.ScanAsync(source.Id, source.RootPath, scanCancellation.Token, details: details)));
                count += opened.MediaCount;
                if (!opened.UsedSavedCatalog) scannedIds.Add(source.Id);
            }
            await LoadSources(); await Refresh(); published = true;
            if (scannedIds.Count == 0)
            {
                Status.Text = $"{count:N0} saved items · Ready";
                Notify(reuseExisting ? $"Opened {count:N0} saved items without rescanning. Use Refresh to discover changed files." : "No connected sources to rescan. Saved media remains available.");
                return;
            }
            foreach (var metadataSource in scannedIds)
            {
                var label=batch.First(s=>s.Id==metadataSource).RootPath;
                ImportFeedback("metadata","Step 2 of 2 · Preparing date reading",label+"\nCounting changed files. Your library is ready to browse.");
                var metadataProgress=MetadataFeedback(label);
                await Task.Run(()=>catalog.EnrichAsync(new WindowsMetadataReader(),metadataSource,scanCancellation.Token,details:metadataProgress));
            }
            Status.Text=$"Import complete · {count:N0} items";
            var warnings=(await catalog.IndexingWarningsAsync()).Count(w=>scannedIds.Contains(w.SourceId));
            Notify(warnings>0 ? $"Indexed {count:N0} media files; {warnings} inaccessible folder(s) skipped. Older catalog entries were retained. See Activity, fix access, then refresh." : $"Scan complete: {count:N0} media files. Metadata saved. Navigate or Reset to apply new dates without shifting your current view.");
        }
        catch(OperationCanceledException)
        {
            Notify(published ? "Date reading paused. Your library is ready to browse. Use Manage sources → More → Refresh metadata to continue changed files." : "Indexing paused. Completed folders are saved on this PC. Choose Activity → Resume paused indexing to continue; your published gallery is intact.");
            throw;
        }
        catch(Exception)
        {
            Status.Text="Indexing paused · checkpoint saved";
            throw;
        }
        finally
        {
            scanning = false; EndImportFeedback(); workClock.Stop(); workTimer.Stop(); WorkElapsed.Text=""; Busy.IsActive = false; CancelButton.Visibility = Visibility.Collapsed;
            AddSourceButton.IsEnabled = true;
            await LoadSources(); if(!published && ready) await Refresh();
        }
    }
    private void CancelScan(object sender, RoutedEventArgs e) { scanCancellation?.Cancel(); preparationCancellation?.Cancel(); Notify("Pause requested. Finishing the current operation safely; saved progress will be retained."); }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectionChrome(GetSelectedCards());
    }

    private void UpdateSelectionChrome(MediaCard[] selected)
    {
        var card = PrimarySelectedCard();
        SelectionBar.Visibility = selected.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionCount.Text = $"{selected.Length:N0} selected";
        SelectionHint.Visibility = selected.Length == 0 && SelectMode.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        var spanInfo = "";
        if (selected.Length > 1)
        {
            var minD = selected.Min(c => c.Date);
            var maxD = selected.Max(c => c.Date);
            var span = maxD - minD;
            var spanLabel = span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds}s"
                : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m"
                : span.TotalDays < 1 ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : $"{(int)span.TotalDays}d {span.Hours}h";
            spanInfo = $" · {minD:dd MMM HH:mm} - {maxD:HH:mm} ({spanLabel} span)";
        }

        ToolTipService.SetToolTip(SelectionCount, $"{selected.Sum(c => (double)c.Item.Media.Length) / 1024 / 1024:N1} MB · {selected.Count(c => !c.Item.Media.IsAvailable):N0} offline{spanInfo}. Reconnect offline sources to copy originals.");
        RefreshSelectionDetails();
        TagsBox.Text = card?.Item.Tags ?? ""; FavoriteButton.Content = selected.Length > 0 && selected.All(c => c.Item.Favorite) ? "★ Unfavorite" : "☆ Favorite";
    }

    private void RefreshSelectionDetails()
    {
        var card = PrimarySelectedCard();
        var meta = card?.Item.Metadata;
        var dimensions = meta?.Width > 0 && meta.Height > 0 ? $"{meta.Width} × {meta.Height}" : "Dimensions unavailable";
        var taken = meta?.TakenTicks is long ticks ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("dd MMM yyyy HH:mm:ss") : "Unavailable";
        var camParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(meta?.CameraModel)) camParts.Add(meta.CameraModel);
        if (meta?.FocalLength is double fl) camParts.Add($"{fl:0.#}mm");
        if (meta?.FNumber is double fn) camParts.Add($"f/{fn:0.#}");
        if (meta?.ExposureSeconds is double exp) camParts.Add(exp < 1 && exp > 0 ? $"1/{Math.Round(1 / exp)}s" : $"{exp:0.##}s");
        if (meta?.Iso is int iso) camParts.Add($"ISO {iso}");
        var camLine = camParts.Count > 0 ? "\nCamera: " + string.Join(" · ", camParts) : "";
        var placeLine = !string.IsNullOrWhiteSpace(meta?.LocationName) ? $"\nPlace: {meta.LocationName}" : "";
        var originNote = meta?.LocationOrigin == "manual" ? " (Manual pin)" : "";
        var gpsLine = meta?.Latitude is double lat && meta.Longitude is double lon ? $"\nGPS: {lat:0.00000}, {lon:0.00000}{originNote}" : "";
        InspectorMapButton.Visibility = meta?.Latitude is not null ? Visibility.Visible : Visibility.Collapsed;
        InspectorPinLocationButton.Visibility = card is not null ? Visibility.Visible : Visibility.Collapsed;

        SelectionText.Text = card is null ? "Select media to inspect" : $"{card.Name}\n\n{card.Item.Media.RelativePath}\n\n{card.Item.Media.Length / 1024d:N0} KB · {card.Item.Media.Kind}\n{dimensions}\n{(meta?.DurationSeconds > 0 ? TimeSpan.FromSeconds(meta.DurationSeconds).ToString(@"hh\:mm\:ss") : "")}\n\nTaken: {taken}\nModified: {new DateTime(card.Item.Media.ModifiedTicks, DateTimeKind.Utc).ToLocalTime():dd MMM yyyy HH:mm:ss}\n{meta?.DateOrigin}{camLine}{placeLine}{gpsLine}\n\n{(card.Item.Media.IsAvailable ? "Source connected" : "Offline preview · reconnect to open original")}";
    }
    private void GridRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var element = e.OriginalSource as FrameworkElement;
        while (element is not null && element.DataContext is not MediaCard) element = VisualTreeHelper.GetParent(element) as FrameworkElement;
        contextCard=element?.DataContext as MediaCard;
        if (contextCard is MediaCard card && !MediaGrid.SelectedItems.Contains(card)) MediaGrid.SelectedItem = card;
    }
    private async void GridDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) { if(SelectMode.IsChecked!=true)await Guard(Open); }
    private string Resolve(CachedMedia media)
    {
        var root = resolver.ResolveRoot(media.SourceId) ?? throw new IOException("This drive is offline. Reconnect it to use the original file.");
        var path = SourcePaths.Combine(root, media.RelativePath);
        if (!File.Exists(path)) throw new FileNotFoundException("This file has moved or been deleted. Rescan the source.");
        if (!MediaCache.VersionMatches(path, media)) throw new IOException("This file changed. Rescan the source before using the original.");
        return path;
    }
    private Task Open()
    {
        var target = PrimarySelectedCard();
        if (target is not null) ShellFiles.OpenInDefaultApp(Resolve(target.Item.Media));
        return Task.CompletedTask;
    }
    private async void OpenSelected(object sender, RoutedEventArgs e) => await Guard(Open);
    private async void RevealSelected(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var target = PrimarySelectedCard();
        if (target is not null) ShellFiles.RevealInExplorer(Resolve(target.Item.Media));
        return Task.CompletedTask;
    });
    private async Task SetClipboardWithRetryAsync(DataPackage data)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Clipboard.SetContent(data);
                Clipboard.Flush();
                return;
            }
            catch (System.Runtime.InteropServices.COMException ex) when (ex.HResult == unchecked((int)0x800401D0))
            {
                if (attempt == 3)
                    throw new InvalidOperationException("Windows clipboard is currently unavailable or locked by another application (or the Windows lock screen). Unlock Windows or use Copy to folder / Move to folder instead.");
                await Task.Delay(60 * (attempt + 1));
            }
        }
    }

    private async void CopyPath(object sender, RoutedEventArgs e) => await ClipboardAction(async () =>
    {
        var selected=GetSelectedCards().Select(c=>c.Item.Media).ToArray();
        if(selected.Length==0)return;
        var paths=await Task.Run(()=>selected.Select(Resolve).ToArray());
        var data=new DataPackage(); data.SetText(string.Join(Environment.NewLine,paths));
        await SetClipboardWithRetryAsync(data);
        Notify($"Copied {paths.Length:N0} file paths.");
    });
    // Clipboard preparation is read-only and may run while background indexing owns
    // the mutation guard. Serialize clipboard requests without blocking browsing.
    private async Task ClipboardAction(Func<Task> action)
    {
        if(clipboardBusy) { Notify("Preparing your previous clipboard selection. Please wait."); return; }
        clipboardBusy=true;
        try { await Guard(action); }
        finally { clipboardBusy=false; }
    }
    private async Task Copy(bool cut)
    {
        var selected = GetSelectedCards(); if (selected.Length == 0) return;
        var files = new List<IStorageItem>();
        if(!scanning && !preparing)Status.Text=$"Preparing {selected.Length:N0} files for the clipboard…";
        var paths=await Task.Run(()=>selected.Select(c=>Resolve(c.Item.Media)).ToArray());
        foreach(var path in paths)files.Add(await StorageFile.GetFileFromPathAsync(path));
        var data = new DataPackage { RequestedOperation = cut ? DataPackageOperation.Move : DataPackageOperation.Copy };
        data.SetStorageItems(files);
        data.SetData("Luma.FileSelection.v1", JsonSerializer.Serialize(selected.Select(c => c.Item.Media).ToArray()));
        await SetClipboardWithRetryAsync(data);
        pendingCutIds.Clear();
        if(cut) foreach(var card in selected) pendingCutIds.Add(card.Item.Id);
        foreach(var card in cards) card.IsCut=pendingCutIds.Contains(card.Item.Id);
        if(!scanning && !preparing)Status.Text=$"{files.Count:N0} files ready to {(cut ? "move" : "copy")}";
        Notify($"{files.Count} file(s) ready to {(cut ? "move" : "copy")}. Paste into a destination folder.");
    }
    private async void CopySelected(object sender, RoutedEventArgs e) => await ClipboardAction(() => Copy(false));
    private async void CutSelected(object sender, RoutedEventArgs e) => await ClipboardAction(() => Copy(true));
    private async Task Paste()
    {
        var data = Clipboard.GetContent();
        if (!data.Contains(StandardDataFormats.StorageItems)) { Notify("Copy or cut files first."); return; }
        var destination = await PickFolder(); if (destination is null) return;
        await TransferData(data, destination, data.RequestedOperation.HasFlag(DataPackageOperation.Move), true);
    }
    private async void PasteSelected(object sender, RoutedEventArgs e) => await Guard(Paste, true);
    private async Task Delete()
    {
        if (scanning) return; var selected = GetSelectedCards(); if (selected.Length == 0) return;
        if (!await Confirm("Delete selected originals?", $"Send {selected.Length} file(s) to the Recycle Bin. Windows may warn if this drive does not support recycling. Check its confirmation carefully.", "Continue")) return;
        var resolved = selected.Select(c => (Card: c, FullPath: Resolve(c.Item.Media))).ToArray();
        var ran = ShellFiles.Run(Handle, 3, resolved.Select(x => x.FullPath));
        var deleted = resolved.Where(x => !File.Exists(x.FullPath)).Select(x => x.Card.Item.Media).ToArray();
        if (!ran && deleted.Length == 0)
        {
            Notify("Delete cancelled. Originals stay untouched.");
            return;
        }
        if (deleted.Length > 0)
        {
            await catalog.RemoveMediaItemsAsync(deleted);
            await LoadAlbums();
            await Refresh();
            Notify($"Moved {deleted.Length:N0} file(s) to the Recycle Bin.");
        }
    }
    private async void DeleteSelected(object sender, RoutedEventArgs e) => await Guard(Delete, true);
    private async Task Rename()
    {
        if (scanning) return;
        var selected = GetSelectedCards();
        if (selected.Length == 0) return;
        if (selected.Length > 1) { await BatchRenameAsync(selected); return; }

        var single = selected[0];
        var media = single.Item.Media;
        var oldName = Path.GetFileName(media.RelativePath);
        var name = await Prompt("Rename file", "Keep the extension to preserve its file type.", oldName);
        if (name is null || name == oldName) return;
        if (!SourcePaths.ValidateWindowsFileName(name, out var nameError))
            throw new ArgumentException(nameError);
        var oldExt = Path.GetExtension(oldName);
        var newExt = Path.GetExtension(name);
        if (!string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase))
        {
            var warning = string.IsNullOrEmpty(newExt)
                ? $"Removing the \"{oldExt}\" extension will hide this file on future scans and keep Windows from opening it cleanly. Rename anyway?"
                : $"Changing the extension from \"{oldExt}\" to \"{newExt}\" may prevent opening or previewing this file. Rename anyway?";
            if (!await Confirm("Change file extension?", warning, "Rename anyway")) return;
        }
        var original = Resolve(media);
        var target = Path.Combine(Path.GetDirectoryName(original)!, name);
        var isCaseOnly = string.Equals(oldName, name, StringComparison.OrdinalIgnoreCase);
        if (!isCaseOnly && File.Exists(target))
            throw new InvalidOperationException("A file with that name already exists in this folder. No files were overwritten.");
        if (isCaseOnly && File.Exists(target))
        {
            var staging = Path.Combine(Path.GetDirectoryName(original)!, $".luma-case-{Guid.NewGuid():N}{newExt}");
            File.Move(original, staging);
            File.Move(staging, target);
        }
        else
        {
            File.Move(original, target); // Never overwrite a different file.
        }
        var renamed = media with { RelativePath = Path.Combine(Path.GetDirectoryName(media.RelativePath) ?? "", name) };
        try { await catalog.RelocateAsync(media, renamed); }
        catch { Notify("File renamed, but catalog update failed. Keep the old catalog; restore the filename in Explorer before retrying.", true); throw; }
        await LoadAlbums(); await Refresh();
    }

    private async Task BatchRenameAsync(MediaCard[] selected)
    {
        var patternBox = new TextBox { Header = "Filename pattern (tokens: {date}, {time}, {orig}, {seq})", Text = "{date}_{orig}" };
        var previewList = new ListView { MaxHeight = 220, SelectionMode = ListViewSelectionMode.None };
        void RefreshPreview()
        {
            previewList.Items.Clear();
            for (int i = 0; i < Math.Min(selected.Length, 40); i++)
            {
                var c = selected[i];
                var nextName = BatchRenameFormatter.FormatName(patternBox.Text, c.Item, i + 1, selected.Length);
                previewList.Items.Add($"{c.Name} -> {nextName}");
            }
            if (selected.Length > 40) previewList.Items.Add($"... and {selected.Length - 40} more");
        }
        patternBox.TextChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(Description($"Batch rename {selected.Length:N0} selected files. Original extensions are preserved and existing collisions are skipped."));
        panel.Children.Add(patternBox);
        panel.Children.Add(previewList);

        var dialog = new ContentDialog { RequestedTheme = Root.ActualTheme, XamlRoot = Root.XamlRoot, Title = "Batch rename files", Content = panel, PrimaryButtonText = $"Rename {selected.Length:N0} files", CloseButtonText = "Cancel" };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;

        int renamedCount = 0, skippedCount = 0, catalogErrorCount = 0;
        for (int i = 0; i < selected.Length; i++)
        {
            var c = selected[i];
            var media = c.Item.Media;
            var newName = BatchRenameFormatter.FormatName(patternBox.Text, c.Item, i + 1, selected.Length);
            if (!SourcePaths.ValidateWindowsFileName(newName, out _) || string.Equals(newName, Path.GetFileName(media.RelativePath), StringComparison.OrdinalIgnoreCase)) { skippedCount++; continue; }
            try
            {
                var original = Resolve(media);
                var target = Path.Combine(Path.GetDirectoryName(original)!, newName);
                if (File.Exists(target)) { skippedCount++; continue; }
                File.Move(original, target);
                try
                {
                    var updated = media with { RelativePath = Path.Combine(Path.GetDirectoryName(media.RelativePath) ?? "", newName) };
                    await catalog.RelocateAsync(media, updated);
                    renamedCount++;
                }
                catch { catalogErrorCount++; }
            }
            catch { skippedCount++; }
        }
        await LoadAlbums(); await Refresh();
        var summary = $"Batch rename finished: {renamedCount:N0} renamed, {skippedCount:N0} skipped.";
        if (catalogErrorCount > 0) summary += $" {catalogErrorCount:N0} renamed file(s) need a Refresh to update the catalog.";
        Notify(summary, catalogErrorCount > 0);
    }

    private async void RenameSelected(object sender, RoutedEventArgs e) => await Guard(Rename, true);
    private async void ToggleFavorite(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var selected = GetSelectedCards(); if (selected.Length == 0) return;
        var favorite = !selected.All(c => c.Item.Favorite);
        foreach (var card in selected) await catalog.AnnotateAsync(card.Item.Media, favorite, card.Item.Tags);
        await Refresh(); Notify($"{selected.Length:N0} item(s) {(favorite ? "added to" : "removed from")} Favorites.");
    }, true);
    private async void SaveTags(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var selected = GetSelectedCards();
        if (selected.Length > 1 && !await Confirm("Replace tags on selected files?", $"Replace tags for {selected.Length} selected items with the entered list.", "Save tags")) return;
        if(selected.Length==0) {Notify("Select a file before saving tags.");return;}
        foreach (var card in selected) await catalog.AnnotateAsync(card.Item.Media, card.Item.Favorite, TagsBox.Text);
        await Refresh(); Notify($"Tags saved for {selected.Length:N0} item(s).");
    }, true);
    private async Task<bool> Confirm(string title, string message, string action)
    {
        var dialog = new ContentDialog { RequestedTheme=Root.ActualTheme, XamlRoot = Root.XamlRoot, Title = title, Content = message, PrimaryButtonText = action, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    private readonly List<ContentDialog> retainedDialogs = new();
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        finally
        {
            // Keep cleared shells alive so WinUI's UIAffinityReleaseQueue never runs ~ContentDialog/~CPopup
            // inside a reentrancy-protected tick while UIA or Narrator is attached.
            dialog.Content = null;
            dialog.DataContext = null;
            retainedDialogs.Add(dialog);
            await Task.Yield();
        }
    }

    private async Task<string?> Prompt(string title, string description, string initial)
    {
        var input = new TextBox { Text = initial, Header = description, MinWidth = 320 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(input,"DialogInput");
        var dialog = new ContentDialog { RequestedTheme=Root.ActualTheme, XamlRoot = Root.XamlRoot, Title = title, Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
        return await ShowDialogAsync(dialog) == ContentDialogResult.Primary ? input.Text.Trim() : null;
    }
    private void ApplyTheme()
    {
        Root.RequestedTheme = Enum.TryParse<ElementTheme>(settings.Theme, out var theme) ? theme : ElementTheme.Default;
        if (MapOverlay.Visibility == Visibility.Visible)
        {
            RenderOfflineCanvas();
            if (mapWebReady && mapWebView?.CoreWebView2 is not null)
                mapWebView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { type = "setTheme", theme = Root.ActualTheme == ElementTheme.Light ? "light" : "dark" }));
        }
    }
    private async void Settings(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var theme = new ComboBox { Header = "Theme", ItemsSource = new[] { "Default", "Light", "Dark" }, SelectedItem = settings.Theme };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(theme, "ThemeSelector");
        var budget = new NumberBox { Header = "Thumbnail cache limit (GiB, applies next start)", Minimum = 1, Maximum = 32, Value = settings.CacheGiB, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var panel = new StackPanel { Spacing = 18, MinWidth = 380 };
        var remember=new CheckBox { Content="Remember where I left off",IsChecked=settings.RememberPlace };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(remember,"RememberPlace");
        ToolTipService.SetToolTip(remember,"Restore your last source, folder, filters, month and visible item. Turn off to start fresh.");
        panel.Children.Add(remember); panel.Children.Add(theme); panel.Children.Add(budget); panel.Children.Add(new TextBlock { Text = "Thumbnails and metadata are saved on this PC. Old thumbnails are removed when the cache fills, including thumbnails from offline drives.", TextWrapping = TextWrapping.Wrap, MaxWidth = 400 });
        var dialog = new ContentDialog { RequestedTheme=Root.ActualTheme, XamlRoot = Root.XamlRoot, Title = "Settings", Content = new ScrollViewer { MaxHeight = 500, Content = panel }, PrimaryButtonText = "Save", CloseButtonText = "Cancel" };
        bool storage=false,exclusions=false,manageTags=false,findDuplicates=false,backupCatalog=false,restoreCatalog=false,resetCatalog=false,copyDiagnostics=false,sendFeedback=false,showShortcuts=false,checkUpdates=false;
        panel.Children.Add(Description("Luma · Your memories, calmly organized.\nVersion 0.7.0 · Windows portable (unsigned)\nData location: " + AppSettings.LocalRoot));
        panel.Children.Add(ActionButton("Keyboard shortcuts & tips (F1)",()=> { showShortcuts=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Manage exclusions…",()=> { exclusions=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Storage & previews",()=> { storage=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Manage tags…",()=> { manageTags=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Find duplicates…",()=> { findDuplicates=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Back up catalog…",()=> { backupCatalog=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Restore catalog from backup…",()=> { restoreCatalog=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Reset catalog (keep originals)…",()=> { resetCatalog=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Copy diagnostic info (paths redacted)",()=> { copyDiagnostics=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Send feedback / Report a problem…",()=> { sendFeedback=true; dialog.Hide(); }));
        panel.Children.Add(ActionButton("Check for updates on GitHub",()=> { checkUpdates=true; dialog.Hide(); }));
        var result=await ShowDialogAsync(dialog);
        if(showShortcuts) { await ShowKeyboardShortcutsDialog(); return; }
        if(storage) { await OpenStorage(); return; }
        if(exclusions) { await OpenExclusions(); return; }
        if(manageTags) { await OpenTagManager(); return; }
        if(findDuplicates) { await OpenDuplicatesFinder(); return; }
        if(backupCatalog) { await BackupCatalogAction(); return; }
        if(restoreCatalog) { await RestoreCatalogAction(); return; }
        if(resetCatalog) { await ResetCatalogAction(); return; }
        if(copyDiagnostics) { await CopyDiagnosticInfo(); return; }
        if(sendFeedback) { await OpenFeedbackIssue(); return; }
        if(checkUpdates)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/smile-plzz/Luma-windows-software/releases/latest") { UseShellExecute = true }); } catch { }
            return;
        }
        if (result != ContentDialogResult.Primary) return;
        settings.Theme = theme.SelectedItem?.ToString() ?? "Default"; settings.CacheGiB = double.IsNaN(budget.Value) ? 2 : (int)Math.Clamp(budget.Value, 1, 32);
        settings.RememberPlace=remember.IsChecked==true; if(!settings.RememberPlace)settings.Place=null;
        settings.Save(); ApplyTheme(); Notify("Settings saved. Cache size changes apply next time you open Luma.");
    }, true);
    private bool HandleShortcut(KeyboardAcceleratorInvokedEventArgs e)
    {
        if (!ready || Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox) return false;
        e.Handled = true; return true;
    }
    private async void CopyShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) { if (HandleShortcut(e)) await ClipboardAction(() => Copy(false)); }
    private async void CutShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) { if (HandleShortcut(e)) await ClipboardAction(() => Copy(true)); }
    private async void PasteShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) { if (HandleShortcut(e)) await Guard(Paste, true); }
    private async void DeleteShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) { if (HandleShortcut(e)) await Guard(Delete, true); }
    private async void RenameShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) { if (HandleShortcut(e)) await Guard(Rename, true); }
    private void SelectShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e) { if (HandleShortcut(e)) { SelectLoaded(sender, new RoutedEventArgs()); Notify($"Selected {cards.Count:N0} loaded items. Use Select all matching results in the context menu for the entire result set."); } }
    private void SearchShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (HandleShortcut(e)) { SearchBox.Focus(FocusState.Programmatic); SearchBox.SelectAll(); }
    }
    private void ToggleSidebarShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (HandleShortcut(e)) ToggleSidebar(sender, new RoutedEventArgs());
    }
    private void ToggleHeaderShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (HandleShortcut(e)) ToggleHeader(sender, new RoutedEventArgs());
    }
    private async void BackHistoryShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (HandleShortcut(e)) await DoNavigateBack();
    }
    private void ScrollTopShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (HandleShortcut(e)) ScrollToTop();
    }
    private void ScrollBottomShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (HandleShortcut(e)) ScrollToBottom();
    }
    private async void HelpShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (!ready) return;
        e.Handled = true;
        await Guard(ShowKeyboardShortcutsDialog);
    }
    private async void RefreshShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (HandleShortcut(e)) await Guard(RescanCurrent, true);
    }
    private async void OpenShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (HandleShortcut(e)) await Guard(Open);
    }
    private void EscapeShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (PreviewOverlay.Visibility == Visibility.Visible)
        {
            DoClosePreview();
            e.Handled = true;
        }
        else if (MapOverlay.Visibility == Visibility.Visible)
        {
            if (mapPinMode)
            {
                mapPinMode = false;
                MapPinModeToggle.IsChecked = false;
                UpdatePinModeVisuals();
            }
            else
            {
                CloseMapView(this, new RoutedEventArgs());
            }
            e.Handled = true;
        }
        else if (GetSelectedCards().Length > 0)
        {
            ClearAllSelections();
            e.Handled = true;
        }
    }

    private async void OpenTagManagerMenu(object sender, RoutedEventArgs e) => await Guard(OpenTagManager, true);
    private async void OpenDuplicatesMenu(object sender, RoutedEventArgs e) => await Guard(OpenDuplicatesFinder, true);

    private async Task CopyDiagnosticInfo()
    {
        var sources = await Task.Run(() => catalog.SourcesAsync());
        var sourceCounts = await catalog.SourceCountsAsync();
        var cacheBytes = cache is null ? 0 : (await cache.SnapshotAsync()).Bytes;
        var driveLetter = Path.GetPathRoot(AppSettings.LocalRoot);
        long freeBytes = 0;
        try
        {
            if (!string.IsNullOrEmpty(driveLetter))
            {
                var dInfo = new DriveInfo(driveLetter);
                freeBytes = dInfo.AvailableFreeSpace;
            }
        }
        catch { }

        string lastError = "None";
        var errPath = Path.Combine(AppSettings.LocalRoot, "last-error.txt");
        if (File.Exists(errPath))
        {
            try { lastError = PathRedactor.Redact(File.ReadAllText(errPath)); } catch { }
        }

        var report = new
        {
            Application = "Luma",
            Version = "0.7.0",
            OSVersion = Environment.OSVersion.ToString(),
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            DotNetRuntime = Environment.Version.ToString(),
            RegisteredSources = sources.Count,
            TotalCatalogItems = sourceCounts.Values.Sum(),
            ThumbnailCacheLimitGiB = settings.CacheGiB,
            ThumbnailCacheUsedMB = Math.Round(cacheBytes / (1024.0 * 1024.0), 2),
            SystemDriveFreeMB = Math.Round(freeBytes / (1024.0 * 1024.0), 2),
            LastError = lastError
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var data = new DataPackage();
        data.SetText(json);
        await SetClipboardWithRetryAsync(data);
        Notify("Diagnostic details copied to clipboard (all paths redacted).");
    }

    private async Task BackupCatalogAction()
    {
        var backupDir = Path.Combine(AppSettings.LocalRoot, "backups");
        Directory.CreateDirectory(backupDir);
        var backupFile = Path.Combine(backupDir, $"catalog-backup-{DateTime.UtcNow:yyyyMMddHHmmss}.db");
        await catalog.CreateBackupAsync(backupFile);
        Notify($"Catalog backup saved to backups\\{Path.GetFileName(backupFile)}.");
    }

    private async Task RestoreCatalogAction()
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".db");
        picker.FileTypeFilter.Add(".bak");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, Handle);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        if (!await Confirm("Restore catalog from backup?", $"Restoring from {file.Name} will replace your current catalog. An automatic safety backup will be created first.", "Restore")) return;

        var restored = await catalog.RestoreBackupAsync(file.Path);
        if (restored)
        {
            await LoadSources();
            await LoadAlbums();
            await Refresh();
            Notify($"Catalog successfully restored from {file.Name}.");
        }
        else
        {
            Notify("The selected backup file is invalid or corrupt.", true);
        }
    }

    private async Task OpenTagManager()
    {
        var tags = await catalog.GetTagsWithCountsAsync();
        var panel = new StackPanel { Spacing = 12, MinWidth = 380 };
        var list = new ListView { ItemsSource = tags, MaxHeight = 280, SelectionMode = ListViewSelectionMode.Single };
        list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid Padding="4,6">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="*"/>
                        <ColumnDefinition Width="Auto"/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Text="{Binding Tag}" FontWeight="SemiBold"/>
                    <TextBlock Grid.Column="1" Text="{Binding Count}" Opacity="0.6"/>
                </Grid>
            </DataTemplate>
            """);

        panel.Children.Add(Description("Rename, merge, or delete tags across your library."));
        panel.Children.Add(list);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        panel.Children.Add(actions);

        var dialog = new ContentDialog { RequestedTheme = Root.ActualTheme, XamlRoot = Root.XamlRoot, Title = "Tag Manager", Content = panel, CloseButtonText = "Close" };

        Func<Task>? pending = null;
        actions.Children.Add(ActionButton("Rename / Merge", () =>
        {
            if (list.SelectedItem is TagCount tc)
            {
                pending = async () =>
                {
                    var newName = await Prompt("Rename Tag", $"Rename '{tc.Tag}' (entering an existing tag name will merge them):", tc.Tag);
                    if (!string.IsNullOrWhiteSpace(newName) && newName != tc.Tag)
                    {
                        await catalog.RenameTagAsync(tc.Tag, newName);
                        await Refresh();
                        Notify($"Tag '{tc.Tag}' renamed to '{newName}'.");
                    }
                };
                dialog.Hide();
            }
        }));

        actions.Children.Add(ActionButton("Delete Tag", () =>
        {
            if (list.SelectedItem is TagCount tc)
            {
                pending = async () =>
                {
                    if (await Confirm("Delete tag?", $"Remove tag '{tc.Tag}' from {tc.Count} items?", "Delete"))
                    {
                        await catalog.DeleteTagAsync(tc.Tag);
                        await Refresh();
                        Notify($"Tag '{tc.Tag}' removed.");
                    }
                };
                dialog.Hide();
            }
        }));

        await ShowDialogAsync(dialog);
        if (pending is not null) await pending();
    }

    private async Task OpenDuplicatesFinder()
    {
        Notify("Scanning for duplicate files by size and hash…");
        var duplicates = await Task.Run(() => catalog.FindDuplicatesAsync(SelectedSource));
        if (duplicates.Count == 0)
        {
            Notify("No duplicate files found in the current scope.");
            return;
        }

        var panel = new StackPanel { Spacing = 14, MinWidth = 440 };
        panel.Children.Add(Description($"Found {duplicates.Count:N0} sets of exact duplicate files ({duplicates.Sum(d => d.FileLength * (d.Items.Count - 1)) / (1024.0 * 1024.0):N1} MB potential reclaimed space). Choose an item to reveal in Explorer."));

        var scroll = new ScrollViewer { MaxHeight = 360 };
        var groupList = new StackPanel { Spacing = 12 };
        scroll.Content = groupList;
        panel.Children.Add(scroll);

        var dialog = new ContentDialog { RequestedTheme = Root.ActualTheme, XamlRoot = Root.XamlRoot, Title = "Duplicates Finder", Content = panel, CloseButtonText = "Close" };

        foreach (var group in duplicates.Take(50))
        {
            var box = new Border { Background = (Brush)Application.Current.Resources["LumaControl"], CornerRadius = new CornerRadius(8), Padding = new Thickness(10) };
            var boxContent = new StackPanel { Spacing = 6 };
            boxContent.Children.Add(new TextBlock { Text = $"Size: {group.FormattedSize} · {group.Items.Count} copies", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });

            foreach (var item in group.Items)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var pathText = new TextBlock { Text = item.RelativePath, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, Opacity = 0.85 };
                Grid.SetColumn(pathText, 0);
                row.Children.Add(pathText);

                var revealBtn = new Button { Content = "Reveal", FontSize = 11, Padding = new Thickness(6, 2, 6, 2) };
                revealBtn.Click += (_, _) =>
                {
                    try { ShellFiles.RevealInExplorer(Resolve(item)); } catch (Exception ex) { Notify(ex.Message, true); }
                };
                Grid.SetColumn(revealBtn, 1);
                row.Children.Add(revealBtn);

                boxContent.Children.Add(row);
            }

            box.Child = boxContent;
            groupList.Children.Add(box);
        }

        await ShowDialogAsync(dialog);
    }

    private async Task ResetCatalogAction()
    {
        if (scanning || preparing)
        {
            Notify("Pause scanning or preview work in Activity before resetting the catalog.", true);
            return;
        }
        if (!await Confirm(
            "Reset Luma catalog?",
            "Clears Luma's local database of registered sources, tags, favorites, and albums. Your original files and folders stay 100% untouched. Tip: choose Back up catalog first if you want a snapshot.",
            "Reset catalog")) return;

        await Task.Run(() => catalog.ResetCatalogAsync());
        sourceSignature = "";
        await LoadSources();
        await LoadAlbums();
        await LoadSmartCollections();
        await Refresh();
        Notify("Catalog reset cleanly. Originals were not touched.");
    }

    private async Task ShowKeyboardShortcutsDialog()
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(Description(
            "Gallery shortcuts:\n" +
            "  Space: Open or close Quick Preview (toggles selection in Select mode)\n" +
            "  Enter: Open selected original in default Windows app (also plays videos)\n" +
            "  Ctrl+F: Focus Search (supports tag:\"Multi Word\", location:\"New York\", year:2025)\n" +
            "  Ctrl+A / Esc: Select loaded items / Clear selection or close overlay\n" +
            "  Ctrl+C / X / V: Copy, Cut, Paste originals\n" +
            "  F2 / Del: Rename or Batch Rename / Move to Recycle Bin\n" +
            "  Ctrl+B / Ctrl+H / F5 / F1: Sidebar / Header / Refresh / Shortcuts\n\n" +
            "Quick Preview shortcuts:\n" +
            "  Left / Right: Step previous or next item\n" +
            "  F / T / M / I / Del: Favorite / Tag / Move / Details / Recycle Bin\n\n" +
            "Capture date precedence & limits:\n" +
            "  Luma reads embedded capture dates (EXIF, QuickTime, XMP) first, then falls back to earliest valid file timestamp.\n" +
            "  While an external drive is offline, cached previews, search, Favorites, tags, and virtual albums remain editable; opening or modifying originals requires reconnecting the drive."
        ));
        var dialog = new ContentDialog
        {
            RequestedTheme = Root.ActualTheme,
            XamlRoot = Root.XamlRoot,
            Title = "Keyboard shortcuts & help",
            Content = new ScrollViewer { MaxHeight = 460, Content = panel },
            PrimaryButtonText = "Send feedback…",
            CloseButtonText = "Close"
        };
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
            await OpenFeedbackIssue();
    }

    private async Task OpenFeedbackIssue()
    {
        var sources = await Task.Run(() => catalog.SourcesAsync());
        var counts = await catalog.SourceCountsAsync();
        var includeDiag = await Confirm(
            "Report a problem or send feedback",
            "Opens GitHub Issues in your default browser with Luma's version, Windows build, and library counts prefilled (personal paths redacted). Luma never sends data automatically.",
            "Open GitHub Issue");
        if (!includeDiag) return;

        string errSnippet = "None";
        var errPath = Path.Combine(AppSettings.LocalRoot, "last-error.txt");
        if (File.Exists(errPath))
        {
            try
            {
                var redacted = PathRedactor.Redact(File.ReadAllText(errPath));
                errSnippet = redacted.Length > 400 ? redacted[..400] + "..." : redacted;
            }
            catch { }
        }

        var body =
            "### What happened\n(Describe what you expected vs what happened)\n\n" +
            "### Environment (auto-filled, paths redacted)\n" +
            $"- App: Luma 0.7.0 ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})\n" +
            $"- Windows: {Environment.OSVersion}\n" +
            $"- .NET: {Environment.Version}\n" +
            $"- Sources: {sources.Count}, Catalog items: {counts.Values.Sum():N0}, Cache ceiling: {settings.CacheGiB} GiB\n" +
            $"- Last redacted error:\n```\n{errSnippet}\n```\n";

        var url = $"https://github.com/smile-plzz/Luma-windows-software/issues/new?template=bug_report.md&body={Uri.EscapeDataString(body)}";
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { Notify("Could not open browser: " + ex.Message, true); }
    }
}
