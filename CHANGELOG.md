# Changelog

All notable changes to Luma are documented in this file.
The format is based on Keep a Changelog. Plain language, no em-dashes.

## [Unreleased]

### Planned for v1.0.0
- Stability: XAML refresh crash investigation, global exception handler and local crash reports, SQLite catalog quick check and repair path, low disk handling, settings resilience.
- Performance: Benchmarks harness (1k-500k), startup profiling under 2.0s cold start, cursor query plans, memory soak test, HDD benchmark kit for physical testing.
- Features: Tags manager, ratings and color labels, smart collections, external app opening, export and share flows.
- Packaging: Per-user installer (Inno Setup / WiX), automated CI release pipeline, winget and Scoop manifests, code signing integration.
- Verification: Independent verification sign-off across all release candidates.

## [0.7.0] - 2026-11-12

### Fixed (Stabilization Audit & Trust Hardening)
- Fixed first-run empty state check (`Sources.Items.Count > 1`) so brand-new installs clearly show `"A little room for memories"` and the primary **Add folder or drive** prompt, while fully excluded views show `"Library media is hidden"` with quick access to **Manage exclusions...**.
- Fixed multi-word tag splitting (`TagManagement.cs`) by splitting exclusively on commas and semicolons rather than spaces (`"Family Trip"` now stays intact across counts, rename/merge, and bulk tagging).
- Fixed search tokenization (`LibraryQueries.ParseSearch`) to support quoted multi-word terms (`location:"New York"`, `tag:"Family Trip"`, `folder:"2025/Trips"`) and multiple combined `tag:` / `-tag:` filters.
- Fixed catalog repair honesty (`RepairCatalogDetailedAsync`): cleans stale `-wal`/`-shm` sidecars before restoring `.bak` backups and accurately distinguishes a restored backup from an empty schema reinitialization.
- Fixed `MainWindow.Guard` to redact personal folder paths via `PathRedactor.Redact` in `last-error.txt`, matching `crash.log`, and wrapped previously unguarded `async void` action handlers safely.
- Fixed `Delete()` (`MainWindow.xaml.cs`) to honor cancelled Windows Recycle Bin dialogs without advancing or triggering unnecessary full-library rescans, using targeted catalog cleanup upon confirmed deletion (`RemoveMediaItemsAsync`).
- Fixed single/batch Rename (`SourcePaths.ValidateWindowsFileName` and `SanitizeWindowsFileNameStem`) to block Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`), sanitize `: * ? " < > |` consistently across platforms, handle case-only NTFS renames safely, confirm extension edits/removals, and separate catalog sync warnings from skipped collisions.
- Fixed **Undo move** (`ReviewLastTransfer`) to check destination drive availability before creating folders and preserve tags, favorites, and virtual albums via `MediaCache.RelocateAsync`.
- Fixed `LoadThumbnail` under low disk space (`< 500 MB` free) so already-cached `.thumb` previews remain readable offline (`TryReadCachedAsync`) while preview writers pause cleanly.
- Fixed `VisibleAnchor` and `PositionAnchor` to save and restore browsing scroll position in **Adaptive** and **Moments** layouts (`JustifiedRowsList`) as well as Grid view, preferring unclipped top items so position restore settles cleanly across restarts, and prevented delayed `FolderFilter.Text` `TextChanged` events during restore from clearing persisted `Month` filters (`SearchChanged` `activeQuery` equality guard).
- Eliminated intermittent WinUI 3 `0xc000027b` (`FailFastWithStowedExceptions`) modal teardown crashes (`DirectUI::UIAffinityReleaseQueue::DoCleanup` -> `CPopup::~CPopup` re-entering `CXcpDispatcher::OnReentrancyProtectedWindowMessage` when Narrator/UIA is active) by routing all modal `ContentDialog` calls through `ShowDialogAsync`, and eliminated synchronous `UpdateLayout()` re-entrancy crashes in `RefreshGallery`.
- Added retry-with-backoff and plain-language lock-screen/contention notices for Windows clipboard operations (`SetClipboardWithRetryAsync`).
- Removed the redundant in-app photo viewer (`MainWindow.Preview.cs` / `PreviewOverlay`) per `D-13` so every open action across the Gallery and Map View (pins, popups, filmstrip, and place drawer) launches originals directly in the user's external default Windows software (`ShellFiles.OpenInDefaultApp`), with in-overlay `MapNotice` offline alerts (`Canvas.ZIndex="20"`).
- Hardened Map View SQLite & in-session metadata caching (`D-13`): added covering index `ix_metadata_lookup` on `metadata(source_id, relative_path, length, modified_ticks, meta_version)`, graceful fallback in `WindowsMetadataReader` so unreadable codecs negative-cache via `MinimalGpsExtractor` + `meta_version = 2` without rescanning unchanged files, and `catalogGeneration`-keyed memoization for Map markers, filter dropdowns, and backfill checks (plus explicit `"Map metadata cache is up to date..."` feedback on manual `Scan GPS`).
- Fixed loading-state interaction freezes/crashes (`D-14` / `F-033`): added a transparent `LoadingVeil` lock (`ProgressBar`, Cancel/Retry/Exit) over `WorkspaceHost` during startup, `RestorePlace`, cache maintenance, catalog backup/restore/reset, duplicate scans, move reconciliation, and transfers/undo; handled `cache.lock` second-instance conflicts cleanly; pre-cancelled and coalesced rapid gallery filter switches (`galleryRequestVersion` + deferred `GalleryResetVeil`); throttled `Activated` focus refreshes; surfaced `ActiveWorkBanner` for non-blocking background indexing/repairs; and added Windows 11 `MicaKind.BaseAlt` translucent sidebar tint, GPU-composited `1.035x` hover zoom + badge scrim + video duration pills (`▶ 0:42`), Adaptive/Moments keyboard & hover parity, `ThemeShadow` floating chrome, and responsive Map layout + drawer filter.
- Fixed `ExternalReconciliation` to respect active exclusion rules, hardened `MinimalGpsExtractor.ParseXmpBytes` with explicit `DtdProcessing.Prohibit` and `XmlResolver = null`, cleaned image MIME sniffing in Map data URIs, and cleared conflicting filters when browsing a source from **Manage sources**.
- Added **`F1` Keyboard shortcuts & tips**, Settings **Reset catalog (keep originals)...**, privacy-respecting **Send feedback / Report a problem...**, structured GitHub issue templates, user documentation (`docs/USER_GUIDE.md`, `docs/TROUBLESHOOTING.md`, `docs/KNOWN_LIMITS.md`), product/architecture audit docs, and removed all em-dashes across code and documentation.

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
- Faster, safer picture moving and curation: MRU `RecentDestinations` quick-move menu, opt-in `Auto-rename incoming` collision mode (`photo (2).jpg`), multi-file `F2` batch pattern rename (`{date}`, `{time}`, `{orig}`, `{seq}`), external Windows app opening (`Enter` / double-click), and `"Review / Undo last transfer…"` from `last-transfer.json`.
- Tag Manager in Settings and Organize menu allowing users to rename, merge, delete, and view counts for all tags.
- Read-only Duplicates Finder detecting identical media by file size and content hash, with per-group Explorer reveal.
- External move reconciliation detecting files moved outside Luma and re-linking tags, favorites, and albums.
- Diagnostic info copy action in Settings producing sanitized, path-redacted system metrics.
- Manual catalog backup and restore actions in Settings with automatic pre-action rolling backups.
- Low disk space safeguard (DiskGuard) proactively preventing thumbnail creation when system free space falls below 500 MB.
- Inno Setup per-user non-admin installer script (`scripts/Luma-Installer.iss`).
- Benchmark harness (`Luma.Benchmarks`) verifying query performance from 1,000 to 500,000 records.

### Changed
- Performance & SQLite query engine upgrade across `MediaCache`, `LibraryQueries`, `BrowsePosition`, `ThumbnailCache`, `OfflinePreparation`, `DuplicateDetection`, `TagManagement`, `ResumableIndexing`, and `MinimalGpsExtractor` (cutting 500k synthetic catalog counts by 14.8x, month breakdown by 24.8x, and saved-position locate by 34.1x while reducing heap churn and NTFS MFT writes).
- Stabilized gallery scroll bottom-loading in `Uniform` and `Natural proportions`/`Moments` layouts via incremental adaptive row updates, explicit scroll-offset preservation, zero-allocation `UpdateVisibleTimeRange`, synchronized `cacheTasks` tracking, and idempotent window shutdown disposal.
- Fixed `Newer`/`Older`/`All months` navigation button enablement when switching months from cached `monthBuckets`, dismissed `ViewFlyout` on `ShowTimeline`, and kept `Select` mode `GridViewItem` Spacebar multi-selection working natively after removing `PreviewOverlay`.
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

