using Luma.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;

namespace Luma.App;

public sealed partial class MainWindow
{
    private CancellationTokenSource? previewCancellation;
    private MediaCard? previewingCard;
    private bool previewMutatedGallery;

    private void SpacePreviewShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (SelectMode.IsChecked == true && PreviewOverlay.Visibility != Visibility.Visible) return;
        var focused = FocusManager.GetFocusedElement(this.Content.XamlRoot);
        if (focused is TextBox or PasswordBox) return;

        args.Handled = true;
        TogglePreview();
    }

    private void TogglePreview()
    {
        if (PreviewOverlay.Visibility == Visibility.Visible)
        {
            DoClosePreview();
        }
        else
        {
            var card = PrimarySelectedCard() ?? (cards.Count > 0 ? cards[0] : null);
            if (card is not null)
            {
                OpenPreviewFor(card);
            }
        }
    }

    private async void OpenPreviewFor(MediaCard card)
    {
        previewingCard = card;
        PreviewOverlay.Visibility = Visibility.Visible;
        PreviewTitle.Text = card.Name;
        PreviewScroller.ChangeView(0, 0, 1.0f);

        var index = cards.IndexOf(card);
        PreviewPosition.Text = index >= 0 ? $"{index + 1} of {cards.Count}" : "";
        PreviewExactTimeBadge.Text = card.ExactDateText;
        PreviewFavoriteButton.Content = card.Item.Favorite ? "★ (F)" : "☆ (F)";
        PreviewMapButton.Visibility = card.Item.Metadata?.Latitude is not null ? Visibility.Visible : Visibility.Collapsed;

        // Offline status indicator
        PreviewOfflineBadge.Visibility = card.Item.Media.IsAvailable ? Visibility.Collapsed : Visibility.Visible;

        // Populate metadata sidebar details cleanly from cached item (works offline)
        PopulatePreviewMetadata(card);

        // Immediate display: show existing thumbnail as immediate preview
        PreviewImage.Source = card.Thumbnail;

        // Cancel previous high-res loading if running
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new();
        var ct = previewCancellation.Token;

        PreviewLoading.IsActive = true;
        try
        {
            if (card.Item.Media.IsAvailable)
            {
                var originalPath = Resolve(card.Item.Media);
                if (File.Exists(originalPath))
                {
                    var ext = Path.GetExtension(originalPath).ToLowerInvariant();
                    if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif")
                    {
                        var storageFile = await StorageFile.GetFileFromPathAsync(originalPath).AsTask(ct);
                        using var stream = await storageFile.OpenReadAsync().AsTask(ct);
                        if (!ct.IsCancellationRequested)
                        {
                            var highResBitmap = new BitmapImage();
                            await highResBitmap.SetSourceAsync(stream);
                            if (!ct.IsCancellationRequested && ReferenceEquals(previewingCard, card))
                                PreviewImage.Source = highResBitmap;
                        }
                    }
                    else
                    {
                        var bytes = await Task.Run(() => thumbnails!.GetAsync(card.Item.Media, 1024, ct), ct);
                        if (bytes is not null && !ct.IsCancellationRequested && ReferenceEquals(previewingCard, card))
                            await ApplyPreviewBytesAsync(bytes);
                    }
                }
            }
            else if (card.Thumbnail is null && thumbnails is not null)
            {
                // Offline fallback from local disk cache
                var cachedBytes = await UseCache(() => thumbnails.GetAsync(card.Item.Media, 256, ct));
                if (cachedBytes is not null && !ct.IsCancellationRequested && ReferenceEquals(previewingCard, card))
                    await ApplyPreviewBytesAsync(cachedBytes);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Fallback to cached thumbnail */ }
        finally
        {
            if (ReferenceEquals(previewingCard, card)) PreviewLoading.IsActive = false;
        }
    }

    private async Task ApplyPreviewBytesAsync(byte[] bytes)
    {
        using var memStream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(memStream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        memStream.Seek(0);
        var highRes = new BitmapImage();
        await highRes.SetSourceAsync(memStream);
        PreviewImage.Source = highRes;
    }

    private void PopulatePreviewMetadata(MediaCard card)
    {
        var meta = card.Item.Metadata;
        var sizeMB = card.Item.Media.Length / (1024.0 * 1024.0);
        var taken = meta?.TakenTicks is long tt ? new DateTime(tt, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "Unavailable";
        var modified = new DateTime(card.Item.Media.ModifiedTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        var lines = new List<string>
        {
            $"Name: {card.Name}",
            $"Folder: {Path.GetDirectoryName(card.Item.Media.RelativePath)}",
            $"Size: {sizeMB:N2} MB ({card.Item.Media.Length:N0} bytes)",
            $"Taken: {taken}",
            $"Modified: {modified}",
            $"Date origin: {meta?.DateOrigin ?? "Modification date fallback"}"
        };

        if (meta is { Width: > 0, Height: > 0 })
            lines.Add($"Dimensions: {meta.Width} x {meta.Height}");
        if (meta is { DurationSeconds: > 0 })
            lines.Add($"Duration: {TimeSpan.FromSeconds(meta.DurationSeconds):hh\\:mm\\:ss}");

        var camParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(meta?.CameraModel)) camParts.Add(meta.CameraModel);
        if (meta?.FocalLength is double fl) camParts.Add($"{fl:0.#}mm");
        if (meta?.FNumber is double fn) camParts.Add($"f/{fn:0.#}");
        if (meta?.ExposureSeconds is double exp) camParts.Add(exp < 1 && exp > 0 ? $"1/{Math.Round(1 / exp)}s" : $"{exp:0.##}s");
        if (meta?.Iso is int iso) camParts.Add($"ISO {iso}");
        if (camParts.Count > 0)
            lines.Add("Camera: " + string.Join(" · ", camParts));

        if (meta?.Latitude is double lat && meta.Longitude is double lon)
            lines.Add($"Coordinates: {lat:0.00000}, {lon:0.00000}");

        if (!string.IsNullOrWhiteSpace(card.Item.Tags))
            lines.Add($"Tags: {card.Item.Tags}");

        lines.Add($"Status: {(card.Item.Media.IsAvailable ? "Connected" : "Offline")}");
        if (!string.IsNullOrEmpty(card.Group))
            lines.Add($"Timeline: {card.Group}");

        PreviewDetailsText.Text = string.Join("\n", lines);
    }

    private void ClosePreview(object sender, RoutedEventArgs e) => DoClosePreview();

    private async void DoClosePreview()
    {
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        previewingCard = null;

        if (previewMutatedGallery)
        {
            previewMutatedGallery = false;
            await Guard(Refresh);
        }

        if (settings.LayoutMode == "Uniform")
            MediaGrid.Focus(FocusState.Programmatic);
    }

    private void PreviewPrevious(object sender, RoutedEventArgs e) => DoPreviewPrevious();

    private void DoPreviewPrevious()
    {
        if (previewingCard is null || cards.Count == 0) return;
        var index = cards.IndexOf(previewingCard);
        if (index > 0)
        {
            var prev = cards[index - 1];
            SelectSingleCard(prev);
            OpenPreviewFor(prev);
        }
    }

    private void PreviewNext(object sender, RoutedEventArgs e) => DoPreviewNext();

    private void DoPreviewNext()
    {
        if (previewingCard is null || cards.Count == 0) return;
        var index = cards.IndexOf(previewingCard);
        if (index >= 0 && index < cards.Count - 1)
        {
            var next = cards[index + 1];
            SelectSingleCard(next);
            OpenPreviewFor(next);
        }
    }

    private void TogglePreviewInspector(object sender, RoutedEventArgs e)
    {
        PreviewInfoSidebar.Visibility = PreviewInfoSidebar.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void PreviewOpenDefault(object sender, RoutedEventArgs e)
    {
        if (previewingCard is not null)
        {
            try { ShellFiles.OpenInDefaultApp(Resolve(previewingCard.Item.Media)); }
            catch (Exception ex) { Notify(ex.Message, true); }
        }
    }

    private void PreviewRevealExplorer(object sender, RoutedEventArgs e)
    {
        if (previewingCard is not null)
        {
            try { ShellFiles.RevealInExplorer(Resolve(previewingCard.Item.Media)); }
            catch (Exception ex) { Notify(ex.Message, true); }
        }
    }

    private async void PreviewToggleFavorite(object sender, RoutedEventArgs e) => await DoPreviewToggleFavorite();

    private async Task DoPreviewToggleFavorite()
    {
        if (previewingCard is null) return;
        var card = previewingCard;
        var newFav = !card.Item.Favorite;
        await catalog.AnnotateAsync(card.Item.Media, newFav, card.Item.Tags);
        card.UpdateItem(card.Item with { Favorite = newFav });
        PreviewFavoriteButton.Content = newFav ? "★ (F)" : "☆ (F)";
        previewMutatedGallery = true;
        Notify(newFav ? "Added to Favorites." : "Removed from Favorites.");
    }

    private async void PreviewQuickTag(object sender, RoutedEventArgs e) => await DoPreviewQuickTag();

    private async Task DoPreviewQuickTag()
    {
        if (previewingCard is null) return;
        var card = previewingCard;
        var updatedTags = await Prompt("Edit tags", "Comma-separated tags for this item:", card.Item.Tags);
        if (updatedTags is null) return;
        await catalog.AnnotateAsync(card.Item.Media, card.Item.Favorite, updatedTags);
        card.UpdateItem(card.Item with { Tags = updatedTags });
        PopulatePreviewMetadata(card);
        previewMutatedGallery = true;
        Notify("Tags updated.");
    }

    private async void PreviewQuickMove(object sender, RoutedEventArgs e) => await DoPreviewQuickMove();

    private async Task DoPreviewQuickMove()
    {
        if (previewingCard is null) return;
        var card = previewingCard;
        var destination = await PickQuickDestinationOrBrowseAsync("Move file to folder");
        if (destination is null) return;

        var original = Resolve(card.Item.Media);
        var currentIndex = cards.IndexOf(card);
        if (await TransferPaths([original], [card.Item.Media], destination, move: true))
        {
            previewMutatedGallery = false;
            if (PreviewAutoAdvanceToggle.IsChecked == true && cards.Count > 0)
            {
                var nextIdx = Math.Clamp(currentIndex, 0, cards.Count - 1);
                OpenPreviewFor(cards[nextIdx]);
            }
            else
            {
                DoClosePreview();
            }
        }
    }

    private async void PreviewDelete(object sender, RoutedEventArgs e) => await DoPreviewDelete();

    private async Task DoPreviewDelete()
    {
        if (previewingCard is null || scanning) return;
        var card = previewingCard;
        if (!await Confirm("Delete original?", $"Send {card.Name} to the Recycle Bin?", "Continue")) return;

        var currentIndex = cards.IndexOf(card);
        ShellFiles.Run(Handle, 3, new[] { Resolve(card.Item.Media) });
        await RescanCurrent();
        previewMutatedGallery = false;
        if (PreviewAutoAdvanceToggle.IsChecked == true && cards.Count > 0)
        {
            var nextIdx = Math.Clamp(currentIndex, 0, cards.Count - 1);
            OpenPreviewFor(cards[nextIdx]);
        }
        else
        {
            DoClosePreview();
        }
    }

    private void PreviewScroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (PreviewScroller.ZoomFactor > 1.1f)
            PreviewScroller.ChangeView(null, null, 1.0f);
        else
            PreviewScroller.ChangeView(null, null, 2.5f);
        e.Handled = true;
    }

    private async void PreviewOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
            case VirtualKey.Space:
                DoClosePreview();
                e.Handled = true;
                break;
            case VirtualKey.Left:
                DoPreviewPrevious();
                e.Handled = true;
                break;
            case VirtualKey.Right:
                DoPreviewNext();
                e.Handled = true;
                break;
            case VirtualKey.I:
                PreviewInfoSidebar.Visibility = PreviewInfoSidebar.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                e.Handled = true;
                break;
            case VirtualKey.F:
                await Guard(DoPreviewToggleFavorite, true);
                e.Handled = true;
                break;
            case VirtualKey.T:
                await Guard(DoPreviewQuickTag, true);
                e.Handled = true;
                break;
            case VirtualKey.M:
                await Guard(DoPreviewQuickMove, true);
                e.Handled = true;
                break;
            case VirtualKey.Delete:
                await Guard(DoPreviewDelete, true);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if (previewingCard is not null)
                {
                    try { ShellFiles.OpenInDefaultApp(Resolve(previewingCard.Item.Media)); }
                    catch (Exception ex) { Notify(ex.Message, true); }
                }
                e.Handled = true;
                break;
        }
    }
}
