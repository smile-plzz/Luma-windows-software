# Luma Swarm Task Board

Base commit: `f796035ec642253553acab84d67f51bee95d8f18` (v0.6.0)
Last updated: 2026-11-11

## Wave 0: Recon, Baseline, Task Board

| Task ID | Role | Title | Pri | Effort | Status | Notes |
|---|---|---|---|---|---|---|
| ORC-1 | ORC | Create BOARD.md and STATUS.md | P0 | S | done | Initialized board from Section 7 |
| ORC-2 | ORC | Merge queue and conflict forecasts | P0 | S | done | Published merge order and hot files |
| ORC-3 | ORC | Batch owner decision and human-only items | P0 | S | done | Packaged for owner sign-off |
| RCN-1 | RCN | Architecture map | P0 | M | done | docs/swarm/ARCHITECTURE_MAP.md |
| RCN-2 | RCN | Repository identity and link resolution | P0 | S | done | Resolved rename to Luma-windows-software |
| RCN-3 | RCN | Drift report | P0 | M | done | docs/swarm/DRIFT_REPORT.md |
| RCN-4 | RCN | User journeys catalogue | P0 | S | done | docs/swarm/USER_JOURNEYS.md |
| RCN-5 | RCN | Performance baseline records | P0 | S | done | Extracted 200k synthetic baselines |
| QAE-1 | QAE | Test baseline verification and flakiness audit | P0 | M | done | 82 core + 4 Win tests pass, headless smoke analyzed |
| QAE-2 | QAE | Fixture generator for tests | P0 | M | planned | Deterministic synthetic media |
| SEC-1 | SEC | Threat model draft | P0 | M | done | docs/swarm/THREAT_MODEL_DRAFT.md |

## Wave 1: Trust (Stability, Data Integrity, Failure Paths) -> Target v0.7.0

| Task ID | Role | Title | Pri | Effort | Status | Notes |
|---|---|---|---|---|---|---|
| STB-1 | STB | XAML refresh crash investigation (0xc000027b) | P0 | M | planned | Stress test and root cause analysis |
| STB-2 | STB | Global exception handling and local crash reports | P0 | S | planned | Path-redacted, local-only report |
| STB-3 | STB | Pre-migration backup, quick_check, repair | P0 | M | planned | Safeguard SQLite catalog |
| STB-5 | STB | Failure-path test suite | P0 | L | planned | Disconnect, kill, disk-full paths |
| STB-6 | STB | Low-disk space handling | P0 | S | planned | Graceful degradation before write |
| STB-7 | STB | Concurrency and threading audit | P1 | M | planned | ConfigureAwait and affinity rules |
| STB-8 | STB | Settings file resilience | P0 | S | planned | Fallback to defaults on corruption |
| MED-1 | MED | Format matrix and acceptance tests | P0 | M | planned | JPEG, PNG, WebP, HEIC, RAW, MP4, MOV |
| MED-2 | MED | Codec guidance in-app | P0 | M | planned | Actionable store link, cached status |
| MED-4 | MED | Corrupt and hostile file handling | P0 | M | planned | Truncated/malformed fuzzing safety |
| IDX-6 | IDX | Scanner robustness | P0 | M | planned | Long paths, Unicode, junctions, cloud |
| IDX-7 | IDX | Volume identity edge cases | P0 | M | planned | Reformat detection, cloned drives |
| SEC-2 | SEC | Process launch and shell verb review | P0 | S | planned | Verify safe argument passing |
| SEC-3 | SEC | File operation security review | P0 | S | planned | Root escaping and reserved name checks |
| QAE-5 | QAE | Upgrade migration tests (v0.2.0 - v0.6.0) | P0 | M | planned | Verify forward schema compatibility |
| PRF-1 | PRF | Benchmark harness (Luma.Benchmarks) | P0 | M | planned | Synthetic catalogs 1k to 500k |

## Wave 2: Speed and Polish -> Target v0.7.x

| Task ID | Role | Title | Pri | Effort | Status | Notes |
|---|---|---|---|---|---|---|
| PRF-2 | PRF | Startup profiling and deferral | P0 | M | planned | Target cold start under 2.0s |
| PRF-3 | PRF | Query plans and indexing tuning | P0 | M | planned | EXPLAIN QUERY PLAN, avoid OFFSET |
| PRF-4 | PRF | Scroll and decode pipeline | P0 | L | planned | Realized tile decode bounds |
| PRF-5 | PRF | Indexing throughput tuning | P1 | M | planned | Batching on simulated slow volumes |
| PRF-6 | PRF | Memory soak and leak hunt | P0 | M | planned | 30-minute scroll loop check |
| PRF-7 | PRF | Cache efficiency and pinning design note | P1 | M | planned | Hit rate reporting and LRU policy |
| PRF-8 | PRF | HDD benchmark kit | P0 | S | planned | Kit for owner physical testing |
| IDX-2 | IDX | Metadata expansion (EXIF / video atoms) | P1 | M | planned | Additive nullable columns |
| IDX-4 | IDX | Search engine upgrade (SQLite FTS5) | P1 | M | planned | Fast prefix search |
| MED-3 | MED | Video thumbnail poster frame extraction | P1 | M | planned | Non-black frame with duration |
| MED-5 | MED | Orientation and large image downscale | P1 | M | planned | EXIF orientation, 100MP+ handling |
| MED-6 | MED | Thumbnail quality and size tiers | P1 | M | planned | Multi-tier or cached scaling |
| MED-7 | MED | Preview regeneration rules | P1 | M | planned | Content version key cache invalidation |
| UXD-1 | UXD | First-run experience | P0 | M | planned | Welcome screen, browse while indexing |
| UXD-2 | UXD | Empty and error states | P0 | M | planned | Actionable recovery states |
| UXD-3 | UXD | Full keyboard navigation | P0 | M | planned | Grid, navigation, shortcuts |
| UXD-4 | UXD | Accessibility pass (UIA, contrast, zoom) | P0 | L | planned | Narrator labels, High Contrast |
| UXD-7 | UXD | Visual refinement and dialog styling | P1 | M | planned | Tokens, typography, context menus |
| UXD-9 | UXD | Status clarity and activity indicators | P1 | M | planned | Unified calm progress bar |
| UXD-10 | UXD | Settings consolidation | P1 | M | planned | Categorized settings page |
| DOC-1 | DOC | README restructure and repo link fix | P0 | M | planned | Plain overview, screenshots, setup |
| DOC-2 | DOC | User guide creation (USER_GUIDE.md) | P0 | M | planned | Complete feature guidance |
| DOC-3 | DOC | Troubleshooting guide | P0 | S | planned | Common issues and codec help |

## Wave 3: User-Centric Features -> Target v0.8.0

| Task ID | Role | Title | Pri | Effort | Status | Notes |
|---|---|---|---|---|---|---|
| IDX-1 | IDX | External-move reconciliation | P1 | L | planned | Fingerprint matching on refresh |
| IDX-3 | IDX | Duplicate detection backend | P1 | L | planned | Exact and perceptual hash engine |
| IDX-5 | IDX | Exclusion engine glob patterns | P2 | M | planned | Import/export and pattern rules |
| STB-4 | STB | Organization backup and restore engine | P1 | M | planned | Export/import tags, albums, favorites |
| ORG-1 | ORG | Ratings and color labels | P1 | M | planned | 1-5 stars, keyboard shortcuts |
| ORG-2 | ORG | Smart collections | P1 | L | planned | Saved dynamic search filters |
| ORG-3 | ORG | Duplicates and similar finder UI | P1 | L | planned | Group view with Recycle Bin safety |
| ORG-4 | ORG | Batch tagging and tag manager | P1 | M | planned | Autocomplete, rename, merge |
| ORG-5 | ORG | Album improvements | P1 | M | planned | Nested albums, cover choice, export |
| ORG-6 | ORG | Saved views | P2 | S | planned | Navigation state bookmarks |
| ORG-7 | ORG | Events grouping in timeline | P2 | M | planned | Date clustering heuristics |
| ORG-9 | ORG | Library stats and insights page | P2 | S | planned | Local distribution metrics |
| ORG-10 | ORG | On this day upgrades | P2 | S | planned | Extended historical views |
| VWR-1 | VWR | Quick preview overlay (Space bar) | P1 | L | planned | Full preview, zoom, pan, metadata |
| VWR-3 | VWR | Slideshow mode | P2 | M | planned | Configurable interval, shuffle |
| VWR-4 | VWR | Export and share flows | P1 | M | planned | Copy, rename pattern, share |
| VWR-5 | VWR | Wallpaper and print helpers | P2 | S | planned | Windows shell verb integration |
| UXD-5 | UXD | Selection and bulk actions polish | P1 | M | planned | Multi-select and action bar |
| UXD-6 | UXD | Drag and drop support | P1 | M | planned | Explorer drag out and folder drop in |
| UXD-8 | UXD | Localization readiness | P2 | M | planned | Resource strings extraction |
| SEC-4 | SEC | Dependency and supply chain audit | P0 | M | planned | CycloneDX SBOM, NuGet vulnerability check |
| SEC-5 | SEC | Privacy statement and network block test | P0 | S | planned | Verify zero telemetry or network calls |
| SEC-6 | SEC | Crash report redaction review | P0 | S | planned | Validate PII/path scrubbing |

## Wave 4: Packaging, Docs, Release -> Target v0.9.0 RC & v1.0.0

| Task ID | Role | Title | Pri | Effort | Status | Notes |
|---|---|---|---|---|---|---|
| PKG-1 | PKG | CI Release pipeline automation | P0 | M | planned | Tag-triggered builds and sums |
| PKG-2 | PKG | Per-user installer (Inno Setup / WiX) | P0 | L | planned | Clean install to %LOCALAPPDATA%\Programs |
| PKG-3 | PKG | Code signing preparation | P0 | M | planned | Pipeline integration, dry-run cert |
| PKG-5 | PKG | Version metadata verification | P0 | S | planned | Exe metadata and assembly stamping |
| PKG-6 | PKG | winget and Scoop manifest drafts | P2 | S | planned | Distribution package definitions |
| PKG-7 | PKG | Installer automated smoke test | P0 | M | planned | Silent install and uninstall gate |
| PKG-8 | PKG | ReadyToRun / trimming evaluation | P2 | M | planned | Startup and binary footprint study |
| DOC-4 | DOC | Developer architecture guide | P0 | M | planned | Build, test, migration instructions |
| DOC-5 | DOC | Release notes and changelog assembly | P0 | M | planned | User-facing changelog entries |
| DOC-6 | DOC | In-app help copy review | P0 | S | planned | Direct language, no em-dashes |
| DOC-7 | DOC | Archive historical documentation | P0 | S | planned | Move legacy phases to docs/history |
| QAE-7 | QAE | Release candidate test plan | P0 | M | planned | 45-minute checklist and suites |
| QAE-8 | QAE | Human-only physical acceptance kit | P0 | M | planned | Test kit for USB/HDD and shell |
| QAE-9 | QAE | CI test hygiene and caching | P0 | S | planned | NuGet caching and job separation |
| VER-1..5 | VER | Independent verification and audit | P0 | M | planned | Cold review and release sign-off |
| ORC-4 | ORC | Release execution | P0 | M | planned | Publish v1.0.0 with SHA256SUMS |
