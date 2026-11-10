using System.Collections.ObjectModel;
using Luma.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Luma.App;
public sealed partial class MainWindow
{
    private readonly ObservableCollection<MediaGroup> groups = new();
    private ScrollViewer? galleryScroll;
    private LibraryQuery? activeQuery;
    private GalleryCursor? cursor;
    private bool loading, exhausted;
    private int peakDecoded;
    private long? beforeTicks, fromTicks;
    private string? selectedMonth;
    private IReadOnlyList<MonthBucket> monthBuckets = Array.Empty<MonthBucket>();
    private LibraryQuery? monthQuery;
    private long libraryTotal;
    private bool IsMemories => Navigation.SelectedItem?.ToString() == "On this day";
    private DateOnly MemoryDay => DateOnly.FromDateTime((MemoryDate.Date ?? DateTimeOffset.Now).LocalDateTime);
    private bool Grouped => settings.GroupIndex > 0 && settings.SortIndex is 0 or 1 or 4;
    private string? SelectedAlbum => (AlbumsBox.SelectedItem as Album)?.Id;

    private void ApplyViewSettings()
    {
        settings.SortIndex=Math.Clamp(settings.SortIndex,0,5); settings.GroupIndex=Math.Clamp(settings.GroupIndex,0,3); settings.ThumbnailSize=Math.Clamp(settings.ThumbnailSize,120,320);
        SortBox.SelectedIndex = settings.SortIndex;
        GroupBox.SelectedIndex = Math.Clamp(settings.GroupIndex,0,3);
        DescendingButton.IsChecked = settings.Descending;
        ThumbnailSlider.Value = Math.Clamp(settings.ThumbnailSize,120,320);
        NamesToggle.IsChecked = settings.ShowNames; DetailsToggle.IsChecked = settings.ShowDetails;
        FitToggle.IsChecked = settings.FitImages; CompactToggle.IsChecked = settings.Compact;
        CommonToggle.IsChecked = settings.CommonOnly; RecursiveToggle.IsChecked = settings.IncludeDescendants;
    }
    private LibraryQuery CurrentQuery() => new(SelectedSource,SearchBox.Text.Trim(),Navigation.SelectedItem?.ToString() == "Photos" ? "photo" : Navigation.SelectedItem?.ToString() == "Videos" ? "video" : null,
        Navigation.SelectedItem?.ToString() == "Favorites",(MediaSort)settings.SortIndex,0,120,FolderFilter.Text.Trim(),settings.Descending,SelectedAlbum,settings.IncludeDescendants,settings.CommonOnly,beforeTicks,MemoriesOn:IsMemories ? MemoryDay : null,FromTicks:fromTicks);

    private readonly SemaphoreSlim galleryGate = new(1, 1);

    private Task Refresh() => RefreshGallery(false);
    private async Task RefreshGallery(bool reuseMonths,int startOffset=0)
    {
        if (!ready) return;
        await galleryGate.WaitAsync();
        try
        {
            queryCancellation?.Cancel(); queryCancellation = new(); var token = queryCancellation.Token;
            loadedStart=startOffset; ResumeNavigation.Visibility=loadedStart>0 ? Visibility.Visible : Visibility.Collapsed;
            cursor = null; exhausted = false; loading = false;
            foreach(var card in cards) { card.Realized=false; card.Thumbnail=null; card.ThumbnailRequest?.Cancel(); card.ThumbnailRequest=null; }
            cards.Clear(); groups.Clear(); activeQuery = CurrentQuery();
            if (Grouped) MediaGrid.ItemsSource = new CollectionViewSource { IsSourceGrouped=true,Source=groups }.View;
            else MediaGrid.ItemsSource = cards;
            galleryScroll?.ChangeView(null,0,null,true);
            EmptyState.Visibility=Visibility.Collapsed; Busy.IsActive=true;
            if(!scanning && !preparing)Status.Text="Loading moments…";

            libraryTotal = (await Task.Run(() => catalog.SourceCountsAsync(token), token)).Values.Sum();
            token.ThrowIfCancellationRequested();
            BuildFilterChips();

            // Render first page immediately for sub-second first paint
            await LoadMore(); token.ThrowIfCancellationRequested();

            // Defer month navigation calculations off critical paint path
            var navQuery = activeQuery with { BeforeTicks = null, FromTicks = null };
            _ = Task.Run(async () =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    IReadOnlyList<MonthBucket>? buckets = null;
                    if (!reuseMonths || navQuery != monthQuery)
                    {
                        buckets = await catalog.MonthBucketsAsync(navQuery, token);
                    }

                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested) return;
                        if (buckets is not null)
                        {
                            monthBuckets = buckets;
                            monthQuery = navQuery;
                            var wasReady = ready; ready = false;
                            DateJump.ItemsSource = monthBuckets;
                            DateJump.SelectedItem = monthBuckets.FirstOrDefault(m => m.Month == selectedMonth);
                            MonthBrowser.Visibility = !IsMemories && settings.SortIndex is 0 or 1 or 4 ? Visibility.Visible : Visibility.Collapsed;
                            var monthIndex = monthBuckets.ToList().FindIndex(m => m.Month == selectedMonth);
                            NewerMonthButton.IsEnabled = monthIndex > 0;
                            OlderMonthButton.IsEnabled = monthBuckets.Count > 0 && monthIndex < monthBuckets.Count - 1;
                            AllMonthsButton.IsEnabled = selectedMonth is not null;
                            ready = wasReady;
                        }
                        UpdateEmptyState();
                    });
                }
                catch (OperationCanceledException) { }
            }, token);

            await LoadFolders(token);
            SchedulePlaceSave();
        }
        catch (OperationCanceledException) { }
        finally
        {
            galleryGate.Release();
        }
    }
    private async Task LoadMore()
    {
        if (!ready || loading || exhausted || activeQuery is null || queryCancellation is null) return;
        var generation = queryCancellation; var ct = generation.Token; var query = activeQuery with { Offset=cursor is null ? loadedStart : 0,Cursor=cursor,KnownTotal=cursor is null ? null : total };
        loading = true; Busy.IsActive = true;
        try
        {
            var page = await Task.Run(() => catalog.QueryAsync(query,ct),ct); ct.ThrowIfCancellationRequested();
            total = page.Total; cursor = page.Next;
            foreach(var item in page.Items)
            {
                var card = new MediaCard(item,settings,IsMemories ? MemoryDay : null) { IsCut=pendingCutIds.Contains(item.Id) }; cards.Add(card);
                if (Grouped)
                {
                    if (groups.Count == 0 || groups[^1].Key != card.Group) groups.Add(new(card.Group));
                    groups[^1].Add(card);
                }
            }
            exhausted = page.Items.Count < query.Limit || loadedStart + cards.Count >= total;
            UpdateEmptyState();
            LibrarySummary.Text = IsMemories ? $"{MemoryDay:MMMM d} · {total:N0} memories · {libraryTotal:N0} library items" : ActiveFilters.Children.Count>0 ? $"{total:N0} of {libraryTotal:N0} items · Filtered" : $"{total:N0} items";
            if (!scanning && !preparing) Status.Text = total == 0 ? "" : exhausted ? "You’re all caught up" : "Scroll to explore";
        }
        catch(OperationCanceledException) { }
        finally { if (generation == queryCancellation) { loading=false; Busy.IsActive=scanning || preparing; } }
    }
    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i); if(child is T result) return result;
            if(Descendant<T>(child) is T nested) return nested;
        }
        return null;
    }
    private void GalleryLoaded(object sender,RoutedEventArgs e)
    {
        galleryScroll = Descendant<ScrollViewer>(MediaGrid);
        if(galleryScroll is not null) galleryScroll.ViewChanged += async (_,_) =>
        {
            SchedulePlaceSave();
            if(ready && !restoringPlace && galleryScroll.VerticalOffset + galleryScroll.ViewportHeight >= galleryScroll.ExtentHeight - 600) await Guard(LoadMore);
        };
    }
    private async void TileChanging(ListViewBase sender,ContainerContentChangingEventArgs e)
    {
        if(e.ItemContainer.Tag is MediaCard previous && (e.InRecycleQueue || !ReferenceEquals(previous,e.Item)))
        { previous.Realized=false; previous.Thumbnail=null; previous.ThumbnailRequest?.Cancel(); previous.ThumbnailRequest=null; }
        e.ItemContainer.Tag = e.InRecycleQueue ? null : e.Item;
        if(e.Item is not MediaCard card) return;
        card.Realized = !e.InRecycleQueue;
        if(e.InRecycleQueue) { card.Thumbnail=null; return; }
        await LoadThumbnail(card);
    }
    private async Task LoadThumbnail(MediaCard card)
    {
        if(!ready || clearing || !card.Realized || card.ThumbnailRequest is not null || card.Thumbnail is not null || queryCancellation is null) return;
        if (!DiskGuard.HasSufficientSpace()) return;
        var request=CancellationTokenSource.CreateLinkedTokenSource(queryCancellation.Token); card.ThumbnailRequest=request; var ct=request.Token;
        try
        {
            var bytes=await UseCache(() => thumbnails!.GetAsync(card.Item.Media,256,ct));
            if(bytes is null || !card.Realized || ct.IsCancellationRequested) return;
            using var stream=new InMemoryRandomAccessStream();
            using(var writer=new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync(); }
            stream.Seek(0);
            var bitmap=new BitmapImage
            {
                DecodePixelWidth = (int)Math.Max(card.TileWidth, 160),
                DecodePixelType = DecodePixelType.Logical
            };
            await bitmap.SetSourceAsync(stream);
            if(card.Realized && !ct.IsCancellationRequested) card.Thumbnail=bitmap;
            if(AppSettings.IsSmokeTest)
            {
                peakDecoded=Math.Max(peakDecoded,cards.Count(c=>c.Thumbnail is not null));
                File.WriteAllText(Path.Combine(AppSettings.LocalRoot,"gallery-metrics.json"),System.Text.Json.JsonSerializer.Serialize(new { Loaded=cards.Count,Decoded=cards.Count(c=>c.Thumbnail is not null),PeakDecoded=peakDecoded,ManagedBytes=GC.GetTotalMemory(false) }));
            }
        }
        catch(OperationCanceledException) { }
        catch(Exception) { /* A missing codec or corrupt preview keeps the accessible placeholder. */ }
        finally { if(ReferenceEquals(card.ThumbnailRequest,request))card.ThumbnailRequest=null; request.Dispose(); }
    }
    private void BuildFilterChips()
    {
        ActiveFilters.Children.Clear();
        void Add(string label,Action clear)
        {
            var button=new Button { Content=label+" ×",MaxWidth=220 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button,"Remove filter: "+label);
            ToolTipService.SetToolTip(button,label);
            button.Click+=async (_,_)=>
            {
                ready=false;
                try { clear(); settings.Save(); }
                finally { ready=true; }
                await Guard(Refresh);
            };
            ActiveFilters.Children.Add(button);
        }
        if(SelectedSource is not null) Add("Source: "+((SourceChoice)Sources.SelectedItem).Label,()=> { Sources.SelectedIndex=0; FolderFilter.Text=""; });
        if(FolderFilter.Text.Length>0)Add("Folder: "+FolderFilter.Text,()=>FolderFilter.Text="");
        if(SearchBox.Text.Trim().Length>0)Add("Search: "+SearchBox.Text.Trim(),()=>SearchBox.Text="");
        if(SelectedAlbum is not null)Add("Album: "+((Album)AlbumsBox.SelectedItem).Name,()=> { AlbumsBox.SelectedItem=null; Heading.Text=Navigation.SelectedItem?.ToString() ?? "Library"; });
        if(settings.CommonOnly)Add("Everyday formats only",()=> { settings.CommonOnly=false; CommonToggle.IsChecked=false; });
        if(!settings.IncludeDescendants)Add("This folder only",()=> { settings.IncludeDescendants=true; RecursiveToggle.IsChecked=true; });
        if(Navigation.SelectedIndex>0)Add(Navigation.SelectedItem?.ToString() ?? "Category",()=> { Navigation.SelectedIndex=0; Heading.Text="Library"; MemoryDate.Visibility=Visibility.Collapsed; });
        if(beforeTicks is not null)Add(selectedMonth is null ? "Date limit" : "Month: "+monthBuckets.FirstOrDefault(m=>m.Month==selectedMonth)?.Label,ClearDateRange);
        FilterBar.Visibility=ActiveFilters.Children.Count>0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private async void ShowTimeline(object sender,RoutedEventArgs e)
    {
        if(!ready)return;
        ready=false; Navigation.SelectedIndex=0; Heading.Text="Library"; MemoryDate.Visibility=Visibility.Collapsed;
        settings.SortIndex=4; settings.GroupIndex=2; settings.Descending=true; ApplyViewSettings(); settings.Save(); ready=true;
        ClearDateRange(); await Guard(Refresh);
    }
    private async void NavigationChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!ready || restoringPlace) return; Heading.Text=Navigation.SelectedItem?.ToString() ?? "Library";
        ready=false;
        MemoryDate.Visibility = IsMemories ? Visibility.Visible : Visibility.Collapsed;
        if (IsMemories) { MemoryDate.MaxDate=DateTimeOffset.Now; MemoryDate.Date=DateTimeOffset.Now; settings.GroupIndex=1; GroupBox.SelectedIndex=1; settings.Descending=true; DescendingButton.IsChecked=true; }
        if(IsMemories)
        {
            settings.SortIndex=4; SortBox.SelectedIndex=4;
            if(settings.GroupIndex==0) { settings.GroupIndex=2; GroupBox.SelectedIndex=2; }
        }
        AlbumsBox.SelectedItem=null; ready=true; settings.Save(); ClearDateRange(); await Guard(Refresh);
    }
    private async void MemoryDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if(!ready || restoringPlace || !IsMemories) return;
        ClearDateRange(); await Guard(Refresh);
    }
    private async void FilterChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!ready || restoringPlace) return;
        if(ReferenceEquals(sender,Sources)) FolderFilter.Text="";
        settings.SortIndex=Math.Max(0,SortBox.SelectedIndex);
        if(settings.SortIndex==1) { settings.Descending=false; DescendingButton.IsChecked=false; }
        settings.Save(); ClearDateRange(); await Guard(Refresh);
    }
    private async void SearchChanged(object sender,TextChangedEventArgs e)
    {
        if(!ready || restoringPlace) return; searchCancellation?.Cancel(); var cts=new CancellationTokenSource(); searchCancellation=cts;
        try { await Task.Delay(300,cts.Token); ClearDateRange(); await Guard(Refresh); } catch(OperationCanceledException) { }
    }
    private async void DirectionChanged(object sender,RoutedEventArgs e)
    { if(!ready || restoringPlace)return; settings.Descending=DescendingButton.IsChecked==true; settings.Save(); await Guard(Refresh); }
    private async void GroupChanged(object sender,SelectionChangedEventArgs e)
    { if(!ready || restoringPlace)return; settings.GroupIndex=Math.Max(0,GroupBox.SelectedIndex); settings.Save(); await Guard(Refresh); }
    private void ThumbnailSizeChanged(object sender,RangeBaseValueChangedEventArgs e)
    { if(!ready)return; settings.ThumbnailSize=(int)e.NewValue; UpdateAppearance(); }
    private void AppearanceChanged(object sender,RoutedEventArgs e)
    {
        if(!ready)return; settings.ShowNames=NamesToggle.IsChecked==true; settings.ShowDetails=DetailsToggle.IsChecked==true;
        settings.FitImages=FitToggle.IsChecked==true; settings.Compact=CompactToggle.IsChecked==true; UpdateAppearance();
    }
    private void UpdateAppearance()
    {
        var anchor=cards.FirstOrDefault(c=>c.Realized);
        foreach(var card in cards) card.UpdateView(); settings.Save();
        if(anchor is not null) MediaGrid.ScrollIntoView(anchor,ScrollIntoViewAlignment.Leading);
    }
    private async void ContentFilterChanged(object sender,RoutedEventArgs e)
    { if(!ready || restoringPlace)return; settings.CommonOnly=CommonToggle.IsChecked==true; settings.IncludeDescendants=RecursiveToggle.IsChecked==true; settings.Save(); await Guard(Refresh); }
    private void ClearDateRange() { beforeTicks=null; fromTicks=null; selectedMonth=null; }
    private async void JumpDate(object sender,SelectionChangedEventArgs e)
    {
        if(!ready || restoringPlace || DateJump.SelectedItem is not MonthBucket month)return;
        await ChooseMonth(month);
    }
    private async Task ChooseMonth(MonthBucket month)
    {
        searchCancellation?.Cancel();
        var date=DateTime.ParseExact(month.Month+"-01","yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture);
        SetMonth(date);
        await Guard(()=>RefreshGallery(true));
    }
    private async void ClearMonth(object sender,RoutedEventArgs e) { ClearDateRange(); await Guard(()=>RefreshGallery(true)); }
    private async void NewerMonth(object sender,RoutedEventArgs e)
    {
        var index=monthBuckets.ToList().FindIndex(m=>m.Month==selectedMonth);
        if(index>0)await ChooseMonth(monthBuckets[index-1]);
    }
    private async void OlderMonth(object sender,RoutedEventArgs e)
    {
        var index=monthBuckets.ToList().FindIndex(m=>m.Month==selectedMonth);
        if(index+1<monthBuckets.Count)await ChooseMonth(monthBuckets[index+1]);
    }
    private void SelectModeChanged(object sender,RoutedEventArgs e)
    {
        if(!ready)return;
        MediaGrid.SelectionMode=SelectMode.IsChecked==true ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended;
        SelectionHint.Visibility=SelectMode.IsChecked==true && MediaGrid.SelectedItems.Count==0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void SelectLoaded(object sender,RoutedEventArgs e) => MediaGrid.SelectAll();
    private void ClearSelection(object sender,RoutedEventArgs e) => MediaGrid.SelectedItems.Clear();
    private async void ResetFilters(object sender,RoutedEventArgs e)
    {
        ready=false; SearchBox.Text=""; FolderFilter.Text=""; AlbumsBox.SelectedItem=null; Sources.SelectedIndex=0; Navigation.SelectedIndex=0;
        settings.CommonOnly=false; settings.IncludeDescendants=true; CommonToggle.IsChecked=false; RecursiveToggle.IsChecked=true; settings.Save(); ready=true;
        Heading.Text="Library"; MemoryDate.Visibility=Visibility.Collapsed; ClearDateRange(); await Guard(Refresh);
    }
    private void InspectorChanged(object sender,RoutedEventArgs e) { if(ready) Inspector.Visibility=InspectorToggle.IsChecked==true ? Visibility.Visible : Visibility.Collapsed; }
    private void GalleryWheel(object sender,PointerRoutedEventArgs e)
    {
        if(!Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(global::Windows.System.VirtualKey.Control).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down))return;
        ThumbnailSlider.Value=Math.Clamp(ThumbnailSlider.Value + Math.Sign(e.GetCurrentPoint(MediaGrid).Properties.MouseWheelDelta)*10,120,320); e.Handled=true;
    }
    private async void SelectAllMatching(object sender,RoutedEventArgs e) => await Guard(async () =>
    {
        if(!await Confirm("Select all matching results?",$"Load and select {total:N0} matching items. File actions will affect this full selection.","Select all"))return;
        var generation=queryCancellation; scanCancellation=new(); CancelButton.Visibility=Visibility.Visible;
        try
        {
            while(!exhausted && ready && generation==queryCancellation && !scanCancellation.IsCancellationRequested)
            { if(loading) { await Task.Delay(50); continue; } await LoadMore(); }
            if(generation!=queryCancellation || scanCancellation.IsCancellationRequested) { Notify("Full-result selection cancelled. No files were changed."); return; }
            MediaGrid.SelectAll();
        }
        finally { CancelButton.Visibility=Visibility.Collapsed; }
    },true);

    private async void UpdateEmptyState()
    {
        if (cards.Count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyState.Visibility = Visibility.Visible;
        EmptyAddButton.Visibility = Visibility.Collapsed;
        EmptyResetButton.Visibility = Visibility.Collapsed;
        EmptyClearSearchButton.Visibility = Visibility.Collapsed;
        EmptyExclusionsButton.Visibility = Visibility.Collapsed;
        EmptyStoreCodecButton.Visibility = Visibility.Collapsed;

        var hasSources = Sources.Items.Count > 0;
        var hasSearch = !string.IsNullOrWhiteSpace(SearchBox.Text);
        var hasActiveFilters = ActiveFilters.Children.Count > 0 || selectedMonth is not null || AlbumsBox.SelectedItem is not null;
        var exclusionsActive = false;
        try { exclusionsActive = (await catalog.ExclusionsAsync()).Count > 0; } catch { }

        if (!hasSources)
        {
            EmptyIcon.Glyph = "\uEB9F";
            EmptyTitle.Text = "A little room for memories";
            EmptyText.Text = "Add a folder or drive to begin. If you already have a library, choose Reset to clear browsing filters.";
            EmptyAddButton.Visibility = Visibility.Visible;
        }
        else if (hasSearch)
        {
            EmptyIcon.Glyph = "\uE721";
            EmptyTitle.Text = "No matching moments";
            EmptyText.Text = "Try another name, folder or tag, or choose Reset to clear browsing filters.";
            if (exclusionsActive) EmptyText.Text += " Exclusions are active. Open Refresh → Manage exclusions to review or restore hidden media.";
            EmptyClearSearchButton.Visibility = Visibility.Visible;
            if (hasActiveFilters) EmptyResetButton.Visibility = Visibility.Visible;
        }
        else if (hasActiveFilters)
        {
            EmptyIcon.Glyph = "\uE71C";
            EmptyTitle.Text = "No moments match these filters";
            EmptyText.Text = "Try loosening your active filters or date range, or choose Reset to return to your full library.";
            EmptyResetButton.Visibility = Visibility.Visible;
        }
        else if (IsMemories)
        {
            EmptyIcon.Glyph = "\uE787";
            EmptyTitle.Text = "No memories for this day yet";
            EmptyText.Text = "Memories look for photos and videos captured on this day in past years. Try picking another date.";
        }
        else if (exclusionsActive)
        {
            EmptyIcon.Glyph = "\uE72E";
            EmptyTitle.Text = "Library media is hidden";
            EmptyText.Text = "Exclusion rules are active for this view. You can review and restore hidden folders and file types.";
            EmptyExclusionsButton.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyIcon.Glyph = "\uEB9F";
            EmptyTitle.Text = "Your library is ready";
            EmptyText.Text = "No media files were found in this source. Refresh to scan or choose another folder.";
            EmptyAddButton.Visibility = Visibility.Visible;
        }
    }

    private void ResetFiltersAction(object sender, RoutedEventArgs e) => ResetFilters(sender, e);
    private void ClearSearchAction(object sender, RoutedEventArgs e) { SearchBox.Text = ""; }
    private void InstallCodecAction(object sender, RoutedEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-windows-store://pdp/?ProductId=9pmmsr1c69tx") { UseShellExecute = true }); } catch { }
    }
    private void EmptyStateDragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
            e.AcceptedOperation = DataPackageOperation.Link;
    }
    private async void EmptyStateDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items)
            {
                if (item is StorageFolder folder)
                {
                    await Guard(async () =>
                    {
                        var source = await Task.Run(() => resolver.Register(folder.Path));
                        await Scan(new[] { new SourceInfo(source.Id, source.RootPath, DateTime.UtcNow, true) }, reuseExisting: true);
                    }, true);
                }
            }
        }
    }
}
