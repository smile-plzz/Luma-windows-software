# Luma Architecture Map

Baseline commit: `f796035ec642253553acab84d67f51bee95d8f18` (v0.6.0 release on main)
Date: 2026-11-11
Audience: Swarm agents (ORC, RCN, STB, PRF, IDX, MED, UXD, ORG, VWR, PKG, QAE, SEC, DOC, VER).

---

## 1. Projects and Namespaces

Luma is a .NET 8 desktop application consisting of three main source projects and two test projects:

### 1.1 `src/Luma.Core` (Target: `net8.0`)
Cross-platform engine with no WinUI or Windows-specific dependencies.
- `BrowseDates.cs`: Maintains capture dates table and schema migrations.
- `BrowsePosition.cs`: Encapsulates last visible anchor and scroll inset restoration.
- `Exclusions.cs`: Reversible folder, source, and file-type exclusion engine.
- `FileTransfer.cs`: Robust file copy, move, and conflict resolution logic.
- `ImportProgress.cs`: Progress report models for traversal and metadata reading.
- `LibraryQueries.cs`: Cursor-based SQLite gallery queries, filtering, sorting, and month navigation.
- `MediaCache.cs`: SQLite database initialization, connection factory, and media catalog queries.
- `OfflineBrowsing.cs`: Offline availability evaluation and folder structure computation.
- `Organization.cs`: Virtual albums, tags, favorites, and source labels.
- `ResumableIndexing.cs`: Checkpointed directory traversal and atomic publication.
- `SourceOpening.cs`: Safe shell opening abstractions.
- `ThumbnailCache.cs`: Persistent disk-based thumbnail storage, LRU eviction, and `cache.lock` ownership.
- `ThumbnailService.cs`: Coordinates decoding, caching, and placeholder delivery.

### 1.2 `src/Luma.Windows` (Target: `net8.0-windows10.0.19041.0`)
Windows-specific integration layer.
- `WindowsMetadataReader.cs`: Reads EXIF date taken, dimensions, and video properties via Windows Shell / WinRT.
- `WindowsSourceResolver.cs`: Stable source identification using Volume GUID (`\\?\Volume{...}\`), volume serial, and relative folder. Rejects network shares and ancestry reparse points.
- `WindowsThumbnailGenerator.cs`: WinRT bitmap decode and downscale to JPEG/PNG bytes.

### 1.3 `src/Luma.App` (Target: `net8.0-windows10.0.19041.0`, win-x64 self-contained)
WinUI 3 desktop shell using Windows App SDK 1.8.260921001.
- `App.xaml` / `App.xaml.cs`: Application lifecycle, resource dictionary, theme activation.
- `AppSettings.cs`: Persistent user preferences in `%LOCALAPPDATA%\Luma\settings.json`.
- `MainWindow.xaml` / `MainWindow.xaml.cs`: Primary UI host, title bar, sidebar, and layout coordination.
- `MainWindow.Exclusions.cs`: Exclusion dialog and rule management UI.
- `MainWindow.Gallery.cs`: Virtualized ItemsRepeater gallery, selection, sorting, and grouping.
- `MainWindow.Management.cs`: Source manager, maintenance, storage cleanup, and refresh commands.
- `MainWindow.Offline.cs`: Drive availability polling and UI rehydration.
- `MainWindow.Organization.cs`: Album management, tag editing, and favorites.
- `MainWindow.Place.cs`: Restoring browsing position and anchor navigation.
- `MainWindow.Progress.cs`: Visual feedback for discovery and metadata phases.
- `MediaCard.cs`: Custom control representing an individual thumbnail card in the gallery.
- `ShellFiles.cs`: Windows Shell verb execution (reveal in Explorer, open in default app, Recycle Bin).
- `Assets/`: `Luma.ico` (multi-resolution app icon), `Luma.svg` (vector logo).

### 1.4 Test Projects
- `tests/Luma.Tests` (`net8.0`): Cross-platform xUnit test suite (82 tests at baseline).
- `tests/Luma.Windows.Tests` (`net8.0-windows10.0.19041.0`): Windows integration tests (4 tests at baseline).

---

## 2. Catalog Schema and Storage

All persistent data resides in `%LOCALAPPDATA%\Luma` (or `%TEMP%\Luma-smoke-<session>` during smoke tests).

### 2.1 SQLite Catalog (`catalog.db`)
Configured with `PRAGMA journal_mode=WAL;`.

#### Tables
1. `luma_migrations(name TEXT PRIMARY KEY)`: Tracks executed schema migrations.
2. `sources(id TEXT PRIMARY KEY, root_path TEXT NOT NULL, last_seen TEXT NOT NULL)`: Registered roots.
3. `media(source_id TEXT NOT NULL, relative_path TEXT NOT NULL, length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, kind TEXT NOT NULL, PRIMARY KEY(source_id, relative_path), FOREIGN KEY(source_id) REFERENCES sources(id))`: Indexed media files.
4. `annotations(source_id TEXT NOT NULL, relative_path TEXT NOT NULL, favorite INTEGER NOT NULL DEFAULT 0, tags TEXT NOT NULL DEFAULT '', PRIMARY KEY(source_id, relative_path))`: User favorites and comma-delimited tags.
5. `exclusions(kind TEXT NOT NULL, source_id TEXT NOT NULL, value TEXT NOT NULL, PRIMARY KEY(kind, source_id, value))`: Reversible rules (`kind`: "folder", "source", "type").
6. `media_ids(id TEXT PRIMARY KEY, source_id TEXT NOT NULL, relative_path TEXT NOT NULL, UNIQUE(source_id, relative_path))`: Stable UUIDs for media items.
7. `albums(id TEXT PRIMARY KEY, name TEXT NOT NULL)`: User-created virtual albums.
8. `album_items(album_id TEXT NOT NULL, media_id TEXT NOT NULL, PRIMARY KEY(album_id, media_id))`: Many-to-many album memberships.
9. `metadata(source_id TEXT NOT NULL, relative_path TEXT NOT NULL, length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, capture_ticks INTEGER, width INTEGER, height INTEGER, duration_ms INTEGER, camera_make TEXT, camera_model TEXT, PRIMARY KEY(source_id, relative_path))`: Enriched properties.
10. `browse_dates(source_id TEXT NOT NULL, relative_path TEXT NOT NULL, date_ticks INTEGER NOT NULL, PRIMARY KEY(source_id, relative_path))`: Materialized capture dates for fast chronological sorting.
11. `source_labels(source_id TEXT PRIMARY KEY, label TEXT NOT NULL)`: Friendly names for sources.
12. `scan_jobs(source_id TEXT PRIMARY KEY)`: Active scanning jobs.
13. `scan_folders(source_id TEXT NOT NULL, path TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0, stamp INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(source_id, path))`: Traversal checkpoint state.
14. `scan_media(source_id TEXT NOT NULL, relative_path TEXT NOT NULL, folder TEXT NOT NULL, length INTEGER NOT NULL, modified_ticks INTEGER NOT NULL, kind TEXT NOT NULL, PRIMARY KEY(source_id, relative_path))`: Uncommitted scanned items staged prior to atomic publication.
15. `scan_warnings(source_id TEXT NOT NULL, folder TEXT NOT NULL, message TEXT NOT NULL, PRIMARY KEY(source_id, folder))`: Access errors or skipped paths during scan.

#### Indexes
- `ix_media_source` on `media(source_id)`
- `ix_media_modified` on `media(modified_ticks DESC, source_id, relative_path)`
- `ix_media_length` on `media(length DESC, source_id, relative_path)`
- `ix_browse_dates_ticks` on `browse_dates(date_ticks DESC, source_id, relative_path)`
- `ix_scan_folders_pending` on `scan_folders(source_id, done, path)`

---

## 3. Thumbnail Cache Layout

Located at `%LOCALAPPDATA%\Luma\thumbnails` (or configured directory).
- Single-instance gate: `cache.lock` held with `FileShare.None`.
- Temporary writes: `*.tmp` files created alongside target and renamed atomically. Abandoned `*.tmp` cleaned up on startup.
- Cache entry format: `key[0..1]/key[2..3]/key.thumb`.
- Cache key format:
  `SHA256("luma-thumbnail-v1:" + pixels + ":" + MediaCache.ThumbnailKey(media))`
- Eviction policy: In-memory `SortedSet` tracking LRU based on access timestamps. Bounded capacity from 1 GiB to 32 GiB (default 2 GiB).
- Protection: Non-evicting bulk offline preparation writes never evict existing thumbnails when full.

---

## 4. Settings Model (`settings.json`)

Serialized as JSON to `%LOCALAPPDATA%\Luma\settings.json` via safe write-to-temp and atomic rename:
- `RememberPlace` (bool, default `true`)
- `Place` (`BrowsePlace` record: SourceId, Folder, Search, AlbumId, Navigation, Month, MemoryDay, AnchorSource, AnchorPath, Inset)
- `SortIndex` (int, default `4` - Date taken)
- `Descending` (bool, default `true`)
- `GroupIndex` (int, default `2` - Month)
- `ThumbnailSize` (int, default `190`, range 100-360)
- `ShowNames` (bool, default `false`)
- `ShowDetails` (bool, default `false`)
- `FitImages` (bool, default `false`)
- `Compact` (bool, default `false`)
- `CommonOnly` (bool, default `false`)
- `FilterDefaultsVersion` (int, default `1`)
- `IncludeDescendants` (bool, default `true`)
- `CacheGiB` (int, default `2`, range 1-32)
- `Theme` (string, default `"Default"`, options: `"Light"`, `"Dark"`, `"Default"`)

---

## 5. Threading Model and Concurrency

- UI Thread: Owns XAML window, controls, collection bindings, and user interactions.
- Background Work:
  - Scanning and Checkpointing: Runs asynchronously on thread pool tasks.
  - Windows Metadata Extraction: Batch reads off UI thread; reports progress via `IProgress<T>`.
  - Thumbnail Generation: Async pipeline via `ThumbnailService`; worker semaphore bounds concurrent WinRT decoders.
- Thread Affinity and Marshaling:
  - Background operations must marshal UI element updates using `DispatcherQueue.TryEnqueue()`.
  - Discovered risk: `ImportFeedback` and `Progress<T>` calls can cause `0xc000027b` if invoked without verified UI thread affinity or during control teardown.

---

## 6. Continuous Gallery Architecture

- Native WinUI `ItemsRepeater` inside `ScrollViewer`.
- Virtualization: Realizes elements only for visible viewport plus buffer.
- Decoded bitmaps: Bound to realized `MediaCard` controls; recycled when scrolled out of view to avoid unbounded memory consumption.
- Cursor pagination: Fetches database batches of 120 items at a time without SQL `OFFSET`.

---

## 7. CI Pipeline (`.github/workflows`)

1. `build.yml` (on push to `main` and pull requests):
   - `core` job: Runs `Luma.Tests` on `windows-latest` and `ubuntu-latest`.
   - `windows` job: Runs `Luma.Windows.Tests` on `windows-latest`.
   - `desktop` job: Publishes self-contained `win-x64`, checks XAML resources (`MainWindow.xbf`), runs `./scripts/Smoke-Test.ps1`, uploads `Luma-Gallery-win-x64` artifact and smoke evidence.
2. `release.yml` (on push to `main` with release note updates):
   - Verifies tested commit SHA and ZIP SHA-256 against release metadata.
   - Publishes GitHub Release draft with `SHA256SUMS.txt` and verified ZIP.
