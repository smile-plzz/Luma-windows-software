# Phase 2: deliberate offline browsing

> Historical Phase 2 build notes. In the current interface, use **Storage & previews → Repair missing previews** for offline preparation; coverage is shown in that same screen. See [the current handoff](TESTING_HANDOFF.md).

Historical milestone record. Both phases and the subsequent gallery redesign are merged. For current behavior use [GALLERY_BUILD.md](GALLERY_BUILD.md); for the shipped download and real-life testing use [TESTING_HANDOFF.md](TESTING_HANDOFF.md). Paging and deferred-feature descriptions below refer to this historical phase.

## Scope

This phase makes offline browsing useful without visiting every media page first. It adds bulk thumbnail preparation, honest coverage reporting and cached folder navigation to the Phase 1 Windows app. The original media stays on its source drive.

| Capability | Behavior |
| --- | --- |
| Prepare offline previews | Processes the selected source or all sources, ignoring view/search/folder filters |
| Progress and cancellation | Counts checked entries; Cancel preserves completed previews; rerun to resume |
| Capacity safety | Preparation never evicts existing entries to make room; page browsing still uses LRU |
| Offline coverage | Current-version previews / indexed media; global cache usage and limit; no original-drive access |
| Cached folders | Select one source, choose a subfolder, then use Up to parent folder; works with existing catalogs offline |
| Clear cache | Explicit confirmation removes all saved previews; catalog, tags, favorites and originals remain |
| Shutdown | Cancels preparation and waits for pending cache work before releasing its directory lock |

## Using it

1. Connect the drive, add its source or rescan to pick up current file versions.
2. Select that source and choose **Prepare offline previews**. Search and folder filters do not limit preparation.
3. Read the saved preview count. If incomplete, reconnect missing drives, rescan changed files, check format/codec support or increase the cache limit in Settings and restart. Preparation does not install codecs or silently delete existing previews.
4. Choose **Check offline coverage** immediately before unplugging. It is a snapshot of saved previews, not a backup guarantee. Ordinary browsing can evict old previews later.
5. Browse source folders, favorites, tags and search offline. Opening or changing originals still requires the drive.

The sidebar can scroll on smaller screens. Folder counts include nested media; the media grid also includes descendants. Empty folders and folders without supported media are absent. The typed folder filter remains available.

## Upgrade and recovery

Extract the complete new portable build into a fresh directory and run `Luma.App.exe`. It reads the same `%LOCALAPPDATA%\Luma` catalog/settings/cache as Phase 1, with no destructive schema migration. Only one app instance can own the thumbnail cache.

Preparation can be rerun after cancellation or restart. Successfully stored entries remain useful. Cache clearing affects every source, including disconnected drives; those previews require the originals to regenerate. Clearing does not automatically refill the current page; subsequent browsing may do so.

## Acceptance boundaries

Automated coverage is described in [QA](QA.md). Physical USB replug, low-disk faults and large-library performance remain manual acceptance work. Coverage verifies cache-file presence and basic size validity, not successful decoding of every saved image. It excludes outdated keys after a rescan. It cannot detect changed originals before a rescan or same-size edits that preserve modification time.

Capture dates/EXIF, albums, filesystem watching, thumbnail pinning and distribution signing are separate roadmap work, not included in this phase.
