# Luma Context

## Project Goals

Deliver Luma v1.0.0 starting from shipped milestone v0.6.0 (`f796035`):
- Take Luma from planned milestone complete to a polished, trustworthy, daily-driver Windows media library.
- Fast on very large libraries (100k to 500k items) and responsive on slow HDDs.
- Safe with the user originals: scanning is read-only, local SQLite catalog and bounded persistent thumbnail cache in `%LOCALAPPDATA%\Luma`.
- Rich in user-centric features: tags, ratings, color labels, smart collections, duplicate finder, quick preview overlay, export/share.
- Accessible, easy to install via per-user installer as well as portable ZIP, verified with independent verification.

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
