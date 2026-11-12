# External-drive caching

Store the catalog and thumbnails under `%LOCALAPPDATA%\Luma` on the internal system disk, never on the source drive. The desktop host selects this location automatically; only the lower-level cache API accepts a custom path.

## Identity and reconnect

Windows registration reads the volume GUID, volume serial and relative folder without writing marker files. Drive letters are not identities. Resolution checks the canonical volume GUID and serial, then asks Windows for its current mount path so the same volume remains addressable to Shell and WinRT after a letter change. A changed serial is treated as a different source. Network shares and junction source aliases are rejected. A cloned volume can duplicate identity; clone collision detection remains future work. Older caller-provided IDs are retained but cannot automatically resolve: explicit source re-registration/migration will be needed.

Catalog reads retain offline records and determine availability using the identity resolver and file existence. A missing drive never deletes catalog entries. The continuous gallery uses internal cursor batches of up to 120 items and resolves source availability once per source per batch; there are no user-facing page controls. The legacy ListAsync API checks individual files. Original-file operations revalidate identity and existence.

## Thumbnails

Cache keys include source identity, relative path, file size, modification time, pixel size and a renderer version. No original file content is copied into the catalog. Encoded thumbnails are saved on the PC. A hit returns before any drive lookup. An offline miss returns null for a placeholder. The service serializes decoder work, rechecks cache after waiting, and validates file size/time and source identity before and after decoding. Windows uses its installed codecs; unsupported formats return null. No codec installation is attempted.

Files are written to a temporary sibling, flushed, then atomically renamed. Only complete entries become visible. A per-directory owner lock and in-process gate protect writes/reads. Startup removes abandoned temporary files and enforces the configured byte budget. LRU uses cache-file modification time and persists across restart. Oldest thumbnails may be evicted even when their source is offline; metadata is unaffected. The default budget is 2 GiB, with a maximum 16 MiB per thumbnail. Lower budgets are supported; the desktop Settings dialog persists a 1–32 GiB budget applied on next startup. One in-flight entry can temporarily exceed the retained byte budget. Startup enumerates cache files once; an in-memory ordered index makes subsequent LRU updates and evictions O(log n), avoiding directory scans on every write.

Missing or stale originals are not decoded under old keys. Existing cached thumbnails remain available until eviction; rescan updates keys after file changes. Same-size edits preserving modification times cannot be detected without hashing. A full error-free scan removes missing catalog entries; partial scans retain them. Catalog scans never delete entries on partial scans or disconnects.

## Limits

Windows CI covers volume resolution and bitmap generation. Physical USB replug testing remains a user acceptance requirement. Directory traversal skips child reparse points; registration rejects source ancestry junctions. File availability can still change immediately after a check; shell operations must revalidate identity at execution. Scans and preview preparation never change originals. Explicit copy/move/rename/delete actions use the file-operation safeguards documented in [the current build guide](GALLERY_BUILD.md).

## Phase 2 offline preparation

Preparation streams the catalog through a SQLite reader, reuses valid cached versions and generates missing previews one at a time. It uses an atomic non-evicting cache write: insufficient capacity leaves existing previews intact, including disconnected sources. Normal gallery browsing retains its existing LRU behavior and can still evict previews. Preparation is not pinning or archival storage.

Coverage matches current catalog version keys against a local cache-file snapshot, without resolving or accessing original drives. It reports retained previews / indexed media and total cache usage across all sources. It is a point-in-time count, not a guarantee that files can be decoded or retained forever. Rescan after changing originals. Missing, empty and oversized cache files do not count; same-size corrupt cache content is not detected by coverage.

Cancellation keeps completed entries, so another run resumes from cache hits. Source/file mutations inside Luma are blocked while preparation runs. A final coverage pass checks retained keys. Closing the window cancels preparation and waits for all registered cache requests, including canceled gallery loads, before releasing ownership.

Folders are derived directly from indexed relative paths. Only folders containing indexed media (including descendants) appear, and counts include descendants regardless of the active search/filter. No schema migration or rescan is needed for a Phase 1 catalog. Empty folders are intentionally absent.

## Reconnect and reopen optimization

Known source identities reopen from the saved catalog without enumerating the drive, including empty sources. New identities receive an initial scan. Reconnect checks update loaded-card availability in place and retry visible missing previews, preserving scroll and selection. They do not reindex or invalidate valid thumbnails. Checks occur on activation, periodically when idle, or via **Refresh → Update drive status only**. **Refresh all connected sources** is the explicit file-discovery path; subsequent metadata enrichment is limited to the scanned sources.

## Interrupted indexing

New imports and explicit scans checkpoint on the PC every 100 media records and at folder boundaries. Resume uses stable source identity, skips completed folders and reuses records in the interrupted folder while enumerating its filenames. The published catalog remains intact until traversal finishes. Activity exposes paused indexing and skipped-folder reports; Manage sources offers **More → Restart indexing**. Folder changes can invalidate a saved snapshot; content edits without a directory timestamp change require a subsequent fresh scan. See [resumable indexing](GALLERY_BUILD.md#resumable-indexing) for recovery, access-denied behavior and limits. Ordinary reconnect remains scan-free.

On this day uses version-matched stored capture dates and the same offline thumbnail cache. It does not need to scan or read original files to select memories.

## Exclusions and packaging update

Reversible folder, source and global extension rules now filter browsing and future scans. Use Settings or Refresh → Manage exclusions to restore them; Reset does not remove rules. Originals and saved organization are retained. Changing rules invalidates affected paused traversal checkpoints, not the published catalog. See [usage and limits](EXCLUSIONS.md) and [executable branding](BRANDING.md).
