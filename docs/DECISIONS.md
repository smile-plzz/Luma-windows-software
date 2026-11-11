# Luma Decision Log

Role: ORC (Coordinator and Release Manager)
Date: 2026-11-11
Audience: All agents and Project Owner

---

## 1. Owner Decision Items (Section 8 Resolutions)

| Decision Item | Finding & Decision | Status |
|---|---|---|
| **D-1: Canonical Repository** | `https://github.com/smile-plzz/Luma` was renamed to `https://github.com/smile-plzz/Luma-windows-software`. GitHub automatically redirects old links. All documentation, CI links, and release manifests are normalized to `smile-plzz/Luma-windows-software`. | Decided & Applied |
| **D-2: Phase Sequencing** | The project owner authorized opening Phase 4 (v0.7.0). Work followed the strict priority order: 1) Stability and data integrity (XAML crash mitigation, catalog backup/repair, settings resilience), 2) Performance benchmarks and scale tuning, 3) Polish and accessibility, 4) Feature enhancements (Tag Manager, Duplicates Finder, Smart Collections, Search Operators). | Decided & Applied |
| **D-3: Installer Architecture** | Preserved the unsigned portable win-x64 ZIP as the primary download channel. Additionally provided an Inno Setup script (`scripts/Luma-Installer.iss`) for per-user, non-admin installs into `%LOCALAPPDATA%\Programs\Luma`. Recommended Azure Trusted Signing if budget permits (~$10/month) for SmartScreen reputation. | Decided & Applied |
| **D-4: Quick Preview Overlay** | The owner prohibited embedded players/editors. A lightweight, read-only full-screen preview overlay was built in `MainWindow.Preview.cs` (triggered via Spacebar or Enter). It renders the image/poster with pan/zoom and metadata inspection without embedded playback controls, maintaining full alignment with the constraint. | Decided & Applied |
| **D-5: Organization Features Priority** | Implemented the high-value features in order: 1) Tag Manager (rename, merge, delete, count), 2) Duplicates Finder (read-only grouping by exact size and hash, with Explorer reveal), 3) Smart Collections (saved dynamic queries), 4) Search Operators (`tag:`, `ext:`, `year:`). | Decided & Applied |
| **D-6: Localization Readiness** | Extracted accessibility labels and dialog text. Bangla localization is ready to receive translated resource dictionaries when the owner requests it. | Deferred for Owner Request |
| **D-7: Physical Hardware Testing** | Synthetically validated all failure paths and large-scale (500k) queries. Converted real-hardware acceptance items into an actionable, numbered protocol in `docs/QA_ACCEPTANCE.md` for human owner execution with disposable media. | Packaged for Owner Sign-off |

---

## 2. Architectural Decisions

- **AD-1: Re-entrancy Synchronization (`galleryGate`):** Applied `SemaphoreSlim(1, 1)` to `RefreshGallery` to eliminate WinUI 3 `0xc000027b` XAML layout race conditions during rapid filtering and source changes.
- **AD-2: Rolling Catalog Backups:** Implemented `catalog.CreateBackupAsync()` before schema migrations, source removals, and manual user requests to eliminate any data-loss risks.
- **AD-3: Report-First External Move Reconciliation:** Detects externally moved media on connected sources using length and modification timestamps, preserving user tags, favorites, and album assignments without modifying disk files.
