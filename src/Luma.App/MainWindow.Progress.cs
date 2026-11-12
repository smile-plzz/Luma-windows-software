using System.Numerics;
using Luma.Core;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace Luma.App;

public sealed partial class MainWindow
{
    private readonly UISettings uiSettings = new();
    private string workDetail = "", workPhase = "";
    private DateTime lastWorkUpdate = DateTime.UtcNow;
    private int loadingLockDepth;
    private CancellationTokenSource? loadingOverlayCts;

    private bool AnimationsEnabled => !AppSettings.IsSmokeTest && uiSettings.AnimationsEnabled;
    private bool IsUiLocked => loadingLockDepth > 0 || (LoadingVeil?.Visibility == Visibility.Visible);

    private static T? FindDescendantByName<T>(DependencyObject? root, string name) where T : FrameworkElement
    {
        if (root is null) return null;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && match.Name == name) return match;
            var nested = FindDescendantByName<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    private void AnimateThumbnailScale(UIElement target, float toScale, int durationMs = 180)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        if (target is FrameworkElement fe && fe.ActualWidth > 0 && fe.ActualHeight > 0)
            visual.CenterPoint = new Vector3((float)(fe.ActualWidth * 0.5), (float)(fe.ActualHeight * 0.5), 0f);
        if (!AnimationsEnabled)
        {
            visual.StopAnimation(nameof(visual.Scale));
            visual.Scale = new Vector3(toScale, toScale, 1f);
            return;
        }
        var anim = visual.Compositor.CreateVector3KeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        anim.InsertKeyFrame(1f, new Vector3(toScale, toScale, 1f),
            visual.Compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f)));
        visual.StartAnimation(nameof(visual.Scale), anim);
    }

    private static void ResetThumbnailVisual(UIElement target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        visual.StopAnimation(nameof(visual.Scale));
        visual.StopAnimation(nameof(visual.Opacity));
        visual.Scale = Vector3.One;
        visual.Opacity = 1f;
    }

    private void FadeInVisual(UIElement target, int durationMs = 140)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        if (!AnimationsEnabled)
        {
            visual.StopAnimation(nameof(visual.Opacity));
            visual.Opacity = 1f;
            return;
        }
        visual.Opacity = 0f;
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(durationMs);
        anim.InsertKeyFrame(1f, 1f);
        visual.StartAnimation(nameof(visual.Opacity), anim);
    }

    private void AnimatePanelEntrance(UIElement panel, float fromOffsetX = 0f, float fromOffsetY = 0f, int durationMs = 170)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(panel, true);
        var visual = ElementCompositionPreview.GetElementVisual(panel);
        if (!AnimationsEnabled)
        {
            visual.StopAnimation(nameof(visual.Opacity));
            visual.StopAnimation("Translation");
            visual.Opacity = 1f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
            return;
        }
        var comp = visual.Compositor;
        var ease = comp.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
        visual.Opacity = 0f;
        visual.Properties.InsertVector3("Translation", new Vector3(fromOffsetX, fromOffsetY, 0f));

        var opAnim = comp.CreateScalarKeyFrameAnimation();
        opAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
        opAnim.InsertKeyFrame(1f, 1f, ease);

        var trAnim = comp.CreateVector3KeyFrameAnimation();
        trAnim.Duration = TimeSpan.FromMilliseconds(durationMs);
        trAnim.InsertKeyFrame(1f, Vector3.Zero, ease);

        visual.StartAnimation(nameof(visual.Opacity), opAnim);
        visual.StartAnimation("Translation", trAnim);
    }

    private void AnimateChevronRotation(UIElement chevron, bool expanded)
    {
        var visual = ElementCompositionPreview.GetElementVisual(chevron);
        if (chevron is FrameworkElement fe)
        {
            var w = fe.ActualWidth > 0 ? fe.ActualWidth : 10;
            var h = fe.ActualHeight > 0 ? fe.ActualHeight : 10;
            visual.CenterPoint = new Vector3((float)(w * 0.5), (float)(h * 0.5), 0f);
        }
        float targetAngle = expanded ? 0f : -90f;
        if (!AnimationsEnabled)
        {
            visual.StopAnimation(nameof(visual.RotationAngleInDegrees));
            visual.RotationAngleInDegrees = targetAngle;
            return;
        }
        var anim = visual.Compositor.CreateScalarKeyFrameAnimation();
        anim.Duration = TimeSpan.FromMilliseconds(140);
        anim.InsertKeyFrame(1f, targetAngle);
        visual.StartAnimation(nameof(visual.RotationAngleInDegrees), anim);
    }

    private void ShowLoadingVeil(string title, string detail, CancellationTokenSource? cts = null, double? percent = null, bool showRetryExit = false)
    {
        loadingLockDepth++;
        loadingOverlayCts = cts;
        if (WorkspaceHost is not null) WorkspaceHost.IsEnabled = false;
        LoadingTitle.Text = title;
        LoadingDetail.Text = detail;
        LoadingProgressBar.Visibility = showRetryExit ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.Visibility = showRetryExit ? Visibility.Collapsed : Visibility.Visible;
        LoadingRing.IsActive = !showRetryExit;
        LoadingProgressBar.IsIndeterminate = percent is null;
        LoadingProgressBar.Value = Math.Clamp(percent ?? 0, 0, 100);
        LoadingRetryButton.Visibility = showRetryExit ? Visibility.Visible : Visibility.Collapsed;
        LoadingExitButton.Visibility = showRetryExit ? Visibility.Visible : Visibility.Collapsed;
        LoadingCancelButton.Visibility = (!showRetryExit && cts is not null) ? Visibility.Visible : Visibility.Collapsed;
        LoadingCancelButton.IsEnabled = true;

        if (LoadingVeil.Visibility != Visibility.Visible)
        {
            LoadingVeil.Visibility = Visibility.Visible;
            AnimatePanelEntrance(LoadingCard, 0f, 8f, 160);
        }
    }

    private void UpdateLoadingVeil(string? title = null, string? detail = null, double? percent = null)
    {
        if (title is not null) LoadingTitle.Text = title;
        if (detail is not null) LoadingDetail.Text = detail;
        if (percent is double pct)
        {
            LoadingProgressBar.IsIndeterminate = false;
            LoadingProgressBar.Value = Math.Clamp(pct, 0, 100);
        }
        else if (title is not null && percent is null && LoadingProgressBar.Visibility == Visibility.Visible)
        {
            // Keep indeterminate unless caller passes a determinate percentage.
        }
    }

    private void HideLoadingVeil(bool force = false)
    {
        loadingLockDepth = force ? 0 : Math.Max(0, loadingLockDepth - 1);
        if (loadingLockDepth > 0) return;
        loadingOverlayCts = null;
        LoadingRing.IsActive = false;
        LoadingVeil.Visibility = Visibility.Collapsed;
        if (WorkspaceHost is not null) WorkspaceHost.IsEnabled = true;
    }

    private async Task RunLockedAsync(
        string title,
        string detail,
        Func<IProgress<(string Detail, double? Percent)>, CancellationToken, Task> work,
        bool cancellable = false)
    {
        using var cts = cancellable ? new CancellationTokenSource() : null;
        ShowLoadingVeil(title, detail, cts, null);
        if (cancellable) LoadingCancelButton.Focus(FocusState.Programmatic);
        var progress = new Progress<(string Detail, double? Percent)>(update =>
        {
            DispatcherQueue.TryEnqueue(() => UpdateLoadingVeil(detail: update.Detail, percent: update.Percent));
        });
        try
        {
            await work(progress, cts?.Token ?? CancellationToken.None);
        }
        finally
        {
            HideLoadingVeil();
        }
    }

    private void CancelLoadingOverlayClicked(object sender, RoutedEventArgs e)
    {
        if (loadingOverlayCts is null) return;
        LoadingCancelButton.IsEnabled = false;
        LoadingDetail.Text = "Stopping safely while keeping catalog data consistent…";
        loadingOverlayCts.Cancel();
    }

    private async void RetryStartupLockClicked(object sender, RoutedEventArgs e)
    {
        HideLoadingVeil(force: true);
        await Guard(InitializeStartupAsync);
    }

    private void ExitAppClicked(object sender, RoutedEventArgs e) => Close();

    private void ShowActiveWorkBanner(string title, string detail, double? percent = null, bool showPause = true)
    {
        ActiveWorkTitle.Text = title;
        ActiveWorkDetail.Text = detail;
        ActiveWorkProgressBar.IsIndeterminate = percent is null;
        ActiveWorkProgressBar.Value = Math.Clamp(percent ?? 0, 0, 100);
        ActiveWorkPauseButton.Visibility = showPause ? Visibility.Visible : Visibility.Collapsed;
        if (ActiveWorkBanner.Visibility != Visibility.Visible)
        {
            ActiveWorkBanner.Visibility = Visibility.Visible;
            AnimatePanelEntrance(ActiveWorkBanner, 0f, -6f, 150);
        }
    }

    private void HideActiveWorkBanner()
    {
        ActiveWorkBanner.Visibility = Visibility.Collapsed;
    }

    private void ImportFeedback(string phase, string summary, string detail, double? percent = null)
    {
        if (!scanning) return;
        Status.Text = summary; workDetail = detail; lastWorkUpdate = DateTime.UtcNow;
        ImportProgress.Visibility = Visibility.Visible;
        ImportProgress.IsIndeterminate = percent is null;
        ImportProgress.Value = Math.Clamp(percent ?? 0, 0, 100);
        ToolTipService.SetToolTip(Status, summary + "\n" + detail);
        ShowActiveWorkBanner(summary, detail.Replace('\n', ' '), percent, showPause: true);
        if (workPhase != phase)
        {
            workPhase = phase;
            recentActivity.Enqueue($"{DateTime.Now:t} · {summary}");
            if (AppSettings.IsSmokeTest) File.AppendAllText(Path.Combine(AppSettings.LocalRoot, "import-phases.txt"), phase + Environment.NewLine);
        }
    }

    private IProgress<MetadataProgress> MetadataFeedback(string source, string stage = "Step 2 of 2") => new Progress<MetadataProgress>(p =>
        ImportFeedback("metadata", $"{stage} · Reading dates · {p.Checked:N0} / {p.Total:N0} checked",
            $"{source}\n{p.Path}\n{p.Saved:N0} updates saved · {p.Skipped:N0} unavailable or changed files skipped.\nThe library is ready to browse. Pause safely; Refresh metadata continues remaining work.",
            p.Total == 0 ? 100 : 100d * p.Checked / p.Total));

    private string ActivityStatus() => Status.Text + (scanning ? $"\nElapsed {workClock.Elapsed:hh\\:mm\\:ss}\n{workDetail}" +
        ((DateTime.UtcNow - lastWorkUpdate).TotalSeconds >= 10 ? $"\nNo new progress report for {(DateTime.UtcNow - lastWorkUpdate).TotalSeconds:N0}s. Drive or codec access can take time; Pause takes effect when the current operation returns." : "") : "");

    private void EndImportFeedback()
    {
        ImportProgress.Visibility = Visibility.Collapsed; workPhase = ""; workDetail = "";
        HideActiveWorkBanner();
        ToolTipService.SetToolTip(Status, Status.Text);
    }
}
