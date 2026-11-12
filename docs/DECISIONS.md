# Luma Product & Architecture Decisions

Log of key product, architecture, and trust decisions following the Section 10 format.
Items marked `[ASK]` require confirmation from Ismail (repository owner) before final release publication or external legal/infrastructure commitments.

---

## D-1: Distribution channel (portable zip vs installer) [ASK]
Date: 2026-11-12
Context: Luma ships as a portable `win-x64` self-contained ZIP (`Luma-v0.6.0-win-x64.zip`). Users must extract the entire ZIP rather than running `Luma.App.exe` inside Explorer's ZIP preview, and `%LOCALAPPDATA%\Luma` stays behind unless manually deleted. An experimental `scripts/Luma-Installer.iss` Inno Setup script also exists.
Options:
- A: Keep portable `win-x64` ZIP as the primary supported release artifact, accompanied by clear extraction docs and SHA-256 verification.
- B: Switch primary distribution to an Inno Setup `.exe` installer or MSIX package.
- C: Ship portable ZIP now, keep Inno Setup script documented as optional/draft, and revisit installer + auto-update once code signing is decided.
Decision: Option C. Portable ZIP works cleanly without admin rights or Start Menu registry clutter during early stabilization, and protects upgrades via `%LOCALAPPDATA%\Luma`. Transitioning default users to an unsigned installer without Authenticode signing replaces one SmartScreen prompt with another ("Unknown publisher installer"). Ask Ismail whether to enable `.exe` installer builds in CI and whether MSIX / Store distribution is desired for `v1.0.0`.
Consequences: Users must extract the whole ZIP to a writable folder; uninstallation leaves `%LOCALAPPDATA%\Luma` intact unless the user deletes it (documented in `docs/USER_GUIDE.md`).
Evidence: `.github/workflows/release.yml`, `scripts/Luma-Installer.iss`, `docs/PACKAGING.md`, `tests/Luma.Tests/UpgradeTests.cs`.
Reversible: yes

## D-2: Code signing and SmartScreen honesty [ASK]
Date: 2026-11-12
Context: Both the `.exe` inside the portable ZIP and any unsigned installer trigger Windows Defender SmartScreen warnings on first download because no Authenticode certificate signs the binary.
Options:
- A: Remain unsigned permanently with no explanation.
- B: Remain unsigned for now, publish SHA-256 checksums, document SmartScreen behavior plainly in README, User Guide, Known Limits, and release notes, and ask owner about Authenticode / SignPath OSS signing.
- C: Spend on commercial EV/OV code signing or apply for SignPath open-source signing before v1.0.0.
Decision: Option B now, escalating Option C to Ismail (`[ASK]`). Paying for certificates or enrolling in SignPath requires owner identity, billing, or OSS foundation eligibility. Publishing checksums and honest SmartScreen documentation builds trust immediately without delaying fixes.
Consequences: First-time downloaders still see SmartScreen's "More info -> Run anyway" step until a certificate or reputation threshold is established.
Evidence: `README.md`, `docs/KNOWN_LIMITS.md`, `docs/RELEASE_CHECKLIST.md`.
Reversible: yes

## D-3: Update mechanism and upgrade continuity
Date: 2026-11-12
Context: Portable ZIP users upgrade by downloading a newer release and replacing the binary folder. Any break in `%LOCALAPPDATA%\Luma` migration would lose tags, favorites, albums, exclusions, or browse position.
Options:
- A: Silent background GitHub Releases polling + auto-updater.
- B: Manual re-download with strict schema/settings forward-migration guarantees, tested `v0.6.0` -> `v0.7.0` upgrade suite, and explicit upgrade notes in `docs/USER_GUIDE.md`.
- C: Opt-in manual "Check for updates" button (requires outbound network call when clicked).
Decision: Option B for v0.7.0 stabilization; evaluate Option C as an opt-in future improvement after stabilization freeze clears (`[ASK]`). Keeping zero unsolicited HTTP checks preserves strict offline trust while `UpgradeTests` guarantees zero catalog loss across versions.
Consequences: Users check GitHub Releases manually for newer versions; upgrades preserve `catalog.db`, `settings.json`, `exclusions`, and cached thumbnails completely.
Evidence: `tests/Luma.Tests/UpgradeTests.cs`, `src/Luma.Core/MediaCache.cs`, `docs/USER_GUIDE.md`.
Reversible: yes

## D-4: Strict stabilization freeze until exit criteria are met
Date: 2026-11-12
Context: Since `v0.6.0`, branch `feat/v0.7.0-trust-speed-and-viewing` added major capabilities (Map/GPS backfill, Photo Stacks, Adaptive/Moments layouts, Smart Collections, Tag Management, Batch Rename, Multi-file Undo). While feature-rich, our audit uncovered 12 S2 defects (including first-run empty state hiding the Add button, multi-word tag splitting, dishonest DB corruption repair notice, unredacted guard logs, and Preview keyboard focus gaps).
Options:
- A: Continue adding requested roadmap features (auto-tagging, editing, built-in video player).
- B: Enforce a strict stabilization freeze: accept only bug fixes, data safety/privacy hardening, UX clarity improvements, tests, and documentation until all Section 12 exit criteria pass.
Decision: Option B. Per prioritization rule 7, trust and core reliability outrank new capabilities.
Consequences: No new speculative features ship before `v0.7.0` stabilizes cleanly. Roadmap expansion resumes only after green CI, smoke, benchmark, upgrade, and owner verification.
Evidence: `docs/assessment/FINDINGS.md`, `docs/assessment/JOURNEYS.md`, `docs/REQUIREMENTS.md`.
Reversible: yes

## D-5: Offline and disconnected external-drive contract
Date: 2026-11-12
Context: Users expect to browse unplugged external HDDs seamlessly without confusing errors when trying to edit or open actual media files that reside on disconnected storage.
Options:
- A: Block all actions on disconnected items.
- B: Allow full offline catalog browsing, search, filter, prepared thumbnail viewing, and catalog-only metadata organization (favorites, tags, virtual albums, map coordinate pinning); block disk mutations (`Open`, `Reveal`, `Copy`, `Cut/Move`, `Rename`, `Delete`, `Undo transfer`) with clear "connect drive first" guidance.
Decision: Option B, paired with fixing `DiskGuard` so warm `.thumb` reads continue working even when system free space is low. This matches real-world external archive workflows honestly.
Consequences: UI surfaces (card badges, Preview overlay, transfer/undo actions) must explicitly communicate catalog-only vs live-file requirements.
Evidence: `src/Luma.Core/OfflineBrowsing.cs`, `src/Luma.App/MainWindow.Offline.cs`, `src/Luma.App/MainWindow.Organization.cs`.
Reversible: yes

## D-6: Deletion safety model (Windows Recycle Bin only)
Date: 2026-11-12
Context: Luma operates directly on personal originals across PC and external drives. One irreversible delete bug could permanently destroy irreplaceable photos or videos.
Options:
- A: Support Shift+Delete permanent erase as a shortcut.
- B: Route every delete exclusively through Windows Shell (`SHFileOperation` with `FO_DELETE` + `FOF_ALLOWUNDO`) after an explicit in-app confirmation, and never advance preview or mutate catalog state if the user cancels Windows' Recycle Bin dialog.
Decision: Option B. Recoverability via Recycle Bin and honoring user cancel strictly (`F-006`) outweigh convenience shortcuts. Drives without Recycle Bin support show Windows' own permanent-delete warning dialog.
Consequences: Fixed `MainWindow.Delete()` and `DoPreviewDelete()` to inspect `ShellFiles.Run` boolean result and perform targeted catalog removal rather than ignoring Cancel or triggering an unnecessary full rescan.
Evidence: `src/Luma.App/ShellFiles.cs`, `src/Luma.App/MainWindow.xaml.cs`, `src/Luma.App/MainWindow.Preview.cs`, `docs/assessment/FINDINGS.md` (`F-006`).
Reversible: yes

## D-7: Privacy-first diagnostics, path redaction, and user feedback [ASK]
Date: 2026-11-12
Context: Luma had no in-app feedback path and zero filed user issues, while `MainWindow.Guard` wrote raw local folder paths into `last-error.txt` despite `App.LogCrash` already using `PathRedactor`.
Options:
- A: Automatic background crash/usage telemetry uploaded to an external endpoint.
- B: Zero automatic network calls or telemetry; always redact personal paths in both `crash.log` and `last-error.txt`; add a manual Settings -> "Send feedback / Report a problem" button opening a prefilled GitHub issue template in the default browser with only version, Windows build, and optional user-attached diagnostic summary.
Decision: Option B. Automatic remote telemetry (`[ASK]`) is explicitly deferred unless Ismail approves and discloses a privacy policy and opt-in UI. Manual GitHub issue filing gives real-use signal while guaranteeing zero unexpected traffic.
Consequences: Users never experience background uploads; contributors get consistent OS/version/redacted diagnostic context via structured issue templates.
Evidence: `src/Luma.App/PathRedactor.cs`, `src/Luma.App/App.xaml.cs`, `src/Luma.App/MainWindow.xaml.cs`, `.github/ISSUE_TEMPLATE/`.
Reversible: yes

## D-8: Repository naming, metadata, and user-first messaging
Date: 2026-11-12
Context: GitHub repository description was simply `"Luma"` with null topics, `README.md` mixed duplicated v0.6.0 blocks with forward links, and an open PR #16 (`docs/gallery-redesign-plan`) sat stale. Multiple files also contained em-dashes against project copy rules.
Options:
- A: Leave developer-centric README and sparse repo metadata as-is.
- B: Set clear one-line product positioning and GitHub topics, rewrite README to lead with user benefit and honest release links, remove all em-dashes across code/docs/metadata, and link user docs cleanly from Settings.
Decision: Option B. Improves discoverability and eliminates contradictory copy.
Consequences: Requires updating `Smoke-Test.ps1:37` in lockstep when removing the em-dash from `Luma.App.csproj` `AssemblyTitle` (`Luma - Local media gallery`).
Evidence: `README.md`, `src/Luma.App/Luma.App.csproj`, `scripts/Smoke-Test.ps1`, `.github/workflows/release.yml`.
Reversible: yes

## D-9: Repository open-source licensing [ASK]
Date: 2026-11-12
Context: No `LICENSE` file exists in the repository root, creating ambiguity for contributors, packagers, and users.
Options:
- A: MIT License (permissive, minimal friction, aligns with standard .NET utilities).
- B: GPL-3.0 (copyleft).
- C: Proprietary / All rights reserved notice.
Decision: Recommend Option A (MIT License) as best fit for a community-trusted free desktop utility, but keep unmerged until Ismail explicitly confirms licensing (`[ASK]`). Adding a binding legal license file without explicit copyright-holder instruction is escalated rather than assumed.
Consequences: `docs/KNOWN_LIMITS.md` and `docs/RELEASE_CHECKLIST.md` record license confirmation as an explicit pre-public-release owner step.
Evidence: Repository root inspection (`gh repo view`, working tree check).
Reversible: no (once published cleanly under a chosen license on tags, old releases keep that license)

## D-10: Supported Windows baseline (Windows 10 2004+ x64) and ARM64 deferral
Date: 2026-11-12
Context: `Luma.App.csproj` targets `net8.0-windows10.0.19041.0` with `win-x64` self-contained runtime. Adding `win-arm64` requires separate native Windows App SDK publishing, runner validation, and WebView2/SQLite bundle verification.
Options:
- A: Ship dual `win-x64` and `win-arm64` binaries without automated ARM64 UI smoke testing.
- B: Support Windows 10 2004+ (build 19041) and Windows 11 on `win-x64` exclusively, and defer ARM64 until real ARM64 CI/hardware testing exists.
Decision: Option B. Shipping an untested architecture binary violates the honesty rule ("never claim support without test evidence").
Consequences: Document Windows 10 2004+ / Windows 11 x64 requirement explicitly in README, `USER_GUIDE.md`, and `KNOWN_LIMITS.md`.
Evidence: `src/Luma.App/Luma.App.csproj`, `.github/workflows/build.yml`.
Reversible: yes

## D-11: External media viewing, video boundaries, and codec honesty
Date: 2026-11-12
Context: Luma lists photos and video files (`mp4`, `mov`, `mkv`, `avi`, `webm`, `wmv`, `m4v`, `3gp`) across local and external drives. Users double-clicking or opening an item expect their native Windows photo viewer or video player rather than maintaining a duplicate in-app media viewer, while bare Windows installs can still lack HEVC/RAW thumbnail codecs.
Options:
- A: Maintain an in-app full-screen preview overlay and/or embed a heavy media engine (`LibVLC` / `FFmpeg` / `MediaPlayerElement`) with audio/codec licensing burdens.
- B: Keep Luma focused as a fast thumbnail browser, organizer, and map index, and open originals directly in the user's preferred Windows default app (`Enter` / double-click / `Open` in gallery or Map via `ShellFiles.OpenInDefaultApp`), alongside codec troubleshooting (`HEVC Video Extensions` / `Raw Image Extension`) when Windows cannot render a thumbnail.
Decision: Option B. Keeps Luma lightweight, avoids duplicating OS viewers, and respects user-chosen viewers and players.
Consequences: Removed in-app `PreviewOverlay` (`MainWindow.Preview.cs`) so every open action across Gallery and Map launches the external Windows default app; documented codec limits in `docs/KNOWN_LIMITS.md`.
Evidence: `src/Luma.App/ShellFiles.cs`, `src/Luma.App/MainWindow.xaml.cs`, `src/Luma.App/MainWindow.Map.cs`, `docs/FORMATS.md`, `docs/KNOWN_LIMITS.md`.
Reversible: yes

## D-12: Metadata extraction and deterministic capture-date fallback
Date: 2026-11-12
Context: Media timelines look scrambled when files lack EXIF dates, carry corrupt `0000:00:00` / `1904` timestamps, or mix photos with screenshots and videos. Users need predictable chronological sorting and visibility into why a date appeared.
Options:
- A: Trust OS `LastWriteTime` blindly.
- B: Use deterministic fallback priority: (1) embedded camera/container capture timestamp (`EXIF DateTimeOriginal` -> Windows `System.Photo.DateTaken` -> QuickTime/MP4 creation atom -> XMP `CreateDate`), validated to sensible calendar bounds; (2) earliest valid filesystem timestamp (`min(CreationTimeUtc, LastWriteTimeUtc)`). Expose fallback explanation in Help / Details inspector and parse XMP strictly with `DtdProcessing.Prohibit` and `XmlResolver = null`.
Decision: Option B. Preserves accurate Memories ("On this day") and avoids false future/1970 clustering when files are copied between drives.
Consequences: Document exact precedence in `docs/USER_GUIDE.md`; add security/torture unit tests in `Luma.Tests`.
Evidence: `src/Luma.Windows/WindowsMetadataReader.cs`, `src/Luma.Core/MinimalGpsExtractor.cs`, `src/Luma.Core/ResumableIndexing.cs`, `tests/Luma.Tests/IndexingAndMemoriesTests.cs`.
Reversible: yes

## D-13: External-only media opening and SQLite + in-memory Map metadata caching
Date: 2026-11-12
Context: Owner feedback highlighted two everyday UX expectations: (1) Map View should persist extracted GPS/EXIF metadata in cache so populated libraries do not repeatedly rescan unchanged files unless new or modified files arrive, and (2) clicking or opening photos from Gallery or Map should hand directly off to the user's external Windows software instead of an internal photo viewer overlay.
Options:
- A: Keep internal `PreviewOverlay` (`Space` / Map clicks) alongside basic DB persistence without in-session Map query/backfill memoization.
- B: Remove `PreviewOverlay` (`MainWindow.Preview.cs`) completely, route all Gallery and Map pin/popup/filmstrip/drawer open actions to `ShellFiles.OpenInDefaultApp` (with in-overlay `MapNotice` offline feedback), add `ix_metadata_lookup` covering index on `metadata`, treat unreadable codec headers via `MinimalGpsExtractor` + `MetaVersion = 2` negative caching (retrying only vanished paths and changed `(length, modified_ticks)`), and memoize Map SQL results, filter dropdowns, and connected backfill cleanliness per `catalogGeneration`.
Decision: Option B. Eliminates redundant SQLite/file rescans in Map View and guarantees native Windows app opening everywhere.
Consequences: Verified zero re-reads of unchanged GPS/non-GPS files in `MapMetadataCache_SkipsUnchangedFilesIncludingNonGpsAndScansOnlyNewOrChangedFiles`, `8/8` Windows reader tests, clean desktop smoke test, and updated user documentation (`README.md`, `USER_GUIDE.md`, `TROUBLESHOOTING.md`, `KNOWN_LIMITS.md`, `ACCESSIBILITY.md`).
Evidence: `src/Luma.Core/Organization.cs`, `src/Luma.Windows/WindowsMetadataReader.cs`, `src/Luma.App/MainWindow.Map.cs`, `src/Luma.App/MainWindow.xaml`, `src/Luma.App/Assets/Map/map.html`, `tests/Luma.Tests/StabilizationTests.cs`.
Reversible: yes

## D-14: Transparent loading-state locking, GPU hover micro-interactions, and Windows 11 Mica surface craft
Date: 2026-11-12
Context: Interacting with Luma while heavy operations ran (startup integrity checks on multi-gigabyte catalogs, `RestorePlace`, `RefreshGallery`, `OpenDuplicatesFinder`, `ResetCatalog`, `RestoreBackup`, `MaintainCache`, or double-launching while `cache.lock` was held) allowed unguarded clicks to queue behind `galleryGate`, leave `cache == null`, or trigger UI freezes/crashes without a visible progress bar. Additionally, opaque brushes hid `MicaBackdrop`, card hover toggled overlays abruptly, and Adaptive/Moments tiles lacked keyboard/hover parity.
Options:
- A: Rely on silent `ViewHost.IsEnabled = false` and leave heavy dialogs and startup unguarded.
- B: Keep the standard OS titlebar with `MicaKind.BaseAlt` + translucent sidebar (`LumaSidebar`) so WinUI caption buttons never shadow dialog `"Close"` UIA controls; protect startup and structural mutations with a transparent `LoadingVeil` (`WorkspaceHost.IsEnabled = false`, live `ProgressBar`, Cancel/Retry/Exit actions), coalesce rapid `RefreshGallery` queries (`galleryRequestVersion` pre-cancellation + deferred `GalleryResetVeil`), throttle `Activated` refreshes, surface a prominent `ActiveWorkBanner` for non-blocking background scans/repairs, and add `UISettings.AnimationsEnabled`-aware Composition animations (inner `1.035x` card zoom, thumbnail fade-in, `ThemeShadow` depth, panel entrances, Adaptive/Moments accessibility parity, Video duration pills, and non-overlapping Map chrome).
Decision: Option B. Eliminates mid-load interaction hangs and gives calm, transparent progress feedback across every critical state.
Consequences: Verified across `131` core unit tests (including `ThumbnailCacheLockAndDuplicateScanCancellationBehaveCleanly`), `8/8` Windows integration tests, and `521.7s` full desktop UIA `Smoke-Test.ps1`.
Evidence: `src/Luma.App/App.xaml`, `src/Luma.App/MediaCard.cs`, `src/Luma.App/MainWindow.xaml`, `src/Luma.App/MainWindow.Progress.cs`, `src/Luma.App/MainWindow.xaml.cs`, `src/Luma.App/MainWindow.Gallery.cs`, `src/Luma.App/MainWindow.Management.cs`, `src/Luma.App/MainWindow.Organization.cs`, `src/Luma.App/MainWindow.Map.cs`, `tests/Luma.Tests/StabilizationTests.cs`.
Reversible: yes
