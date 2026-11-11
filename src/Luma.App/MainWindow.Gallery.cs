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
using Windows.System;

namespace Luma.App;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<MediaGroup> groups = new();
    private readonly ObservableCollection<GalleryRow> justifiedRows = new();
    private readonly HashSet<MediaCard> adaptiveSelectedCards = new();
    private ScrollViewer? galleryScroll;
    private ScrollViewer? adaptiveScroll;
    private LibraryQuery? activeQuery;
    private GalleryCursor? cursor;
    private bool loading, exhausted, pendingLoadMore;
    private int suppressFilterEvents;
    private int peakDecoded;
    private long? beforeTicks, fromTicks;
    private string? selectedMonth;
    private IReadOnlyList<MonthBucket> monthBuckets = Array.Empty<MonthBucket>();
    private LibraryQuery? monthQuery;
    private long libraryTotal;
    private bool monthBarDismissed;

    // Visual Filter Fields
    private string? filterOrientation;
    private bool filterUntaggedOnly;
    private long? filterMinLength;
    private bool filterHasGpsOnly;
    private GeoBounds? activeMapBounds;

    private int cachedAlbumsCount, cachedCollectionsCount, cachedSourcesCount;

    private bool IsMemories => Navigation.SelectedItem?.ToString() == "On this day";
    private DateOnly MemoryDay => DateOnly.FromDateTime((MemoryDate.Date ?? DateTimeOffset.Now).LocalDateTime);
    private bool Grouped => settings.GroupIndex > 0 && settings.SortIndex is 0 or 1 or 4;
    private string? SelectedAlbum => (AlbumsBox.SelectedItem as Album)?.Id;

    private void ApplyViewSettings()
    {
        settings.SortIndex = Math.Clamp(settings.SortIndex, 0, 8);
        settings.GroupIndex = Math.Clamp(settings.GroupIndex, 0, 4);
        settings.ThumbnailSize = Math.Clamp(settings.ThumbnailSize, 120, 320);
        SortBox.SelectedIndex = settings.SortIndex;
        GroupBox.SelectedIndex = settings.GroupIndex;
        DescendingButton.IsChecked = settings.Descending;
        ThumbnailSlider.Value = settings.ThumbnailSize;
        NamesToggle.IsChecked = settings.ShowNames;
        DetailsToggle.IsChecked = settings.ShowDetails;
        ShowExactTimeToggle.IsChecked = settings.ShowExactTime;
        FitToggle.IsChecked = settings.FitImages;
        CompactToggle.IsChecked = settings.Compact;
        CommonToggle.IsChecked = settings.CommonOnly;
        RecursiveToggle.IsChecked = settings.IncludeDescendants;
        FavoritesFirstToggle.IsChecked = settings.FavoritesFirst;
        StackSimilarToggle.IsChecked = settings.StackSimilar;
        LayoutModeBox.SelectedIndex = settings.LayoutMode switch
        {
            "Adaptive" => 1,
            "Moments" => 2,
            _ => 0
        };
        ApplyLayoutModeVisibility();
        ApplyCollapsibleState();
    }

    private void ApplyCollapsibleState()
    {
        SidebarColumn.Width = settings.SidebarCollapsed ? new GridLength(0) : new GridLength(224);
        SidebarPanel.Visibility = settings.SidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;

        GalleryToolbarRow.Visibility = settings.HeaderCollapsed ? Visibility.Collapsed : Visibility.Visible;
        HeaderCollapseButton.Content = settings.HeaderCollapsed ? "⌄" : "⌃";

        ViewsSection.Visibility = settings.ViewsExpanded ? Visibility.Visible : Visibility.Collapsed;
        AlbumsSection.Visibility = settings.AlbumsExpanded ? Visibility.Visible : Visibility.Collapsed;
        SmartCollectionsSection.Visibility = settings.SmartCollectionsExpanded ? Visibility.Visible : Visibility.Collapsed;
        SourcesSection.Visibility = settings.SourcesExpanded ? Visibility.Visible : Visibility.Collapsed;
        UpdateSidebarCounts();
    }

    private void UpdateSidebarCounts(int? albumsCount = null, int? collectionsCount = null, int? sourcesCount = null)
    {
        if (albumsCount.HasValue) cachedAlbumsCount = albumsCount.Value;
        if (collectionsCount.HasValue) cachedCollectionsCount = collectionsCount.Value;
        if (sourcesCount.HasValue) cachedSourcesCount = sourcesCount.Value;

        ViewsHeaderLabel.Text = settings.ViewsExpanded ? "▾ Views" : "▸ Views";
        AlbumsHeaderLabel.Text = settings.AlbumsExpanded
            ? "▾ Albums"
            : $"▸ Albums ({cachedAlbumsCount})";
        SmartCollectionsHeaderLabel.Text = settings.SmartCollectionsExpanded
            ? "▾ Smart collections"
            : $"▸ Smart collections ({cachedCollectionsCount})";
        SourcesHeaderLabel.Text = settings.SourcesExpanded
            ? "▾ Folders & drives"
            : $"▸ Folders & drives ({cachedSourcesCount})";
    }

    private void ToggleSidebar(object sender, RoutedEventArgs e)
    {
        settings.SidebarCollapsed = !settings.SidebarCollapsed;
        ApplyCollapsibleState();
        settings.Save();
        if (settings.LayoutMode != "Uniform") RebuildAdaptiveRows();
    }

    private void ToggleHeader(object sender, RoutedEventArgs e)
    {
        settings.HeaderCollapsed = !settings.HeaderCollapsed;
        ApplyCollapsibleState();
        settings.Save();
    }

    private void ToggleViewsSection(object sender, RoutedEventArgs e) { settings.ViewsExpanded = !settings.ViewsExpanded; ApplyCollapsibleState(); settings.Save(); }
    private void ToggleAlbumsSection(object sender, RoutedEventArgs e) { settings.AlbumsExpanded = !settings.AlbumsExpanded; ApplyCollapsibleState(); settings.Save(); }
    private void ToggleSmartCollectionsSection(object sender, RoutedEventArgs e) { settings.SmartCollectionsExpanded = !settings.SmartCollectionsExpanded; ApplyCollapsibleState(); settings.Save(); }
    private void ToggleSourcesSection(object sender, RoutedEventArgs e) { settings.SourcesExpanded = !settings.SourcesExpanded; ApplyCollapsibleState(); settings.Save(); }

    private ScrollViewer? ActiveGalleryScroll() => settings.LayoutMode == "Uniform" ? galleryScroll : adaptiveScroll;

    private void EnsureAdaptiveScrollHooked()
    {
        adaptiveScroll ??= Descendant<ScrollViewer>(JustifiedRowsList);
        if (adaptiveScroll is not null)
        {
            adaptiveScroll.ViewChanged -= OnGalleryScrollViewChanged;
            adaptiveScroll.ViewChanged += OnGalleryScrollViewChanged;
        }
    }

    private void JustifiedListLoaded(object sender, RoutedEventArgs e) => EnsureAdaptiveScrollHooked();

    private void ApplyLayoutModeVisibility()
    {
        var uniform = settings.LayoutMode == "Uniform";
        MediaGrid.Visibility = uniform ? Visibility.Visible : Visibility.Collapsed;
        JustifiedRowsList.Visibility = uniform ? Visibility.Collapsed : Visibility.Visible;
        if (!uniform)
        {
            JustifiedRowsList.ItemsSource = justifiedRows;
            EnsureAdaptiveScrollHooked();
        }
    }

    private async void OnGalleryScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        SchedulePlaceSave();
        UpdateVisibleTimeRange();
        if (!ready || restoringPlace || exhausted) return;
        if (sender is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;

        double threshold = Math.Max(1400, sv.ViewportHeight * 1.8);
        if (sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - threshold)
        {
            if (loading)
            {
                pendingLoadMore = true;
                return;
            }
            await Guard(LoadMore);
        }
    }

    private void LayoutModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || restoringPlace) return;
        settings.LayoutMode = LayoutModeBox.SelectedIndex switch
        {
            1 => "Adaptive",
            2 => "Moments",
            _ => "Uniform"
        };
        settings.Save();
        ApplyLayoutModeVisibility();
        if (settings.LayoutMode != "Uniform")
        {
            RebuildAdaptiveRows();
        }
        else
        {
            foreach (var card in cards.Where(c => c.Realized).ToArray())
                _ = LoadThumbnail(card);
        }
        UpdateVisibleTimeRange();
    }

    private async void StackModeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || restoringPlace) return;
        settings.StackSimilar = StackSimilarToggle.IsChecked == true;
        settings.Save();
        await Guard(Refresh);
    }

    private void RebuildAdaptiveRows()
    {
        if (settings.LayoutMode == "Uniform") return;
        EnsureAdaptiveScrollHooked();
        double savedOffset = adaptiveScroll?.VerticalOffset ?? 0;
        bool hadRows = justifiedRows.Count > 0;

        var width = JustifiedRowsList.ActualWidth > 100 ? JustifiedRowsList.ActualWidth : Math.Max(480, Root.ActualWidth - (settings.SidebarCollapsed ? 40 : 264));
        var built = JustifiedRowBuilder.Build(cards, width, settings.ThumbnailSize, settings.Compact, Grouped, settings.LayoutMode == "Moments");

        // Sync incrementally instead of Clear() so realized rows and scroll position stay intact during infinite scroll
        int samePrefix = 0;
        int minCount = Math.Min(justifiedRows.Count, built.Count);
        while (samePrefix < minCount &&
               string.Equals(justifiedRows[samePrefix].GroupHeader, built[samePrefix].GroupHeader, StringComparison.Ordinal) &&
               justifiedRows[samePrefix].Items.SequenceEqual(built[samePrefix].Items))
        {
            samePrefix++;
        }

        for (int i = samePrefix; i < minCount; i++)
            justifiedRows[i] = built[i];

        while (justifiedRows.Count > built.Count)
            justifiedRows.RemoveAt(justifiedRows.Count - 1);

        for (int i = justifiedRows.Count; i < built.Count; i++)
            justifiedRows.Add(built[i]);

        if (hadRows && savedOffset > 40 && adaptiveScroll is not null && !restoringPlace)
            PreserveScrollAfterAppend(adaptiveScroll, savedOffset, queryCancellation);
    }

    private void JustifiedList_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (settings.LayoutMode != "Uniform") EnsureAdaptiveScrollHooked();
        if (ready && settings.LayoutMode != "Uniform" && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 24)
            RebuildAdaptiveRows();
    }

    private MediaCard[] GetSelectedCards()
    {
        if (settings.LayoutMode == "Uniform")
            return MediaGrid.SelectedItems.Cast<MediaCard>().ToArray();
        return adaptiveSelectedCards.ToArray();
    }

    private MediaCard? PrimarySelectedCard()
    {
        if (settings.LayoutMode == "Uniform")
            return MediaGrid.SelectedItem as MediaCard;
        return adaptiveSelectedCards.FirstOrDefault();
    }

    private void SelectSingleCard(MediaCard card)
    {
        if (settings.LayoutMode == "Uniform")
        {
            MediaGrid.SelectedItem = card;
        }
        else
        {
            foreach (var old in adaptiveSelectedCards) old.IsSelectedAdaptive = false;
            adaptiveSelectedCards.Clear();
            adaptiveSelectedCards.Add(card);
            card.IsSelectedAdaptive = true;
            UpdateSelectionChrome(adaptiveSelectedCards.ToArray());
        }
    }

    private void ClearAllSelections()
    {
        MediaGrid.SelectedItems.Clear();
        foreach (var old in adaptiveSelectedCards) old.IsSelectedAdaptive = false;
        adaptiveSelectedCards.Clear();
        UpdateSelectionChrome(Array.Empty<MediaCard>());
    }

    private void AdaptiveCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaCard card }) return;
        var ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shiftDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (SelectMode.IsChecked == true || ctrlDown)
        {
            if (adaptiveSelectedCards.Contains(card))
            {
                adaptiveSelectedCards.Remove(card);
                card.IsSelectedAdaptive = false;
            }
            else
            {
                adaptiveSelectedCards.Add(card);
                card.IsSelectedAdaptive = true;
            }
        }
        else if (shiftDown && adaptiveSelectedCards.Count > 0)
        {
            var anchor = adaptiveSelectedCards.First();
            int i1 = cards.IndexOf(anchor);
            int i2 = cards.IndexOf(card);
            if (i1 >= 0 && i2 >= 0)
            {
                int lo = Math.Min(i1, i2), hi = Math.Max(i1, i2);
                foreach (var old in adaptiveSelectedCards) old.IsSelectedAdaptive = false;
                adaptiveSelectedCards.Clear();
                for (int i = lo; i <= hi; i++)
                {
                    adaptiveSelectedCards.Add(cards[i]);
                    cards[i].IsSelectedAdaptive = true;
                }
            }
        }
        else
        {
            foreach (var old in adaptiveSelectedCards) old.IsSelectedAdaptive = false;
            adaptiveSelectedCards.Clear();
            adaptiveSelectedCards.Add(card);
            card.IsSelectedAdaptive = true;
        }
        UpdateSelectionChrome(adaptiveSelectedCards.ToArray());
    }

    private async void AdaptiveCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaCard card } && SelectMode.IsChecked != true)
        {
            SelectSingleCard(card);
            await Guard(Open);
        }
    }

    private void AdaptiveCardRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaCard card } fe)
        {
            contextCard = card;
            if (!adaptiveSelectedCards.Contains(card)) SelectSingleCard(card);
            if (MediaGrid.ContextFlyout is MenuFlyout flyout)
            {
                GalleryContextMenuOpening(flyout, EventArgs.Empty);
                flyout.ShowAt(fe, e.GetPosition(fe));
            }
        }
    }

    private LibraryQuery CurrentQuery() => new(
        SelectedSource,
        SearchBox.Text.Trim(),
        Navigation.SelectedItem?.ToString() == "Photos" ? "photo" : Navigation.SelectedItem?.ToString() == "Videos" ? "video" : null,
        Navigation.SelectedItem?.ToString() == "Favorites",
        (MediaSort)settings.SortIndex,
        0,
        120,
        FolderFilter.Text.Trim(),
        settings.Descending,
        SelectedAlbum,
        settings.IncludeDescendants,
        settings.CommonOnly,
        beforeTicks,
        MemoriesOn: IsMemories ? MemoryDay : null,
        FromTicks: fromTicks,
        FavoritesFirst: settings.FavoritesFirst,
        Orientation: filterOrientation,
        UntaggedOnly: filterUntaggedOnly,
        MinLength: filterMinLength,
        HasGpsOnly: filterHasGpsOnly,
        MapBounds: activeMapBounds);

    private readonly SemaphoreSlim galleryGate = new(1, 1);

    private Task Refresh() => RefreshGallery(false);
    private async Task RefreshGallery(bool reuseMonths, int startOffset = 0)
    {
        if (!ready) return;
        await galleryGate.WaitAsync();
        try
        {
            queryCancellation?.Cancel(); queryCancellation = new(); var token = queryCancellation.Token;
            loadedStart = startOffset; ResumeNavigation.Visibility = loadedStart > 0 ? Visibility.Visible : Visibility.Collapsed;
            cursor = null; exhausted = false; loading = false; pendingLoadMore = false;
            foreach (var card in cards) { card.Realized = false; card.Thumbnail = null; card.ThumbnailRequest?.Cancel(); card.ThumbnailRequest = null; }
            cards.Clear(); groups.Clear(); justifiedRows.Clear(); adaptiveSelectedCards.Clear();
            activeQuery = CurrentQuery();
            if (Grouped) MediaGrid.ItemsSource = new CollectionViewSource { IsSourceGrouped = true, Source = groups }.View;
            else MediaGrid.ItemsSource = cards;
            galleryScroll?.ChangeView(null, 0, null, true);
            adaptiveScroll?.ChangeView(null, 0, null, true);
            EmptyState.Visibility = Visibility.Collapsed; Busy.IsActive = true;
            if (!scanning && !preparing) Status.Text = "Loading moments…";

            libraryTotal = (await Task.Run(() => catalog.SourceCountsAsync(token), token)).Values.Sum();
            token.ThrowIfCancellationRequested();
            BuildFilterChips();

            // Render first page immediately for sub-second first paint
            await LoadMore(); token.ThrowIfCancellationRequested();

            // Update month controls immediately when reusing existing buckets, then refresh buckets asynchronously if needed.
            UpdateMonthNavigationControls();

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
                            suppressFilterEvents++;
                            try
                            {
                                DateJump.ItemsSource = monthBuckets;
                            }
                            finally
                            {
                                suppressFilterEvents--;
                            }
                        }
                        UpdateMonthNavigationControls();
                        UpdateEmptyState();
                    });
                }
                catch (OperationCanceledException) { }
            }, token);

            await LoadFolders(token);
            SchedulePlaceSave();
            UpdateVisibleTimeRange();
        }
        catch (OperationCanceledException) { }
        finally
        {
            galleryGate.Release();
        }
    }

    private void PreserveScrollAfterAppend(ScrollViewer sv, double savedOffset, CancellationTokenSource? generation)
    {
        if (savedOffset <= 40 || restoringPlace) return;

        void TryRestore()
        {
            if (restoringPlace || generation != queryCancellation) return;
            if (sv.ScrollableHeight > 40 && sv.VerticalOffset < Math.Min(24, savedOffset * 0.25))
            {
                sv.ChangeView(null, Math.Min(savedOffset, sv.ScrollableHeight), null, true);
            }
        }

        if (settings.LayoutMode == "Uniform") MediaGrid.UpdateLayout();
        else JustifiedRowsList.UpdateLayout();

        TryRestore();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TryRestore);
    }

    private async Task LoadMore()
    {
        if (!ready || loading || exhausted || activeQuery is null || queryCancellation is null) return;
        var generation = queryCancellation; var ct = generation.Token; var query = activeQuery with { Offset = cursor is null ? loadedStart : 0, Cursor = cursor, KnownTotal = cursor is null ? null : total };
        var isSubsequentPage = cards.Count > 0;
        loading = true;
        Busy.IsActive = true;
        if (isSubsequentPage)
        {
            LoadMorePill.Visibility = Visibility.Visible;
            LoadMoreRing.IsActive = true;
        }
        try
        {
            var page = await Task.Run(() => catalog.QueryAsync(query, ct), ct); ct.ThrowIfCancellationRequested();
            var activeScroll = ActiveGalleryScroll();
            var preservedOffset = activeScroll?.VerticalOffset ?? 0;
            var hadScroll = isSubsequentPage && preservedOffset > 40;

            total = page.Total; cursor = page.Next;

            if (settings.StackSimilar)
            {
                var allowBurst = settings.SortIndex is 0 or 1 or 4;
                var stacks = PhotoStackBuilder.Build(page.Items, allowBurst);
                foreach (var stack in stacks)
                {
                    var card = new MediaCard(stack.Cover, settings, IsMemories ? MemoryDay : null, stack.Items, stack.Kind)
                    {
                        IsCut = pendingCutIds.Contains(stack.Cover.Id)
                    };
                    AddCardToActiveCollections(card);
                }
            }
            else
            {
                foreach (var item in page.Items)
                {
                    var card = new MediaCard(item, settings, IsMemories ? MemoryDay : null)
                    {
                        IsCut = pendingCutIds.Contains(item.Id)
                    };
                    AddCardToActiveCollections(card);
                }
            }

            if (settings.LayoutMode != "Uniform") RebuildAdaptiveRows();
            if (hadScroll && activeScroll is not null)
                PreserveScrollAfterAppend(activeScroll, preservedOffset, generation);

            exhausted = page.Items.Count < query.Limit || loadedStart + cards.Count >= total;
            UpdateEmptyState();
            LibrarySummary.Text = IsMemories
                ? $"{MemoryDay:MMMM d} · {total:N0} memories · {libraryTotal:N0} library items"
                : ActiveFilters.Children.Count > 0
                    ? $"{total:N0} of {libraryTotal:N0} items · Filtered"
                    : $"{total:N0} items";
            if (!scanning && !preparing) Status.Text = total == 0 ? "" : exhausted ? "You’re all caught up" : "Scroll to explore";
            UpdateVisibleTimeRange();
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (generation == queryCancellation)
            {
                loading = false;
                Busy.IsActive = scanning || preparing;
                LoadMoreRing.IsActive = false;
                LoadMorePill.Visibility = Visibility.Collapsed;

                if (pendingLoadMore)
                {
                    pendingLoadMore = false;
                    var sv = ActiveGalleryScroll();
                    if (ready && !restoringPlace && !exhausted && sv is not null &&
                        sv.ScrollableHeight > 0 &&
                        sv.VerticalOffset + sv.ViewportHeight >= sv.ExtentHeight - Math.Max(1400, sv.ViewportHeight * 1.8))
                    {
                        DispatcherQueue.TryEnqueue(() => { if (!loading && !exhausted && ready && generation == queryCancellation) _ = Guard(LoadMore); });
                    }
                }
            }
        }
    }

    private void AddCardToActiveCollections(MediaCard card)
    {
        cards.Add(card);
        if (Grouped)
        {
            if (groups.Count == 0 || groups[^1].Key != card.Group) groups.Add(new(card.Group));
            groups[^1].Add(card);
        }
    }

    private string? lastVisibleTimeRangeText;

    private void UpdateVisibleTimeRange()
    {
        if (cards.Count == 0 || settings.SortIndex is not (0 or 1 or 4))
        {
            if (VisibleTimePill.Visibility != Visibility.Collapsed)
                VisibleTimePill.Visibility = Visibility.Collapsed;
            return;
        }

        MediaCard? first = null;
        MediaCard? last = null;
        for (int i = 0; i < cards.Count; i++)
        {
            if (cards[i].Realized) { first = cards[i]; break; }
        }
        for (int i = cards.Count - 1; i >= 0; i--)
        {
            if (cards[i].Realized) { last = cards[i]; break; }
        }
        if (first is null)
        {
            first = cards[0];
            last = cards[Math.Min(11, cards.Count - 1)];
        }
        else if (last is null)
        {
            last = first;
        }

        var d0 = first.Date;
        var d1 = last.Date;
        var minDate = d0 < d1 ? d0 : d1;
        var maxDate = d0 > d1 ? d0 : d1;

        var formatted = minDate.Date == maxDate.Date
            ? $"{minDate:dd MMM yyyy · HH:mm:ss} - {maxDate:HH:mm:ss}"
            : $"{minDate:dd MMM yyyy HH:mm} - {maxDate:dd MMM yyyy HH:mm}";
        if (!string.Equals(lastVisibleTimeRangeText, formatted, StringComparison.Ordinal))
        {
            lastVisibleTimeRangeText = formatted;
            VisibleTimeRange.Text = formatted;
        }
        if (VisibleTimePill.Visibility != Visibility.Visible)
            VisibleTimePill.Visibility = Visibility.Visible;
    }

    private static T? Descendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T result) return result;
            if (Descendant<T>(child) is T nested) return nested;
        }
        return null;
    }

    private void GalleryLoaded(object sender, RoutedEventArgs e)
    {
        galleryScroll = Descendant<ScrollViewer>(MediaGrid);
        if (galleryScroll is not null)
        {
            galleryScroll.ViewChanged -= OnGalleryScrollViewChanged;
            galleryScroll.ViewChanged += OnGalleryScrollViewChanged;
        }
    }

    private async void TileChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.ItemContainer.Tag is MediaCard previous && (e.InRecycleQueue || !ReferenceEquals(previous, e.Item)))
        { previous.Realized = false; previous.Thumbnail = null; previous.ThumbnailRequest?.Cancel(); previous.ThumbnailRequest = null; }
        e.ItemContainer.Tag = e.InRecycleQueue ? null : e.Item;
        if (e.Item is not MediaCard card) return;
        card.Realized = !e.InRecycleQueue;
        if (e.InRecycleQueue) { card.Thumbnail = null; return; }
        await LoadThumbnail(card);
    }

    private void JustifiedRowChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        if (e.ItemContainer.Tag is GalleryRow prevRow && (e.InRecycleQueue || !ReferenceEquals(prevRow, e.Item)))
        {
            foreach (var c in prevRow.Items)
            {
                c.Realized = false;
                c.Thumbnail = null;
                c.ThumbnailRequest?.Cancel();
                c.ThumbnailRequest = null;
            }
        }
        e.ItemContainer.Tag = e.InRecycleQueue ? null : e.Item;
        if (e.Item is not GalleryRow row) return;
        foreach (var card in row.Items)
        {
            card.Realized = !e.InRecycleQueue;
            if (e.InRecycleQueue) { card.Thumbnail = null; }
            else _ = LoadThumbnail(card);
        }
    }

    private async Task LoadThumbnail(MediaCard card)
    {
        if (!ready || clearing || !card.Realized || card.ThumbnailRequest is not null || card.Thumbnail is not null || queryCancellation is null) return;
        if (!DiskGuard.HasSufficientSpace()) return;
        var request = CancellationTokenSource.CreateLinkedTokenSource(queryCancellation.Token); card.ThumbnailRequest = request; var ct = request.Token;
        try
        {
            var bytes = await UseCache(() => thumbnails!.GetAsync(card.Item.Media, 256, ct));
            if (bytes is null || !card.Realized || ct.IsCancellationRequested) return;
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream.GetOutputStreamAt(0))) { writer.WriteBytes(bytes); await writer.StoreAsync(); await writer.FlushAsync(); }
            stream.Seek(0);
            var bitmap = new BitmapImage
            {
                DecodePixelWidth = (int)Math.Max(card.TileWidth, 160),
                DecodePixelType = DecodePixelType.Logical
            };
            await bitmap.SetSourceAsync(stream);
            if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                card.DecodedWidth = bitmap.PixelWidth;
                card.DecodedHeight = bitmap.PixelHeight;
            }
            if (card.Realized && !ct.IsCancellationRequested) card.Thumbnail = bitmap;
            if (AppSettings.IsSmokeTest)
            {
                peakDecoded = Math.Max(peakDecoded, cards.Count(c => c.Thumbnail is not null));
                File.WriteAllText(Path.Combine(AppSettings.LocalRoot, "gallery-metrics.json"), System.Text.Json.JsonSerializer.Serialize(new { Loaded = cards.Count, Decoded = cards.Count(c => c.Thumbnail is not null), PeakDecoded = peakDecoded, ManagedBytes = GC.GetTotalMemory(false) }));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* A missing codec or corrupt preview keeps the accessible placeholder. */ }
        finally { if (ReferenceEquals(card.ThumbnailRequest, request)) card.ThumbnailRequest = null; request.Dispose(); }
    }

    private void CardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaCard card }) card.IsHovered = true;
    }

    private void CardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaCard card }) card.IsHovered = false;
    }

    private async void CardQuickFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaCard card }) return;
        var fav = !card.Item.Favorite;
        await catalog.AnnotateAsync(card.Item.Media, fav, card.Item.Tags);
        card.UpdateItem(card.Item with { Favorite = fav });
        Notify(fav ? $"Added {card.Name} to Favorites." : $"Removed {card.Name} from Favorites.");
    }

    private void CardStackBadgeClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaCard card } && card.IsStack)
            ToggleStackExpansion(card);
    }

    private void ToggleSelectedStack(object sender, RoutedEventArgs e)
    {
        var card = PrimarySelectedCard() ?? contextCard;
        if (card is not null && card.IsStack) ToggleStackExpansion(card);
    }

    private void ToggleStackExpansion(MediaCard coverCard)
    {
        if (!coverCard.IsStack) return;
        int idx = cards.IndexOf(coverCard);
        if (idx < 0) return;

        if (!coverCard.IsExpandedStack)
        {
            coverCard.IsExpandedStack = true;
            int insertPos = idx + 1;
            foreach (var child in coverCard.StackItems)
            {
                if (child.Media.SourceId == coverCard.Item.Media.SourceId && child.Media.RelativePath == coverCard.Item.Media.RelativePath) continue;
                var childCard = new MediaCard(child, settings, IsMemories ? MemoryDay : null)
                {
                    IsCut = pendingCutIds.Contains(child.Id)
                };
                cards.Insert(insertPos++, childCard);
            }
            Notify($"Expanded stack of {coverCard.StackItems.Count} items.");
        }
        else
        {
            coverCard.IsExpandedStack = false;
            var childKeys = coverCard.StackItems
                .Where(c => !(c.Media.SourceId == coverCard.Item.Media.SourceId && c.Media.RelativePath == coverCard.Item.Media.RelativePath))
                .Select(c => c.Media.SourceId + "\n" + c.Media.RelativePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int i = cards.Count - 1; i >= 0; i--)
            {
                var k = cards[i].Item.Media.SourceId + "\n" + cards[i].Item.Media.RelativePath;
                if (childKeys.Contains(k))
                {
                    cards[i].ThumbnailRequest?.Cancel();
                    cards[i].Thumbnail = null;
                    cards.RemoveAt(i);
                }
            }
            Notify($"Collapsed stack of {coverCard.StackItems.Count} items.");
        }

        if (Grouped)
        {
            groups.Clear();
            foreach (var c in cards)
            {
                if (groups.Count == 0 || groups[^1].Key != c.Group) groups.Add(new(c.Group));
                groups[^1].Add(c);
            }
        }
        if (settings.LayoutMode != "Uniform") RebuildAdaptiveRows();
    }

    private void GalleryContextMenuOpening(object? sender, object e)
    {
        var card = contextCard ?? PrimarySelectedCard();
        ContextStackToggle.Visibility = card?.IsStack == true ? Visibility.Visible : Visibility.Collapsed;
        ContextShowOnMap.Visibility = card?.Item.Metadata?.Latitude is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BuildFilterChips()
    {
        ActiveFilters.Children.Clear();
        void Add(string label, Action clear)
        {
            var button = new Button { Content = label + " ×", MaxWidth = 240 };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "Remove filter: " + label);
            ToolTipService.SetToolTip(button, label);
            button.Click += async (_, _) =>
            {
                ready = false;
                try { clear(); settings.Save(); }
                finally { ready = true; }
                await Guard(Refresh);
            };
            ActiveFilters.Children.Add(button);
        }
        if (SelectedSource is not null) Add("Source: " + ((SourceChoice)Sources.SelectedItem).Label, () => { Sources.SelectedIndex = 0; FolderFilter.Text = ""; });
        if (FolderFilter.Text.Length > 0) Add("Folder: " + FolderFilter.Text, () => FolderFilter.Text = "");
        if (SearchBox.Text.Trim().Length > 0) Add("Search: " + SearchBox.Text.Trim(), () => SearchBox.Text = "");
        if (SelectedAlbum is not null) Add("Album: " + ((Album)AlbumsBox.SelectedItem).Name, () => { AlbumsBox.SelectedItem = null; Heading.Text = Navigation.SelectedItem?.ToString() ?? "Library"; });
        if (SmartCollectionsBox.SelectedItem is SmartCollection sc) Add("Collection: " + sc.Name, () => { SmartCollectionsBox.SelectedItem = null; Heading.Text = Navigation.SelectedItem?.ToString() ?? "Library"; });
        if (settings.CommonOnly) Add("Everyday formats only", () => { settings.CommonOnly = false; CommonToggle.IsChecked = false; });
        if (!settings.IncludeDescendants) Add("This folder only", () => { settings.IncludeDescendants = true; RecursiveToggle.IsChecked = true; });
        if (Navigation.SelectedIndex > 0) Add(Navigation.SelectedItem?.ToString() ?? "Category", () => { Navigation.SelectedIndex = 0; Heading.Text = "Library"; MemoryDate.Visibility = Visibility.Collapsed; });
        if (beforeTicks is not null || fromTicks is not null)
        {
            var monthLabel = selectedMonth is not null ? "Month: " + monthBuckets.FirstOrDefault(m => m.Month == selectedMonth)?.Label : "Date range";
            Add(monthLabel, () => { ClearDateRange(); FilterFromDate.Date = null; FilterToDate.Date = null; });
        }
        if (filterOrientation is not null) Add("Orientation: " + filterOrientation, () => { filterOrientation = null; OrientationFilterBox.SelectedIndex = 0; });
        if (filterMinLength is long minLen) Add($"Size >= {minLen / (1024 * 1024)} MB", () => { filterMinLength = null; MinSizeFilterBox.SelectedIndex = 0; });
        if (filterUntaggedOnly) Add("Untagged only", () => { filterUntaggedOnly = false; UntaggedFilterToggle.IsChecked = false; });
        if (filterHasGpsOnly) Add("Has GPS", () => { filterHasGpsOnly = false; HasGpsFilterToggle.IsChecked = false; });
        if (activeMapBounds is not null) Add("Map area", () => activeMapBounds = null);

        FilterBar.Visibility = ActiveFilters.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ShowTimeline(object sender, RoutedEventArgs e)
    {
        ViewFlyout?.Hide();
        if (!ready) return;
        PushNavigationHistory();
        monthBarDismissed = false;
        ready = false; Navigation.SelectedIndex = 0; Heading.Text = "Library"; MemoryDate.Visibility = Visibility.Collapsed;
        settings.SortIndex = 4; settings.GroupIndex = 2; settings.Descending = true; ApplyViewSettings(); settings.Save(); ready = true;
        ClearDateRange(); await Guard(Refresh);
    }

    private void HideMonthBrowser(object sender, RoutedEventArgs e)
    {
        monthBarDismissed = true;
        MonthBrowser.Visibility = Visibility.Collapsed;
    }

    private async void NavigationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || restoringPlace) return;
        PushNavigationHistory();
        Heading.Text = Navigation.SelectedItem?.ToString() ?? "Library";
        ready = false;
        MemoryDate.Visibility = IsMemories ? Visibility.Visible : Visibility.Collapsed;
        if (IsMemories) { MemoryDate.MaxDate = DateTimeOffset.Now; MemoryDate.Date = DateTimeOffset.Now; settings.GroupIndex = 1; GroupBox.SelectedIndex = 1; settings.Descending = true; DescendingButton.IsChecked = true; }
        if (IsMemories)
        {
            settings.SortIndex = 4; SortBox.SelectedIndex = 4;
            if (settings.GroupIndex == 0) { settings.GroupIndex = 2; GroupBox.SelectedIndex = 2; }
        }
        AlbumsBox.SelectedItem = null; SmartCollectionsBox.SelectedItem = null; ready = true; settings.Save(); ClearDateRange(); await Guard(Refresh);
    }

    private async void MemoryDateChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!ready || restoringPlace || !IsMemories) return;
        ClearDateRange(); await Guard(Refresh);
    }

    private async void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (suppressFilterEvents > 0 || !ready || restoringPlace) return;
        if (ReferenceEquals(sender, Sources) && SelectedSource == activeQuery?.SourceId && Sources.SelectedItem is not null && activeQuery is not null) return;
        if (ReferenceEquals(sender, SortBox) && Math.Max(0, SortBox.SelectedIndex) == settings.SortIndex && activeQuery is not null) return;
        PushNavigationHistory();
        if (ReferenceEquals(sender, Sources)) FolderFilter.Text = "";
        settings.SortIndex = Math.Max(0, SortBox.SelectedIndex);
        if (settings.SortIndex == 1) { settings.Descending = false; DescendingButton.IsChecked = false; }
        settings.Save(); ClearDateRange(); await Guard(Refresh);
    }

    private async void SearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!ready || restoringPlace) return; searchCancellation?.Cancel(); var cts = new CancellationTokenSource(); searchCancellation = cts;
        try { await Task.Delay(300, cts.Token); RecordRecentSearch(SearchBox.Text.Trim()); ClearDateRange(); await Guard(Refresh); } catch (OperationCanceledException) { }
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) RecordRecentSearch(SearchBox.Text.Trim());
    }

    private void RecordRecentSearch(string term)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length < 2) return;
        settings.RecentSearches.RemoveAll(s => string.Equals(s, term, StringComparison.OrdinalIgnoreCase));
        settings.RecentSearches.Insert(0, term);
        if (settings.RecentSearches.Count > 8) settings.RecentSearches.RemoveRange(8, settings.RecentSearches.Count - 8);
        try { settings.Save(); } catch { }
    }

    private void SearchHelperFlyout_Opening(object? sender, object e)
    {
        SearchHelperPanel.Children.Clear();
        SearchHelperPanel.Children.Add(new TextBlock { Text = "Quick search operators", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 13 });
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var chips2 = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        void AddOp(StackPanel target, string op)
        {
            var b = new Button { Content = op, Padding = new Thickness(7, 3, 7, 3), FontSize = 11, Background = (Brush)Application.Current.Resources["LumaControl"] };
            b.Click += (_, _) =>
            {
                SearchBox.Text = string.IsNullOrWhiteSpace(SearchBox.Text) ? op : SearchBox.Text.TrimEnd() + " " + op;
                SearchBox.SelectionStart = SearchBox.Text.Length;
                SearchHelperFlyout.Hide();
                SearchBox.Focus(FocusState.Programmatic);
            };
            target.Children.Add(b);
        }
        AddOp(chips, "tag:"); AddOp(chips, "ext:jpg"); AddOp(chips, "year:2026"); AddOp(chips, "size:>10mb");
        AddOp(chips2, "is:untagged"); AddOp(chips2, "is:portrait"); AddOp(chips2, "has:gps"); AddOp(chips2, "is:fav");
        SearchHelperPanel.Children.Add(chips);
        SearchHelperPanel.Children.Add(chips2);

        if (settings.RecentSearches.Count > 0)
        {
            SearchHelperPanel.Children.Add(new TextBlock { Text = "Recent searches", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var rs in settings.RecentSearches.ToArray())
            {
                var btn = new Button { Content = rs, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 4, 8, 4) };
                btn.Click += (_, _) =>
                {
                    SearchBox.Text = rs;
                    SearchHelperFlyout.Hide();
                };
                SearchHelperPanel.Children.Add(btn);
            }
        }
    }

    private async void FilterDateRangeChanged(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!ready || restoringPlace) return;
        selectedMonth = null;
        fromTicks = FilterFromDate.Date is DateTimeOffset fd ? DateTime.SpecifyKind(fd.Date, DateTimeKind.Local).ToUniversalTime().Ticks : null;
        beforeTicks = FilterToDate.Date is DateTimeOffset td ? DateTime.SpecifyKind(td.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime().Ticks - 1 : null;
        await Guard(Refresh);
    }

    private async void VisualFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || restoringPlace) return;
        filterOrientation = OrientationFilterBox.SelectedIndex switch { 1 => "landscape", 2 => "portrait", 3 => "square", _ => null };
        filterMinLength = MinSizeFilterBox.SelectedIndex switch
        {
            1 => 5L * 1024 * 1024,
            2 => 25L * 1024 * 1024,
            3 => 100L * 1024 * 1024,
            4 => 1024L * 1024 * 1024,
            _ => null
        };
        await Guard(Refresh);
    }

    private async void VisualFilterToggleChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || restoringPlace) return;
        filterUntaggedOnly = UntaggedFilterToggle.IsChecked == true;
        filterHasGpsOnly = HasGpsFilterToggle.IsChecked == true;
        await Guard(Refresh);
    }

    private async void TagFilterBox_DropDownOpened(object? sender, object e)
    {
        var tags = await catalog.GetTagsWithCountsAsync();
        TagFilterBox.ItemsSource = tags;
    }

    private void TagFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || TagFilterBox.SelectedItem is not TagCount tc) return;
        SearchBox.Text = $"tag:{tc.Tag}";
    }

    private async void DirectionChanged(object sender, RoutedEventArgs e)
    { if (!ready || restoringPlace) return; settings.Descending = DescendingButton.IsChecked == true; settings.Save(); await Guard(Refresh); }

    private async void GroupChanged(object sender, SelectionChangedEventArgs e)
    { if (!ready || restoringPlace) return; settings.GroupIndex = Math.Max(0, GroupBox.SelectedIndex); settings.Save(); await Guard(Refresh); }

    private void ThumbnailSizeChanged(object sender, RangeBaseValueChangedEventArgs e)
    { if (!ready) return; settings.ThumbnailSize = (int)e.NewValue; UpdateAppearance(); }

    private void AppearanceChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        settings.ShowNames = NamesToggle.IsChecked == true;
        settings.ShowDetails = DetailsToggle.IsChecked == true;
        settings.ShowExactTime = ShowExactTimeToggle.IsChecked == true;
        settings.FitImages = FitToggle.IsChecked == true;
        settings.Compact = CompactToggle.IsChecked == true;
        UpdateAppearance();
    }

    private void UpdateAppearance()
    {
        var anchor = cards.FirstOrDefault(c => c.Realized);
        foreach (var card in cards) card.UpdateView(); settings.Save();
        if (settings.LayoutMode != "Uniform") RebuildAdaptiveRows();
        else if (anchor is not null) MediaGrid.ScrollIntoView(anchor, ScrollIntoViewAlignment.Leading);
    }

    private async void ContentFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || restoringPlace) return;
        settings.CommonOnly = CommonToggle.IsChecked == true;
        settings.IncludeDescendants = RecursiveToggle.IsChecked == true;
        settings.FavoritesFirst = FavoritesFirstToggle.IsChecked == true;
        settings.Save();
        await Guard(Refresh);
    }

    private void ClearDateRange() { beforeTicks = null; fromTicks = null; selectedMonth = null; }

    private async void JumpDate(object sender, SelectionChangedEventArgs e)
    {
        if (suppressFilterEvents > 0 || !ready || restoringPlace || DateJump.SelectedItem is not MonthBucket month) return;
        if (string.Equals(month.Month, selectedMonth, StringComparison.Ordinal) && activeQuery is not null) return;
        await ChooseMonth(month);
    }

    private async void JumpToExactDay(CalendarDatePicker sender, CalendarDatePickerDateChangedEventArgs args)
    {
        if (!ready || restoringPlace || DayJumpPicker.Date is not DateTimeOffset picked) return;
        PushNavigationHistory();
        selectedMonth = null;
        fromTicks = DateTime.SpecifyKind(picked.Date, DateTimeKind.Local).ToUniversalTime().Ticks;
        beforeTicks = DateTime.SpecifyKind(picked.Date.AddDays(1), DateTimeKind.Local).ToUniversalTime().Ticks - 1;
        await Guard(() => RefreshGallery(true));
    }

    private async Task ChooseMonth(MonthBucket month)
    {
        searchCancellation?.Cancel();
        PushNavigationHistory();
        var date = DateTime.ParseExact(month.Month + "-01", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        SetMonth(date);
        await Guard(() => RefreshGallery(true));
    }

    private int FindSelectedMonthIndex()
    {
        if (selectedMonth is null) return -1;
        for (int i = 0; i < monthBuckets.Count; i++)
        {
            if (string.Equals(monthBuckets[i].Month, selectedMonth, StringComparison.Ordinal))
                return i;
        }
        return -1;
    }

    private void UpdateMonthNavigationControls()
    {
        suppressFilterEvents++;
        try
        {
            var matched = selectedMonth is null ? null : monthBuckets.FirstOrDefault(m => string.Equals(m.Month, selectedMonth, StringComparison.Ordinal));
            if (!ReferenceEquals(DateJump.SelectedItem, matched))
                DateJump.SelectedItem = matched;
            MonthBrowser.Visibility = !monthBarDismissed && !IsMemories && settings.SortIndex is 0 or 1 or 4
                ? Visibility.Visible
                : Visibility.Collapsed;
            var monthIndex = FindSelectedMonthIndex();
            NewerMonthButton.IsEnabled = monthIndex > 0;
            OlderMonthButton.IsEnabled = monthBuckets.Count > 0 && monthIndex < monthBuckets.Count - 1;
            AllMonthsButton.IsEnabled = selectedMonth is not null || fromTicks is not null || beforeTicks is not null;
        }
        finally
        {
            suppressFilterEvents--;
        }
    }

    private async void ClearMonth(object sender, RoutedEventArgs e) { ClearDateRange(); DayJumpPicker.Date = null; await Guard(() => RefreshGallery(true)); }
    private async void NewerMonth(object sender, RoutedEventArgs e)
    {
        var index = FindSelectedMonthIndex();
        if (index > 0) await ChooseMonth(monthBuckets[index - 1]);
    }
    private async void OlderMonth(object sender, RoutedEventArgs e)
    {
        var index = FindSelectedMonthIndex();
        if (index + 1 < monthBuckets.Count) await ChooseMonth(monthBuckets[index + 1]);
    }

    private void SelectModeChanged(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        MediaGrid.SelectionMode = SelectMode.IsChecked == true ? ListViewSelectionMode.Multiple : ListViewSelectionMode.Extended;
        SelectionHint.Visibility = SelectMode.IsChecked == true && GetSelectedCards().Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SelectLoaded(object sender, RoutedEventArgs e)
    {
        if (settings.LayoutMode == "Uniform") MediaGrid.SelectAll();
        else
        {
            adaptiveSelectedCards.Clear();
            foreach (var c in cards) { adaptiveSelectedCards.Add(c); c.IsSelectedAdaptive = true; }
            UpdateSelectionChrome(adaptiveSelectedCards.ToArray());
        }
    }

    private void ClearSelection(object sender, RoutedEventArgs e) => ClearAllSelections();

    private async void ResetFilters(object sender, RoutedEventArgs e)
    {
        PushNavigationHistory();
        ready = false; SearchBox.Text = ""; FolderFilter.Text = ""; AlbumsBox.SelectedItem = null; SmartCollectionsBox.SelectedItem = null; Sources.SelectedIndex = 0; Navigation.SelectedIndex = 0;
        filterOrientation = null; OrientationFilterBox.SelectedIndex = 0;
        filterMinLength = null; MinSizeFilterBox.SelectedIndex = 0;
        filterUntaggedOnly = false; UntaggedFilterToggle.IsChecked = false;
        filterHasGpsOnly = false; HasGpsFilterToggle.IsChecked = false;
        activeMapBounds = null; FilterFromDate.Date = null; FilterToDate.Date = null; DayJumpPicker.Date = null; TagFilterBox.SelectedItem = null;
        settings.CommonOnly = false; settings.IncludeDescendants = true; CommonToggle.IsChecked = false; RecursiveToggle.IsChecked = true; settings.Save(); ready = true;
        Heading.Text = "Library"; MemoryDate.Visibility = Visibility.Collapsed; ClearDateRange(); await Guard(Refresh);
    }

    private void InspectorChanged(object sender, RoutedEventArgs e) { if (ready) Inspector.Visibility = InspectorToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; }

    private void GalleryWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        ThumbnailSlider.Value = Math.Clamp(ThumbnailSlider.Value + Math.Sign(e.GetCurrentPoint(MediaGrid).Properties.MouseWheelDelta) * 10, 120, 320); e.Handled = true;
    }

    private async void SelectAllMatching(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (!await Confirm("Select all matching results?", $"Load and select {total:N0} matching items. File actions will affect this full selection.", "Select all")) return;
        var generation = queryCancellation; scanCancellation = new(); CancelButton.Visibility = Visibility.Visible;
        try
        {
            while (!exhausted && ready && generation == queryCancellation && !scanCancellation.IsCancellationRequested)
            { if (loading) { await Task.Delay(50); continue; } await LoadMore(); }
            if (generation != queryCancellation || scanCancellation.IsCancellationRequested) { Notify("Full-result selection cancelled. No files were changed."); return; }
            SelectLoaded(sender, e);
        }
        finally { CancelButton.Visibility = Visibility.Collapsed; }
    }, true);

    private void ScrollTopClicked(object sender, RoutedEventArgs e) => ScrollToTop();
    private void ScrollToTop()
    {
        if (settings.LayoutMode == "Uniform") galleryScroll?.ChangeView(null, 0, null);
        else adaptiveScroll?.ChangeView(null, 0, null);
    }
    private void ScrollToBottom()
    {
        if (settings.LayoutMode == "Uniform" && galleryScroll is not null) galleryScroll.ChangeView(null, galleryScroll.ScrollableHeight, null);
        else if (adaptiveScroll is not null) adaptiveScroll.ChangeView(null, adaptiveScroll.ScrollableHeight, null);
    }

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
            if (exclusionsActive) EmptyText.Text += " Exclusions are active. Open Refresh -> Manage exclusions to review or restore hidden media.";
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
