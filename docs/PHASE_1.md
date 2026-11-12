# Phase 1: usable native media library

Historical milestone record. Both phases and the subsequent gallery redesign are merged. For current behavior use [GALLERY_BUILD.md](GALLERY_BUILD.md); for the shipped download and real-life testing use [TESTING_HANDOFF.md](TESTING_HANDOFF.md). Paging and deferred-feature descriptions below refer to this historical phase.

## Acceptance scope

Phase 1 delivers a Windows desktop app that can index existing folders/drives, browse photos and videos as thumbnails, preserve a useful offline catalog, and perform familiar file actions. It is local-first, uses Windows default apps to open originals, and has no login, media player or cloud backend.

| Area | Phase 1 behavior |
| --- | --- |
| Native app | WinUI 3, system/light/dark theme, accessible named controls, Windows x64 portable build |
| Sources | Add folder/drive through Windows picker, rescan/cancel, online/offline indicator, remove catalog source without deleting originals |
| Library | Virtualized thumbnail grid; pages of 120 bound memory and rendering work |
| Discovery | Name/folder/tag search, photo/video/favorite filters, source and relative-folder filters; name/date/size ordering |
| Timeline | Month groups based on file modification time; group headers repeat across pages where needed |
| Organization | Persistent favorites and comma-separated tags; tags edited for the selected item, favorites support multiple selections |
| Opening | Double-click or Open launches the Windows default app; Show in Explorer and Copy Path |
| File actions | Multi-select copy/cut, paste into a picked folder, rename without overwrite, deletion through Windows Recycle Bin dialogs |
| Safety | Confirm delete/remove source, native collision prompts, source identity revalidation, unchanged originals during scans |
| Cache | PC-local metadata/thumbnails; cache-first browsing, bounded LRU, crash-safe writes, configured limit and changed-drive-letter resolution |
| Delivery | Self-contained Windows x64 build artifact, core tests, Windows integration tests, native-window smoke check |

Phase 1 is the baseline milestone. See [Phase 2](PHASE_2.md) for offline preparation and cached folder navigation added afterward.

## Defined limits (at Phase 1)

- Windows 10 2004+ or Windows 11, x64. Not a macOS/Linux app. No signing certificate or installer in Phase 1.
- Dates currently use file modification time. EXIF capture dates/dimensions, geolocation, albums, drag-and-drop, tree-style folder navigation, thumbnail-size controls and richer undo/history are follow-up features.
- Offline viewing includes metadata and thumbnails previously browsed; uncached media shows placeholders. Cache eviction can remove offline thumbnails. It does not store full originals.
- Rescan is manual. Source availability is checked periodically. No filesystem watcher or automatic full reindex.
- Core reading is paged; whole-source scans remain transactional. 100k-item performance has not been certified.
- Windows-installed codecs determine HEIC/video support. Unknown/corrupt media stays a placeholder.
- Clipboard supports real files, not virtual items/folders. Multi-select applies to the current page. Shell operations may block the app while Windows shows its native operation dialog.
- File moves performed outside the app, including cut/paste, do not preserve tags across paths. In-app rename preserves the selected item's tags/favorite.
- Native Windows shell dialogs control conflict resolution and whether a drive supports recycling; use Explorer/Recycle Bin for restore. Do not assume an external drive supports undo.
- Network sources and source-folder junction aliases are not supported. Cloned volume identity collisions need a future identity migration workflow.

## User acceptance on a real PC

1. Add a folder with photos/videos; confirm the app displays media and opens it in the default app.
2. Test all filters, paging, tags, favorites and timeline; restart and verify saved organization.
3. Browse an external-drive folder, disconnect it, and verify cached thumbnails remain. Reconnect with a changed drive letter and open the same file.
4. Use disposable fixtures to test copy, cut/paste, rename, cancellation, conflicting destinations and Recycle Bin recovery.
5. Test HEIC/MP4 with installed codecs, slow drives, inaccessible subfolders and real USB disconnects.

Automated tests cannot substitute for the physical-drive and destructive-operation acceptance checks above.
