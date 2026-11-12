# Luma Product Brief

## One-Line Positioning

A calm, fast, local-only Windows media library for photos and videos stored across your PC folders and external drives, without importing, duplicating, or risking originals.

## Core Intent

"I have years of photos and videos scattered across my PC and external drives. I want one fast, calm place to browse them, find things, and keep them organized, without importing, duplicating, uploading, or risking my originals."

## Target Personas

### Primary Persona: Multi-Drive Desktop Collector
A Windows 10/11 desktop user with tens of thousands to hundreds of thousands of personal photos and videos spread across internal drives, NAS/network shares, and external USB drives. Mildly technical, privacy-conscious, dislikes cloud subscription lock-in and heavy catalog tools that copy or restructure folders on disk.

### Secondary Persona: Archival External-Drive Browser
Someone who keeps large historical archives on external HDDs stored in a drawer and connects them only occasionally, yet wants to browse what is on those drives, check favorite memories, and manage albums or tags even when the drives are unplugged.

## Jobs to Be Done

1. **Add and index safely:** Register any folder or drive and start browsing early results quickly, with honest progress, pause/resume, cancel, and resumable checkpoints on large or slow drives.
2. **Browse smoothly at scale:** Scroll a responsive, date-grouped timeline (Months, Adaptive, or Moments rows; collapsed photo stacks; month navigation; saved position) without stutters or blank thumbnails.
3. **Find anything fast:** Search by filename, folder, tag, date, or map location; narrow by source, album, file format, favorites, and smart collections with accurate counts and one-click Reset.
4. **Keep durable organization:** Save favorites, multi-word tags, and virtual albums in a local SQLite catalog linked by persistent media IDs and volume/relative identities that survive drive-letter changes and in-app moves or renames.
5. **Browse disconnected drives honestly:** View cached metadata and prepared offline previews when an external HDD is unplugged, know clearly what actions require the drive online, and reconnect seamlessly in place when plugged back in.
6. **Operate on originals safely:** Open in the default app, reveal in Explorer, copy, cut/move, rename, batch-rename, or recycle originals with explicit confirmation, zero silent overwrites, and clear partial-failure reporting.
7. **Trust the app:** Know that scanning is strictly read-only, cache bounds and disk-space guards protect the PC, crash logs redact personal folder paths, and no telemetry leaves the machine.
8. **Rediscover memories and places:** Revisit capture-date "On this day" memories and explore geotagged photos on an offline world map (or optional online map tiles only when explicitly chosen).

## Explicit Non-Goals

- Cloud storage, account sign-in, automatic syncing, or background telemetry.
- Non-destructive or destructive image/video editing (crop, color grading, transcoding).
- Built-in video playback engine or transcoding codecs (Luma previews frames and hands playback to the Windows default app).
- AI cloud face recognition or remote analytics.
- Copying, importing, or restructuring user folders into a proprietary folder tree.
- Cross-platform macOS/Linux UI (Luma targets Windows 10 2004+ and Windows 11 x64).

## Distribution & Business Model Reality

- Free, local-only Windows desktop software built on .NET 8 and Windows App SDK.
- Currently shipped as a self-contained portable `win-x64` ZIP (plus an optional Inno Setup installer script under evaluation).
- Unsigned binaries trigger Windows SmartScreen on first download; documentation states this plainly and provides SHA-256 checksums for verification.

