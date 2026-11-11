# Changelog

All notable changes to Luma are documented in this file.
The format is based on Keep a Changelog. Plain language, no em-dashes.

## [Unreleased]

### Planned for v1.0.0
- Stability: XAML refresh crash investigation, global exception handler and local crash reports, SQLite catalog quick check and repair path, low disk handling, settings resilience.
- Performance: Benchmarks harness (1k-500k), startup profiling under 2.0s cold start, cursor query plans, memory soak test, HDD benchmark kit for physical testing.
- Features: Tags manager, ratings and color labels, smart collections, duplicate and similar finder, quick preview overlay, export and share flows.
- Packaging: Per-user installer (Inno Setup / WiX), automated CI release pipeline, winget and Scoop manifests, code signing integration.
- Verification: Independent verification sign-off across all release candidates.

## [0.7.0] - 2026-11-11

### Added
- Collapsible sidebar (`Ctrl+B`), collapsible sidebar accordion sections (`Views`, `Albums`, `Smart collections`, `Folders & drives`) with item counts, and collapsible clean top toolbar mode (`Ctrl+H`).
- Viewing layout modes in `View`: `Uniform grid`, `Natural proportions` (aspect-accurate virtualized justified rows), and `Moments (relevant size)` (highlighting favorites and stack covers), plus warm light/dark surface palettes and Windows 11 Mica backdrop.
- Photo Stacks (`Stack burst & RAW+JPEG pairs`): non-destructive grouping of consecutive shots within 30s and same-stem RAW+JPEG pairs with inline expand/collapse.
- Dedicated free, one-time opt-in online Map View using public OpenStreetMap tiles (`WebView2` + zero-CDN `Assets/Map/map.html` with `Minimal Light`/`Minimal Dark`/`Mono Slate`/`OSM Streets` tile styles), plus an interactive offline vector coordinate canvas before consent.
- Broad minimal coordinate & location extraction (`MinimalGpsExtractor` + `WindowsMetadataReader`): recovers JPEG/TIFF/DNG/RAW/PNG/WebP/HEIC EXIF (including incomplete Refs and coarse DMS), embedded XMP and adjacent `.xmp` sidecars, MP4/MOV QuickTime ISO-6709 video atoms (`©xyz` / `keys+ilst`), Windows Shell GPS fallbacks, and text place tags (`location_name`), plus automatic connected-drive backfill for legacy catalogs (`meta_version`).
- Map filter strip (`All`/`Photos`/`Videos`, `Favorites`, `Year`, `Source`, `Pin origin`, search / `location:`), view modes (`Clusters`, `Pins`, chronological `Trail`), collapsible viewport thumbnail filmstrip, unlocated/place-tag drawer, and catalog-only manual map pins (originals stay strictly read-only).
- Exact time details while browsing: optional `HH:mm:ss` card captions, rich hover tooltips, live viewport `VisibleTimeRange` header HUD, selection time-span calculations, `Day & Session` grouping, and extended camera EXIF metadata (`camera_model`, `iso`, `f_number`, `exposure_seconds`, `focal_length`).
- Faster navigation: clickable folder breadcrumbs, `FolderList` right-click menu, exact `Jump to day` calendar picker, `Alt+Left` Back navigation history, `Home`/`End` top/bottom scroll, and Smart Collections sidebar UI with presets and `+ Save view`.
- Cursor-safe new sorting options (`Filename`, `Resolution`, `Video duration`, and `Favorites first`) and visual `Filters` flyout (date range, orientation, minimum size, untagged, GPS, tag picker) alongside search operators (`date:`, `after:`, `before:`, `size:>`, `folder:`, `is:fav`, `is:untagged`, `is:portrait`, `has:gps`, `-tag:`, `-ext:`) and recent search history.
- Faster, safer picture moving and curation: MRU `RecentDestinations` quick-move menu, opt-in `Auto-rename incoming` collision mode (`photo (2).jpg`), multi-file `F2` batch pattern rename (`{date}`, `{time}`, `{orig}`, `{seq}`), Spacebar preview triage hotkeys (`F`, `T`, `M`, `Delete`, `Auto-next`), and `"Review / Undo last transfer…"` from `last-transfer.json`.
- Quick preview overlay triggered by Spacebar or Enter with zoom, pan, and offline-safe metadata sidebar.
- Tag Manager in Settings and Organize menu allowing users to rename, merge, delete, and view counts for all tags.
- Read-only Duplicates Finder detecting identical media by file size and content hash, with per-group Explorer reveal.
- External move reconciliation detecting files moved outside Luma and re-linking tags, favorites, and albums.
- Diagnostic info copy action in Settings producing sanitized, path-redacted system metrics.
- Manual catalog backup and restore actions in Settings with automatic pre-action rolling backups.
- Low disk space safeguard (DiskGuard) proactively preventing thumbnail creation when system free space falls below 500 MB.
- Inno Setup per-user non-admin installer script (`scripts/Luma-Installer.iss`).
- Benchmark harness (`Luma.Benchmarks`) verifying query performance from 1,000 to 500,000 records.

### Changed
- Mitigated intermittent WinUI 3 XAML refresh crash (0xc000027b) via strict `galleryGate` SemaphoreSlim synchronization.
- Settings file resilience: damaged settings files fallback cleanly to defaults and preserve a `.corrupt` copy.
- Unhandled exceptions write sanitized, path-redacted crash reports to `last-error.txt` and `last-error.json`.
- All repository and download links updated to canonical repository `https://github.com/smile-plzz/Luma-windows-software`.

## [0.6.0] - 2026-10-13

### Added
- Reopen the last source, folder, album, search query and month with visible media item and scroll inset restored from local catalog.
- Earlier items button to load preceding 120 records without moving the current view.
- Explicit import progress: finding media shows examined files and completed folders; reading dates shows checked files and updates against known total.
- Themed dialogs matching light or dark gallery appearance.
- Tooltips and accessible help explaining browsing, filters, file actions, exclusions and maintenance.

### Changed
- Refined feedback across favorites, tags, settings and pause requests.
- Disabled remembering browsing position by default when configured in Settings.

## [0.5.0] - 2026-10-13

### Added
- Reversible folder, source and global extension exclusion rules.
- Dedicated exclusion management dialog in Settings and Refresh menu.
- Embedded high-resolution executable branding icon and Windows metadata.

## [0.4.0] - 2026-10-13

### Added
- Explicit month navigation with media item counts.
- Multi-file selection mode with bulk operations.
- Indexed capture-date catalog queries for large libraries up to 200,000 records.

## [0.3.0] - 2026-10-13

### Added
- Filter bar for active filters, non-everyday media formats and chip removal.
- Reset action restoring unfiltered library view.
- Timeline navigation and pinned settings destinations.

## [0.2.0] - 2026-10-12

### Added
- Source manager for adding, removing and viewing volume identities.
- Explicit refresh scopes per source and whole library.
- Storage and preview cache maintenance view.
- Activity monitor with progress and cancellation controls.

## [0.1.0] - 2026-10-12

### Added
- Initial portable Windows media library desktop app.
- Read-only background scanning and volume GUID identity tracking.
- Bounded LRU thumbnail cache and SQLite local catalog.

