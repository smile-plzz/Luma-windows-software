# Luma User Guide

Welcome to Luma, a calm, local-only media gallery for Windows 10 (version 2004 or newer) and Windows 11 (x64).

Luma lets you browse photos and videos stored across your PC folders and external drives without importing, duplicating, or risking your original files.

## 1. Getting Started (First 60 Seconds)

1. Download the `win-x64` portable ZIP from the GitHub Releases page.
2. Right-click the ZIP and choose **Extract All...** into a writable folder (for example, `C:\Apps\Luma`). Do not run `Luma.App.exe` directly from inside Explorer's ZIP preview, and keep all bundled `.dll`, `.pri`, and `Assets` files beside `Luma.App.exe`.
3. On first launch, Windows Defender SmartScreen may warn that the app is unrecognized because Luma is currently unsigned. Select **More info**, then **Run anyway**. You can verify the ZIP against the published `SHA256SUMS.txt` digest before running.
4. Click **Add folder or drive** (or drag any folder onto the welcome screen). Scanning is strictly read-only: your first photos appear quickly while indexing continues in the background.

## 2. How Browsing & Viewing Work

- **Timeline & layouts:** Switch between **Uniform Grid**, **Adaptive** (aspect-ratio preserving), and **Moments** in Gallery View Options (`Sliders` button). Group by Year, Month, Day, or Session, or turn off grouping cleanly.
- **Month navigation & Day jump:** Use **Browse month**, **Older month**, **Newer month**, **Jump to day**, or **All months** to jump across decades without scrolling endlessly.
- **Photo stacks:** Similar burst/edited shots collapse cleanly under one cover thumbnail. Click the stack badge to expand or collapse all stacked items. Note: actions triggered on a collapsed stack apply to its cover photo.
- **Open Originals Externally (`Enter` / Double-Click):** Press `Enter` or double-click any item in the gallery (or click **Open** from a Map pin, filmstrip, or place drawer) to open the original photo or video in your preferred Windows default software. Favorites (`☆` / **Favorite**), tags (`Tags…`), move, rename (`F2`), delete (`Delete`), and metadata details work directly from gallery cards, the Selection Ribbon, and the right Details Inspector (`Open` / `Explorer` quick buttons) without an internal viewer overlay.
- **Loading & Single-Instance Transparency:** Startup catalog checks, heavy operations (duplicate scanning, catalog backup/restore/reset, cache clearing, undo moves), and two-window conflicts lock controls under a visible progress bar card (with **Cancel** or **Try again** / **Exit** when `cache.lock` is held by another Luma window). Background indexing and preview repair display an active banner above the gallery while letting you keep browsing.
- **Remembers where you left off:** When enabled in Settings, Luma restores your active view, filters, and scroll position on your next launch without rescanning external drives.

## 3. Organizing & Finding Media

- **Search bar (`Ctrl+F`):** Type any part of a filename, folder, tag, camera model, or place name. Use quotes for multi-word values:
  - `tag:"Family Trip"` `-tag:blur`
  - `location:"New York"` `has:gps`
  - `folder:"2025/Trips"` `ext:dng` `-ext:avi`
  - `year:2024` `after:2024-05-01` `before:2024-06-30`
  - `is:fav`, `is:untagged`, `is:portrait`, `is:landscape`, `is:4k`, `size:>10mb`
- **Multi-word tags & Tag Manager:** Separate multiple tags with commas (`,`) or semicolons (`;`). Spaces inside a tag name (such as `Family Trip`) are preserved cleanly. Manage, merge, rename, or delete tags globally from **Organize tools -> Manage tags...** or **Settings -> Manage tags...**.
- **Favorites & Virtual Albums:** Stored exclusively in Luma's local database on your PC. Drag selected items onto an album or use `+ Album` without moving originals.
- **On this day:** Surfaces media captured on this calendar day in past years (`Memory day` picker lets you explore any date).
- **Photo Map:** Uses bundled offline vector world maps by default. Your coordinates and files never leave your PC. Extracted GPS coordinates and EXIF metadata are cached persistently in `catalog.db` (`meta_version = 2` keyed by file size and modified timestamp) and memoized in memory per catalog change, so Map View never rescans unchanged files until new or modified media arrive. Loading public OpenStreetMap street tiles requires an explicit per-session opt-in click.

## 4. Working Safely with External Drives

- Luma identifies drives by Windows Volume GUID and serial fingerprint, not only drive letter. Replugging an archive HDD as `F:` instead of `D:` reconnects your existing library, tags, favorites, albums, and thumbnails in place.
- **What works offline (drive unplugged):** Browsing metadata, search, filters, On this day, Map overview, viewing prepared thumbnails, and editing Favorites, tags, virtual albums, and manual map pins.
- **What requires connecting the drive:** Opening full originals, showing in Explorer, copying, moving, renaming, undoing past moves back to that drive, deleting, and generating missing previews.

## 5. Touching Originals & Data Safety

- **Zero silent overwrites:** Copy/Move skip conflicting destination filenames by default. Review or undo recent moves from **Organize tools -> Last transfer / Undo...**.
- **Recycle Bin only:** Luma never permanently deletes files directly. Deletes route through Windows' confirmation dialog (`Delete to Recycle Bin...`).
- **Capture-date order:** Luma reads embedded camera/container dates first (`EXIF DateTimeOriginal`, Windows `System.Photo.DateTaken`, MP4/QuickTime creation tags, `XMP CreateDate`) and falls back to the earliest valid file timestamp (`min(Created, Modified)`).

## 6. What Luma Stores on This PC

By default, all Luma app data lives in `%LOCALAPPDATA%\Luma`:
- `catalog.db` (`-wal`, `-shm`, `.bak`): Sources, indexed metadata, favorites, tags, albums, exclusions, and scan checkpoints.
- `thumbnails\`: Bounded LRU disk cache of 256px/1024px preview files (`<hash>.thumb`). Limit is configurable from **1 to 32 GiB** in **Settings**.
- `settings.json`: Theme, layout preferences, saved browse position, and recent search/destination history.
- `last-transfer.json`: Summary used for **Undo move**.
- `crash.log` & `last-error.txt`: Local diagnostic logs with personal paths automatically redacted.

To uninstall Luma cleanly, close Luma, delete the extracted Luma app folder, and optionally delete `%LOCALAPPDATA%\Luma`. Your original photo/video folders are never stored inside this folder.

## 7. Keyboard Shortcuts (`F1`)

- `F1`: Open Keyboard shortcuts & help
- `Ctrl+F`: Focus search
- `Space`: Toggle focused card in Select mode
- `Enter`: Open in default Windows app / play video
- `Ctrl+A` / `Esc`: Select loaded cards / Clear selection or close overlay
- `Ctrl+C` / `Ctrl+X` / `Ctrl+V`: Copy, Cut, Paste originals
- `F2` / `Delete`: Rename (or Batch Rename multiple) / Recycle Bin
- `F5` / `Alt+Left`: Refresh / Back in browsing history
- `Ctrl+B` / `Ctrl+H`: Toggle Sidebar / Header toolbar
- `Home` / `End`: Jump to top or bottom of gallery

