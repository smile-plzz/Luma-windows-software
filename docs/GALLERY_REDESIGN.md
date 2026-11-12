# Luma gallery redesign

Status: historical approved specification, implemented and merged into `main` through PR #6. Development of this milestone is closed. The specification below preserves the original targets; [the build guide](GALLERY_BUILD.md) is authoritative for shipped behavior and explicit deviations, and [the testing handoff](TESTING_HANDOFF.md) records verified evidence and remaining bench acceptance.

## Product direction

Luma is a gallery for existing Windows drives, with the customization and file control expected from a desktop application. Its primary task is to make everyday photos and videos easy to rediscover by time, source and folder. It opens originals in the user's chosen applications.

The visual direction is Apple-inspired: media takes priority, spacing is deliberate, colors are restrained, typography is clear, and secondary controls appear when useful. Keep native Windows window controls, keyboard conventions, context menus, accessibility and file dialogs. Do not reproduce macOS window chrome or introduce a web wrapper.

The approved default is a chronological gallery, newest first, grouped by month. A plain continuous grid is one switch away. Day and year grouping are additional choices. Preferences persist across restarts.

## Original problem

The former Phase 2 WinUI grid replaced its contents in pages of 120. Users must repeatedly choose Next to explore a drive. Tiles always show filename, type, date and state, while source maintenance, cache controls and organization fields consume substantial space. Timeline groups use modification dates and repeat at page boundaries.

Keep the useful foundations: SQLite catalog, stable volume identities, read-only scanning, versioned thumbnail cache, offline preparation, default-app opening and native file actions. Redesign the presentation and browsing pipeline around those foundations.

## Screen structure

| Area | Contents and behavior |
| --- | --- |
| Sidebar | Library, Photos, Videos, Favorites; Albums; Drives with expandable cached folders. Collapsible sections, source labels users can rename, full path in tooltip/details, and a quiet offline indicator. |
| Header | Current library/folder/album title, breadcrumb where applicable, item count, search and compact view controls. |
| Gallery | Continuous virtualized scrolling, chronological headers, responsive columns, consistent image spacing and subtle selection indicators. No Next/Previous buttons. |
| View menu | Group by year/month/day/none; thumbnail size; crop-to-fill or fit; comfortable/compact spacing; show filenames and show details. |
| Sort menu | Date taken, date modified, name, size and file type; ascending/descending. Explain capture-date fallback. |
| Selection toolbar | Appears when items are selected: count, open, copy, cut, favorite and more actions. Do not permanently reserve a large editing panel. |
| Inspector | Optional details pane for path, source status, dates, dimensions, duration, size, tags and album membership. |
| Background activity | Small activity indicator opens scan/preparation progress and cancellation. Drive actions and Settings contain maintenance controls. |

First launch should emphasize adding a folder or drive. A populated library should emphasize the media. Empty search results, offline misses, unavailable codecs and loading errors need distinct states.

## Browsing and customization rules

- Default: month groups, newest first, medium thumbnails, comfortable spacing, cropped previews, filenames/details hidden. Video and offline badges remain available without covering the subject.
- Search combines with the active source, folder, album and photo/video filters. Show active filters and provide a clear reset action.
- Date grouping follows the selected date field and direction. Choosing name, size or type switches to an ungrouped grid rather than creating repeated or misleading date groups. Returning to date sorting restores the last date-grouping choice.
- Date navigation lists available years/months with counts from the catalog. Jumping to an old month must not load every intervening thumbnail.
- Preserve the visible item and its screen position when resizing thumbnails or toggling details. Reset scroll intentionally when changing the actual result set.
- Folder navigation uses the cached hierarchy. Default to including descendants, with a visible option for the current folder only.
- Remember appearance and browsing preferences separately from source-specific navigation. A removed or unavailable source must not leave the app trapped on a broken view.
- Ctrl+mouse-wheel changes thumbnail size when the gallery has focus. Keyboard navigation, touchpad scrolling and high-DPI scaling remain first-class paths.

## Everyday media

Prioritize JPEG/JPG, PNG, HEIC/HEIF, MP4 and MOV in visual QA and fixture coverage; retain GIF, WebP and other existing supported formats. Offer a common-media view and an extended-formats option for RAW, SVG and less-common formats without deleting their catalog entries. Do not silently discard previously indexed media when preferences change.

An indexed extension does not guarantee a working Windows decoder. Unsupported preview formats keep their filename/type and an understandable placeholder; opening them still uses the default app. Codec installation is not automatic. A library remains useful without an embedded photo editor or video player.

## Continuous scrolling architecture

1. Separate the query session, lightweight media records and decoded images. The current `Refresh` method must stop owning an entire replaceable page of cards.
2. Keep bounded database batches behind the interface. Introduce stable cursor queries for sequential browsing, with the selected sort value plus source ID and relative path as tie-breakers. Cursor comparisons must use the same collation, null treatment and direction as ordering.
3. Give each result set a generation/cancellation token. A late search, folder query or thumbnail decode must never append to a newer result set.
4. Load ahead near the viewport. Retain a bounded window of decoded thumbnails around visible items and release images outside it. Do not keep every scrolled image alive in an ever-growing collection.
5. Prototype bidirectional windowing and scroll anchoring in the native control before committing to the final control composition. Scrolling back must reload previous windows without jumping. Avoid wrapping the gallery in an outer scrolling container that defeats virtualization.
6. Merge adjacent date groups across internal batch boundaries. Date headers belong to the result sequence, not to database pages.
7. Use indexed catalog aggregates for date counts and direct jumps. A date jump establishes a new anchored query window.
8. During a scan or metadata refresh, offer a quiet refresh indication rather than reordering the media under the user's pointer. Apply the new snapshot on explicit refresh or navigation.
9. Distinguish item loading from thumbnail loading. Cached metadata can appear immediately; missing previews arrive progressively through the existing serialized native decoder.

Existing offset queries can remain for compatibility and tests during migration. Merely hiding the Next button and loading the whole drive into memory is not an acceptable implementation.

## Dates and metadata

Introduce a versioned metadata record keyed to the current media identity and file version. Extract capture date, dimensions, orientation and video duration only for new or changed files; preserve results offline. Metadata extraction is a cancellable background operation, separate from basic file discovery, so adding a drive does not wait for every decoder.

Store date provenance and whether a timezone is known. Preserve timezone-less EXIF dates as camera-local wall time rather than inventing UTC precision. Capture-date sorting uses a documented normalized ordering value; missing capture dates fall back to modification time and are identified in the inspector. Video encoding time must not be described as a verified capture time. Implausible or unreadable metadata falls back without losing the item.

Migrate older catalogs additively and transactionally. Retain tags, favorites, source identities and cache keys. Existing catalogs must open before background enrichment completes. Timeline date choices must be explicit while enrichment is incomplete.

## Windows file control and organization

| Interaction | Required behavior |
| --- | --- |
| Selection | Ctrl-click, Shift-range, keyboard navigation and a persistent selection count. Preserve stable selected identities while visual containers recycle. |
| Select all | Ctrl+A selects currently loaded results and states that scope. A separate explicit action selects all matching results with a total count; never silently expand a destructive operation. |
| Clipboard | Keep native copy/cut/paste and Explorer interoperability. Show pending cut state without changing the original until the move succeeds. |
| Drag-and-drop | Use real Windows file payloads, clear target feedback and modifier-key copy/move semantics. Treat folder, album and external-app drops differently and explain the action before execution. |
| File destinations | Copy/move into an actual folder changes files; adding to an album changes catalog membership only. Album drops must never move originals. |
| Rename/delete | Keep destination collision handling and native confirmations; accurately report partial completion, cancellation and unavailable sources. |
| Albums | Create, rename, remove and manage virtual memberships across sources. Removing an album must not delete its files. |
| Tags/details | Editable in the inspector; specify whether an edit affects one selected item or the whole selection. |

Introduce stable catalog item IDs before album memberships and move preservation. Migrate current source/path annotations to those IDs without losing data. Confirmed in-app moves and renames update paths, tags, favorites and album memberships for each successfully completed file. Filesystem operations and SQLite updates are not one atomic transaction: record outcomes and reconcile partial failures rather than promising rollback of physical moves. External moves remain a documented limitation until identity reconciliation is implemented.

Before any operation on originals, resolve the source again and validate the selected file version. Recycled visual rows must never become the authority for the file being modified. Offline selection and organization remain available; original-file actions explain that reconnection is required.

## Visual and accessibility specification

- Use a restrained neutral surface, subtle sidebar separation, one accent color, minimal tile borders and modest corner radii. Preserve system contrast and light/dark modes.
- Use a small consistent spacing scale and clear title/group/body hierarchy. Keep media dominant at both 1024×768 and larger desktop sizes.
- Use native Windows typography and controls where possible, styled for calmness and consistency rather than decorative imitation.
- Keep controls discoverable without requiring hover. Provide visible keyboard focus, accessible names, selection announcements and non-color status cues.
- Honor reduced-motion preferences. Loading should not continually animate large parts of the screen or shift already-visible content.
- Validate with real-looking everyday photo/video fixtures as well as synthetic test files. Synthetic colored thumbnails alone are insufficient for visual review.

## Implementation sequence

| Step | Deliverable | Exit check |
| --- | --- | --- |
| 1. Gallery foundation | Simplified shell, contextual toolbar/inspector, virtualized continuous scrolling, thumbnail windowing, existing modification-date grouping | Browse beyond several former pages in both directions without paging controls, gaps, duplicates or lost scroll position; test cancellation and memory retention. |
| 2. View control | Persisted size/density/fit/label preferences, complete sort direction controls, plain-grid toggle, cached drive/folder hierarchy and filter reset | Settings survive restart; sorting is deterministic; keyboard selection remains accurate after view changes. |
| 3. Meaningful timeline | Additive metadata migration, incremental enrichment, capture/modification date choice, year/month/day groups and indexed date jumps | Missing/ambiguous dates fall back predictably; existing libraries retain organization; old dates can be reached without sequentially loading the drive. |
| 4. File organization | Stable item IDs, virtual albums, explicit selection scopes, drag-and-drop and operation-result reconciliation | Album actions do not move originals; successful in-app moves preserve organization; failed/partial operations remain understandable and recoverable. |
| 5. Stabilization and delivery | Accessibility/visual pass, everyday-format matrix, performance evidence, updated usage/upgrade/QA docs and portable Windows artifact | All regression and native UI gates pass; documented manual acceptance and measured performance limits accompany the build. |

Each step should be a reviewable change with runnable Windows evidence. The Phase 2 caching foundation is retained throughout. The redesign is complete only when these steps satisfy their acceptance checks; finishing the shell alone is not the whole milestone.

## Verification and release criteria

Automate cursor ties and sort directions, filter transitions during loading, cross-batch group merging, forward/backward traversal, date jumps, metadata fallback/migration and album membership rules. Extend Windows UI Automation to scroll past multiple internal batches, navigate back, adjust thumbnail size, switch grouping/sorting, restart with persisted preferences and verify selected-file actions on disposable fixtures.

Retain Phase 2 tests for offline hits without drive probes, preparation cancellation/resume, capacity preservation and cache clearing. Exercise actual JPEG/PNG fixtures and MP4/MOV/HEIC with known installed-codec conditions; report skipped codec-dependent cases explicitly.

Measure first useful gallery render, time to cached previews, fast-scroll frame behavior, query/decode counts, peak memory and memory after prolonged forward/backward browsing. Use 1k, 10k and 100k catalog fixtures, plus a physical USB source on the user's bench. Initial engineering targets are a useful warm gallery within two seconds for 10k local catalog records and decoded-image memory that plateaus with the configured viewport window. These are targets to validate, not current performance claims; record hardware, cache state and dataset for every result.

Manual acceptance includes USB disconnect/reconnect under a changed drive letter, insufficient cache/disk space, unsupported codecs, Explorer drag/drop, native collision/recycle dialogs, partial moves and high-DPI/keyboard usability. Publish exact tested commit, CI run, screenshot evidence, known limitations and the Windows ZIP before calling the redesign ready for testing.

## Scope boundaries

No cloud account, embedded player/editor, face recognition, automatic original-file rearrangement or full-original backup is required. Preview caching remains bounded and is not backup storage. The implementation sequence above is complete at the scope recorded in the build guide. The next action is real-life testing and feedback, not restarting Step 1.

## Implementation decisions

The native gallery keeps lightweight loaded records for backward navigation and stable selection; it releases decoded images as containers recycle. This replaces the initially proposed removal of entire record windows, avoiding scroll-position churn. Sources use a cached child-folder navigator with a parent action rather than a fully expanded recursive tree. Date jumps target available months and continue into older results. Conflicting transfer filenames are preserved/skipped rather than offering overwrite. These behaviors are explicit in the build guide and tested at their actual scope.
