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
- Quick preview overlay triggered by Spacebar or Enter with zoom, pan, and metadata sidebar.
- Tag Manager in Settings allowing users to rename, merge, delete, and view counts for all tags.
- Read-only Duplicates Finder detecting identical media by file size and content hash, with per-group Explorer reveal.
- Smart Collections infrastructure supporting saved dynamic queries in the local catalog.
- Advanced Search operators supporting `tag:`, `ext:`, and `year:` filter prefixes.
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

