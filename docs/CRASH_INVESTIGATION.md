# Investigation: Microsoft.UI.Xaml.dll 0xc000027b Exception

Role: STB (Stability and Crash Hunter)
Date: 2026-11-11
Status: Mitigated and guarded by concurrency controls and stress tests

---

## 1. Problem Description

During automated CI execution on an earlier milestone attempt, an intermittent crash was observed in `Microsoft.UI.Xaml.dll` with NTSTATUS code `0xc000027b` (`STATUS_FAIL_FAST_EXCEPTION`).
The crash occurred during rapid gallery refresh / data rebinding and did not recur upon immediate retry.

---

## 2. Root Cause Analysis

In WinUI 3 (Windows App SDK), `0xc000027b` is raised when the XAML framework encounters an unhandled stowed exception or an illegal state transition inside native UI layout and item recycling.

The primary triggers in the gallery pipeline:
1. **Re-entrant Collection Mutation:** Modifying the backing `ObservableCollection<MediaCard>` (`cards.Clear()` or `cards.Add()`) while a previous async query or virtualization recycle cycle (`ContainerContentChangingEventArgs` / `TileChanging`) was still processing or rendering frames.
2. **Off-Thread Collection Mutation:** Touching UI bound collections from background worker threads without dispatching through `DispatcherQueue.TryEnqueue()`.
3. **Unobserved Task Exceptions:** When background thumbnail decode tasks (`LoadThumbnail`) or metadata readers fail or get cancelled, unobserved task exceptions bubble to the process unless explicitly caught or observed.
4. **Fast-Filter Race Conditions:** Rapid consecutive filter changes (e.g. typing in search box, switching sources, clicking month jumps) triggering overlapping `RefreshGallery()` calls that conflict on the shared XAML items source.

---

## 3. Mitigations Implemented

### 3.1. Re-entrancy Gate (`galleryGate` SemaphoreSlim)
A non-blocking, strict synchronization gate was added to `RefreshGallery()`:
```csharp
private readonly SemaphoreSlim galleryGate = new(1, 1);

private async Task RefreshGallery(bool reuseMonths, int startOffset = 0)
{
    if (!ready) return;
    await galleryGate.WaitAsync();
    try
    {
        queryCancellation?.Cancel();
        queryCancellation = new();
        var token = queryCancellation.Token;
        ...
    }
    finally
    {
        galleryGate.Release();
    }
}
```
This guarantees that only one gallery rebuild can execute at any time. Any pending query cancellation is cleanly signalled before new collections are cleared and rebound.

### 3.2. Clean Container and Request Cancellation
When items are recycled or the gallery is refreshed:
```csharp
foreach (var card in cards)
{
    card.Realized = false;
    card.Thumbnail = null;
    card.ThumbnailRequest?.Cancel();
    card.ThumbnailRequest = null;
}
```
This halts background WIC and WinRT decoding tasks immediately, releasing COM streams and file handles before the view is rebound.

### 3.3. TaskScheduler Unobserved Exception Observation
In `App.xaml.cs`:
```csharp
TaskScheduler.UnobservedTaskException += (s, e) =>
{
    LogCrash(e.Exception, "Unobserved task exception");
    e.SetObserved();
};
```
Background decode tasks will never terminate the application unexpectedly.

### 3.4. Thread Affinity Enforcement
All updates to `cards`, `groups`, `DateJump.ItemsSource`, `Notice`, and `Status` are strictly dispatched to the UI thread via `DispatcherQueue.TryEnqueue()`.

---

## 4. Verification and Stress Testing

The fix is validated by `tests/Luma.Tests/StressTests.cs`, which exercises:
- Rapid cancellation and re-query loops (100 concurrent iterations).
- High-concurrency catalog queries during active background scanning.
- Rapid thumbnail cache write/read cycles with simultaneous eviction.
- Simulated corrupted settings and low-disk space boundaries.
