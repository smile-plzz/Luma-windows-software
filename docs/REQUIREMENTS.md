# Luma Product Requirements

Status: Baseline derived from product handoff and code audit against the v0.7.0 stabilization target.
Scope: Functional (FR) and Non-Functional (NFR) requirements tied to verifiable tests or acceptance checks.

## 1. Functional Requirements

### Library Sources, Scanning, and Resilience
- **FR-01 Register sources safely:** Users can add any local folder, external drive, or network path. Duplicate root registrations resolve to the same persistent source identity (`VolumeGuid`, serial/disk fingerprint, or fallback folder identity) without creating duplicate library entries.
- **FR-02 Read-only background indexing:** Scanning never modifies, touches, or deletes source files or folder timestamps. Users can browse, search, favorite, and tag already-indexed items while a background scan runs.
- **FR-03 Resumable checkpoints & control:** Large scans persist directory/file checkpoints cleanly. Users can Pause, Resume, Cancel, or Restart interrupted scans from Activity. Only an error-free scan may reconcile and remove missing stale catalog entries.
- **FR-04 Honest Activity & skipped-folder reporting:** Activity explains discovery vs metadata progress, shows current path/counts, and records skipped/unreadable folders instead of silently ignoring them.
- **FR-05 Exclusions:** Users can exclude specific sources, subfolders, or file extensions reversibly. Exclusion leaves originals untouched, preserves catalog organization where practical, and updates counts cleanly when restored.

### Browsing, Viewing, Navigation, and Places
- **FR-06 Continuous virtualized gallery:** Supports Grid, Adaptive, and Moments layouts, year/month/day grouping, capture-date fallback chain (EXIF/QuickTime/XMP capture date -> earliest valid filesystem timestamp), month navigation with counts, and collapsed photo-stack indicators.
- **FR-07 Remembered browsing context:** Restores the user's visible anchor item (across Grid, Adaptive, and Moments layouts), thumbnail size, fit/crop mode, density, label visibility, sort direction, and theme (light/dark/system) across restarts.
- **FR-08 Quick Preview & keyboard triage:** Spacebar or Enter opens Quick Preview with responsive `Left`/`Right` navigation, zoom, Favorites (`F`), Tag (`T`), Album (`M`), Info (`I`), Open in default app (`Enter`), and safe Recycle Bin delete (`Delete`). Video previews clearly inform the user to press `Enter` or double-click for playback in the default Windows app.
- **FR-09 Offline-capable Places / Map overview:** Geotagged items are aggregated into visited places and an interactive offline vector world map by default, with optional online tile switching only when explicitly requested by the user.

### Search, Filters, and Organization
- **FR-10 Search & structured filters:** Supports free-text filename/path search plus operators (`tag:`, `folder:`, `location:`, `type:`, `year:`, `ext:`), including quoted multi-word values (e.g., `location:"New York"`, `tag:"Family Trip"`), removable filter chips with accurate filtered counts, Smart Collections, On this day memories, and a one-click Reset.
- **FR-11 Favorites, multi-word tags, and albums:** Users can toggle favorites, add/remove/rename tags containing spaces (comma/semicolon delimited, never split on single spaces), and curate virtual albums linked to persistent `media_ids`. Organization survives file renames, in-app transfers, and external drive-letter changes.
- **FR-12 Multi-file selection:** Select mode supports single/range clicks, `Ctrl+A` select all, batch favorite, bulk tagging, batch album assignment, batch rename, copy/cut/paste, and delete.

### External Drives & Offline Browsing
- **FR-13 Volume-based identity & catalog-first reopen:** Returning drives are resolved by volume GUID/serial/fingerprint before drive letter fallback, reopening from the local catalog immediately without forcing a rescan.
- **FR-14 In-place reconnect & offline honesty:** Switching a drive online/offline updates visible card indicators in place and retries missing visible previews without discarding scroll position or selection. While offline, users can browse cached thumbnails/metadata and edit catalog organization (favorites, tags, albums); actions requiring original bytes explain clearly that the drive must be connected.
- **FR-15 Offline preview preparation & cache management:** Users can prepare offline previews with cancel/resume support, inspect coverage and disk footprint, change the cache ceiling (1 to 32 GiB, applied on restart), clear cache with confirmation, repair missing thumbnails, rebuild caches cleanly, or reset the catalog without touching originals.

### Original-File Operations & Safety
- **FR-16 Protected original operations:** Open in default app, Reveal in Explorer, Copy, Cut/Move, Rename, Batch Rename, and Recycle Bin delete never silently overwrite existing destination filenames and never bypass Windows Recycle Bin confirmation.
- **FR-17 Filenames & error clarity:** Rename validates Windows invalid characters, trailing dots/spaces, Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`), case-only renames, and accidental extension removal before mutating disk files. Move undo preserves catalog tags/favorites/albums via `RelocateAsync` and checks source-drive connectivity first. Partial failures produce a clear summary report.

### Trust, Help, and Feedback
- **FR-18 Transparent storage & contextual help:** In-app Help explains keyboard shortcuts (`F1`), metadata capture-date fallback order, offline limits, video playback boundaries, `%LOCALAPPDATA%\Luma` storage layout, and known limits.
- **FR-19 User feedback channel:** In-app "Send feedback / Report a problem" opens a prefilled GitHub issue URL containing app version, Windows version, and opt-in path-redacted diagnostic context (with no background network calls or automatic uploads).

## 2. Non-Functional Requirements & Numeric Targets

| ID | Category | Target | Measurement / Evidence Method |
|---|---|---|---|
| **NFR-01** | Cold start (100k catalog) | <= 1,500 ms to usable window & initial page | `Luma.Benchmarks` warm DB open + initial page query + `Smoke-Test.ps1` startup |
| **NFR-02** | Query latency (100k items) | Next-page cursor <= 30 ms; first 120 + count <= 250 ms | `Luma.Benchmarks` 100k library |
| **NFR-03** | Query latency (500k items) | Next-page cursor <= 30 ms; first 120 + count <= 750 ms | `Luma.Benchmarks` 500k library |
| **NFR-04** | Month breakdown & anchor locate (100k items) | Locate anchor <= 120 ms; month breakdown <= 400 ms | `Luma.Benchmarks` & `GalleryTests` |
| **NFR-05** | Indexing throughput | >= 2,000 items/sec cached metadata upsert (SSD synthetic); time-to-first-visible-thumbnails <= 2.0 s on initial batch | `Luma.Benchmarks` + `Smoke-Test.ps1` |
| **NFR-06** | UI thread responsiveness | Zero blocking DB/disk work > 100 ms on UI thread; operations > 1.0 s show determinate or textual status | Static `async void`/UI review + `GalleryTests` concurrent browse-during-scan |
| **NFR-07** | Memory ceiling | Thumbnail cache bounded by user ceiling (1 to 32 GiB); gallery viewport realizes visible window only, keeping 100k idle working set < 600 MB in steady state | `ThumbnailCache` eviction tests + `Smoke-Test.ps1` |
| **NFR-08** | Crash & corruption resilience | Mid-scan kill leaves prior committed catalog readable and checkpoint resumable; corrupt DB restores from `.bak` cleanly or reports honestly if both were lost | `FailurePathTests`, `StressTests`, `MediaCache` unit tests |
| **NFR-09** | Low-disk safety | Under < 500 MB free space, cached `.thumb` reads continue working for offline/warm viewing while cache writes, repairs, and writes pause gracefully without corrupting files | `FailurePathTests` + UI verification |
| **NFR-10** | Upgrade compatibility | Opening a v0.6.0 catalog and `settings.json` in v0.7.0 migrates schema non-destructively and retains sources, tags, favorites, albums, exclusions, and browse position | `UpgradeTests` (schema v6->current) |
| **NFR-11** | Path, locale, and filename compatibility | Full support for Windows extended/long paths (> 260 chars), non-ASCII/Bangla/CJK/RTL scripts, emoji, spaces, and protection against Windows reserved device names | `Luma.Tests` + `Luma.Windows.Tests` torture suite |
| **NFR-12** | Accessibility & DPI | Every primary action reachable by keyboard, `AutomationProperties.Name` set on interactive controls, light/dark high-contrast compliance, clean layout from 100% to 200% DPI | Static XAML audit + `Smoke-Test.ps1` UIA check + manual Narrator/DPI signoff checklist |
| **NFR-13** | Privacy & Zero Unsolicited Network | Zero outbound connections on startup, library scan, offline map use, or gallery browsing; `PathRedactor` strips user paths from crash/error logs | Offline socket check / code audit + `FailurePathTests` |
