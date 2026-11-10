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

    private void SpacePreviewShortcut(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
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
            var card = Selected ?? (cards.Count > 0 ? cards[0] : null);
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

        // Offline status indicator
        PreviewOfflineBadge.Visibility = card.Item.Media.IsAvailable ? Visibility.Collapsed : Visibility.Visible;

        // Populate metadata sidebar details
        PopulatePreviewMetadata(card);

        // Immediate display: show existing thumbnail as immediate preview
        if (card.Thumbnail is not null)
        {
            PreviewImage.Source = card.Thumbnail;
        }

        // Cancel previous high-res loading if running
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = new();
        var ct = previewCancellation.Token;

        PreviewLoading.IsActive = true;
        try
        {
            // If the drive is online, load full original image or large preview
            if (card.Item.Media.IsAvailable)
            {
                var originalPath = Resolve(card.Item.Media);
                if (File.Exists(originalPath))
                {
                    var ext = Path.GetExtension(originalPath).ToLowerInvariant();
                    // For standard images, load directly
                    if (ext is ".jpg" or ".jpeg" or ".png" or ".bmp" or ".webp" or ".gif")
                    {
                        var storageFile = await StorageFile.GetFileFromPathAsync(originalPath).AsTask(ct);
                        using var stream = await storageFile.OpenReadAsync().AsTask(ct);
                        if (!ct.IsCancellationRequested)
                        {
                            var highResBitmap = new BitmapImage();
                            await highResBitmap.SetSourceAsync(stream);
                            if (!ct.IsCancellationRequested && ReferenceEquals(previewingCard, card))
                            {
                                PreviewImage.Source = highResBitmap;
                            }
                        }
                    }
                    else
                    {
                        // For RAW, HEIC or video, fetch 1024px preview from thumbnail generator
                        var bytes = await Task.Run(() => thumbnails!.GetAsync(card.Item.Media, 1024, ct), ct);
                        if (bytes is not null && !ct.IsCancellationRequested && ReferenceEquals(previewingCard, card))
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
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* Fallback to cached thumbnail */ }
        finally
        {
            if (ReferenceEquals(previewingCard, card)) PreviewLoading.IsActive = false;
        }
    }

    private void PopulatePreviewMetadata(MediaCard card)
    {
        var originalPath = Resolve(card.Item.Media);
        var sizeMB = card.Item.Media.Length / (1024.0 * 1024.0);
        var dateTaken = card.Date;

        var text = $"Name: {card.Name}\n" +
                   $"Folder: {Path.GetDirectoryName(card.Item.Media.RelativePath)}\n" +
                   $"Size: {sizeMB:N2} MB ({card.Item.Media.Length:N0} bytes)\n" +
                   $"Date: {dateTaken:yyyy-MM-dd HH:mm:ss}\n" +
                   $"Status: {(card.Item.Media.IsAvailable ? "Connected" : "Offline")}\n";

        if (!string.IsNullOrEmpty(card.Group))
        {
            text += $"Timeline: {card.Group}\n";
        }

        PreviewDetailsText.Text = text;
    }

    private void ClosePreview(object sender, RoutedEventArgs e) => DoClosePreview();

    private void DoClosePreview()
    {
        previewCancellation?.Cancel();
        previewCancellation?.Dispose();
        previewCancellation = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        previewingCard = null;

        // Restore focus to grid
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
            MediaGrid.SelectedItem = prev;
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
            MediaGrid.SelectedItem = next;
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
            ShellFiles.OpenInDefaultApp(Resolve(previewingCard.Item.Media));
        }
    }

    private void PreviewRevealExplorer(object sender, RoutedEventArgs e)
    {
        if (previewingCard is not null)
        {
            ShellFiles.RevealInExplorer(Resolve(previewingCard.Item.Media));
        }
    }

    private void PreviewScroller_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (PreviewScroller.ZoomFactor > 1.1f)
        {
            PreviewScroller.ChangeView(null, null, 1.0f);
        }
        else
        {
            PreviewScroller.ChangeView(null, null, 2.5f);
        }
        e.Handled = true;
    }

    private void PreviewOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
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
            case VirtualKey.Enter:
                if (previewingCard is not null) ShellFiles.OpenInDefaultApp(Resolve(previewingCard.Item.Media));
                e.Handled = true;
                break;
        }
    }
}

