# Luma delivery roadmap

Status: the portable gallery development milestone is closed and merged into `main` through [PR #6](https://github.com/smile-plzz/Luma-windows-software/pull/6). The first real-use follow-up, [PR #7](https://github.com/smile-plzz/Luma-windows-software/pull/7), is also merged and shipped: import-lock correction and minimal UI refinement. [PR #8](https://github.com/smile-plzz/Luma-windows-software/pull/8) is also merged and shipped: catalog-first reopening and reconnect updates that preserve the gallery. [PR #9](https://github.com/smile-plzz/Luma-windows-software/pull/9) is merged and verified: resumable indexing and On this day memories. [PR #10](https://github.com/smile-plzz/Luma-windows-software/pull/10) is merged and verified for v0.2.0: source management, refresh, storage maintenance and Activity. [PR #11](https://github.com/smile-plzz/Luma-windows-software/pull/11) is merged and verified for v0.3.0: visible formats and active filters, complete Reset, consolidated navigation and pinned maintenance destinations. [PR #12](https://github.com/smile-plzz/Luma-windows-software/pull/12) is merged and verified for v0.4.0: explicit month browsing, visible multi-file selection and measured large-catalog query improvements. [PR #13](https://github.com/smile-plzz/Luma-windows-software/pull/13) is merged and verified for v0.5.0: reversible library/scan exclusions and executable branding. [PR #14](https://github.com/smile-plzz/Luma-windows-software/pull/14) closes the planned build in v0.6.0 with remembered views, clear import progress and final UX feedback. The current stage is real-life testing and everyday use. No additional feature phase is being started automatically.

| Milestone | Scope | Status |
| --- | --- | --- |
| Phase 1 — native library | Source identity, read-only scanning, thumbnails, search/tags/favorites, native file actions | Merged |
| Phase 2 — offline browsing | Bulk preparation, cancellation/resume, coverage, cached folders, cache clearing | Merged |
| Phase 3 — gallery redesign | Continuous gallery, customization/sorting, timeline, albums and Windows file control | Merged; automated gates passed; portable Windows shipment available |
| Final v0.6.0 refinement | Saved views, import progress, tooltips, outcomes and dialog consistency | Complete; merged and verified |
| Real-life testing | Personal media, physical drives, Windows file interactions and daily usability | Current stage; user feedback pending |
| Future delivery work | External-move reconciliation, measured full-library responsiveness, signing/installer | Deferred; prioritize from real-world feedback |

## Current handoff

Use [TESTING_HANDOFF.md](TESTING_HANDOFF.md) for the exact tested commit, download, upgrade instructions, known limits and feedback format. [GALLERY_BUILD.md](GALLERY_BUILD.md) describes current behavior; [QA.md](QA.md) separates automated evidence from manual acceptance.

Keep the shipped code stable while testing. Prioritize reported data-integrity or startup failures first, then recurring browsing/performance problems, then visual/usability refinements. A future fix should reproduce the report, receive appropriate regression checks, and identify its own tested build before shipment.

The app opens originals in default applications. Cloud accounts, an embedded player/editor, automatic original-file rearrangement and full-original backup are outside this milestone. Preview pinning needs a separate capacity design. Signing and an installer are not required to begin testing the unsigned portable build.

## Resumable indexing and memory browsing

Implemented persistent indexing checkpoints with resume/restart/report controls and atomic catalog publication, plus On this day capture-date browsing with date selection and offline previews. All automated checks passed (58 core cases per OS, four Windows integration cases and native desktop smoke). The downloadable shipment is tracked in TESTING_HANDOFF.md. Smart collections, pinned previews and installer/update notifications remain separate future work.

## Library management refinement

User-confirmed real-machine functionality led to a discoverability-focused follow-up: Manage sources, explicit refresh scopes, Storage & previews, safe rebuild/repair, unused-preview cleanup and Activity. Browsing-position restoration ships in v0.6.0. Catalog backup/restore remains deferred; it is not included.

## Exclusions and packaging update

Reversible folder, source and global extension rules now filter browsing and future scans. Use Settings or Refresh → Manage exclusions to restore them; Reset does not remove rules. Originals and saved organization are retained. Changing rules invalidates affected paused traversal checkpoints, not the published catalog. See [usage and limits](EXCLUSIONS.md) and [executable branding](BRANDING.md).

## Final experience refinement

Saved browsing position, explicit discovery/date-reading progress, accessible action hints and theme-matched dialogs complete the planned build. See [behavior and limits](FINAL_EXPERIENCE.md). Verified shipment evidence is recorded in the testing handoff.
