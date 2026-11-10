# Luma Concurrency and Thread-Affinity Rules

Audience: All developers and swarm agents modifying Luma.App or Luma.Core.

---

## 1. Thread Affinity Rules

### 1.1 The WinUI UI Thread
- The UI thread owns the XAML window, controls, visual tree, `ObservableCollection` bindings, and user input events.
- Direct property access on XAML controls (`Status.Text`, `Notice.IsOpen`, `MediaGrid.ItemsSource`) must execute on the UI thread.
- If running from a background task pool thread or callback, always marshal to the UI thread using:
  ```csharp
  DispatcherQueue.TryEnqueue(() => { /* update UI */ });
  ```

### 1.2 Long-Running and I/O Work
- Database queries, file enumeration, metadata extraction, and thumbnail decoding must NEVER execute synchronously on the UI thread.
- Wrap I/O operations in `await Task.Run(...)` or use asynchronous APIs (`OpenAsync`, `ExecuteReaderAsync`, `ReadAsync`).
- Always pass cancellation tokens to async calls to allow immediate cancellation on user navigation or application close.

---

## 2. Synchronization and Locking Hierarchy

### 2.1 Gallery Gate (`galleryGate`)
- Type: `SemaphoreSlim(1, 1)`.
- Scope: Controls `RefreshGallery` in `MainWindow.Gallery.cs`.
- Purpose: Prevents concurrent or re-entrant gallery refreshes from modifying `cards` or `groups` while queries are awaiting completion. Fixes `0xc000027b` WinUI collection corruption.

### 2.2 Decoder Gate (`decodeGate`)
- Type: `SemaphoreSlim(Math.Clamp(Environment.ProcessorCount - 1, 2, 6))`.
- Scope: Controls concurrent WinRT image decoding in `ThumbnailService.cs`.
- Purpose: Multi-core parallelism while bounding CPU and memory consumption.

### 2.3 Cache Ownership Lock (`cache.lock`)
- Type: File lock with `FileShare.None`.
- Scope: Owned by `ThumbnailCache` constructor.
- Purpose: Ensures only one Luma process writes to the thumbnail directory at any given time.

### 2.4 Indexing Lease (`catalog.db.indexing.lock`)
- Type: File lock with `FileShare.None`.
- Scope: Held during `ScanAsync`.
- Purpose: Guarantees that interrupted scans cleanly release lease upon process exit.

---

## 3. ConfigureAwait Guidelines
- In `Luma.Core` (cross-platform non-UI library), prefer `ConfigureAwait(false)` on internal helper awaits where context synchronization is unnecessary.
- In `Luma.App`, leave default context behavior on methods that resume execution directly to update UI controls.

