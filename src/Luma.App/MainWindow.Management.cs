using Luma.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Luma.App;

public sealed partial class MainWindow
{
    private readonly Queue<string> recentActivity=new();
    private static TextBlock Description(string text) => new() { Text=text,TextWrapping=TextWrapping.Wrap,MaxWidth=440 };
    private Button ActionButton(string text,Action action)
    {
        var button=new Button { Content=text,HorizontalAlignment=HorizontalAlignment.Left };
        var help=text switch {
            "Repair missing previews"=>"Create missing previews for this scope. Existing previews and originals are preserved.",
            "Rebuild previews"=>"Regenerate previews. Keep existing previews when originals are offline or generation fails.",
            "Clear selected previews"=>"Remove cached previews for the chosen scope after confirmation. Originals and organization remain.",
            "Clean unused previews"=>"Remove orphaned previews across all sources after confirmation; previews retained for exclusions are kept.",
            "Browse"=>"Open this source from its saved catalog, including while offline.",
            "Remove from Luma"=>"Remove this source's library records and organization after confirmation. Originals remain untouched.",
            "Restore"=>"Remove this exclusion and reveal already cataloged media. Refresh later to discover skipped files.",
            "Exclude file type"=>"Exclude the entered extension across every source after confirmation.",
            "Exclude selected source"=>"Hide the selected registered source and skip its scans until restored.",
            "Exclude a folder…"=>"Choose a folder inside a registered source to exclude along with its subfolders.",
            "Pause current work"=>"Request a safe pause. Saved scan checkpoints and completed metadata batches are retained.",
            "Resume paused indexing"=>"Continue saved discovery checkpoints on connected sources.",
            _=>null };
        if(help is not null) { ToolTipService.SetToolTip(button,help); Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button,help); }
        button.Click+=(_,_)=>action(); return button;
    }
    private async Task RefreshSources(string? id)
    {
        var sources=await Task.Run(()=>catalog.SourcesAsync());
        var connected=sources.Where(s=>(id is null || s.Id==id) && s.IsOnline).ToArray();
        if(connected.Length==0) { Notify("No connected sources to refresh. Reconnect the drive; saved media remains available."); return; }
        var offline=sources.Count(s=>(id is null || s.Id==id) && !s.IsOnline);
        await Scan(connected);
        if(offline>0) Notify($"Refresh finished. {offline} offline source(s) were retained without scanning. See Activity for results.");
    }
    private async void RefreshAllSources(object sender,RoutedEventArgs e) => await Guard(()=>RefreshSources(null),true);
    private async void RefreshSelectedSource(object sender,RoutedEventArgs e) => await Guard(async () =>
    {
        if(SelectedSource is not string id) { Notify("Choose a source first, or use Refresh all connected sources."); return; }
        await RefreshSources(id);
    },true);
    private async void ManageSources(object sender,RoutedEventArgs e) => await Guard(async () =>
    {
        var sources=await Task.Run(()=>catalog.SourcesAsync());
        var counts=await catalog.SourceCountsAsync(); var labels=await catalog.SourceLabelsAsync();
        var checkpoints=await catalog.IndexingCheckpointsAsync();
        var panel=new StackPanel { Spacing=16,MinWidth=400 };
        var dialog=new ContentDialog { RequestedTheme=Root.ActualTheme, XamlRoot=Root.XamlRoot,Title="Manage sources",Content=new ScrollViewer { MaxHeight=440,Content=panel },CloseButtonText="Close" };
        Func<Task>? action=null;
        void Choose(Func<Task> selected) { action=selected; dialog.Hide(); }
        panel.Children.Add(Description("Your folders stay where they are. Removing a source only removes it from Luma."));
        panel.Children.Add(ActionButton("Add folder or drive",()=>Choose(async () =>
        {
            var folder=await PickFolder(); if(folder is null)return;
            var source=await Task.Run(()=>resolver.Register(folder));
            await Scan([new(source.Id,source.RootPath,DateTime.UtcNow,true)],reuseExisting:true);
        })));
        if(sources.Count==0) panel.Children.Add(Description("No sources yet. Add a folder or external drive to begin."));
        foreach(var source in sources)
        {
            var label=labels.GetValueOrDefault(source.Id,Path.GetFileName(source.RootPath.TrimEnd('\\')));
            if(string.IsNullOrWhiteSpace(label))label=source.RootPath;
            var row=new StackPanel { Spacing=6 };
            row.Children.Add(new TextBlock { Text=label,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap });
            row.Children.Add(Description(source.RootPath));
            var checkpoint=checkpoints.FirstOrDefault(c=>c.SourceId==source.Id);
            row.Children.Add(Description($"{(source.IsOnline ? "Connected" : "Offline")} · {counts.GetValueOrDefault(source.Id):N0} items · Last indexed {source.LastSeenUtc.ToLocalTime():g}"+
                (checkpoint is null ? "" : $"\nPaused · {checkpoint.MediaCount:N0} file records saved")));
            var buttons=new StackPanel { Orientation=Orientation.Horizontal,Spacing=4 };
            buttons.Children.Add(ActionButton("Browse",()=>Choose(async ()=>
            {
                ready=false;
                Sources.SelectedItem=((IEnumerable<SourceChoice>)Sources.ItemsSource).FirstOrDefault(s=>s.Id==source.Id);
                FolderFilter.Text=""; SearchBox.Text=""; AlbumsBox.SelectedItem=null; SmartCollectionsBox.SelectedItem=null;
                filterOrientation=null; OrientationFilterBox.SelectedIndex=0;
                filterMinLength=null; MinSizeFilterBox.SelectedIndex=0;
                filterUntaggedOnly=false; UntaggedFilterToggle.IsChecked=false;
                filterHasGpsOnly=false; HasGpsFilterToggle.IsChecked=false;
                activeMapBounds=null; FilterFromDate.Date=null; FilterToDate.Date=null; DayJumpPicker.Date=null; TagFilterBox.SelectedItem=null;
                Navigation.SelectedIndex=0; MemoryDate.Visibility=Visibility.Collapsed; Heading.Text="Library"; ClearDateRange();
                ready=true;
                await Refresh();
            })));
            buttons.Children.Add(ActionButton("Rename",()=>Choose(async ()=>
            {
                var name=await Prompt("Name this source","A label in Luma; the folder itself will not be renamed.",label);
                if(name is null)return; await catalog.LabelSourceAsync(source.Id,name); await LoadSources(); Notify("Source label updated.");
            })));
            var refresh=ActionButton(checkpoint is null ? "Refresh" : "Resume",()=>Choose(()=>RefreshSources(source.Id))); refresh.IsEnabled=source.IsOnline; buttons.Children.Add(refresh);
            buttons.Children.Add(ActionButton("Remove from Luma",()=>Choose(()=>RemoveManagedSource(source.Id))));
            var more=new Button { Content="More",Flyout=new MenuFlyout() };
            var menu=(MenuFlyout)more.Flyout;
            var metadata=new MenuFlyoutItem { Text="Refresh metadata",IsEnabled=source.IsOnline };
            metadata.Click+=(_,_)=>Choose(()=>RefreshMetadataFor(source.Id)); menu.Items.Add(metadata);
            var restart=new MenuFlyoutItem { Text="Restart indexing",IsEnabled=source.IsOnline };
            restart.Click+=(_,_)=>Choose(async ()=>
            {
                if(!await Confirm("Restart indexing?",$"Discard the saved indexing checkpoint for {source.RootPath} and start a fresh scan? The published gallery and originals stay intact.","Restart"))return;
                await catalog.DiscardIndexingAsync(source.Id); await RefreshSources(source.Id);
            }); menu.Items.Add(restart);
            var reconcile = new MenuFlyoutItem { Text = "Reconcile moved files", IsEnabled = source.IsOnline };
            reconcile.Click += (_, _) => Choose(async () =>
            {
                Notify($"Checking {source.RootPath} for externally moved files…");
                var report = await Task.Run(() => catalog.ReconcileExternalMovesAsync(source.Id));
                if (report.Count > 0)
                {
                    await LoadAlbums();
                    await Refresh();
                    Notify($"Reconciled {report.Count} moved file(s). Organization, favorites, and albums preserved!");
                }
                else
                {
                    Notify("No moved files detected in this source.");
                }
            });
            menu.Items.Add(reconcile);
            row.Children.Add(buttons); row.Children.Add(more); panel.Children.Add(row);
        }
        await ShowDialogAsync(dialog); if(action is not null) await action();
    },true);

    private async Task RemoveManagedSource(string id)
    {
        var sources=await Task.Run(()=>catalog.SourcesAsync()); var source=sources.FirstOrDefault(s=>s.Id==id); if(source is null)return;
        var count=(await catalog.SourceCountsAsync()).GetValueOrDefault(id);
        var keys=await catalog.PreviewKeysAsync(id);
        var estimate=cache is null ? new CacheRemoval(0,0) : await UseCache(()=>cache.InspectRemovalAsync(keys));
        var removePreviews=new CheckBox { Content=$"Also remove {estimate.Count:N0} current previews ({estimate.Bytes/1048576d:N1} MiB)",IsChecked=false };
        var content=new StackPanel { Spacing=14 };
        content.Children.Add(Description($"Remove {source.RootPath} from Luma?\n\n{count:N0} catalog items, their favorites, tags and album memberships will be removed. Original files will not be deleted or moved. Adding it again will scan it as a new source."));
        content.Children.Add(removePreviews);
        var dialog=new ContentDialog { RequestedTheme=Root.ActualTheme, XamlRoot=Root.XamlRoot,Title="Remove source from Luma?",Content=content,PrimaryButtonText="Remove from Luma",CloseButtonText="Cancel",DefaultButton=ContentDialogButton.Close };
        if(await ShowDialogAsync(dialog)!=ContentDialogResult.Primary)return;
        try
        {
            var backupDir = Path.Combine(AppSettings.LocalRoot, "backups");
            Directory.CreateDirectory(backupDir);
            await catalog.CreateBackupAsync(Path.Combine(backupDir, $"catalog-pre-remove-{DateTime.UtcNow:yyyyMMddHHmmss}.db"));
        }
        catch { }
        await catalog.RemoveSourceAsync(id);
        if(SelectedSource==id) { var wasReady=ready; ready=false; FolderFilter.Text=""; ClearDateRange(); ready=wasReady; }
        await LoadSources(); await LoadAlbums(); await Refresh();
        if(removePreviews.IsChecked==true && cache is not null)
            await MaintainCache(async ()=> { var removed=await cache.RemoveAsync(keys); DispatcherQueue.TryEnqueue(()=>Notify($"Removed source and {removed.Count:N0} previews. Originals are untouched.")); });
        else Notify("Source removed from Luma. Originals are untouched. Storage cleanup can remove its unused previews.");
    }

    private async void StorageSettings(object sender,RoutedEventArgs e) => await Guard(OpenStorage,true);
    private async Task OpenStorage()
    {
        if(cache is null)return;
        var sources=await Task.Run(()=>catalog.SourcesAsync()); var labels=await catalog.SourceLabelsAsync();
        var choices=new List<SourceChoice> { new(null,"All sources") };
        choices.AddRange(sources.Select(s=>new SourceChoice(s.Id,labels.GetValueOrDefault(s.Id,s.RootPath)+(s.IsOnline ? "" : " · Offline"))));
        var scope=new ComboBox { Header="Applies to",ItemsSource=choices,SelectedItem=choices.FirstOrDefault(c=>c.Id==SelectedSource) ?? choices[0],HorizontalAlignment=HorizontalAlignment.Stretch };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(scope,"StorageScope");
        var stats=Description(""); Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(stats,"StorageCoverage");
        async Task UpdateStats()
        {
            var id=(scope.SelectedItem as SourceChoice)?.Id;
            var coverage=await UseCache(()=>Preparation.InspectAsync(id));
            if((scope.SelectedItem as SourceChoice)?.Id==id) stats.Text=$"{coverage.Cached:N0} / {coverage.Total:N0} current previews saved\nTotal cache: {coverage.CacheBytes/1048576d:N1} / {coverage.CacheLimitBytes/1048576d:N0} MiB";
        }
        scope.SelectionChanged+=async (_,_)=>await Guard(UpdateStats);
        var panel=new StackPanel { Spacing=12,MinWidth=400 }; panel.Children.Add(scope); panel.Children.Add(stats);
        panel.Children.Add(Description("Repair creates missing previews. Rebuild replaces previews only after successful generation, preserving offline previews. Neither changes your originals or organization."));
        var dialog=new ContentDialog { RequestedTheme=Root.ActualTheme, XamlRoot=Root.XamlRoot,Title="Storage & previews",Content=panel,CloseButtonText="Close" };
        Func<Task>? action=null;
        void Choose(Func<string?,Task> selected) { var id=(scope.SelectedItem as SourceChoice)?.Id; action=()=>selected(id); dialog.Hide(); }
        panel.Children.Add(ActionButton("Repair missing previews",()=>Choose(id=>RunPreviewMaintenance(id,false))));
        panel.Children.Add(ActionButton("Rebuild previews",()=>Choose(id=>RunPreviewMaintenance(id,true))));
        panel.Children.Add(ActionButton("Clear selected previews",()=>Choose(ClearScopedPreviews)));
        panel.Children.Add(ActionButton("Clean unused previews",()=>Choose(_=>CleanUnusedPreviews())));
        panel.Children.Add(Description("Cleanup covers all sources and removes only previews no longer referenced by the catalog. Cache size is configurable in Settings."));
        await UpdateStats(); await ShowDialogAsync(dialog); if(action is not null)await action();
    }

    private async Task MaintainCache(Func<Task> action)
    {
        clearing=true; queryCancellation?.Cancel();
        try
        {
            try { await Task.WhenAll(SnapshotCacheTasks()); } catch(OperationCanceledException) { }
            await UseCache(async ()=> { await action(); return true; });
        }
        finally
        {
            clearing=false;
            if(ready) { queryCancellation=new(); foreach(var card in cards)card.Thumbnail=null; foreach(var card in cards.Where(c=>c.Realized).ToArray()) _=LoadThumbnail(card); }
        }
    }
    private async Task RunPreviewMaintenance(string? id,bool rebuild)
    {
        if (!DiskGuard.HasSufficientSpace())
        {
            Notify("Low disk space (< 500 MB free). Free space on your system drive before generating offline previews.", true);
            return;
        }
        var targetSource = id is null ? null : (await catalog.SourcesAsync()).FirstOrDefault(s => s.Id == id);
        if (id is not null && targetSource is null) { Notify("Selected source is no longer registered."); return; }
        var scope = targetSource?.RootPath ?? "all sources";
        if(rebuild && !await Confirm("Rebuild previews?",$"Regenerate previews for {scope}. Offline or failed previews will be kept. This can take time; you can cancel and run it again.","Rebuild"))return;
        preparing=true; preparationCancellation=new(); CancelButton.Visibility=Visibility.Visible; Busy.IsActive=true;
        try
        {
            await MaintainCache(async ()=>
            {
                var progress=new Progress<long>(n=>DispatcherQueue.TryEnqueue(()=>Status.Text=$"{(rebuild ? "Rebuilding" : "Repairing")} previews · {n:N0} checked · {scope}"));
                string message;
                if(rebuild)
                {
                    var result=await Task.Run(()=>Preparation.RebuildAsync(id,progress,preparationCancellation.Token),preparationCancellation.Token);
                    message=$"Rebuilt {result.Updated:N0} of {result.Checked:N0} previews. Offline, unsupported or capacity-limited previews were preserved.";
                }
                else
                {
                    var result=await Task.Run(()=>Preparation.PrepareAsync(id,progress,preparationCancellation.Token),preparationCancellation.Token);
                    message=$"Repair finished: {result.Cached:N0} / {result.Total:N0} previews saved. Remaining gaps may need a connected source, supported codec or more space.";
                }
                DispatcherQueue.TryEnqueue(()=>Notify(message));
            });
        }
        catch(OperationCanceledException) { Notify("Preview work paused. Completed previews are saved; retry Repair or Rebuild from Storage & previews."); }
        finally { preparing=false; if(ready) { CancelButton.Visibility=Visibility.Collapsed; Busy.IsActive=false; Status.Text="Preview maintenance finished"; } }
    }
    private async Task ClearScopedPreviews(string? id)
    {
        var keys=id is null ? (await cache!.SnapshotAsync()).Keys : await catalog.PreviewKeysAsync(id);
        var estimate=await cache!.InspectRemovalAsync(keys);
        var scope=id is null ? "all sources" : (await catalog.SourcesAsync()).First(s=>s.Id==id).RootPath;
        if(!await Confirm("Clear saved previews?",$"Remove {estimate.Count:N0} previews ({estimate.Bytes/1048576d:N1} MiB) for {scope}? Offline previews will be lost until the source reconnects. Originals, catalog, tags and albums remain intact.","Clear previews"))return;
        await MaintainCache(async ()=> { var result=await cache.RemoveAsync(keys); DispatcherQueue.TryEnqueue(()=>Notify($"Cleared {result.Count:N0} previews. Originals and organization are intact.")); });
    }
    private async Task CleanUnusedPreviews()
    {
        var current=await catalog.PreviewKeysAsync(); var snapshot=await cache!.SnapshotAsync();
        var unused=snapshot.Keys.Where(k=>!current.Contains(k)).ToHashSet(StringComparer.Ordinal);
        var estimate=await cache.InspectRemovalAsync(unused);
        if(estimate.Count==0) { Notify("No unused previews to clean up."); return; }
        if(!await Confirm("Clean unused previews?",$"Free {estimate.Bytes/1048576d:N1} MiB by removing {estimate.Count:N0} previews for older file versions or removed sources. Current previews, including offline sources, are retained.","Clean up"))return;
        await MaintainCache(async ()=> { var result=await cache.RemoveAsync(unused); DispatcherQueue.TryEnqueue(()=>Notify($"Cleaned {result.Count:N0} unused previews · {result.Bytes/1048576d:N1} MiB freed.")); });
    }

    private async void ShowActivity(object sender,RoutedEventArgs e) => await Guard(async () =>
    {
        var checkpoints=await catalog.IndexingCheckpointsAsync(); var warnings=await catalog.IndexingWarningsAsync();
        var sources=await catalog.SourcesAsync();
        string Name(string id)=>sources.FirstOrDefault(s=>s.Id==id)?.RootPath ?? id;
        var live=Description(ActivityStatus()); var panel=new StackPanel { Spacing=12,MinWidth=400 }; panel.Children.Add(live);
        var pause=ActionButton("Pause current work",()=> { CancelScan(sender,e); });
        panel.Children.Add(pause);
        panel.Children.Add(Description(string.Join("\n",checkpoints.Select(c=>$"Paused: {Name(c.SourceId)} · {c.MediaCount:N0} file records saved"))));
        panel.Children.Add(Description(string.Join("\n",warnings.Select(w=>$"Skipped {Name(w.SourceId)} / {w.Folder}: {w.Message}"))));
        panel.Children.Add(Description("Recent activity this session\n"+string.Join("\n\n",recentActivity.Reverse())));
        var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(500) };
        timer.Tick+=(_,_)=> { live.Text=ActivityStatus(); pause.IsEnabled=scanning || preparing; };
        pause.IsEnabled=scanning || preparing;
        var dialog=new ContentDialog { RequestedTheme=Root.ActualTheme, XamlRoot=Root.XamlRoot,Title="Activity",Content=new ScrollViewer { MaxHeight=440,Content=panel },CloseButtonText="Close" };
        var resume=ActionButton("Resume paused indexing",()=>dialog.Hide());
        bool retry=false; resume.Click+=(_,_)=>retry=true; resume.IsEnabled=!operation && checkpoints.Count>0; panel.Children.Add(resume);
        timer.Start(); try { await ShowDialogAsync(dialog); } finally { timer.Stop(); }
        if(retry) ResumeIndexing(sender,e);
    });
}
