# Luma

**[Download Luma for Windows](https://github.com/smile-plzz/Luma-windows-software/releases/latest/download/Luma-v0.7.0-win-x64.zip)** · [Release notes](https://github.com/smile-plzz/Luma-windows-software/releases/latest)

A native Windows media library for existing folders and drives. Browse photos and videos, organize favorites and tags, find duplicates, preview instantly with Spacebar, and open originals in your default apps.

---

## 3-Step Quick Start

1. **Download:** Grab the latest `Luma-v0.7.0-win-x64.zip` from [Releases](https://github.com/smile-plzz/Luma-windows-software/releases/latest).
2. **Extract:** Unzip to any writable folder and run `Luma.App.exe`.
3. **Add media:** Click **Add folder or drive** (or drag and drop a folder onto the window) to start browsing immediately.

Windows 10 version 2004+ or Windows 11, x64. The portable build bundles .NET and Windows App SDK dependencies.

---

## What works in v0.7.0

- **Collapsible Clean Canvas:** Toggle the sidebar (`Ctrl+B`), collapse individual sidebar sections (`Views`, `Albums`, `Smart collections`, `Folders & drives`), or collapse the top toolbar (`Ctrl+H`) for spacious, distraction-free browsing.
- **Photo Stacks & Adaptive Layouts:** Group 30-second bursts and RAW+JPEG pairs into expandable stacks, and switch `View -> Gallery layout` between `Uniform grid`, `Natural proportions` (no awkward portrait/panorama cropping), and `Moments` (highlighting favorites and stack covers).
- **Dedicated Map View (Privacy-First):** Explore geotagged photos and videos on an opt-in OpenStreetMap view (zero background network calls; loads public tiles only after one-time session confirmation) or browse offline coordinate clusters and filter the gallery to any map area.
- **Exact Time & Camera Details:** Enable `Show exact time (HH:mm:ss)` on cards, inspect live viewport time ranges and selection durations, view camera EXIF (`model`, `focal length`, `aperture`, `shutter`, `ISO`), and group by `Day & Session`.
- **Fast Navigation, Sorting & Filtering:** Clickable folder breadcrumbs, `Jump to day`, `Alt+Left` Back history, Smart Collections sidebar presets, sorting by `Filename`/`Resolution`/`Duration`/`Favorites first`, visual `Filters` flyout, and search operators (`tag:`, `ext:`, `year:`, `date:`, `size:>10mb`, `is:untagged`, `has:gps`).
- **Fast Curation & Moving:** Recent Move Destinations, opt-in `Auto-rename incoming` conflict handling, multi-file `F2` Batch Pattern Rename, Spacebar preview triage (`F` favorite, `T` tag, `M` move, `Del` recycle), and `"Review / Undo last transfer…"`.
- **Spacebar Quick Preview:** Press Spacebar on any photo or video for an immediate, read-only preview overlay with zoom, pan, and offline-capable metadata sidebar.
- **Tag Manager & Duplicates Finder:** Rename/merge/delete tags with counts, and locate exact duplicate files by size and hash with Explorer reveal.
- **External Move Reconciliation:** Re-link catalog organization and tags when files are moved in Windows Explorer.
- **Diagnostics & Backup:** One-click "Copy diagnostic info" (with path redaction) and manual catalog backup/restore in Settings.
- **Library exclusions**: Right-click to hide a folder, whole source, or file extension, skip it during scans, and restore it without losing organization. [Exclusion guide](docs/EXCLUSIONS.md).
- **Manage sources**: Browse, rename, refresh/resume, or remove a source without deleting original files.
- **Storage & previews**: Scoped coverage, missing-preview repair, safe rebuild, clearing, and confirmed unused-preview cleanup.
- **Activity monitor**: Live progress, pause/cancel, saved indexing checkpoints, and recent results.
- **Offline honesty**: Cached hits never probe an unplugged drive. Volume GUID tracking resolves drive-letter changes automatically upon reconnect.
- **Originals are sacred**: Scanning and indexing are strictly read-only. File moves and copies stream to atomic sibling files before committing. Deletions route through the native Windows Recycle Bin.

---

## Frequently Asked Questions (FAQ)

### Why does Windows show a SmartScreen warning?
Luma is an open-source, community-distributed app without an expensive commercial code-signing certificate. Windows Defender SmartScreen may display "Windows protected your PC". Click **More info**, then **Run anyway** to launch the app safely.

### Why do some HEIC photos show an icon instead of a thumbnail?
Windows requires the official Microsoft Store "HEIF Image Extensions" to decode iPhone HEIC photos. When absent, Luma shows a placeholder with a direct link to install the extension from the Microsoft Store.

### Can I browse when my external hard drive is unplugged?
Yes! Luma saves thumbnails and metadata to your local drive. You can browse, search, favorite, tag, and inspect metadata while your drive is disconnected. Reconnecting the drive immediately restores the ability to open, copy, or rename originals.

### Where is Luma data stored?
All database catalogs, settings, and cached thumbnails live in `%LOCALAPPDATA%\Luma` on your PC. Your original photos and videos remain on their source drives and are never modified by scans.

### How do I reset or uninstall Luma?
- To reset your library: Close Luma and delete the `%LOCALAPPDATA%\Luma` folder.
- To uninstall: Simply delete the folder where you extracted `Luma-win-x64`. No registry keys or background services are left behind.

- **Library exclusions**: right-click to hide a folder, whole source or file extension, skip it during scans, and restore it without losing organization. [Exclusion guide](docs/EXCLUSIONS.md).
- Original app icon and Windows executable branding. [Identity and icon source](docs/BRANDING.md).

- **Manage sources**: browse, rename, refresh/resume or remove a source without deleting original files.
- **Refresh** menu: current source, all connected sources, or status-only checks.
- **Storage & previews**: scoped coverage, missing-preview repair, safe rebuild, clearing and confirmed unused-preview cleanup.
- **Activity**: live progress, pause/cancel, saved indexing checkpoints and recent results.

- Background read-only folder/drive scanning, cancellation, rescanning and safe stale-entry reconciliation after an error-free scan.
- Continuous virtualized gallery; year/month/day grouping; capture-date fallback; date jumps, search, source/folder/album filters, favorites and tags.
- Remembered thumbnail size, fit/crop, density, labels, format filters and sort direction.
- Virtual albums and stable catalog identities preserve organization across in-app moves.
- Default-app opening, Explorer reveal, copy/cut/paste, rename and Windows Recycle Bin deletion dialogs.
- Cached subfolder navigation with parent navigation, available offline without rescanning older libraries.
- Bulk offline preview preparation with cancellation/resume, coverage and cache usage, and confirmed cache clearing.
- PC-local SQLite catalog and bounded persistent thumbnails. Cached hits do not probe an external drive.
- Catalog-first reopening of known drives, in-place reconnect status, and visible-preview retry without a full scan. Use **Refresh → Refresh all connected sources** explicitly for new or changed files.
- Volume GUID/serial/folder identities that resolve a drive's current mount instead of trusting its old letter.
- Light/dark/system themes and configurable cache limit (1–32 GiB, applied at restart).

## Shortcuts

| Shortcut | Action |
| --- | --- |
| Ctrl+C / Ctrl+X | Copy / cut selected originals |
| Ctrl+V | Choose a destination folder and paste |
| Ctrl+mouse wheel | Resize gallery thumbnails |
| Ctrl+A | Select loaded results (explicit full-result selection is also available) |
| F2 | Rename selected original |
| Delete | Confirm deletion through Windows shell |
| Double-click | Open in the default app |

Text inputs keep their normal editing shortcuts. Files on offline drives can be browsed from cache but cannot be opened or modified. Luma skips existing filenames during its copy/move transfers; native Windows dialogs handle Recycle Bin deletion. See the build guide for operation reports and partial failures.

## Storage

`%LOCALAPPDATA%\Luma` contains the catalog, settings and thumbnails on the PC. Original files stay on their source volumes. Default thumbnail limit: 2 GiB. Offline thumbnail retention depends on cache capacity. Open **Storage & previews**, choose the selected source (or all sources), and **Repair missing previews**. Check the coverage shown there before disconnecting. Preparation preserves existing thumbnails when space runs out; ordinary browsing still uses LRU eviction. Previews are not backups of originals.

## Build and test

On Windows with .NET 8 SDK and Windows build tools (Visual Studio's Windows application development workload):

```powershell
dotnet test tests/Luma.Tests/Luma.Tests.csproj -c Release
dotnet test tests/Luma.Windows.Tests/Luma.Windows.Tests.csproj -c Release
dotnet publish src/Luma.App/Luma.App.csproj -c Release -r win-x64 -p:Platform=x64 -o artifacts/Luma-win-x64
./scripts/Smoke-Test.ps1 -Executable artifacts/Luma-win-x64/Luma.App.exe
```

Core tests also run on Linux. GitHub Actions builds the app, runs Windows integration tests, verifies its native window and uploads build/test evidence. See the specific workflow result before treating a binary as validated.

[Gallery build guide](docs/GALLERY_BUILD.md) · [Phase 2 guide](docs/PHASE_2.md) · [Gallery redesign plan](docs/GALLERY_REDESIGN.md) · [Roadmap](docs/ROADMAP.md) · [Phase 1 scope and limits](docs/PHASE_1.md) · [External-drive design](docs/EXTERNAL_DRIVES.md) · [QA](docs/QA.md)

### Indexing recovery and memories

Interrupted imports save progress on your PC. Use **Activity → Resume paused indexing** after reconnecting; **Manage sources → More → Restart indexing** starts fresh and Activity lists skipped folders. Your published gallery remains available while indexing finishes. **On this day** brings together matching capture dates from earlier years and works with saved offline previews. [Usage and limits](docs/GALLERY_BUILD.md#resumable-indexing).

## Final experience refinement

Saved browsing position, explicit discovery/date-reading progress, accessible action hints and theme-matched dialogs complete the planned build. See [behavior and limits](docs/FINAL_EXPERIENCE.md). Verified shipment evidence is recorded in the testing handoff.
