# Luma: shipment and real-life testing

Shipment updated: 13 October 2026 (Asia/Dhaka). The portable gallery milestone is merged into `main` through PR #6, followed by UI/import refinements in PR #7 and the shipped reconnect optimization in [PR #8](https://github.com/smile-plzz/Luma-windows-software/pull/8). Resumable indexing and On this day memories are merged in [PR #9](https://github.com/smile-plzz/Luma-windows-software/pull/9). Real-life testing and everyday use now guide further refinements. This is a testing shipment, not a claim that physical-device acceptance is complete.

## What changed in this shipment

- [PR #14](https://github.com/smile-plzz/Luma-windows-software/pull/14) completes saved source/folder/month/visible-item restoration, explicit discovery and metadata progress, contextual help, action feedback and theme-matched dialogs. Settings can disable remembered position. [Behavior and limits](FINAL_EXPERIENCE.md). The planned product build is closed; subsequent work follows defects and everyday-use feedback.

- [PR #13](https://github.com/smile-plzz/Luma-windows-software/pull/13) adds confirmed right-click folder, source and global file-type exclusions. Settings and Refresh expose a manager to add or restore rules. Scans, metadata and previews skip excluded media while catalog organization and originals remain intact. Empty views point to restoration. Changing rules restarts affected paused traversals.
- An original photo-card icon appears on the EXE and window, with descriptive Windows file properties and version 0.5.0. [Exclusion guide](EXCLUSIONS.md) · [Branding source](BRANDING.md).

- [PR #12](https://github.com/smile-plzz/Luma-windows-software/pull/12) adds **Months**, a named month/count picker, exact month filtering, Older/Newer and All months. **Select** enables plain-click multi-selection; Copy to, clipboard Copy/Cut and More expose file and album actions. Clipboard preparation remains available during indexing.
- Captured-date pages use a maintained SQLite index, continuation pages reuse counts, metadata writes commit in batches, and recycled tiles cancel queued preview work. Scanning shows elapsed time and distinct discovery/date-reading phases. A one-time local index migration requires no source rescan.

- [PR #11](https://github.com/smile-plzz/Luma-windows-software/pull/11) addresses review items 1 and 3: all supported formats appear by default, active filters have removable chips, counts show filtered results against the whole library, and Reset clears all content filters. On upgrade, the former default everyday-format filter is disabled once; subsequent explicit choices persist.
- Timeline is **View → Show timeline** within Library. Settings and Storage & previews stay pinned below the sidebar. Manage sources, Refresh, Storage & previews and Activity each own their maintenance tasks; the duplicate Drive tools panel is removed.

- [PR #10](https://github.com/smile-plzz/Luma-windows-software/pull/10) adds visible Manage sources, explicit refresh scopes, Storage & previews, safe rebuild/repair, unused-preview cleanup and Activity. Removal refreshes album counts and never deletes originals.

- Indexing saves persistent checkpoints every 100 media records and at folder boundaries. Resume after cancellation, app restart or reconnect; the published gallery stays intact until indexing finishes. Activity includes paused indexing and skipped-folder reports; Manage sources offers **More → Restart indexing**.
- Protected folders produce a report and prevent pruning older catalog entries. Completed folders are reused; changed folder timestamps conservatively invalidate the checkpoint. This is not a filesystem journal.
- On this day shows matching capture dates from earlier years, with year labels, a date picker, existing filters and offline previews. Missing or stale capture metadata is excluded; modification dates are not treated as memories.

- Known drives reopen from their saved catalog without scanning, including empty registered folders. Reconnect updates availability and visible missing previews while retaining selection and scroll. New or changed files are discovered with explicit **Refresh all connected sources**.
- Metadata enrichment after a scan is limited to the sources actually scanned.

- Fixed the gallery read transaction that caused SQLite lock errors while a long external-drive scan was running. Regression checks cover concurrent browsing with both scan completion and cancellation.
- Refined light/dark surfaces, rounded controls, toolbar, typography and photo tiles; folder controls appear when a source is selected.
- Removed placeholder icons beneath loaded transparent previews and simplified gallery status text.
- Retained existing catalogs, albums, settings and original-file behavior. No database reset or source re-import is required for this update.

## Verified shipment

Native restart tests verified the deep visible anchor and inset, earlier-item loading without moving that anchor, and source/folder/month restoration from the offline catalog. Both import phases and accessible Activity help were verified. The final desktop attempt passed after the earlier runtime observation documented below.

| Item | Value |
| --- | --- |
| Platform | Windows 10 2004+ / Windows 11, x64 |
| Package | Unsigned, self-contained portable ZIP; no installer |
| Download | [Luma v0.6.0 Windows ZIP](https://github.com/smile-plzz/Luma-windows-software/releases/download/v0.6.0/Luma-v0.6.0-win-x64.zip) |
| Tested source | `f655715982253824c41329425c181c8876d9f3d5` |
| Merge commit | `c85baa965e2220f7ee7ee17c4d9b7634a24a2d0d` |
| Automated evidence | [Successful run 38355699422](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38355699422) |
| Desktop evidence | [Screenshots, settings and smoke results](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38355699422/artifacts/11792314247) |
| ZIP SHA-256 | `320e0d5dea5226563b980b18927fc99612bdbe6022153d3b46896dd1ef1aef5c` |

Native exclusions checks verify folder cancellation, all three exclusion scopes, Reset persistence, filtered refresh, restoration from an empty gallery, retained original files, and descriptive EXE metadata with an embedded icon. The exclusion manager screenshot and extracted icon were inspected.

82 core tests passed on Windows and Linux, four Windows integration tests passed, and the packaged app passed native desktop automation. The UI run covers continuous scrolling across 260 fixtures and back, recycled images, search, favorites, albums, folders, offline preparation/disconnection, view preferences after restart and theme switching. Light/dark and month-selection screenshots were inspected. Native month tests verify exact counts, adjacent navigation and All months. A two-file selection was copied and both paths were verified in the Windows clipboard. Native reconnect checks verified preserved selection/scroll and no automatic indexing of an added file. Native management checks passed for removal cancellation/confirmation, refreshed album counts, all-source refresh, preview repair/rebuild, Activity and cleanup. Native format/navigation checks verified a DNG catalog entry is visible by default, the everyday-format filter changes the count from 261 to 260, chip removal and Reset reveal it, and Timeline and pinned footer actions are reachable. The offline memories smoke passed; core tests cover resume across restart/mount changes, checkpoint invalidation, cancellation, concurrent scanner exclusion, filtering and leap days. Catalog-only scale tests cover 1k/10k/100k/200k records.

The versioned [v0.6.0 Release](https://github.com/smile-plzz/Luma-windows-software/releases/tag/v0.6.0) contains the exact verified ZIP plus SHA256SUMS.txt. Release assets do not have the Actions artifact's automatic expiration. Source code archives are separate and do not contain the EXE. The release workflow verifies the successful source run and ZIP digest before publication. Source, build automation, release notes, tests and documentation remain versioned in Git; binaries are under Releases.

## First run and upgrade

1. Close any running Luma. If you have an existing library, copy `%LOCALAPPDATA%\Luma` to a backup location while the app is closed.
2. Extract the entire ZIP into a fresh writable folder. Run `Luma.App.exe` with all bundled files beside it; do not copy only the EXE.
3. Add a representative folder containing your usual JPEG/PNG photos and MP4/MOV videos. Scanning catalogs originals without rearranging them; metadata enrichment follows discovery.
4. Explore **Months**, the month picker, Older/Newer and All months, folders, sorting, View options, search, favorites and albums. Open originals in their usual applications. Close and reopen to check retained settings and organization.
5. Add the external drive, repair missing previews in **Storage & previews**, and check coverage before disconnecting. Saved previews are not original-file backups and may later be evicted by normal browsing.

The first upgrade from older versions turns off **Everyday formats only** once so no supported format is hidden by the former default. You can re-enable it in View; this explicit choice will persist.

This upgrade builds a local timeline index once, preserving existing data and scan checkpoints. Initial discovery and metadata reading still depend on drive and codec speed; a 200,000-file first import can remain lengthy. Use Activity to pause discovery; resume date reading through Manage sources → More → Refresh metadata. [Measured performance and limits](PERFORMANCE.md).

User data remains in `%LOCALAPPDATA%\Luma` when replacing the executable folder. Do not run an older app against a library updated by this build: older versions do not maintain the new album identities during file operations. For rollback, close Luma and restore the matching pre-upgrade data backup along with the prior app. Preserve the newer data separately first; rollback discards organization added since that backup. Original-file moves/deletes are not undone by restoring the catalog.

## Bench checklist

| Area | Try | Record |
| --- | --- | --- |
| Exclusions | Right-click a file: cancel then confirm folder/source/type exclusion; restart, Reset, refresh, restore | Correct scope/counts, skipped discovery, retained originals/favorites/albums |
| Remembered place | Scroll deep into a collection; select a source/folder/month; close, disconnect and reopen | Same visible item and filters, earlier-item navigation, opt-out behavior |
| Daily browsing | Scroll a real collection, search, change sorting/grouping and tile size, restart | Delays, confusing controls, missing previews, lost preferences |
| Everyday formats | JPEG/PNG, HEIC, MP4/MOV and your camera formats | Extension, Windows codec availability, preview versus default-app behavior |
| External drive | Prepare previews, disconnect, restart, browse offline, reconnect under another letter | Coverage before/after, source status, whether originals reopen |
| Indexing recovery | Cancel a large import, restart Luma, reconnect and Activity → Resume paused indexing; inspect the report | Saved progress, final totals, skipped folders and retained organization |
| Memories | Open On this day, choose another date, search and browse offline | Capture-date accuracy, year groups and prepared previews |
| Maintenance | Manage sources; cancel then confirm removal on a disposable source; repair/rebuild previews; clean unused previews | Original files retained, correct album counts, scope, reclaimed space and offline previews |
| Organization | Favorites, tags, album create/rename/remove, selected-item actions | Whether membership and annotations remain correct after restart |
| File control | On disposable copies: copy/move, collision, cancel, rename, Explorer drag/drop, Recycle Bin | Intended and actual paths, skipped items, recovery behavior |
| Usability | Typical window size, keyboard navigation, light/dark mode and display scaling | Screenshot, resolution/scaling, exact interaction |
| Performance | Record collection size, disk type, cold versus warm cache, RAM and task duration | Reproducible timings; avoid interpreting catalog benchmarks as frame-rate results |

For lower-capacity cache, interrupted work, limited disk space and source-identity edge cases, follow [QA.md](QA.md). Hardware and native shell acceptance remain pending until actually exercised. Do not reformat a personal drive for an identity test; use a disposable test volume if that scenario is needed.

## Known limits and refinement priorities

An earlier attempt had a Windows UI runtime crash during refresh (`Microsoft.UI.Xaml.dll`, `0xc000027b`). It did not recur in the complete retry; its cause has not been established and is not claimed fixed. This remains a real-use follow-up observation. If it recurs, record the action and Windows version; include Luma’s local `last-error.txt` if present and the Windows Application event, with private paths removed.

- Windows-installed codecs determine previews. No embedded player/editor or automatic codec installation is included.
- There is no filesystem watcher. Rescan after changing originals outside Luma. External moves do not automatically preserve catalog organization.
- Cached previews are bounded and evictable; preparation is resumable but is not pinning or backup.
- Capture dates have documented timezone/provenance limits; video encoding time is not treated as verified capture time.
- Lightweight loaded records accumulate during browsing; decoded thumbnails are recycled. Full 100k-photo scrolling and physical USB performance are not certified.
- Transfers skip collisions. Filesystem operations and catalog updates are not one atomic transaction; inspect `%LOCALAPPDATA%\Luma\last-transfer.json` if a transfer reports partial failure.
- The package is unsigned and portable. Signing, installer packaging and external-move reconciliation are deferred until feedback establishes their priority.

No new feature work is required before starting this testing stage. Startup/data-integrity defects take priority, followed by recurring performance/usability issues, then additional capabilities.

## 10-Minute Owner Release Candidate Verification Script (v0.7.0-rc1)

Run this exact checklist against the `v0.7.0-rc1` portable ZIP on your personal Windows machine and a real USB external drive (~10 minutes):

1. **Clean extraction & upgrade backup (1 min):** Back up `%LOCALAPPDATA%\Luma`, extract the complete ZIP to a fresh folder, launch `Luma.App.exe` (confirm SmartScreen prompt behaves cleanly and existing v0.6.0 library, tags, albums, and position open without re-scanning).
2. **First-run / Multi-word tags / Search (2 min):** Assign a multi-word tag such as `Dhaka Trip` to 2 test photos, rename it cleanly in **Organize tools -> Manage tags...**, and search `tag:"Dhaka Trip"` or `location:"..."` to verify quote-aware search preserves spaces.
3. **Physical external HDD unplug & drive-letter swap (3 min):** In **Storage & previews**, repair previews on a disposable/test folder on your external HDD, unplug the drive cleanly, verify offline previews + metadata + favorites still work offline, replug the HDD (under a different drive letter if possible), and run **Refresh -> Update drive status only** to confirm in-place reconnect without full rescanning.
4. **Quick Preview keyboard triage & Videos (2 min):** Press `Space` on a photo, step `Left`/`Right`, press `F` (favorite), `T` (tag), `I` (details), press `Esc`. Press `Space` on a video file to verify the playback hint (`Enter` opens in your default player).
5. **Safe Undo & Recycle Cancel (1 min):** Move a disposable test file between folders inside Luma, open **Organize tools -> Review / Undo last transfer...** and undo it (confirm tags stay attached). Press `Delete` -> confirm dialog -> click **Cancel** inside Windows' Recycle prompt and verify the card stays in place without triggering a full scan.
6. **Accessibility, DPI, & Feedback (1 min):** Press `F1` to open Keyboard shortcuts, check Light & Dark themes and 150%+ Windows display scaling if applicable, and verify **Settings -> Send feedback / Report a problem...** opens the prefilled GitHub issue template cleanly.

Record results and any real-drive friction via GitHub Issues or send back findings to feed into `docs/assessment/FINDINGS.md`.

## Sending feedback

Send feedback in the project conversation or open a [GitHub issue](https://github.com/smile-plzz/Luma-windows-software/issues/new?template=testing-feedback.md). Include the build/run, Windows version, drive type, approximate file count, exact steps, expected and actual result, and reproducibility. A screenshot or short recording helps. For transfer problems, include the relevant sanitized operation-report entries. Avoid posting your entire catalog or personal media; paths and filenames may contain private information.

See [the build guide](GALLERY_BUILD.md) for detailed behavior and [the roadmap](ROADMAP.md) for the closed milestone and deferred work.

## Exclusions and packaging update

Reversible folder, source and global extension rules now filter browsing and future scans. Use Settings or Refresh → Manage exclusions to restore them; Reset does not remove rules. Originals and saved organization are retained. Changing rules invalidates affected paused traversal checkpoints, not the published catalog. See [usage and limits](EXCLUSIONS.md) and [executable branding](BRANDING.md).

## Final experience refinement

Saved browsing position, explicit discovery/date-reading progress, accessible action hints and theme-matched dialogs complete the planned build. See [behavior and limits](FINAL_EXPERIENCE.md). Verified shipment evidence is recorded in the testing handoff.
