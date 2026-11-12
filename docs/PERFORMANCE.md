# Large-library performance

## v0.7.0 scale suite (PRF-1, optimized query engine)

Measurements gathered on Windows 11 with 8 logical cores using `Luma.Benchmarks` (`-c Release`). Exercises synthetic catalog query and timeline aggregation scaling from 1,000 to 500,000 records.

### After optimization vs baseline on the same machine

| Records | First 120 + Count | Next Page (Cursor) | Month Breakdown | Locate Middle Anchor | Memory Delta |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 | 30.21 ms | 9.78 ms | 15.85 ms | 7.99 ms | 0.11 MB |
| 10,000 | 4.51 ms | 4.84 ms | 7.02 ms | 1.26 ms | 0.11 MB |
| 100,000 | 11.25 ms | 1.80 ms | 31.47 ms | 4.70 ms | 0.11 MB |
| 200,000 | 20.89 ms | 1.16 ms | 45.90 ms | 7.15 ms | 0.11 MB |
| 500,000 | 39.51 ms | 1.04 ms | 84.50 ms | 40.89 ms | 0.11 MB |

Baseline on the same machine prior to optimization (`cc3de45`):
- 100,000 records: First 120 + Count `62.03 ms`, Next Page `4.05 ms`, Month Breakdown `752.84 ms`, Locate Anchor `600.87 ms`
- 200,000 records: First 120 + Count `70.97 ms`, Next Page `2.78 ms`, Month Breakdown `671.70 ms`, Locate Anchor `448.29 ms`
- 500,000 records: First 120 + Count `583.91 ms`, Next Page `2.41 ms`, Month Breakdown `2,095.56 ms`, Locate Anchor `1,396.14 ms`

[v0.7.0 raw measurements](performance/windows-v0.7.0-benchmarks.json). Note on cold vs warm runs: running `Luma.Benchmarks` (`-c Release`) from scratch on 2026-11-12 during stabilization verification measured SQLite synthetic population + cold first-page count vs warm cursor continuation on this workstation:
- 1,000 records: Populated `373.5 ms`, First 120 + Count `430.14 ms` (JIT warmup), Next Page (Cursor) `123.97 ms`, Months `385.61 ms`, Locate `147.09 ms`
- 10,000 records: Populated `3,174.1 ms`, First 120 + Count `43.08 ms`, Next Page (Cursor) `24.17 ms`, Months `30.59 ms`, Locate `7.64 ms`
- 100,000 records: Populated `37,473.7 ms`, First 120 + Count `153.91 ms`, Next Page (Cursor) `25.41 ms`, Months `371.1 ms`, Locate `93.76 ms`
- 200,000 records: Populated `86,334.8 ms`, First 120 + Count `241.91 ms`, Next Page (Cursor) `19.13 ms`, Months `746.14 ms`, Locate `120.19 ms`
- 500,000 records: Populated `251,890.8 ms`, First 120 + Count `562.23 ms`, Next Page (Cursor) `18.84 ms`, Months `1,321.47 ms`, Locate `737.65 ms`
Cursor continuation stays steady around `19-25 ms` across 10k-500k records. Single-run synthetic database timings; not physical HDD scan, image decoding, or UI frame rate. First paint path in v0.7.0 defers month timeline calculation to avoid blocking first 120 card render.

## v0.6.0 saved-position check

[Run 38355699422](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38355699422), source `f655715982253824c41329425c181c8876d9f3d5`, correctly located a middle anchor in 200,000 records in **574.92 ms** on the Windows runner. First 120 records: 35.09 ms; continuation: 0.70 ms; month breakdown: 461.89 ms. These are single-run catalog-only timings, not physical HDD or full UI measurements. Reopening loads at most an initial 120-record window around the anchor, with earlier items available explicitly. [Raw result](performance/windows-v0.6.0-200000.json).

## v0.5.0 validation

[Run 38350255657](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38350255657), source `b7d9eb16cc3547c64928e8fbdc202b7e0a400083`, exercises 200,000 catalog records with the exclusion-aware query implementation and no active rules.

| Environment | First 120 records and count | Next page | Month breakdown |
| --- | ---: | ---: | ---: |
| Windows | 53.06 ms | 1.67 ms | 506.31 ms |
| Linux | 42.01 ms | 1.51 ms | 967.90 ms |

[Windows raw result](performance/windows-v0.5.0-200000.json) · [Linux raw result](performance/linux-v0.5.0-200000.json). Single-run synthetic database timings; not a controlled before/after comparison, UI frame rate or physical-drive throughput. Exclusion predicates add catalog work; real timings depend on rules and filtering. The retained records make exclusion restoration immediate without rediscovery.

## v0.4.0 reference measurements

Measured in [run 38307725690](https://github.com/smile-plzz/Luma-windows-software/actions/runs/38307725690), source `a1c03b8323e5b80644b090bf69da88ffec92ae64`. Fixture: 200,000 synthetic SQLite records, four runner processors, 120 items per page. These are single-run measurements, not percentiles or a physical-drive benchmark.

| Environment | First page including count | Next page, count reused | Month breakdown | Previous capture-sort expression only |
| --- | ---: | ---: | ---: | ---: |
| Windows | 8.78 ms | 0.60 ms | 245.90 ms | 67.99 ms |
| Linux | 7.58 ms | 1.29 ms | 530.85 ms | 88.63 ms |

The previous-expression comparison executes the former COALESCE(capture date, modification date) ordering with version-matched metadata, without its extra count or annotation/identity joins. It is a conservative comparison of sorting cost, not an end-to-end old-application benchmark. New filtered views still compute counts and month breakdowns; month-to-month navigation reuses its breakdown. Scanning and metadata writes can change timings.

[Windows raw measurement](performance/windows-200000.json) · [Linux raw measurement](performance/linux-200000.json)

## Improvements

Capture-date ordering uses a transactional index maintained when media or metadata changes. Stale capture metadata falls back to modification time. Upgrade builds the index locally once.

Query and pipeline optimizations in this pass:
- Dynamic SQL join & predicate pruning (`BuildFilteredFrom` in `LibraryQueries.cs`): omits unreferenced `LEFT JOIN`s (`annotations`, `media_ids`, `metadata`, `media`), omits `AllowedMediaSql` when `exclusions` is empty (`HasExclusionsAsync`), drops `$param='' OR ...` clauses for inactive filters, and switches required `metadata` predicates to inner `JOIN metadata` so partial indexes such as `ix_metadata_coords` activate cleanly.
- Bounded covering-index month aggregation (`TryFastMonthBucketsAsync`): counts up to 120 local calendar months directly against `ix_browse_dates_desc` when unfiltered on capture timeline, avoiding 500k per-row `strftime(..., 'localtime')` conversions while falling back seamlessly for larger month spans, DST edge cases, or filtered views.
- Index-range position locate (`BrowsePosition.LocateAsync`): replaces 500k `ROW_NUMBER() OVER(...)` window materialization with an anchor lookup plus split index-seek counts (`value > $aval` + equality tie-break).
- SQLite connection tuning (`OpenConfiguredConnectionAsync`): sets per-connection `PRAGMA cache_size=-32768`, `PRAGMA temp_store=MEMORY`, and `PRAGMA busy_timeout=5000` without holding OS memory-mapped file handles across pool cleanup.
- Thumbnail cache & offline prep (`ThumbnailCache.cs`, `OfflineBrowsing.cs`, `MediaCache.cs`): zero-heap-allocation UTF-8 key hashing (`ArrayPool` + `stackalloc`), monotonic in-memory LRU ordering, single-open `FileStream` reads with 15-minute NTFS MFT timestamp write throttling, and skipping redundant PNG byte reads during warm `PrepareAsync`.
- Indexing, duplicates, tags, and GPS I/O (`ResumableIndexing.cs`, `DuplicateDetection.cs`, `TagManagement.cs`, `MinimalGpsExtractor.cs`): single `DirectoryInfo` stat per folder, extension filtering before relative-path normalization, prepared `scan_folders` & tag SQL statements, single-CTE duplicate candidate lookup with bounded parallel hashing, and skipping container stream opens on `.bmp`/`.gif`/non-ISOBMFF video formats unless `.xmp` sidecars apply.
- UI responsiveness & shutdown safety (`MainWindow.*.cs`): zero-allocation scroll range updates in `UpdateVisibleTimeRange` with change deduplication, synchronized `cacheTasks` access, and idempotent `OnClosed` guard preventing duplicate disposal when both `Closed` and `AppWindow.Closing` fire.

## Limits and bench evaluation

Initial discovery still walks a new drive. Windows metadata extraction and preview codecs still open originals; HDD latency and file types can keep initial imports lengthy. No measured physical scan-time reduction is claimed. Known-drive reconnect remains catalog-first without an automatic scan.

Search, filtered counts and month breakdowns can visit many catalog records. Lightweight loaded records accumulate during unrestricted scrolling; Select all matching results deliberately loads the full match set. Use month browsing and smaller selections for a 200,000-item collection. Full-window data virtualization is not claimed.

Compare first usable gallery, warm month switching, scrolling, discovery and date-reading durations separately on the real bench. Record HDD/USB connection, file types, count and cache state. Exercise pause/restart, offline browsing and clipboard copies into a disposable folder. Database results do not certify USB throughput, 200,000-image decoding, high-DPI layouts or frame rate.

## Exclusions and packaging update

Reversible folder, source and global extension rules now filter browsing and future scans. Use Settings or Refresh → Manage exclusions to restore them; Reset does not remove rules. Originals and saved organization are retained. Changing rules invalidates affected paused traversal checkpoints, not the published catalog. See [usage and limits](EXCLUSIONS.md) and [executable branding](BRANDING.md).
