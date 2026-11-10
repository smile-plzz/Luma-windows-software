# Large-library performance

## v0.7.0 scale suite (PRF-1)

Measurements gathered on Windows 11 with 8 logical cores using `Luma.Benchmarks`. Exercises synthetic catalog query and timeline aggregation scaling from 1,000 to 500,000 records.

| Records | First 120 + Count | Next Page (Cursor) | Month Breakdown | Locate Middle Anchor | Memory Delta |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 | 24.06 ms | 5.17 ms | 12.05 ms | 9.23 ms | 0.11 MB |
| 10,000 | 13.04 ms | 3.63 ms | 63.53 ms | 33.66 ms | 0.11 MB |
| 100,000 | 71.68 ms | 2.66 ms | 1,127.20 ms | 571.16 ms | 0.11 MB |
| 200,000 | 85.10 ms | 2.63 ms | 874.82 ms | 542.05 ms | 0.11 MB |
| 500,000 | 203.89 ms | 1.82 ms | 2,376.98 ms | 1,346.09 ms | 0.11 MB |

[v0.7.0 raw measurements](performance/windows-v0.7.0-benchmarks.json). Single-run synthetic database timings; not physical HDD scan, image decoding, or UI frame rate. Pagination with cursors remains bounded below 3 ms across all library sizes up to 500,000 records. First paint path in v0.7.0 defers month timeline calculation to avoid blocking first 120 card render.

## v0.6.0 saved-position check

[Run 38355699422](https://github.com/smile-plzz/Luma/actions/runs/38355699422), source `f655715982253824c41329425c181c8876d9f3d5`, correctly located a middle anchor in 200,000 records in **574.92 ms** on the Windows runner. First 120 records: 35.09 ms; continuation: 0.70 ms; month breakdown: 461.89 ms. These are single-run catalog-only timings, not physical HDD or full UI measurements. Reopening loads at most an initial 120-record window around the anchor, with earlier items available explicitly. [Raw result](performance/windows-v0.6.0-200000.json).

## v0.5.0 validation

[Run 38350255657](https://github.com/smile-plzz/Luma/actions/runs/38350255657), source `b7d9eb16cc3547c64928e8fbdc202b7e0a400083`, exercises 200,000 catalog records with the exclusion-aware query implementation and no active rules.

| Environment | First 120 records and count | Next page | Month breakdown |
| --- | ---: | ---: | ---: |
| Windows | 53.06 ms | 1.67 ms | 506.31 ms |
| Linux | 42.01 ms | 1.51 ms | 967.90 ms |

[Windows raw result](performance/windows-v0.5.0-200000.json) · [Linux raw result](performance/linux-v0.5.0-200000.json). Single-run synthetic database timings; not a controlled before/after comparison, UI frame rate or physical-drive throughput. Exclusion predicates add catalog work; real timings depend on rules and filtering. The retained records make exclusion restoration immediate without rediscovery.

## v0.4.0 reference measurements

Measured in [run 38307725690](https://github.com/smile-plzz/Luma/actions/runs/38307725690), source `a1c03b8323e5b80644b090bf69da88ffec92ae64`. Fixture: 200,000 synthetic SQLite records, four runner processors, 120 items per page. These are single-run measurements, not percentiles or a physical-drive benchmark.

| Environment | First page including count | Next page, count reused | Month breakdown | Previous capture-sort expression only |
| --- | ---: | ---: | ---: | ---: |
| Windows | 8.78 ms | 0.60 ms | 245.90 ms | 67.99 ms |
| Linux | 7.58 ms | 1.29 ms | 530.85 ms | 88.63 ms |

The previous-expression comparison executes the former COALESCE(capture date, modification date) ordering with version-matched metadata, without its extra count or annotation/identity joins. It is a conservative comparison of sorting cost, not an end-to-end old-application benchmark. New filtered views still compute counts and month breakdowns; month-to-month navigation reuses its breakdown. Scanning and metadata writes can change timings.

[Windows raw measurement](performance/windows-200000.json) · [Linux raw measurement](performance/linux-200000.json)

## Improvements

Capture-date ordering uses a transactional index maintained when media or metadata changes. Stale capture metadata falls back to modification time. Upgrade builds the index locally once. Simple counts avoid unnecessary joins; subsequent pages reuse the view total.

Metadata enrichment reads only missing/stale versions in batches of 100. Readers close before native decoding; completed batches share a write transaction. Unchanged versions do not open originals. Scanning uses prepared statements, directory-enumeration attributes, a pending-folder index and a running checkpoint count. Recycled tiles cancel queued preview requests; decoded images remain tied to realized tiles.

## Limits and bench evaluation

Initial discovery still walks a new drive. Windows metadata extraction and preview codecs still open originals; HDD latency and file types can keep initial imports lengthy. No measured physical scan-time reduction is claimed. Known-drive reconnect remains catalog-first without an automatic scan.

Search, filtered counts and month breakdowns can visit many catalog records. Lightweight loaded records accumulate during unrestricted scrolling; Select all matching results deliberately loads the full match set. Use month browsing and smaller selections for a 200,000-item collection. Full-window data virtualization is not claimed.

Compare first usable gallery, warm month switching, scrolling, discovery and date-reading durations separately on the real bench. Record HDD/USB connection, file types, count and cache state. Exercise pause/restart, offline browsing and clipboard copies into a disposable folder. Database results do not certify USB throughput, 200,000-image decoding, high-DPI layouts or frame rate.

## Exclusions and packaging update

Reversible folder, source and global extension rules now filter browsing and future scans. Use Settings or Refresh → Manage exclusions to restore them; Reset does not remove rules. Originals and saved organization are retained. Changing rules invalidates affected paused traversal checkpoints, not the published catalog. See [usage and limits](EXCLUSIONS.md) and [executable branding](BRANDING.md).
