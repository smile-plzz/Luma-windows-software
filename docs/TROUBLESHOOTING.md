# Luma Troubleshooting Guide

Plain-language solutions for the most common issues when setting up or using Luma.

## Windows SmartScreen warns "Windows protected your PC"
- **Why:** Luma portable releases are currently unsigned while development stabilizes (`D-2`).
- **What to do:** Click **More info** -> **Run anyway** only after downloading directly from the official GitHub Releases page and verifying the `SHA256SUMS.txt` checksum in PowerShell (`Get-FileHash Luma-v0.7.0-win-x64.zip -Algorithm SHA256`).

## Luma opens and immediately closes, or shows missing DLL errors
- **Why:** Windows Explorer lets you double-click inside an unextracted `.zip` archive, which runs `Luma.App.exe` alone in a temporary folder without its bundled runtime and `Assets/` files.
- **What to do:** Right-click the ZIP, choose **Extract All...**, extract every file into its own folder (e.g., `C:\Apps\Luma`), and launch `Luma.App.exe` from there. Never delete `Assets\` or `.dll`/`.pri` files beside the `.exe`.

## Photos or folders seem missing after adding a drive
Check these causes in order:
1. **Scan is still running or paused:** Open **Activity** in the bottom-right footer. If paused, click **Resume paused indexing**. Also check for **Skipped** folder warnings caused by Windows permissions.
2. **Active search, month, album, or visual filters:** Look below the toolbar for removable filter chips or click **Reset**.
3. **Exclusion rules:** Open **Refresh -> Manage exclusions...** (or click **Manage exclusions...** in the empty screen) to restore any excluded folder, source, or file extension.
4. **Cloud placeholders (OneDrive Files On-Demand):** Luma intentionally skips offline cloud-only placeholder files to avoid triggering unwanted multi-gigabyte downloads during a local scan.

## Thumbnails are blank for HEIC, AVIF, or camera RAW files (CR2, CR3, NEF, ARW)
- **Why:** Luma uses Windows Imaging Component (WIC) codecs installed on your PC. A fresh Windows installation may lack Microsoft's **HEIF Image Extensions**, **HEVC Video Extensions**, or **Raw Image Extension**.
- **What to do:**
  1. Install the needed free Windows extensions from the Microsoft Store (and HEVC if decoding iPhone video/HEIC streams).
  2. Open **Storage & previews -> Repair missing previews** in Luma to regenerate previews using the newly installed codecs.

## Opening photos full-screen or playing videos
- **Why:** By design (`D-11`, `D-13`), Luma keeps indexing and browsing lightweight and delegates full-screen photo viewing and video playback to the default apps installed on your PC.
- **What to do:** Press `Enter`, double-click the item in the gallery, or click **Open** (in gallery menus or Map View) to open the original file in your preferred Windows app (Photos, IrfanView, VLC, Movies & TV).

## External drive shows "Offline" even though it is plugged in
- **What to do:**
  1. Wait a few seconds or click **Refresh -> Update drive status only** (`Refresh drive status`) to probe currently attached volumes without rescanning files.
  2. If the drive was reformatted or cloned (giving it a new Volume GUID and serial), add the newly mounted drive via `+ Add folder or drive` and remove the old retired entry in **Manage sources** after checking your organization backups.
  3. On network shares (`\\SERVER\Photos`), make sure Windows Explorer can access the share first (stored credentials active) before updating drive status.

## Low disk space banner ("< 500 MB free on system drive")
- **What happens:** To protect Windows stability, Luma pauses writing NEW `.thumb` files and background repairs when free space on the system/cache drive falls below 500 MB. Already-cached offline previews continue loading cleanly (`F-008`).
- **What to do:** Free space on your system drive, lower the cache budget in **Settings -> Thumbnail cache limit** (applies next restart), or run **Storage & previews -> Clean unused previews**.

## Catalog seems stuck, stale, or slow after a crash
1. Try **Settings -> Copy diagnostic info** first if reporting a bug (`Settings -> Send feedback / Report a problem...`).
2. Use **Settings -> Back up catalog...** to save a snapshot before troubleshooting.
3. If needed, run **Settings -> Reset catalog (keep originals)...**. This rebuilds only Luma's local database (`catalog.db`) cleanly while leaving every original photo, video, and folder 100% untouched.

## Still stuck?
Use **Settings -> Send feedback / Report a problem...** (or open [GitHub Issues](https://github.com/smile-plzz/Luma-windows-software/issues/new/choose)) and attach the redacted diagnostic snippet. Never share private folder paths publicly.

