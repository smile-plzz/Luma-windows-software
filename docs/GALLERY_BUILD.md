# Gallery build: usage, architecture and acceptance

This build replaces the paged Phase 2 interface with a native chronological gallery, configurable views, versioned metadata and virtual albums. It keeps the existing catalog, volume identity and offline preview cache.

The implementation is merged into `main`; development is closed for this milestone and real-life testing is beginning. See [the shipment and testing handoff](TESTING_HANDOFF.md) for the exact verified download and feedback procedure.

## Install and upgrade

Download **Luma-Gallery-win-x64** from a successful Build and test workflow run, extract the entire ZIP into a fresh writable folder and launch `Luma.App.exe`. Keep all bundled DLLs and resources beside it. The repository file listing contains source code; the runnable Windows package is an Actions artifact. Windows 10 2004+ / Windows 11 x64 are supported. The portable package is unsigned.

Existing data remains in `%LOCALAPPDATA%\Luma`. Before upgrading an important library, close Luma and copy that folder as a backup. New identity, metadata and album tables are additive; initialization can run repeatedly without replacing organization. Do not run an older build against a library after using the new organization features: older builds do not maintain album identity during file operations.

## Browse

- **Library** opens a chronological gallery, grouped by month and sorted by date taken with modification-time fallback. Photos, Videos and Favorites filter it.
- Scroll continuously in both directions. Database batches remain internal; there are no Next/Previous buttons. File records already loaded stay available for navigation and selection, while native container recycling releases decoded images outside the realized region.
- **View** changes thumbnail size, compact spacing, fit/crop, filename/detail visibility, date grouping, everyday formats and nested-folder inclusion. These preferences persist. Ctrl+mouse-wheel resizes thumbnails.
- Sort by date taken, date modified, name, size or extension; use the direction button for ascending/descending. Name/size/type show an ungrouped grid. Group by year, month, day or none when using date ordering.
- **Browse month** shows month names and filtered item counts. Selecting a month shows only that local-calendar month, in either sort direction. Older/newer buttons visit adjacent non-empty months; All months removes the date range. **Reset** restores the full result set and clears search/source/folder/album filters.
- Choose a drive/source, enter its cached subfolders and use **Up to parent folder**. A friendly source label can be assigned in **Manage sources**. Folder counts include descendants even when the grid's nested-folder option is off.
- **Details** opens the optional inspector for the selected file, metadata provenance and tags. The selection toolbar appears only when needed.

Everyday formats include JPEG/JPG, PNG, HEIC/HEIF, GIF, WebP, BMP, MP4 and MOV. Disable the everyday-only view to include other supported indexed extensions. A recognized extension does not guarantee a Windows preview codec; unsupported previews remain placeholders and originals open in their associated application.

## Timeline metadata

Scanning discovers files first, makes the gallery available, and then enriches changed versions with Windows image/video properties. Enrichment completion does not jump or reorder the active gallery; navigate or Reset to apply new dates. **Manage sources → More → Refresh metadata** enriches older catalogs without a full scan. Completed metadata survives cancellation and is retained offline. Unchanged versions are not decoded repeatedly.

Photos use the date taken reported by Windows when plausible. The original camera timezone is not verified, and that limitation is shown in the inspector. Video encoding dates are not labeled capture dates; videos use modification time for timeline ordering. Dimensions, orientation and duration are stored when available. Missing or unsupported properties fall back explicitly.

Metadata is joined only when its recorded size and modification time match the catalog version. Rescan after editing originals. Same-size edits preserving modification timestamps remain undetectable without content hashing.

## Albums and selection

Create, rename and delete virtual albums in the sidebar. Select files and use **Add to album** from the inspector or context menu. Removing membership or deleting an album never deletes original files. To add by drag-and-drop, select the destination album and drop Luma items onto the album selector; this changes membership only and works with offline catalog items.

Ctrl-click and Shift-selection follow native Windows behavior. Ctrl+A selects the currently loaded results and reports that scope. **Select all matching results** explicitly confirms the total, loads lightweight records and selects the whole result set. Changing filters cancels that expansion. Image decoding remains tied to realized tiles. File actions always state a selected-file count.

Favorites support multi-selection. Saving tags for multiple files confirms that their tag lists will be replaced. Source removal also removes its album memberships and annotations; deleting an album does not remove a source.

## File control

Open/default-app, Explorer reveal, copy path, clipboard copy/cut/paste, rename and native Recycle Bin deletion remain available. Cut selections appear dimmed until a new clipboard operation or successful in-app paste. Text inputs retain their editing shortcuts.

**Copy to folder** and **Move to folder** provide direct context-menu actions. Dropping real files on cached folders defaults to copy; hold Shift for move. A destination/count confirmation precedes the transfer. Existing target filenames are skipped rather than overwritten; choose another destination/name to resolve a conflict. Only real files are transferred; add folders as sources or use Explorer for folder-level moves.

Copies stream to a private temporary sibling, honor cancellation, preserve modification time, revalidate the source, and publish by rename. A move removes the original only after publishing the complete copy and checking the source again. Existing-file conflicts and partial failures keep a per-file outcome. Successful in-app moves/renames retain stable IDs, favorites, tags and album membership. A destination outside registered sources is registered so moved items remain discoverable.

The latest transfer report is `%LOCALAPPDATA%\Luma\last-transfer.json`. If files moved but a catalog update failed, the app reports it and avoids an automatic reconciliation that could discard the old organization. Use the report to restore the original path or reconcile deliberately. Filesystem changes and database transactions are not a single rollbackable operation. External Explorer moves, including moves completed after dragging out of Luma, do not currently preserve catalog organization automatically.

## Offline behavior and activity

Refresh controls scanning, Manage sources provides metadata refresh, and Storage & previews provides offline preparation, coverage and cache clearing. The footer reports work and provides cancellation. The existing Phase 2 non-evicting bulk preparation and bounded browsing LRU remain in force: preparation preserves existing previews at capacity, but later browsing may evict previews. Cached media and virtual organization remain useful without the drive; original-file operations require a connected, unchanged file.

Availability checks update source indicators and loaded-card availability in place without rebuilding the gallery or losing selection/scroll. They also retry visible missing previews when the source reconnects. Checks run periodically (30 seconds), when the window becomes active, or on **Refresh → Update drive status only**. A file action always resolves identity and validates its version again. No original is moved merely by scanning, enriching metadata or organizing an album.

## Verification

Core coverage includes cursor traversal with tied sort values in both directions, catalog migrations, albums following relocations, metadata invalidation/enrichment, folder depth and format filters, date jumps, collision-free transfer, cancellation, and all previous offline/cache regressions. Scale cases exercise 1k, 10k, 100k and 200k synthetic SQLite records and save first/next-batch and month-index timings with OS information. Those figures measure catalog queries, not USB throughput or full-library image decoding.

The Windows integration suite checks actual volume identity plus BMP, JPEG and PNG thumbnail decoding and dimensions. The native UI smoke uses 260 isolated synthetic images, scrolls across several internal batches and back, checks the peak decoded-image count, and exercises search, favorites, virtual albums, folders, clearing/preparing previews and a simulated unavailable source. It restarts the isolated library and verifies thumbnail/label preferences are restored. It writes screenshot, coverage and image-lifetime evidence.

Physical USB reconnects, low-disk faults, native Recycle Bin prompts, Explorer drag/drop and a diverse real-world codec set still need acceptance on the user's Windows bench. Synthetic CI screenshots are functional evidence, not a substitute for visual review with personal photos/videos. The build does not claim a measured 100k-image scrolling/frame-rate guarantee. Lightweight loaded records accumulate during exploration; decoded images are recycled. There is no cloud account, embedded player/editor, automatic original-file rearrangement, signing certificate or installer.

## Browsing while importing

The gallery reads the last committed catalog while a scan indexes a drive. Newly discovered files appear after that source scan commits; cancelling retains the previous committed catalog. The import-lock fix uses a deferred read transaction so browsing does not compete for the scan's SQLite write lock.

If an older build shows `SQLite Error 5: database is locked` during indexing, let indexing finish (or use Cancel), then choose Reset. Do not delete the catalog or rescan repeatedly in response to that message. The [current verified shipment](TESTING_HANDOFF.md) includes this fix and supports concurrent browsing. This workaround addresses the reported gallery/scan conflict; persistent locking with no scan running should be reported separately.

## Visual refinement after initial testing

The polished gallery uses a soft neutral sidebar, a quieter photo canvas, rounded borderless controls and a single compact view toolbar. Light and dark palettes adapt with the existing theme setting, and high-contrast colors use system resources. Folder navigation is shown after selecting a source; maintenance lives in Manage sources, Storage & previews and Activity. File counts and progress stay visible without database/loading terminology dominating the gallery. Loaded transparent previews no longer show a placeholder icon underneath them.

The interaction model stays the same: Windows selection and shortcuts, the optional inspector, accessible control names, continuous browsing and default-app opening. This is a frontend refinement, with the separately documented import-lock correction included in the same follow-up build.

## Fast reopening of external drives

After the first completed scan, adding the same registered folder/drive again reuses its PC-local catalog instead of enumerating the HDD. Sources with zero indexed media are remembered too. Browsing reads catalog data and saved thumbnail files immediately; cache hits do not probe the original drive. Missing previews are generated only for visible items unless you request bulk preparation.

Reconnecting updates source/card availability and retries visible missing previews without clearing cards, selection or scroll position. Original-file actions still resolve the actual volume identity and validate the file before use. The status refresh checks registered roots; it does not walk source directories or discover files. It is available through **Refresh → Update drive status only** and runs on window activation and every 30 seconds when idle.

Use **Refresh all connected sources** to discover additions, deletions or edits made outside Luma. This remains an explicit directory walk, not a filesystem journal or incremental change detector. Metadata enrichment following a scan is now limited to the sources actually scanned, and unchanged file versions continue to reuse saved metadata. For reliable offline browsing, prepare previews and check coverage before unplugging; bounded cache eviction rules are unchanged.

## Resumable indexing

Indexing saves file records in batches of 100 and folder completion checkpoints in the PC-local database. Cancellation, app closure, process termination or a disconnect retains committed checkpoints. The previous published gallery, albums and annotations remain available; the new snapshot replaces it only when traversal finishes. Up to the current uncommitted batch is repeated after interruption.

Open **Activity → Resume paused indexing** after reconnecting to continue all paused, connected sources. **Refresh current source** and **Refresh all connected sources** resume pending checkpoints in their respective scopes. Adding a source with unfinished indexing also resumes it. Completed source reopening and ordinary drive reconnect remain catalog-only. Paused sources appear in Activity and Manage sources after restarting Luma, including an interrupted first import.

Resume resolves the same volume identity at its current mount path. Completed folders are checked by directory attributes, without enumerating their files. In the interrupted folder, filenames are enumerated again but committed file records are reused. This is not a filesystem journal: file content edits that leave directory timestamps unchanged require a fresh rescan after completion. If a completed folder's timestamp changed or it disappeared, Luma conservatively starts a fresh snapshot. Do not edit the source while indexing if you want maximum checkpoint reuse.

**Manage sources → More → Restart indexing** confirms discarding the selected source's checkpoint and starts a fresh traversal. The published gallery and originals are retained. **Activity** shows paused progress and inaccessible folders. Access-denied folders are skipped with a warning; an incomplete traversal never prunes existing catalog entries. Other I/O errors pause the job for retry. Fix access and rescan to obtain a complete snapshot. A process-level lease prevents overlapping scanners; the operating system releases it after termination. Removing a source clears its checkpoints. In-app relocation invalidates affected paused snapshots so old paths cannot be republished.

## On this day

Choose **On this day** in the sidebar to see media captured on this month and day in earlier years, grouped by year with “years ago” labels. It defaults to the PC's current local date. The date picker lets you revisit another day; the selected date's year is the comparison year. Search, source, folder, album and format filters continue to apply. Reset returns to the full library. Normal selection, favorites, albums and file controls remain available.

Memories read saved, version-matched capture metadata and cached previews. They work offline without scanning or decoding originals. A missing preview shows a placeholder until the source is available. Files without capture dates, modification-date fallbacks, dates from the selected year or future years, and stale metadata are excluded. Use **Refresh metadata** on a connected source to enrich an older catalog. Current Windows metadata support does not treat video encoding dates as capture dates, so such videos stay in the regular library.

Dates follow the PC's local timezone, consistent with the timeline; original camera timezones may be unknown. February 29 memories appear only when February 29 is selected, without shifting them to February 28 or March 1. The empty state explains missing dates and offers another day. No uploads, face analysis, automatic albums or duplicate media files are created.

## Managing sources and refreshing the library

**Manage sources** is always visible beneath Folders & drives. Each entry shows its label, actual path, connected/offline state, saved media count and indexing status. Browse clears unrelated filters, Rename changes only the Luma label, and Refresh/Resume operates on that source. Add folder or drive is available inside the manager too.

**Remove from Luma** works for connected and offline sources. Its confirmation names the source and item count and explains that catalog entries, favorites, tags and album memberships are removed; originals are untouched. It optionally clears that source's current preview versions. Older preview versions can be removed with unused-preview cleanup. Album counts and source/folder filters update immediately. Re-adding a removed source scans it anew and does not restore removed organization; cancel removal if you wish to keep it.

The visible **Refresh** menu offers **Refresh this source**, **Refresh all connected sources**, and **Update drive status only**. Refresh discovers filesystem changes and resumes pending indexing checkpoints. Offline sources remain saved. Status-only checks never scan; ordinary reconnect remains fast. Refresh is not cache clearing and does not reset organization.

## Storage and preview maintenance

Open **Storage & previews** from the sidebar or Settings. Choose one source or all sources. Coverage applies to the chosen scope; cache usage/limit describes the whole PC-local cache.

- **Repair missing previews** generates missing current previews without discarding saved ones. Repeating it resumes through cache hits; it does not certify arbitrary image-byte corruption.
- **Rebuild previews** attempts regeneration for each catalog item in the chosen scope. A replacement is generated and validated before atomically replacing the old preview. Offline, changed, unsupported, failed or capacity-limited items keep their existing previews. No other source's previews are evicted. The completion message reports updated versus checked items.
- **Clear selected previews** explicitly confirms the count and space. Clearing one source affects its current versions; all-source clearing covers the cache. This intentionally removes offline previews, but never catalog organization or originals.
- **Clean unused previews** estimates reclaimable space across the entire library, then confirms removal of keys no longer referenced by current catalog media. Current offline sources are retained. Old versions and removed-source previews are eligible. Cleanup does not require the HDD.

Maintenance temporarily pauses preview loading, waits for pending cache work and refreshes visible images afterward. Rebuild/repair supports cancellation; completed writes persist. Cache-budget settings still apply on the next app launch. Preview pinning and original-file backup are not provided by these actions.

## Activity

The footer's **Activity** button remains available while work runs. It shows live progress, a pause/cancel action, saved indexing checkpoints with source paths, skipped-folder reports and the last 20 notifications from the current session. Resume paused indexing is offered when no conflicting operation is active. Preview work can be retried from Storage & previews. Checkpoints and indexing warnings survive restart; the recent notification list is session-only.

## Visible filters and consolidated navigation (v0.3.0)

New libraries show all supported media formats. On the first upgrade from settings written before this release, the old everyday-only default is cleared once, revealing previously hidden RAW/TIFF and other indexed formats. Other preferences are retained. After that migration, an explicitly enabled format filter remains saved across restarts.

Each active source, folder, search, album, category, format, nesting or date restriction appears as a removable chip above the gallery. Chip tooltips and accessible names expose the full label. Removing one chip keeps the other restrictions. Filtered results show matching count versus the complete saved library count; unsupported previews do not remove media from the gallery. Reset clears every restriction, including format and nested-folder preferences. First-import and other incomplete scans still publish only when traversal finishes.

Library is the browsing destination. Choose **View → Show timeline** for capture-date sorting with month groups; there is no separate Timeline sidebar destination. Photos, Videos, Favorites and On this day remain useful filtered destinations. Settings and Storage & previews stay pinned beneath the scrolling navigation.

The legacy Drive tools section has been removed. Source labels, removal and source refresh/resume live in Manage sources. Its **More** menu provides metadata refresh and confirmed indexing restart. Refresh provides explicit current/all-source refresh and scan-free status checks. Storage & previews owns repair, rebuild, coverage, clearing and cleanup. Activity owns live progress, paused indexing and skipped-folder reports. Existing originals and organization are unchanged by this navigation refinement.


## Month browsing, selection and large libraries (v0.4.0)

**Months** in the toolbar switches to capture-date order and month grouping. The month navigator remains visible for date-based views; it respects source, folder, album, search and format filters. Dates use the PC local calendar, including month/year boundaries. Capture dates fall back to modification time when unavailable or stale. Changing sort/search/source clears the month range; changing direction retains it. On this day keeps its separate date picker and year groups.

Choose **Select** to toggle several tiles with ordinary clicks or Space. Outside Select mode, Ctrl-click and Shift-click retain normal Windows selection behavior. Double-click opens an original only outside Select mode. The selection bar exposes **Copy to…**, **Copy**, **Cut**, Favorites and **More**. More includes Move to folder, Add to album, Copy paths, Select loaded files, Select all matching results, Clear selection and Paste. Ctrl+A selects loaded items only; selecting every matching result explicitly loads them and can be expensive at 200,000 items. Clipboard copy includes the selected Windows files for pasting into Explorer and remains available while background indexing runs. Clipboard requests are serialized separately; mutating operations remain guarded. Offline or changed originals stop the operation with an explanation; no incomplete clipboard replaces the current clipboard. File transfers retain confirmation and collision handling.

Capture-date ordering now uses a transactional, version-maintained SQLite date index rather than re-sorting a joined catalog on every page. A one-time upgrade builds the index from existing metadata without rescanning originals. Follow-up pages reuse the current result count; new views recompute it. Month-to-month navigation reuses its month breakdown. The count describes the view when loaded; explicitly refresh after catalog changes.

Indexing uses prepared batch inserts, a pending-folder index and one running checkpoint count. Directory enumeration reuses file attributes supplied by the OS. Metadata enrichment reads missing/stale records in batches of 100, closes readers before native decoding, and commits each completed batch in one transaction. It keeps no write transaction open while reading originals. Cancellation can reread at most the unfinished metadata batch. Source identity and file versions remain checked before/after decoding. Native decoder throughput and disk latency still determine initial-import time.

Tiles cancel outstanding thumbnail requests when recycled; visible tiles get priority over stale queued work. Decoded images remain tied to realized tiles. Scan feedback separates finding media from reading dates and shows elapsed time. The library is available after file discovery while dates are read. Pausing date reading keeps committed metadata; continue with Manage sources → More → Refresh metadata. There is no estimated completion time because codec and drive costs vary.

The scale benchmark now includes 200,000 catalog records and records first-page, continuation, month-breakdown and previous capture-sort timings. These are database measurements, not a claim about 200,000 real images, USB throughput or UI frame rate. Lightweight loaded records still accumulate in an unrestricted continuous scroll; month filtering is the bounded browsing path for large collections.

## Exclusions and packaging update

Reversible folder, source and global extension rules now filter browsing and future scans. Use Settings or Refresh → Manage exclusions to restore them; Reset does not remove rules. Originals and saved organization are retained. Changing rules invalidates affected paused traversal checkpoints, not the published catalog. See [usage and limits](EXCLUSIONS.md) and [executable branding](BRANDING.md).

## Final experience refinement

Saved browsing position, explicit discovery/date-reading progress, accessible action hints and theme-matched dialogs complete the planned build. See [behavior and limits](FINAL_EXPERIENCE.md). Verified shipment evidence is recorded in the testing handoff.


## v0.7.0 Workspace, Viewing, Map, and Navigation Additions

- Collapsible workspace chrome: `Ctrl+B` collapses the sidebar to 0 px (`SidebarCollapsed`), `Ctrl+H` collapses the top controls (`HeaderCollapsed`), accordion sections collapse `Views`, `Albums`, `Smart collections`, and `Folders & drives` with collapsed item counts, and action popovers (`Filters`, `Organize`) plus `SelectionBar` reveal controls on demand.
- Viewing layouts and non-destructive stacks: `LayoutMode` switches between `Uniform` grid cards and virtualized `Adaptive` / `Moments` justified rows (`JustifiedRowBuilder` with 1.28x sizing for favorites and stacks), alongside `PhotoStackBuilder` grouping for RAW+JPEG pairs and bursts within 30 seconds.
- Exact time and metadata ergonomics: `Show exact time (HH:mm:ss)` card captions, `VisibleTimeRange` header HUD, selection time span, `Day & Session` grouping, camera EXIF plus GPS schema columns (`latitude`, `longitude`, `camera_model`, `iso`, `f_number`, `exposure_seconds`, `focal_length`), and offline-safe `PopulatePreviewMetadata`.
- Navigation, sorting, filtering, and Smart Collections: clickable `FolderBreadcrumbs`, `DayJumpPicker`, `Alt+Left` navigation history, `Home`/`End` gallery scroll, cursor-safe `Filename` / `Resolution` / `Duration` / `FavoritesFirst` sorting, extended search operators (`date:`, `after:`, `before:`, `size:>`, `size:<`, `folder:`, `is:fav`, `is:untagged`, `is:portrait`, `is:landscape`, `is:square`, `is:4k`, `has:gps`, `-tag:`, `-ext:`), and Smart Collections UI with `+ Save view`.
- Dedicated one-time Map View: lazy `WebView2` map (`Assets/Map/map.html`, isolated `%LOCALAPPDATA%\Luma\webview2` profile) gated behind an explicit one-time session consent prompt, paired with an offline interactive vector coordinate canvas before consent, automatic connected-drive GPS/EXIF/XMP/sidecar/video ISO-6709 backfill (`MinimalGpsExtractor` + `meta_version`), in-map filter strip (`All`/`Photos`/`Videos`, `Favorites`, `Year`, `Source`, `Origin`, `Search`), view modes (`Clusters`, `Pins`, `Trail`), collapsible viewport filmstrip, place-tag (`location_name`) discovery, and catalog-only manual pins (`location_origin='manual'`).
- Fast transfers and curation: `RecentDestinations`, opt-in `TransferConflictMode.Rename`, multi-file `F2` batch rename tokens (`{date}`, `{time}`, `{orig}`, `{seq}`), Spacebar preview triage hotkeys (`F`, `T`, `M`, `Delete`, `Auto-next`), and `last-transfer.json` review/undo.
