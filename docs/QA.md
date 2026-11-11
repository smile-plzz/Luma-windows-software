# QA

## v0.6.0 final experience shipment

[Successful run 38355699422](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38355699422), attempt 2, source `f655715982253824c41329425c181c8876d9f3d5`, merged through [PR #14](https://github.com/smile-plzz/Luma-windows-software/pull/14) at `c85baa965e2220f7ee7ee17c4d9b7634a24a2d0d`. All 82 core tests passed on each of Windows and Linux, four Windows integration tests passed, and the packaged native UI checks passed. Deep visible-item/inset restoration, earlier-item navigation, source/folder/month restoration while disconnected, Reset, both import phases and accessible help were verified. Existing gallery, clipboard, exclusion and maintenance workflows passed. The themed dialog screenshot was inspected.

ZIP SHA-256: `320e0d5dea5226563b980b18927fc99612bdbe6022153d3b46896dd1ef1aef5c`. The release contains this exact tested artifact and SHA256SUMS.txt.

An earlier attempt had a Windows UI runtime crash during refresh (`Microsoft.UI.Xaml.dll`, `0xc000027b`). It did not recur in the complete retry; its cause has not been established and is not claimed fixed. This remains a real-use follow-up observation. If it recurs, record the action and Windows version; include Luma’s local `last-error.txt` if present and the Windows Application event, with private paths removed.

Evidence: `final-experience.txt`, `import-phases.txt`, `before-offline-restart.json`, existing workflow summaries and themed dialog screenshots. Scale coverage includes anchor lookup in a 200,000-record catalog.

## v0.5.0 verified exclusions and branding shipment

Exact package from [successful build 38350255657](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38350255657), source `b7d9eb16cc3547c64928e8fbdc202b7e0a400083`, merged through [PR #13](https://github.com/smile-plzz/Luma-windows-software/pull/13) at `4941618f865a95cccbae01f1fc464d1b97e9e4a7`. All 77 core tests passed on each of Windows and Linux, four Windows integration tests passed, and the packaged app passed native UI automation for exclusions and existing workflows. The exclusion dialog screenshot and embedded executable icon were inspected.

SHA-256: `e28cdc156a234b57b68a2561bd8fad51bbbc43c9d8351c16f90312a9c2513aad`. Also supplied in SHA256SUMS.txt. Physical HDD performance and codec acceptance remain part of real-life testing.

Native `exclusions.txt` verifies folder cancellation, folder/type/source hiding, persistence through Reset and refresh, restoration from an empty gallery, counts and originals retained. `executable-metadata.txt` verifies the product, descriptive file properties and version; `executable-icon.png` shows the embedded icon. Existing native workflows remain green.

## v0.3.0 verified filters and navigation shipment

[Run 38260963380](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38260963380), source `35103a06e88f85a2129ff7eb96bb6e65996f1185`: 67 core tests pass on each OS, four Windows integrations pass, and packaged native desktop smoke passes. `filters-navigation.txt` verifies non-everyday media visibility, filtered counts, chip removal, Reset and consolidated navigation; the script also opens Timeline and checks pinned Settings/Storage visibility. Existing gallery, offline, reconnect, memories and management checks pass. Light/dark screenshots were inspected. Merged through [PR #11](https://github.com/smile-plzz/Luma-windows-software/pull/11). See TESTING_HANDOFF.md for the exact package and checksum.

## v0.2.0 verified management shipment

[Run 38215576271](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38215576271), source `3119f225944b9362b21c8e97383ad33ce16de2d7`: 63 core tests pass on each OS, four Windows integrations pass and native desktop smoke passes. `management.txt` confirms source manager actions, removal cancellation and confirmation, updated album counts, all-source refresh, repair, rebuild, Activity and unused-preview cleanup. The package and compiled XAML resources passed publication checks; the screenshot was inspected. PR #10 is merged at `106dcb700b58a5ae663b0648b2dc58e83e2f3d53`. See TESTING_HANDOFF.md for the release and checksum.

## Previous shipment: indexing recovery and memories

[Run 38168157835](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38168157835), source `ef23349e82723c98636f9e3a6acad5d7c210f135`: all jobs passed. 58 core cases passed on each of Windows and Linux; four Windows integration tests and the native desktop smoke passed. `memories.txt` verifies capture-date browsing while offline; `reconnect.txt` verifies preserved selection/scroll without indexing. Light/dark screenshots were inspected. PR #9 is merged at `ab08a6c67faf894d09dce5a7a1feddd8b5a90d5e`; its source tree matches the tested commit. See [the handoff](TESTING_HANDOFF.md) for the ZIP and checksum.

## Earlier reconnect shipment

[Run 38151631336](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38151631336), commit `4e31cf2d0b6473a4dc51fdbc4cada0915912cb17`: all jobs passed. 51 core cases pass on each of Windows and Linux, four Windows integrations pass, and the complete desktop smoke passes. Its reconnect evidence verifies selection and scroll retention and no silent discovery of a new file. PR #8 is merged. See [the current handoff](TESTING_HANDOFF.md) for the verified ZIP.

## UI refinement shipment

[Run 38114261821](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38114261821), commit `55df818d8e0c2234681af2eb6962e735e2d90689`: all jobs passed. 49 core tests pass on Windows and Linux, four Windows integration cases pass, and the packaged desktop passes the complete UI smoke including theme switching. PR #7 is merged. Light/dark screenshots were inspected; physical-drive confirmation of the reported import issue remains pending.

## Original gallery shipment

[Run 38077921235](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38077921235), commit `6b94e38c5e4ee4cc0112419fc4b28a3fe6450173`: all jobs passed. 47 core cases pass on each of Windows and Linux; four Windows integration cases pass; self-contained publishing, compiled XAML resources and native UI smoke pass. This code is merged through PR #6. The [testing handoff](TESTING_HANDOFF.md) links the exact ZIP and evidence.

The 260-fixture native run peaked at 58 decoded images and restored 170px thumbnail size with filenames hidden after restart. Offline preparation reported 260/260 saved previews. These are synthetic functional checks, not physical-drive or full-library performance certification.

## Automated coverage

`Luma.Tests` exercises persistent restart reads, LRU eviction after touch, reduced-budget startup, abandoned temporary cleanup, cancelled writes, oversized entries, invalid keys/path escape, key invalidation, concurrent writes, offline hits without source probes, offline misses without decoding, moved-root reconnect, duplicate decode coalescing, stale files, unplug during generation, persisted catalog availability and cancelled scans.

`Luma.Windows.Tests` exercises actual Windows volume GUID registration/resolution, stable registration with trailing separators, distinct folder IDs and malformed IDs. A generated BMP fixture also tests WinRT thumbnail generation through a resolved volume path and cached retrieval after deleting that fixture. Core CI runs on Windows and Linux; Windows integration runs on Windows.

Native media tests use small fixtures; catalog scale tests use 1k, 10k, 100k and 200k synthetic SQLite records. They do not establish full-library UI responsiveness or 100,000-image decoding performance. Simulated root changes and disconnects do not replace hardware testing. Cancellation coverage includes both pre-cancelled calls and cancellation after 100 indexed files; identity loss during indexing also verifies transaction rollback. Low-disk faults still require manual coverage.

## Real-life acceptance still pending

- Real USB drive: index, generate thumbnails, close/reopen app, unplug, browse saved thumbnails, replug under a changed drive letter.
- Use another drive at the old letter; confirm original source stays offline. Reformat and confirm it is not mistaken for the prior volume.
- Disconnect during enumeration and decoding; original files must be unchanged and existing cache remain usable.
- Decode JPEG/PNG, HEIC with and without codec, MP4/MOV, corrupt files, long/unicode paths, locked files and permission-denied folders.
- Kill process during cache write; reopen and verify complete prior data, no half-thumbnail, temporary cleanup.
- Fill local disk, reduce budget, and test concurrent app launch/cache ownership errors.
- Benchmark cold scan, warm thumbnail browsing, memory, eviction and 100k media items to evaluate continuous-gallery responsiveness.
- Verify default-app opening, clipboard interoperability, rename and native copy/move/delete dialogs on disposable files. These are implemented but their native shell interactions still need real-PC acceptance.

## Phase 2 validation baseline

The suite contains 26 core tests (run on both Windows and Linux) and 2 Windows integration tests. It covers cache restart/eviction/concurrency, offline behavior, identity resolution, Unicode source folders, changed serial rejection, real bitmap decoding, catalog paging/search/annotations, completed-scan reconciliation and transaction rollback during cancellation/disconnect.

The desktop pipeline publishes the self-contained x64 folder and launches it on Windows. Its UI Automation smoke test uses an isolated temporary catalog with synthetic BMP files to exercise the populated grid, search, selection and favorite filtering, cached folder navigation, and offline preparation with full fixture coverage. It records a screenshot and accessible-control evidence. It never touches a user's normal Luma catalog.

Consult the latest run linked from the pull request for the result of the exact commit being downloaded. Windows/Linux core tests and Windows integration have passed during this change; the desktop packaging and smoke check are required gates for the downloadable artifact. The Linux development container cannot execute the full .NET test runner due to process-information failures, so CI is the execution source of truth. Physical USB and native destructive-operation QA remain separate acceptance checks.

## Verified Phase 1 run

[Run 37933213641](https://github.com/smile-plzz/Luma-windows-software/actions/runs/37933213641), commit `790c8625a364054511a72d09c9ba2ea4a516d785`: all jobs passed. 20 core tests pass on each of Windows and Linux; both Windows integration tests pass; the self-contained desktop build and populated-library UI Automation smoke test pass. The `Luma-Phase1-win-x64` ZIP and desktop screenshot/control evidence are attached to that run.

## Phase 2 coverage and acceptance

Six additional core regressions cover catalog-only folder discovery with literal `%`/`_` names, cancellation/resume and restart with no drive probes, partial coverage under a full cache without evicting another source, stale/missing-key coverage, cache clearing without changing originals or annotations, and offline misses without decoding. Native UI smoke selects a source, enters its cached `Trips` folder, returns to the parent, prepares all eight previews and asserts `8 / 8` coverage.

Before a general release, manually prepare a multi-page removable source, cancel/resume, disconnect it, restart Luma and check coverage. Repeat with insufficient cache space and another offline source already cached. Confirm that preparation does not evict that source, ordinary browsing may evict it, and the confirmation for clearing previews clearly affects all sources. Closing the app mid-preparation must allow the next launch to obtain cache ownership. Verify large catalogs and low-disk failures separately; the small-fixture CI does not certify performance or physical unplug behavior.

## Gallery redesign coverage

See [GALLERY_BUILD.md](GALLERY_BUILD.md) for the current implementation and limits. The gallery suite adds ten cursor sort/direction cases, seven metadata/organization/transfer regressions, and three catalog scale cases (1k/10k/100k). The native smoke now uses 260 fixtures, scrolls to the end and back without paging buttons, records peak decoded images, then performs the existing offline preparation/disconnection sequence. Windows integration also checks BMP/JPEG/PNG decoding, dimensions and missing capture-date fallback. Native smoke additionally creates/populates an album and checks view preferences after restarting the isolated library. The verified gallery run above records the final counts and outcomes.

The original gallery suite contained 47 core test cases (including three scale cases) and four Windows integration cases. A source-list regression also checks that reconnecting under a different mount path updates the displayed path. Core cases run on both Windows and Linux. Native UI smoke is an additional independent gate.

## Import lock regression

The first real-drive report showed `SQLite Error 5: database is locked` while browsing during a long scan. `QueryAsync` used an immediate transaction despite executing only SELECTs, competing with the scanner for the writer slot. It now uses a deferred read snapshot; count and rows remain consistent while WAL allows the scan to write.

Two regressions hold an active scan at a progress checkpoint while querying capture-date pages, cursor continuation, filtered album/favorites and month navigation. Queries must complete before the scanner is released, must see only committed rows, and must retain organization after either scan commit or cancellation. These raise core coverage to 49 cases per OS. Physical-drive confirmation remains pending.

## Catalog-first reconnect regression

Two additional core tests cover reopening saved sources across a fresh catalog instance without resolver calls or scan progress, preservation of organization, deferred discovery of external changes until explicit rescan, and reuse of empty registered sources. This raises the core suite to 51 cases per OS. Native smoke uses drive-status refresh for simulated disconnect/reconnect, verifies the exact selected card and scroll position survive, and checks that a newly added file is not silently indexed. Physical USB timing and varied Windows hardware still need user acceptance.

## Resumable indexing and memories follow-up

New automated cases exercise checkpoint reuse after restart/mount change, cancellation and disconnect without partial publication, completed-folder invalidation, checkpoint discard/removal, overlapping scanner exclusion, organization retention, offline capture-date memories, cursor traversal and filters, stale metadata and exact leap-day matching. The native desktop smoke now enters On this day with the source offline, verifies a previous-year capture fixture and excludes files without capture metadata. The successful run and downloadable artifact are recorded in TESTING_HANDOFF.md.

On the physical bench, interrupt a large multi-folder import, close and reopen Luma, reconnect the HDD (including a new drive letter), and choose **Activity → Resume paused indexing**. Verify saved progress, stable albums/favorites and final totals. Repeat with a large flat folder. Check Activity for protected-folder reports. Try another date in memories, then disconnect and verify prepared previews. Test the source's actual filesystem and camera-date conventions; synthetic CI cannot certify those.

## Library management follow-up

Core tests cover successful and offline rebuilds, cancellation and identity loss retaining old previews, capacity protection for other sources, exact cleanup estimates and removal, and source removal updating counts and album membership while retaining originals. Native smoke exercises visible source management, removal cancellation and confirmation, refreshed album counts, all-source refresh, missing-preview repair, successful preview rebuild, Activity and cleanup after source removal. The successful run and versioned release are recorded in TESTING_HANDOFF.md.

## Filter and navigation refinement

Tests cover new-library defaults, one-time settings migration, retention of later explicit filter choices, all-format catalog counts/cursor filtering, and native display of an indexed non-everyday DNG fixture. Desktop checks enable the opt-in format filter, verify matching-versus-library counts, remove its chip, and use Reset to reveal the file again. The existing source/storage/activity tests now use consolidated destinations. A DNG fixture with no valid image payload verifies catalog visibility independently from preview decoding.


## v0.4.0 month and large-library acceptance

Core checks cover exact local month ranges in both directions, filtered month counts, migration of an existing catalog, idempotent initialization, metadata invalidation/deletion, source removal, cursor ties, and cancellation/resume across 100-record metadata commits. Scale fixtures include 200,000 records and report the old capture-sort expression separately from the new continuation query.

Native smoke selects September 2026, verifies its 29-item fixture count, visits the adjacent month and returns, selects two files in Select mode, and checks that the Windows clipboard contains both actual file paths. It clears selection and returns to All months. `months-selection.png` and `months-selection.txt` are the review evidence. Existing offline, reconnect, thumbnail recycling, storage and management checks remain required.

Bench acceptance: upgrade a backed-up real catalog while Luma is closed; compare date grouping against known photo dates; browse small and large months; test Ctrl/Shift selection and the simple Select mode; paste copies into a disposable Explorer folder. Measure time to first usable gallery, warm month switching and scrolling on the 200,000-item library. Pause and resume both discovery and date reading. Note HDD model, connection, file types, collection/folder counts and cache state. Do not compare synthetic SQLite timings with an initial physical-drive scan.

Verified v0.4.0: [run 38307725690](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38307725690), source `a1c03b8323e5b80644b090bf69da88ffec92ae64`, merged through PR #12. 71 core tests on each OS, four Windows integrations, packaged XAML and native desktop checks passed. The exact package is identified in TESTING_HANDOFF.md.

## Exclusions and packaging update

Reversible folder, source and global extension rules now filter browsing and future scans. Use Settings or Refresh → Manage exclusions to restore them; Reset does not remove rules. Originals and saved organization are retained. Changing rules invalidates affected paused traversal checkpoints, not the published catalog. See [usage and limits](EXCLUSIONS.md) and [executable branding](BRANDING.md).

Regression coverage includes case-insensitive extensions, Unicode folder names, literal wildcard characters, sibling boundaries, album/favorite preservation, source and month counts, preview retention, paused traversal invalidation, active-scan exclusion, source removal and restoration. Native automation exercises all three context actions, cancellation, Reset, a filtered rescan, restoration from an empty gallery, and EXE metadata/icon inspection. The successful v0.5.0 run and exact package are recorded in the shipment handoff.

## Final experience refinement

Saved browsing position, explicit discovery/date-reading progress, accessible action hints and theme-matched dialogs complete the planned build. See [behavior and limits](FINAL_EXPERIENCE.md). Verified shipment evidence is recorded in the testing handoff.
