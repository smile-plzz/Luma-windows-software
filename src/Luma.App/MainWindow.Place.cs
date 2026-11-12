using Luma.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
namespace Luma.App;

public sealed partial class MainWindow
{
    private bool restoringPlace;
    private int loadedStart;
    private readonly DispatcherTimer placeTimer=new() { Interval=TimeSpan.FromSeconds(1) };
    private readonly Stack<BrowsePlace> navigationHistory = new();

    private void PushNavigationHistory()
    {
        if (!ready || restoringPlace) return;
        var current = new BrowsePlace(SelectedSource, FolderFilter.Text, SearchBox.Text, SelectedAlbum, Navigation.SelectedIndex,
            selectedMonth, IsMemories ? MemoryDay.ToString("yyyy-MM-dd") : null);
        if (navigationHistory.Count > 0 && navigationHistory.Peek() == current) return;
        navigationHistory.Push(current);
        if (navigationHistory.Count > 25)
        {
            var trimmed = navigationHistory.Take(25).Reverse().ToArray();
            navigationHistory.Clear();
            foreach (var p in trimmed) navigationHistory.Push(p);
        }
        BackHistoryButton.Visibility = navigationHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void NavigateBack(object sender, RoutedEventArgs e) => await DoNavigateBack();

    private async Task DoNavigateBack()
    {
        if (navigationHistory.Count == 0) return;
        var previous = navigationHistory.Pop();
        BackHistoryButton.Visibility = navigationHistory.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        restoringPlace = true;
        ready = false;
        try
        {
            var choices = Sources.ItemsSource as IEnumerable<SourceChoice>;
            if (choices is not null) Sources.SelectedItem = choices.FirstOrDefault(s => s.Id == previous.SourceId) ?? choices.FirstOrDefault();
            FolderFilter.Text = previous.Folder ?? "";
            SearchBox.Text = previous.Search ?? "";
            Navigation.SelectedIndex = Math.Clamp(previous.Navigation, 0, 4);
            var albums = AlbumsBox.ItemsSource as IEnumerable<Album>;
            if (albums is not null) AlbumsBox.SelectedItem = albums.FirstOrDefault(a => a.Id == previous.AlbumId);
            Heading.Text = IsMemories ? "On this day" : (AlbumsBox.SelectedItem as Album)?.Name ?? Navigation.SelectedItem?.ToString() ?? "Library";
            MemoryDate.Visibility = IsMemories ? Visibility.Visible : Visibility.Collapsed;
            ClearDateRange();
            if (settings.SortIndex is 0 or 1 or 4 && DateTime.TryParseExact(previous.Month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var month))
                SetMonth(month);
        }
        finally { restoringPlace = false; ready = true; }
        await Guard(Refresh);
    }
    private (MediaCard? Card,double Inset) VisibleAnchor()
    {
        if (settings.LayoutMode == "Uniform")
        {
            var found = FindAnchorInHost(MediaGrid, cards.Where(c => c.Realized).Select(c => ((object)c, c)));
            if (found is not null) return found.Value;
        }
        else
        {
            var found = FindAnchorInHost(JustifiedRowsList, justifiedRows.Where(r => r.Items.Count > 0).Select(r => ((object)r, r.Items[0])));
            if (found is not null) return found.Value;
        }
        return (cards.FirstOrDefault(),0);
    }
    private static (MediaCard Card, double Inset)? FindAnchorInHost(ListViewBase host, IEnumerable<(object ContainerItem, MediaCard Card)> items)
    {
        (MediaCard Card, double Inset)? partialTopFallback = null;
        foreach (var (containerItem, card) in items)
        {
            if (host.ContainerFromItem(containerItem) is not FrameworkElement container) continue;
            var y = container.TransformToVisual(host).TransformPoint(new Point(0, 0)).Y;
            if (y >= 0 && y < host.ActualHeight) return (card, y);
            if (partialTopFallback is null && y + container.ActualHeight > 0 && y < host.ActualHeight)
                partialTopFallback = (card, y);
        }
        return partialTopFallback;
    }
    private void SchedulePlaceSave()
    {
        if(!ready || restoringPlace || !settings.RememberPlace)return;
        placeTimer.Stop(); placeTimer.Start();
    }
    private void SavePlace()
    {
        placeTimer.Stop();
        if(!ready || restoringPlace || loading || !settings.RememberPlace)return;
        var (card,inset)=VisibleAnchor();
        settings.Place=new(SelectedSource,FolderFilter.Text,SearchBox.Text,SelectedAlbum,Navigation.SelectedIndex,
            selectedMonth,IsMemories ? MemoryDay.ToString("yyyy-MM-dd") : null,card?.Item.Media.SourceId,card?.Item.Media.RelativePath,inset);
        SaveMapPreferencesToSettings();
        try {settings.Save();} catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
    private async Task RestorePlace()
    {
        var place=settings.RememberPlace ? settings.Place : null;
        if(place is null) {ready=true;await Refresh();return;}
        restoringPlace=true;
        string message="Welcome back. Your last view is restored.";
        try
        {
            var choices=((IEnumerable<SourceChoice>)Sources.ItemsSource).ToList();
            if(place.SourceId is not null && !choices.Any(s=>s.Id==place.SourceId))
            {place=new();message="Your previous source was removed. Showing the library.";}
            Sources.SelectedItem=choices.FirstOrDefault(s=>s.Id==place.SourceId) ?? choices[0];
            FolderFilter.Text=place.Folder ?? ""; SearchBox.Text=place.Search ?? "";
            Navigation.SelectedIndex=Math.Clamp(place.Navigation,0,4);
            AlbumsBox.SelectedItem=((IEnumerable<Album>)AlbumsBox.ItemsSource).FirstOrDefault(a=>a.Id==place.AlbumId);
            Heading.Text=IsMemories ? "On this day" : (AlbumsBox.SelectedItem as Album)?.Name ?? Navigation.SelectedItem?.ToString() ?? "Library";
            MemoryDate.Visibility=IsMemories ? Visibility.Visible : Visibility.Collapsed;
            if(IsMemories) { MemoryDate.MaxDate=DateTimeOffset.Now; MemoryDate.Date=DateTime.TryParse(place.MemoryDay,out var day) && day<=DateTime.Today ? new DateTimeOffset(day) : DateTimeOffset.Now; }
            if(settings.SortIndex is 0 or 1 or 4 && DateTime.TryParseExact(place.Month+"-01","yyyy-MM-dd",System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.None,out var month) && month.Year<9999)
                SetMonth(month);
            int offset=0;
            if(place.AnchorSource is not null && place.AnchorPath is not null)
            {
                var query=CurrentQuery();
                var position=await Task.Run(()=>catalog.LocateAsync(query,place.AnchorSource,place.AnchorPath));
                if(position is int found)offset=Math.Max(0,found-60);
                else message="Your previous item is no longer in this view. Showing the first available items.";
            }
            ready=true; await RefreshGallery(false,offset);
            Notify(message);
            var anchor=cards.FirstOrDefault(c=>c.Item.Media.SourceId==place.AnchorSource && c.Item.Media.RelativePath==place.AnchorPath);
            if(anchor is not null)await PositionAnchor(anchor,double.IsFinite(place.Inset) ? Math.Clamp(place.Inset,-1000,1000) : 0);
        }
        finally {restoringPlace=false;ready=true;SchedulePlaceSave();}
    }
    private async Task PositionAnchor(MediaCard anchor,double inset)
    {
        if (settings.LayoutMode == "Uniform")
        {
            await PositionItemInHostAsync(MediaGrid, () => galleryScroll ??= Descendant<ScrollViewer>(MediaGrid), anchor, inset);
        }
        else
        {
            var targetRow = justifiedRows.FirstOrDefault(r => r.Items.Contains(anchor));
            if (targetRow is null) return;
            EnsureAdaptiveScrollHooked();
            await PositionItemInHostAsync(JustifiedRowsList, () => adaptiveScroll ??= Descendant<ScrollViewer>(JustifiedRowsList), targetRow, inset);
        }
    }
    private static async Task PositionItemInHostAsync(ListViewBase host, Func<ScrollViewer?> resolveScroll, object item, double inset)
    {
        host.UpdateLayout();
        host.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
        ScrollViewer? scroll = null;
        FrameworkElement? container = null;
        for (var wait = 0; wait < 6; wait++)
        {
            await Task.Delay(50);
            host.UpdateLayout();
            scroll = resolveScroll();
            container = host.ContainerFromItem(item) as FrameworkElement;
            if (scroll is not null && container is not null) break;
        }
        if (scroll is null || container is null) return;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var y = container.TransformToVisual(host).TransformPoint(new Point(0, 0)).Y;
            if (Math.Abs(y - inset) <= 4) break;
            var target = Math.Clamp(scroll.VerticalOffset + y - inset, 0, Math.Max(0, scroll.ScrollableHeight));
            if (Math.Abs(target - scroll.VerticalOffset) <= 1) break;
            scroll.ChangeView(null, target, null, true);
            await Task.Delay(60);
            host.UpdateLayout();
            container = host.ContainerFromItem(item) as FrameworkElement;
            if (container is null) break;
        }
    }
    private void SetMonth(DateTime month)
    {
        selectedMonth=month.ToString("yyyy-MM",System.Globalization.CultureInfo.InvariantCulture);
        fromTicks=DateTime.SpecifyKind(month,DateTimeKind.Local).ToUniversalTime().Ticks;
        beforeTicks=DateTime.SpecifyKind(month.AddMonths(1),DateTimeKind.Local).ToUniversalTime().Ticks-1;
    }
    private async void EarlierItems(object sender,RoutedEventArgs e)=>await Guard(async ()=>
    {
        if(loadedStart==0 || loading || activeQuery is null)return;
        restoringPlace=true;ViewHost.IsEnabled=false;
        try
        {
            var anchor=VisibleAnchor(); var start=Math.Max(0,loadedStart-120);
            var page=await Task.Run(()=>catalog.QueryAsync(activeQuery with {Offset=start,Limit=loadedStart-start},queryCancellation!.Token));
            loadedStart=start;
            foreach(var item in page.Items.Reverse())cards.Insert(0,new MediaCard(item,settings,IsMemories ? MemoryDay : null));
            if(Grouped)
            {
                groups.Clear();foreach(var card in cards)
                {if(groups.Count==0 || groups[^1].Key!=card.Group)groups.Add(new(card.Group));groups[^1].Add(card);}
            }
            ResumeNavigation.Visibility=loadedStart>0 ? Visibility.Visible : Visibility.Collapsed;
            if(settings.LayoutMode != "Uniform") RebuildAdaptiveRows();
            if(anchor.Card is not null)await PositionAnchor(anchor.Card,anchor.Inset);
            Notify($"Loaded {page.Items.Count:N0} earlier items. Your position is preserved.");
        }
        finally {restoringPlace=false;ViewHost.IsEnabled=true;SchedulePlaceSave();}
    });
    private async void BackToStart(object sender,RoutedEventArgs e)=>await Guard(Refresh);
}
