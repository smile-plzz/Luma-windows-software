# Luma Physical Hardware and Real-Life Acceptance Protocol

Role: QAE (QA and Test Automation)
Date: 2026-11-11
Audience: Project Owner and Hardware Testing Engineers
Target Release: v0.7.0

> [!WARNING]
> SAFETY WARNING: Always execute these tests with disposable sample media. Never run destructive or disconnect tests on your only copy of irreplaceable photos or family videos.

---

## 1. Physical Hardware Acceptance Checklist

The following tests require physical hardware (external USB drive, spinning HDD, SD card) and human interaction to certify real-world robustness.

| ID | Test Scenario | Hardware Setup | Expected Result | Result |
|---|---|---|---|---|
| **HW-01** | **USB Drive Unplug While Browsing** | External USB drive with 1,000+ photos plugged into PC. | Unplugging drive while scrolling does not crash app; cached thumbnails and metadata remain browsable; drive indicator turns offline (○); file operations show "Drive is offline". | Pending Owner Sign-off |
| **HW-02** | **Drive Letter Shift on Reconnect** | USB drive unplugged and remounted under a different letter via `diskmgmt.msc` (e.g. `E:\` -> `F:\`). | Luma resolves drive by volume GUID and serial number; drive indicator updates to online (●); originals open without re-indexing. | Pending Owner Sign-off |
| **HW-03** | **Disconnect During Active Scan** | USB drive unplugged mid-discovery (Step 1) or mid-date reading (Step 2). | Scan pauses safely; completed folder checkpoints remain saved in `%LOCALAPPDATA%\Luma`; no corrupted SQLite state; resume available upon reconnect. | Pending Owner Sign-off |
| **HW-04** | **Reformatted Drive Detection** | External drive reformatted with new filesystem/serial. | Luma detects volume serial mismatch; does not mistake new empty drive for existing catalog; prompts to register as new source. | Pending Owner Sign-off |
| **HW-05** | **HEIC Codec Handling** | Folder with iPhone HEIC photos on clean Windows machine. | With HEIF Image Extensions installed: full thumbnails render. Without codec: placeholder icon displays cleanly without crashing; actionable store link offered. | Pending Owner Sign-off |
| **HW-06** | **Corrupt & Truncated Media** | Fuzzed, zero-byte, and truncated JPEG/PNG/MP4 files in library. | Decoders catch corrupt streams gracefully; card displays accessible placeholder; scanning and thumbnail generation continue uninterrupted. | Automated Windows integration verified (`CorruptAndZeroByteFiles_DoNotThrowUnhandledExceptions`); real RAW/HEIC codecs pending owner check |
| **HW-07** | **Deep & Unicode Paths** | Folders with 260+ character paths, spaces, emoji, and non-ASCII characters. | Scanner enumerates and catalogs all items; thumbnails generate; default app opens successfully. | Automated Windows integration verified (`BanglaUnicodeAndExtendedLongPath_ScanMetadataAndThumbnailWorkSafely`) |
| **HW-08** | **Process Kill During Thumbnail Write** | Terminate `Luma.App.exe` via Task Manager during high-volume offline preparation. | No corrupt files left in thumbnail cache; on relaunch, SQLite WAL recovers cleanly; LRU cache prunes broken entries automatically. | Pending Owner Sign-off |
| **HW-09** | **Low Disk Space (< 500 MB)** | System drive filled to < 500 MB free space. | Proactive DiskGuard prevents disk-filling thumbnail writes; displays informative banner; existing catalog remains intact and read-only. | Pending Owner Sign-off |
| **HW-10** | **Concurrent Launch** | Attempt to start multiple instances of `Luma.App.exe` simultaneously. | SQLite WAL locks coordinate safely; secondary instance either activates primary window or exits cleanly without database corruption. | Pending Owner Sign-off |
| **HW-11** | **Large Real-World Library (100k+)** | Physical external HDD with 100,000+ real media files. | Cold startup under 2.0s; first usable gallery renders within 1.0s; smooth scrolling with bounded memory consumption (below 350 MB). | Pending Owner Sign-off |
| **HW-12** | **Native Shell File Operations** | Disposable media files on external volume. | F2 Rename updates disk and catalog; Delete sends to Windows Recycle Bin with native confirmation; Copy/Paste transfers safely via atomic sibling files. | Pending Owner Sign-off |

---

## 2. Test Execution Sign-off Table

| Date | Tester Name | Hardware Tested (Model, Connection) | Tests Passed | Tests Failed | Notes |
|---|---|---|---|---|---|
| 2026-11-11 | Automated Baseline | Synthetic 200k catalog on Windows 11 runner | Synthetic pass (103 unit/integration tests) | None | Physical hardware suite pending human owner verification |

