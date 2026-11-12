# Luma Phase B User Journey Audit

Assessment of all 12 core user journeys from discovery and first launch through external drive lifecycles, failures, and upgrades. Each friction point is cross-referenced to `docs/assessment/FINDINGS.md` (`F-*`).

Note on methodology (Phase D): Because no external user feedback exists yet on the public repository (0 stars, 0 forks, 0 filed issues as of 2026-11-12), scenarios below combine direct code walkthroughs, WinUI UIA/test inspection, and proxy scenarios representing our Multi-Drive Collector and Archival External-Drive personas rather than field interviews.

---

## Journey 1: Discover and Download
- **User tries to:** Understand Luma from `README.md`, download the right Windows file, extract it properly, and get past Windows SmartScreen without losing data or DLLs.
- **Audit notes:**
  - `README.md` currently opens decently, then embeds a duplicated/misplaced v0.6.0 bullet block under the FAQ (`F-013`).
  - `README.md` points `releases/latest` at `Luma-v0.7.0-win-x64.zip` even though the latest published GitHub release at audit time is still `v0.6.0` (`F-014`).
  - GitHub repo description is only `"Luma"` with zero topics, reducing organic discovery (`F-015`).
  - Portable ZIP extraction rule ("extract the whole folder; keep bundled files beside `Luma.App.exe`") and unsigned SmartScreen warning are explained, though adding a concise SHA-256 verification note and link to `docs/USER_GUIDE.md` will prevent partial-extract mistakes.
- **Highest severity:** S3 (confusion / broken latest-release assumption before v0.7.0 ships).

## Journey 2: First Run
- **User tries to:** Launch `Luma.App.exe` for the first time, understand what the empty canvas wants, click `+ Add folder or drive`, and see first photos within 60 seconds.
- **Audit notes:**
  - Critical UX defect (**`F-001`, S2**): In `MainWindow.Gallery.cs:1185`, `UpdateEmptyState()` checks `Sources.Items.Count > 0`. Because `"All sources"` is always inserted at index 0, `hasSources` is ALWAYS `true` even on a brand-new empty install! New users see `"Your library is ready / No media files were found in this source"` instead of `"A little room for memories / Add a folder or drive to start..."` and miss the prominent `EmptyAddButton`! Wait: let's confirm `EmptyAddButton` visibility: it is explicitly collapsed when `hasSources` is true (`line 1179`), hiding the primary first-run CTA!
  - First-scan explanation ("Your originals stay untouched. Luma builds a fast local catalog on this PC.") and sample expectation can be clearer during first scan (`F-016`).
- **Highest severity:** S2 (`F-001`).

## Journey 3: Add a Large Library (10k to 500k items, slow USB HDD)
- **User tries to:** Add a multi-hundred-gigabyte folder/drive, browse already-found photos while scanning continues, and pause, resume, cancel, or inspect skipped folders cleanly.
- **Audit notes:**
  - `ResumableIndexing` + WAL deferred read snapshots (`GalleryTests.cs`) allow responsive browsing while indexing holds the writer slot, and Activity reports discovery vs metadata phase, elapsed time, and skipped folders.
  - Benchmark numbers (`windows-v0.7.0-benchmarks.json`) show 100k paged query at ~2.5 ms and 500k paged query at ~12 ms, well within targets; cold index/metadata still depends linearly on drive/WinRT I/O speed on USB HDDs.
  - Low-disk safety gap (**`F-008`, S2**): `OfflinePreparation` / cache writes do not uniformly check `DiskGuard` before heavy writes, while `LoadThumbnail` blocks already-cached reads under low disk.
- **Highest severity:** S2 (`F-008`).

## Journey 4: Browse
- **User tries to:** Scroll a smooth date-grouped timeline (Uniform Grid, Adaptive, Moments), use month navigation and day jumps, collapse/expand stacks, toggle labels/density, and return to the exact spot later.
- **Audit notes:**
  - Saved scroll position (`VisibleAnchor` / `PositionAnchor` in `MainWindow.Place.cs:57,117`) inspects ONLY `MediaGrid` (`Uniform` layout) and silently fails to save or restore anchor position when the user browses in `Adaptive` or `Moments` layout (**`F-010`, S2**).
  - Collapsed stacks display a stacked backing card and count badge, but action menus/shortcuts on a stack operate on the cover photo without reminding the user before bulk actions (`F-017`, S3).
  - Keyboard shortcuts are discoverable only by reading the README or tooltips (`F-018`, S3: missing in-app `F1` / Shortcuts dialog).
- **Highest severity:** S2 (`F-010`).

## Journey 5: Find
- **User tries to:** Search by filename, folder, multi-word tag, or place (`location:"New York"`), combine with format/date/source/album/SmartCollection chips, check filtered counts, and Reset cleanly.
- **Audit notes:**
  - `LibraryQueries.ParseSearch` splits blindly on space (`' '`), breaking multi-word values like `location:"New York"` or `tag:"Family Trip"` and letting repeated `tag:` filters overwrite each other (**`F-012`, S2**).
  - Clicking `Manage sources -> Browse` (`MainWindow.Management.cs:74`) switches source filter without clearing active Smart Collections or visual filters, potentially leaving `"No matching moments"` unexpectedly (`F-019`, S3).
  - Removable filter chips and `Reset` work well once cleared.
- **Highest severity:** S2 (`F-012`).

## Journey 6: Organize (Favorites, Multi-Word Tags, Albums, Select Mode, Undo)
- **User tries to:** Star favorites, assign multi-word tags like `"Family Trip"`, organize virtual albums, tag in Select mode or Quick Preview, and undo accidental moves.
- **Audit notes:**
  - **Data corruption bug (`F-002`, S2):** `TagManagement.cs` (`GetTagsWithCountsAsync`, `RenameTagAsync`, `BulkTagAsync`) splits tag strings on `' '` (space) in addition to `,` and `;`, whereas `AnnotateAsync` splits only on `,`. Any multi-word tag such as `"Family Trip"` gets shredded into `"Family"` and `"Trip"` upon tag counting, renaming, or bulk tagging!
  - **Undo organization loss (`F-005`, S2):** `ReviewLastTransfer` undo (`MainWindow.Organization.cs:435-445`) moves files back via `FileTransfer.RunAsync` and calls `ScanAsync` instead of `RelocateAsync`, leaving favorites, tags, and album membership orphaned at the old destination path. It also fails to check offline drive availability before calling `Directory.CreateDirectory`.
  - **Quick Preview focus trap (`F-007`, S2):** Opening Quick Preview (from double-click, button, or Map) does not move keyboard focus inside `PreviewOverlay`, so keyboard triage shortcuts (`Left`, `Right`, `F`, `T`, `M`, `I`, `Space`, `Del`) do nothing until the user clicks inside the overlay. Video previews show a still frame without an explicit in-overlay cue explaining that `Enter` opens playback in the default Windows app (`F-020`, S3).
- **Highest severity:** S2 (`F-002`, `F-005`, `F-007`).

## Journey 7: External Drive Lifecycle (Highest-Risk Journey)
- **User tries to:** Connect a USB HDD, index it, unplug it, browse thumbnails/tags/albums offline, reconnect it later (possibly under a new drive letter), and reconcile external changes.
- **Audit notes:**
  - Core volume GUID + serial/label matching (`WindowsSourceResolver`) and in-place reconnect work cleanly in unit and smoke tests.
  - **DiskGuard conflict (`F-008`, S2):** On a PC with < 500 MB free space on `C:`, `LoadThumbnail` refuses even to read already-cached `.thumb` previews!
  - **Reconciliation respects no exclusions (`F-011`, S2):** `ExternalReconciliation` scans disk folders without checking `exclusions`, re-adding excluded folders/extensions, and opens separate DB connections / `IndexingLease` per reconciled file.
  - Physical USB 3.0 slow-HDD unplug/letter-swap acceptance on real hardware cannot be automated in sandboxed CI and remains on Ismail's 10-minute verification script (`QA_ACCEPTANCE.md`).
- **Highest severity:** S2 (`F-008`, `F-011`).

## Journey 8: File Operations (Open, Reveal, Copy, Cut/Move, Rename, Delete)
- **User tries to:** Act on originals safely without overwriting existing files, losing catalog state, or getting cryptic I/O errors.
- **Audit notes:**
  - **Delete cancel / heavy rescan (`F-006`, S2):** When a user clicks Cancel in Windows' Recycle Bin confirmation (`ShellFiles.Run` returns `false`), Luma ignores the return value, advances Quick Preview anyway, and runs a full `RescanCurrent()` of all connected sources. Even on confirmed deletion, a full source rescan is triggered instead of targeted catalog removal.
  - **Rename validation gaps (`F-009`, S2):** Single `Rename()` does not block Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`), treats case-only renames (`photo.jpg` -> `PHOTO.JPG`) awkwardly if collisions check exists/misses error handling, and silently allows removing the extension (making the media disappear on the next scan) without confirmation. `BatchRenameAsync` swallows `RelocateAsync` failures into `skippedCount` without flagging partial catalog drift. Also `MainWindow.Organization.cs:370` constructs `new FileInfo(source)` without checking if `source` disappeared mid-session (`F-021`, S3).
- **Highest severity:** S2 (`F-006`, `F-009`).

## Journey 9: Maintenance (Storage, Previews, Cleanup, Exclusions, Reset)
- **User tries to:** Clear/repair/rebuild previews, set cache limits (1 to 32 GiB), restore exclusions, or reset the catalog without risking originals.
- **Audit notes:**
  - `RunPreviewMaintenance` (`MainWindow.Management.cs:192`) uses unsafe `.First(...)` rather than safe lookup (`F-022`, S3).
  - There is currently no explicit in-app `"Reset catalog (keep originals)..."` control in Settings (`F-023`, S3). Users must manually find and delete `%LOCALAPPDATA%\Luma\catalog.db`.
- **Highest severity:** S3 (`F-022`, `F-023`).

## Journey 10: Return Visit
- **User tries to:** Reopen Luma days later, land quickly on their previous view and scroll spot without rescanning external drives.
- **Audit notes:**
  - Catalog-first reopen + persisted browse position work for Uniform grid; broken for Adaptive/Moments anchor (`F-010`, S2).
  - Rapid view switching triggers `async void UpdateEmptyState` without a post-await query-token check, risking stale empty-state overlays flashing after results load (`F-024`, S3).
- **Highest severity:** S2 (`F-010`).

## Journey 11: Failure and Recovery
- **User tries to:** Survive sudden process kill mid-scan, full disk, corrupt `catalog.db`, deleted cache folder, unreadable folder permissions, or personal path privacy when reporting errors.
- **Audit notes:**
  - **Misleading corruption recovery (`F-004`, S2):** `MediaCache.RepairCatalogAsync` returns `true` even when `catalog.db.bak` is missing or also corrupt and it had to wipe the DB and initialize an empty schema. `MainWindow.xaml.cs:95` then wrongly tells the user `"Catalog corruption detected and safely restored from backup."` when their tags and albums were actually wiped!
  - **Unredacted crash log (`F-003`, S2):** `MainWindow.Guard` (`MainWindow.xaml.cs:158`) writes raw `ex.ToString()` (including local usernames and folder paths) to `last-error.txt`, bypassing `PathRedactor.Redact` used in `App.LogCrash`.
  - Multiple unguarded `async void` event handlers (`CardQuickFavoriteClick`, `MapPlaceFilterClicked`, `ManageSourcesBrowse`, etc.) risk unobserved task crashes (`F-025`, S2).
  - `MinimalGpsExtractor.ParseXmpBytes` should enforce explicit `DtdProcessing.Prohibit` + `XmlResolver = null` when parsing untrusted XMP packets from arbitrary files (`F-026`, S3).
- **Highest severity:** S2 (`F-003`, `F-004`, `F-025`).

## Journey 12: Update and Uninstall (Portable ZIP Flow)
- **User tries to:** Upgrade from `v0.6.0` portable ZIP to `v0.7.0` preserving catalog, tags, albums, and settings, or remove Luma completely.
- **Audit notes:**
  - Upgrading replaces binaries in the app folder while `%LOCALAPPDATA%\Luma` preserves `catalog.db` and `settings.json`, verified in `UpgradeTests.cs`. We will add explicit v0.6.0 -> v0.7.0 schema + exclusions + multi-word tags + browse-position roundtrip assertions (`F-027`, S3).
  - Missing in-app `"Send feedback / Report a problem"`, `"Known limits"` doc, Troubleshooting guide, and structured GitHub issue templates leave real-world upgrade/drive problems uncaptured (`F-028`, S3).
  - `.github/workflows/release.yml` still points to `v0.6.0` and contains an em-dash, and em-dashes remain in `Luma.App.csproj`, `Smoke-Test.ps1`, and several docs (`F-029`, S3).
- **Highest severity:** S3 (`F-027`, `F-028`, `F-029`).

