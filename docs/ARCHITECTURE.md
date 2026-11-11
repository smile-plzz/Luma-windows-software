# Luma Architecture

## 1. System Overview

Luma is a local-first, offline-honest desktop media library for Windows 10 (2004+) and Windows 11. It indexes photos and videos on local and removable drives without moving, modifying, or locking the originals.

Core design principles:
- Originals are sacred: Scanning and indexing are 100% read-only.
- Local-first and private: All metadata and thumbnails live in `%LOCALAPPDATA%\Luma`. Zero telemetry and zero network calls.
- Catalog-first: Browsing cached media never probes offline or sleeping drives.
- Resilient: Schema migrations are idempotent and preceded by automated catalog backups.
- Virtualized and bounded: Thumbnails are cached in an LRU store with a user-configurable limit (default 2 GiB). Memory footprint is bounded by recycling off-screen bitmaps.

---

## 2. Project Structure and Responsibilities

The codebase is organized into three source projects under `src/` and two test projects under `tests/`:

```
Luma/
├── src/
│   ├── Luma.Core/          # Cross-platform business logic, SQLite catalog, caching
│   ├── Luma.Windows/       # Windows-specific integrations (WinRT, Shell, WIC)
│   └── Luma.App/           # WinUI 3 desktop shell, views, and viewmodels
└── tests/
    ├── Luma.Tests/         # Cross-platform unit and regression tests
    ├── Luma.Windows.Tests/ # Windows integration tests (WinRT codecs, shell)
    └── Luma.Benchmarks/    # Synthetic scale benchmark harness (1k - 500k records)
```

### 2.1. Luma.Core (`net8.0`)
Contains platform-agnostic business logic and data access:
- `MediaCache.cs`: SQLite database manager with WAL journal mode, parameterized SQL, pre-migration backup, quick_check integrity validation, and WAL checkpointing.
- `LibraryQueries.cs`: Query execution, sorting (capture date, modified date, name, size, random), pagination via seek cursors, and month grouping.
- `ThumbnailCache.cs`: Thread-safe, bounded disk LRU cache storing decoded JPEG/WebP thumbnails keyed by source and relative path.
- `ThumbnailService.cs`: Coordinates thumbnail generation, disk cache lookups, and concurrency limits.
- `Exclusions.cs`: Library exclusions engine (sources, folders, file extensions) applied to both scanning and browsing.
- `Organization.cs`: Virtual albums, favorites, and tag annotations.
- `FileTransfer.cs`: Safe, atomic copy and move operations using temporary sibling files.
- `ResumableIndexing.cs`: Folder checkpointing during discovery and metadata enrichment.
- `BrowsePosition.cs`: State persistence for restoring previous viewport position.

### 2.2. Luma.Windows (`net8.0-windows10.0.19041.0`)
Encapsulates platform-specific Windows APIs:
- `WindowsSourceResolver.cs`: Identifies physical and removable drives using volume GUIDs and volume serial numbers, resolving drive-letter shifts dynamically upon reconnection.
- `WindowsMetadataReader.cs`: Extracts EXIF capture dates, image dimensions, and video duration via Windows Shell and WIC.
- `WindowsThumbnailGenerator.cs`: Generates high-quality thumbnails using Windows Imaging Component (WIC) and WinRT thumbnail APIs with format fallback.

### 2.3. Luma.App (`net8.0-windows10.0.19041.0`, self-contained win-x64)
WinUI 3 desktop user interface and window management:
- `MainWindow.xaml` / `MainWindow.xaml.cs`: Top-level window hosting navigation, toolbar, search, and status bar.
- `MainWindow.Gallery.cs`: Virtualized media grid, thumbnail loading coordinator, scroll tracking, and filter bar.
- `MainWindow.Preview.cs`: Full-screen/modal preview overlay (Spacebar), pan/zoom, and metadata inspector.
- `MainWindow.Organization.cs`: Tag editing, album management, and native Windows clipboard/drag-and-drop file operations.
- `MainWindow.Management.cs`: Source management dialog, storage & preview maintenance, and activity monitor.
- `MainWindow.Exclusions.cs`: Reversible folder, source, and extension exclusions UI.
- `MainWindow.Offline.cs`: Offline preview generation, repair, and cache clearing.
- `AppSettings.cs`: Persistent user preferences stored in `%LOCALAPPDATA%\Luma\settings.json` with corruption recovery.
- `DiskGuard.cs`: Proactive disk-space checker safeguarding against out-of-disk write failures (< 500 MB threshold).
- `PathRedactor.cs`: Redacts user profile paths in logs and crash reports for privacy.
- `ShellFiles.cs`: Safe wrapper for Windows Explorer reveal, default app opening, and Recycle Bin deletion.

---

## 3. Data Flow and Concurrency

### 3.1. Source Scanning and Resumable Indexing
1. User adds a folder or drive via `AddSource()` or drag-and-drop.
2. `WindowsSourceResolver` maps the path to a unique volume GUID and serial number.
3. **Step 1 (Discovery):** Traverses the directory tree read-only. Every 100 items discovered are committed to SQLite in a single transaction with a folder checkpoint. If cancelled or interrupted, scanning resumes from the last completed folder.
4. **Step 2 (Metadata Enrichment):** Reads EXIF capture dates and dimensions in batches of 100. Unchanged files (matching length and modification timestamp) are skipped.

### 3.2. Gallery Browsing and Thumbnail Virtualization
1. `RefreshGallery()` executes a paginated query against SQLite.
2. Initial batch (120 items) loads via fast indexed seek query (`MediaCache.QueryAsync`). Month buckets are queried asynchronously on a background task so first card paint is never blocked.
3. As the user scrolls, `TileChanging` realizes media cards entering the viewport.
4. `LoadThumbnail()` checks the LRU cache on disk. If absent and the drive is online, `WindowsThumbnailGenerator` extracts the thumbnail and saves it to the cache.
5. Recycled cards entering `InRecycleQueue` have pending requests cancelled and bitmap references cleared to ensure stable memory consumption.

### 3.3. Threading and Dispatcher Rules
- SQLite database transactions run on background threads (`Task.Run`).
- WinUI 3 collection modifications (`ObservableCollection<MediaCard>`) occur strictly on the UI thread (`DispatcherQueue`).
- A `SemaphoreSlim(1, 1)` gate (`galleryGate`) synchronizes gallery refresh operations, preventing re-entrancy during fast filtering or rapid UI events.
