# Luma Known Limits

Honest summary of what Luma does today, current boundaries, and platform constraints as of v0.7.0.

## Platform & Distribution
- **Windows x64 only:** Requires Windows 10 version 2004 (build 19041) or later, or Windows 11 on `x64`. `ARM64`, macOS, and Linux desktop UI builds are not supported.
- **Unsigned portable binary:** Triggering Windows Defender SmartScreen on first download is expected until an Authenticode signing decision (`D-2`) is made. Always extract the entire ZIP to a writable folder and verify `SHA256SUMS.txt`.

## Scanning & External Changes
- **Explicit Rescan for external edits:** After initial indexing, Luma does not run a permanent NTFS/USN filesystem watcher across external drives in the background. Files added, renamed, or deleted outside Luma appear after clicking **Refresh library** (and moved files can reconcile via saved size/timestamp heuristics).
- **OneDrive / Cloud placeholders skipped:** Offline cloud-only reparse/placeholder files are intentionally skipped during scans so indexing never forces accidental cloud downloads.
- **Symlinks & junction points skipped:** Prevents directory loops and duplicate indexing.

## Formats, Codecs, and Videos
- **External photo viewing and video playback:** Luma focuses on fast library browsing and organization, without an internal full-screen viewer overlay or embedded video player (`D-11`, `D-13`). Pressing `Enter`, double-clicking, or clicking **Open** (in gallery menus or Map) hands originals directly to your Windows default app.
- **System codec dependency:** RAW formats (CR2, CR3, NEF, ARW, DNG), HEIC/HEIF, AVIF, and certain MKV/MOV streams depend on Windows Imaging / Store Extensions installed on your PC. Missing codecs fall back cleanly to accessible placeholder cards.
- **Embedded GPS/EXIF tolerance:** Exif/QuickTime/XMP metadata extraction runs read-only and skips corrupt files safely; capture dates outside valid calendar bounds fall back to earliest valid filesystem timestamps (`D-12`).

## Offline Browsing & Cache Bounds
- **Offline previews must be prepared or viewed first:** Unplugging a drive keeps catalog metadata (`name`, `folder`, `date`, `tags`, `favorites`, `albums`) readable, plus thumbnails that were previously viewed or generated via **Storage & previews -> Repair missing previews**.
- **Single global cache ceiling (LRU):** Cached `.thumb` previews live inside `%LOCALAPPDATA%\Luma\thumbnails` up to the configured 1 to 32 GiB ceiling (applied on next launch). Once the limit is reached, the oldest unused thumbnails are evicted first, including offline previews if space is needed.
- **500 MB low-disk safety floor:** Generating new previews pauses below 500 MB free space on the cache drive; already-cached thumbnails still render cleanly.

## Original-File & Stack Operations
- **No silent overwrites:** Copying or moving files into a folder that already contains the same filename skips the conflicting file cleanly rather than overwriting originals.
- **Collapsed photo stacks:** When **Stack similar photos** is enabled, quick actions (Favorite, Delete, Rename, Move) apply to the collapsed stack's cover item unless you expand the stack first.
- **Recycle Bin dependency:** Delete operations delegate to Windows Shell Recycle Bin confirmation. Removable FAT32/exFAT drives or network shares where Windows does not support a Recycle Bin display Windows' permanent-delete warning prompt.

## Privacy & Map Tiles
- **Offline map default:** Luma never contacts remote map servers on startup, scanning, or map launch. Only clicking **Load online map (this session)** requests public OpenStreetMap tiles in WebView2. Your files and coordinates never upload anywhere.

