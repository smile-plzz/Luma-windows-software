# Luma

**A fast, local-only Windows media library for photos and videos scattered across your PC folders and external drives, without importing, duplicating, or risking originals.**

[User Guide](docs/USER_GUIDE.md) | [Troubleshooting](docs/TROUBLESHOOTING.md) | [Known Limits](docs/KNOWN_LIMITS.md) | [Releases](https://github.com/smile-plzz/Luma-windows-software/releases)

---

## Why Luma?

Your photos and videos stay exactly where they are on internal drives, USB drives, or network shares. Luma builds a lightweight SQLite catalog and bounded thumbnail cache on your PC (`%LOCALAPPDATA%\Luma`) so you can scroll smoothly, curate favorites, multi-word tags, and albums, explore places offline, and keep using your library even after unplugging external archive drives.

## 3-Step Quick Start

1. **Download:** Get the latest verified `win-x64` ZIP from [GitHub Releases](https://github.com/smile-plzz/Luma-windows-software/releases) (check `SHA256SUMS.txt`; currently signed releases are pending, see SmartScreen note below).
2. **Extract:** Right-click the ZIP -> **Extract All...** into a writable folder (keep all `.dll`, `.pri`, and `Assets` files beside `Luma.App.exe`), then launch `Luma.App.exe`.
3. **Add media:** Click **Add folder or drive** or drop a folder onto the welcome screen. Scanning is strictly read-only and you can browse as soon as the first thumbnails load.

Platform floor: **Windows 10 version 2004+ or Windows 11 (x64)**. Bundles .NET 8 Desktop Runtime and Windows App SDK dependencies.

---

## Core Capabilities

- **Safe Read-Only Indexing & Resumable Scans:** Background scanning saves checkpoints cleanly and supports Pause, Resume, Cancel, and Restart from **Activity**. Stale entries are reconciled only after an error-free scan.
- **External Drive Resilience & Offline Mode:** Drives track by Volume GUID/serial fingerprint rather than drive letter alone. Browse cached metadata, search, Favorites, multi-word tags, and virtual albums offline; reconnect seamlessly in place when plugged back in.
- **Calm, Customizable Browsing:** Virtualized timeline grouped by Year, Month, Day, or Session; **Uniform**, **Adaptive**, or **Moments** layouts; photo stacks; Month navigation with counts; remembered browse position across restarts.
- **Quick Preview & Triage (`Space`):** Instant preview with zoom, `Left`/`Right` stepping, keyboard triage (`F` favorite, `T` tag, `M` move, `I` details, `Delete` Recycle Bin), and explicit `Enter` handoff to play videos in your default Windows player.
- **Powerful Local Search & Curation:** Quote-aware search (`tag:"Family Trip"`, `location:"New York"`, `folder:"2025/Trips"`, `year:2024`, `ext:dng`, `is:fav`, `has:gps`, `size:>10mb`), removable filter chips, global Tag Manager, Duplicates Finder, Smart Collections, and **On this day** memories.
- **Privacy-First Map View:** Bundled offline world vector map by default; public OpenStreetMap street tiles load only after an explicit per-session opt-in click.
- **Originals Protected:** Non-overwriting Copy/Move transfers, **Undo move** with catalog link retention, Windows reserved-name/extension validation on rename, Recycle Bin deletion only, reversible Exclusions, and path-redacted diagnostics.

---

## Frequently Asked Questions

### Why does Windows show a SmartScreen warning?
Current portable builds are unsigned (`D-2`). Windows Defender SmartScreen shows "Windows protected your PC" on first download. Verify the published SHA-256 checksum if desired, then click **More info -> Run anyway**.

### Can I browse when an external drive is unplugged?
Yes. Catalog metadata, search, Favorites, multi-word tags, virtual albums, and prepared thumbnails stay available offline. Open **Storage & previews -> Repair missing previews** before disconnecting an archive drive to cache offline previews ahead of time. Opening or editing actual files on disk prompts you cleanly if the drive is offline.

### How do I play videos or view HEIC/RAW photos?
Quick Preview renders a still preview frame cleanly; press `Enter` or double-click any video to open it in your default Windows player (VLC, Photos, Movies & TV). Camera RAW and HEIC decoding rely on Windows Store Imaging/HEVC extensions installed on your PC (`docs/TROUBLESHOOTING.md`).

### Where is Luma's data stored, and how do I uninstall or reset?
All Luma state lives in `%LOCALAPPDATA%\Luma` (`catalog.db`, `thumbnails\`, `settings.json`, redacted logs). To reset the catalog while leaving originals untouched, use **Settings -> Reset catalog (keep originals)...**. To uninstall, close Luma, delete the extracted program folder, and optionally remove `%LOCALAPPDATA%\Luma`.

---

## Documentation Index

- **Users:** [User Guide](docs/USER_GUIDE.md) | [Troubleshooting](docs/TROUBLESHOOTING.md) | [Known Limits](docs/KNOWN_LIMITS.md) | [Changelog](CHANGELOG.md)
- **Engineering & Product:** [Product Brief](docs/PRODUCT_BRIEF.md) | [Requirements](docs/REQUIREMENTS.md) | [Decisions Log](docs/DECISIONS.md) | [Architecture](docs/assessment/ARCHITECTURE.md) | [Performance](docs/PERFORMANCE.md) | [Testing Handoff](docs/TESTING_HANDOFF.md) | [Release Checklist](docs/RELEASE_CHECKLIST.md)

## Build and Test from Source

On Windows x64 with .NET 8+ SDK and Windows App SDK build tools:

```powershell
dotnet test tests/Luma.Tests/Luma.Tests.csproj -c Release
dotnet test tests/Luma.Windows.Tests/Luma.Windows.Tests.csproj -c Release
dotnet publish src/Luma.App/Luma.App.csproj -c Release -r win-x64 -p:Platform=x64 -o artifacts/Luma-win-x64
./scripts/Smoke-Test.ps1 -Executable artifacts/Luma-win-x64/Luma.App.exe
```
