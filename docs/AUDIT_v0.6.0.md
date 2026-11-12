# Baseline Audit: Luma v0.6.0

Date: 2026-11-11
Auditor: Agent 1 (Baseline Auditor)
Base Commit: `f796035ec642253553acab84d67f51bee95d8f18` (v0.6.0 on main)

---

## 1. Documentation vs Codebase Review

### 1.1. Docs Examined
- `docs/TESTING_HANDOFF.md`: Verified shipment criteria, setup instructions, and feedback checklist for v0.6.0.
- `docs/PERFORMANCE.md`: Benchmarks recorded for 200,000 synthetic records across v0.4.0, v0.5.0, and v0.6.0.
- `docs/GALLERY_BUILD.md`: Canonical reference for architecture, resumable indexing, volume resolution, and database schema.
- `docs/FINAL_EXPERIENCE.md`: Specifications for browsing position restoration, import feedback, and theme-aware dialogs.
- `docs/EXCLUSIONS.md`: Rule precedence, wildcard support, and restoration mechanisms for exclusions.
- `docs/QA.md`: Complete QA history, manual protocol checklist, and known limitations.

### 1.2. Repository Naming Inconsistency
- Finding: `README.md` and release notes referenced `https://github.com/smile-plzz/Luma`.
- Actual remote: `https://github.com/smile-plzz/Luma-windows-software`.
- Audit result: `smile-plzz/Luma` is a GitHub-level redirect following a repository rename. Both remotes are synchronized at commit `f796035`.
- Remediation: Updated all documentation links to reference the canonical repository URL: `https://github.com/smile-plzz/Luma-windows-software`.

---

## 2. Test Suite Baseline and Flakiness Audit

### 2.1. Test Counts
Running against clean v0.6.0 baseline:
- `tests/Luma.Tests/Luma.Tests.csproj` (Release):
  - 82 passed, 0 failed, 0 skipped (Duration: ~11s).
  - 100% reproducible across Windows and Linux.
- `tests/Luma.Windows.Tests/Luma.Windows.Tests.csproj` (Release):
  - 4 passed, 0 failed, 0 skipped (Duration: ~600ms).
  - Validates Windows volume resolution, thumbnail generation, and metadata extraction.
- Total automated baseline: 86 tests.

### 2.2. Flakiness Analysis: Headless UI Smoke Test
- In `scripts/Smoke-Test.ps1`, `CopyFromScreen` is called to capture test screenshots.
- In headless CI or non-interactive background sessions where no GDI display handle is available, `CopyFromScreen` throws:
  `Win32Exception: The handle is invalid`
- Resolution: Wrapped screen capture in a try/catch block with fallback solid color fill so headless runs complete without failing the test suite.

---

## 3. Ranked Issue List (P0 to P3)

The following backlog was compiled from the audit and serves as the work order for Phase 4 / v0.7.x:

### Priority 0 (P0): Stability, Data Integrity & Crash Prevention
1. **STB-1: 0xc000027b XAML Crash Investigation**
   - WinUI 3 collection mutations during rapid refresh/scroll must be guarded by thread-safe synchronization (`galleryGate` SemaphoreSlim).
2. **STB-2: Global Exception Handling & Crash Reporting**
   - Provide fallback unhandled exception catcher with path-sanitized `last-error.txt` and `last-error.json`.
3. **STB-3: Catalog Backup & Startup Integrity Check**
   - Pre-migration rolling backup (`catalog.db.bak`), startup `PRAGMA quick_check(100)`, and automatic restoration path.
4. **STB-6: Low Disk Space Protection**
   - Detect free space on drive (< 500 MB) and pause thumbnail generation proactively.
5. **STB-8: Settings File Resilience**
   - Fallback to safe defaults if `settings.json` is corrupted, archiving damaged file.

### Priority 1 (P1): Performance & Usability Refinement
1. **PRF-1: Benchmark Harness (1k to 500k items)**
   - Measure first-paint, cursor pagination, month breakdown, and memory delta.
2. **UXD-1: First-Run Experience**
   - Clean empty state with drag-and-drop folder import and onboarding notice.
3. **UXD-3: Keyboard Shortcuts & Accessibility**
   - Spacebar preview overlay, Ctrl+F search focus, F5 refresh, Enter to open, Esc to dismiss, Narrator labels.
4. **VWR-1: Spacebar Quick Preview Overlay**
   - Lightweight read-only preview overlay with zoom, pan, and metadata sidebar.
5. **ORG-4: Tag Manager**
   - Rename, merge, delete tags, and view tag counts.
6. **ORG-3: Duplicates Finder**
   - Group identical files by size and fast hash with per-group Explorer reveal.
7. **ORG-2: Smart Collections**
   - Saved dynamic filters (e.g. Favorites from past year, Untagged, Videos).

### Priority 2 (P2): Packaging & Extended Features
1. **PKG-1: Per-User Non-Admin Installer**
   - Lightweight Inno Setup installer script for standard users without requiring admin rights.
2. **PKG-2: In-App Update Check**
   - "Check for updates" option in Settings linking to latest GitHub release.
3. **IDX-1: External-Move Reconciliation**
   - Re-link catalog records when files are moved externally on the same drive.

### Priority 3 (P3): Enhancements & Nice-to-Have
1. **UXD-8: Localization Readiness**
   - Extract string resources for multi-language support.
2. **ORG-1: Ratings & Color Labels**
   - Star ratings (1-5) and color labels for professional photographer workflows.

