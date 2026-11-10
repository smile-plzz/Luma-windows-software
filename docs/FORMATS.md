# Luma Supported Media Formats Matrix

Baseline: v0.7.0
Date: 2026-11-11

---

## 1. Supported Format Matrix

| Format | Extensions | Primary Decoder | Fallback | Codec Requirement | Corrupt File Handling |
|---|---|---|---|---|---|
| JPEG | `.jpg`, `.jpeg` | WinRT WIC BitmapDecoder | Shell Thumbnail | Native Windows component | Catch COMException, return null placeholder |
| PNG | `.png` | WinRT WIC BitmapDecoder | Shell Thumbnail | Native Windows component | Catch COMException, return null placeholder |
| GIF | `.gif` | WinRT WIC BitmapDecoder (first frame) | Shell Thumbnail | Native Windows component | Extract frame 0 safely |
| BMP | `.bmp` | WinRT WIC BitmapDecoder | Shell Thumbnail | Native Windows component | Native Windows support |
| WebP | `.webp` | WinRT WIC WebP Decoder | Shell Thumbnail | Native Windows 10/11 component | Fallback to icon placeholder |
| TIFF | `.tif`, `.tiff` | WinRT WIC TIFF Decoder | Shell Thumbnail | Native Windows component | Catch COMException, return null placeholder |
| HEIC/HEIF | `.heic`, `.heif` | Windows HEIF Image Extension | Shell Thumbnail | HEIF Image Extension (Store) | Cached in missingCodecs, show Store link |
| AVIF | `.avif` | Windows AV1 Video Extension | Shell Thumbnail | AV1 Video Extension (Store) | Catch COMException, return null placeholder |
| RAW (DNG) | `.dng` | WinRT WIC Raw Decoder | Shell Thumbnail | Native Windows 10/11 component | Bounded decode downscale |
| RAW (Camera) | `.cr2`, `.cr3`, `.nef`, `.arw`, `.orf`, `.rw2` | Raw Image Extension | Shell Thumbnail | Microsoft Raw Image Extension (Store) | Cached in missingCodecs, show Store link |
| MP4 | `.mp4`, `.m4v` | WinRT Video Thumbnail | Shell Thumbnail | Native Media Foundation | Extract poster frame with duration badge |
| QuickTime | `.mov` | WinRT Video Thumbnail | Shell Thumbnail | Native Media Foundation | Extract poster frame with duration badge |
| Matroska | `.mkv` | WinRT Video Thumbnail | Shell Thumbnail | Native Media Foundation | Extract poster frame with duration badge |
| AVI | `.avi` | WinRT Video Thumbnail | Shell Thumbnail | Native Media Foundation | Extract poster frame with duration badge |
| WebM | `.webm` | WinRT Video Thumbnail | Shell Thumbnail | WebM Video Extension (Store) | Extract poster frame with duration badge |

---

## 2. In-App Codec Guidance

When decoding fails due to a missing Windows codec:
1. The failure state is cached in memory per file extension (`missingCodecs`).
2. The user is not repeatedly notified for every single item.
3. An actionable notification banner provides a safe Microsoft Store link:
   - HEIF/HEIC: `ms-windows-store://pdp/?ProductId=9pmmsr1c69tx`
   - RAW Image Extension: `ms-windows-store://pdp/?ProductId=9nctdw2w1bh8`
   - HEVC Video Extension: `ms-windows-store://pdp/?ProductId=9n4wgh0z6vhq`
   - AV1 Video Extension: `ms-windows-store://pdp/?ProductId=9mvzqbx419yt`
4. The media item remains cataloged, searchable, and can still be opened in its default application.

