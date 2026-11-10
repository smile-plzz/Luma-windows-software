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

