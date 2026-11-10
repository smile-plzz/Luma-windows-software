# Luma Threat Model and Security Review Draft

Role: SEC (Security and Privacy Reviewer)
Baseline: v0.6.0
Date: 2026-11-11
Audience: All swarm agents

---

## 1. System Overview and Trust Boundaries

Luma is an offline, local-first Windows media library.
- Trust boundary 1: Untrusted file systems and media files (external HDDs, USB sticks, user directories).
- Trust boundary 2: Local storage and configuration (`%LOCALAPPDATA%\Luma`).
- Trust boundary 3: Windows Shell and external processes (default media players, Explorer, Recycle Bin).
- Trust boundary 4: Network boundary (strictly zero network traffic allowed by design).

---

## 2. Threat Analysis

### Threat T-1: Malicious or Malformed Media Files
- Risk: Malformed JPEG, PNG, HEIC, RAW, or MP4 files causing memory corruption or denial of service in WinRT codecs.
- Impact: Process crash, memory exhaustion, or potential arbitrary code execution in codec runtime.
- Mitigations:
  - Validate file headers, reported dimensions, and file sizes before invoking decoders.
  - Implement dimension limits to reject image decompression bombs (e.g. max 100 megapixels before downscaling).
  - Wrap all decode operations in timeout and cancellation-aware routines.
  - Isolate decoder failures with graceful fallback to generic placeholders.

### Threat T-2: Path Traversal and Junction Loops
- Risk: Symbolic links, directory junctions, or cyclic directory trees causing infinite traversal or escaping intended source roots.
- Impact: Infinite scanning loops, disk exhaustion, indexing unauthorized system files.
- Mitigations:
  - Source registration rejects junction and symlink ancestry.
  - Scanner explicitly checks `FileAttributes.ReparsePoint` and skips directory symlinks/junctions.
  - Path normalization validates that relative paths cannot contain `..` or escape the source root.

### Threat T-3: Windows Reserved Names and Malformed Paths
- Risk: Paths containing Windows reserved device names (`CON`, `PRN`, `AUX`, `NUL`, `COM1-9`, `LPT1-9`) or trailing dots/spaces.
- Impact: Win32 API hangs, unhandled exceptions, or unreadable files.
- Mitigations:
  - Check file names against reserved device names prior to copy, move, or rename.
  - Use `\\?\` prefix or .NET 8 long-path support for path normalization.

### Threat T-4: Shell Verb and Argument Injection
- Risk: Maliciously named files (e.g. containing quotes, spaces, or switches) executed via Windows Shell verbs.
- Impact: Arbitrary command execution or command switch injection in default applications.
- Mitigations:
  - Never concatenate user strings into command-line strings.
  - Use `ProcessStartInfo.ArgumentList` or Win32 `ShellExecuteEx` passing the exact target path as a file argument without command shell evaluation.

### Threat T-5: Silent File Overwrites or Data Loss
- Risk: In-app copy, move, or duplicate-management overwriting originals.
- Impact: Permanent loss of original user media.
- Mitigations:
  - Strict rule: scanning is read-only.
  - File transfers stream to a private temporary sibling file, verify integrity, and publish by atomic rename.
  - Existing destination filenames are skipped, never overwritten silently.
  - Deletions always route through native Windows Recycle Bin.

### Threat T-6: Catalog Tampering or SQLite Injection
- Risk: Malicious filenames or metadata injecting SQL into SQLite catalog.
- Impact: Catalog corruption, loss of user tags or albums.
- Mitigations:
  - 100% parameterized SQL queries via `SqliteCommand.Parameters.AddWithValue()`.
  - WAL journal mode with bounded checkpointing.
  - Automatic pre-migration backup before applying schema changes.
  - `PRAGMA quick_check` at startup.

### Threat T-7: Privacy Leaks and Telemetry
- Risk: Personal filenames, directory paths, or usernames leaking through error reports or network calls.
- Impact: Violation of privacy promise.
- Mitigations:
  - Strict zero-telemetry policy.
  - Crash reports (`last-error.txt`) must scrub usernames (`C:\Users\<user>\` -> `C:\Users\[REDACTED]\`) and machine identifiers.
  - CI test verifying zero network sockets opened during app execution.

---

## 3. Immediate Action Items for Wave 1
1. Audit `ShellFiles.cs` for safe shell verb execution (SEC-2).
2. Audit `FileTransfer.cs` and `MainWindow.xaml.cs` for path traversal and reserved names (SEC-3).
3. Implement path redaction for crash logs (SEC-6).

