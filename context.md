# Luma Context

## Project Goals

Deliver Luma `v0.7.0` (under active stabilization freeze `D-4` toward `v1.0.0`) starting from `v0.6.0` (`f796035`):
- Make Luma calm, trustworthy, and frustration-free from first download to daily multi-drive use.
- Zero open S1/S2 defects (`docs/assessment/FINDINGS.md`); all 12 core user journeys (`docs/assessment/JOURNEYS.md`) verified with tests and native UIA checks.
- Fast on very large libraries (100k to 500k items) and honest on slow external HDDs.
- Safe with user originals: scanning is read-only, local SQLite catalog and bounded persistent thumbnail cache live in `%LOCALAPPDATA%\Luma`.
- Rich yet calm curation: multi-word tags & Tag Manager, duplicates finder, Smart Collections, Spacebar Quick Preview, photo stacks, Adaptive/Moments views, and privacy-first Map view.
- Clear user documentation (`docs/USER_GUIDE.md`, `docs/TROUBLESHOOTING.md`, `docs/KNOWN_LIMITS.md`) and in-app feedback loop (`F1` help + Settings issue link + structured `.github/ISSUE_TEMPLATE/`).

## Technology Stack

- Language and Runtime: C#, .NET 8 (`net8.0` and `net8.0-windows10.0.19041.0`)
- UI Framework: WinUI 3 desktop app (Windows App SDK 1.8.260921001), self-contained win-x64
- Architecture (in `src/`):
  - `Luma.App`: WinUI 3 desktop shell and views
  - `Luma.Core`: Cross-platform catalog, SQLite queries, caching, indexing, reconciliation
  - `Luma.Windows`: Windows volume resolution, thumbnail generation, shell metadata
- Testing:
  - `tests/Luma.Tests`: Cross-platform xUnit core tests (Windows and Linux)
  - `tests/Luma.Windows.Tests`: Windows integration tests
  - `scripts/Smoke-Test.ps1`: Native UI Automation desktop smoke test

## Ground Rules and Constraints

- Originals are sacred: scanning is read-only. File moves, copies, and deletes happen through explicit user actions and native Windows shell mechanisms.
- Data integrity and startup failures come first.
- Catalog-first and offline-honest: cached browsing does not probe offline drives.
- No destructive schema migrations: existing `%LOCALAPPDATA%\Luma` catalogs upgrade in place idempotently with pre-migration backup.
- Privacy by default: no telemetry, no network calls with user data, no cloud.
- Synthetic benchmarks do not certify physical-drive behavior.
- Small, reviewable PRs with verified test evidence.
- Do not break portable builds: unsigned portable ZIP stays supported alongside installers.
- Plain, direct writing style: no em-dashes anywhere in code comments, commit messages, docs, or UI strings.

