# Luma Documentation and Code Drift Report

Baseline commit: `f796035ec642253553acab84d67f51bee95d8f18` (v0.6.0 on main)
Date: 2026-11-11
Audience: Swarm agents (ORC, RCN, STB, PRF, IDX, MED, UXD, QAE, DOC)

---

## 1. Repository Identity and Links (RCN-2)

### Finding
README.md, release notes, and documentation consistently link to:
`https://github.com/smile-plzz/Luma`
However, the active repository name is:
`https://github.com/smile-plzz/Luma-windows-software`

### Verification
- Both `origin` (`Luma.git`) and `upstream` (`Luma-windows-software.git`) point to the exact same git commit `f796035ec642253553acab84d67f51bee95d8f18`.
- Querying GitHub CLI confirmed `smile-plzz/Luma` is an alias/redirect to `smile-plzz/Luma-windows-software` resulting from a repository rename.
- GitHub Actions runs and releases exist on both paths due to the GitHub redirect.

### Required Action
Task `DOC-1` and `PKG-1` must update all canonical links in README.md, documentation, CI workflows, and release manifests to `smile-plzz/Luma-windows-software`.

---

## 2. Test Suite Counts and Baseline Verification (RCN-3 & QAE-1)

### Documented Claims
- Docs state: "All 82 core tests passed on each of Windows and Linux, four Windows integration tests passed, and the packaged native UI checks passed." (docs/QA.md, releases/v0.6.0.md).

### Code Verification
- `tests/Luma.Tests`: Ran `dotnet test tests/Luma.Tests/Luma.Tests.csproj -c Release`.
  - Result: 82 passed, 0 failed, 0 skipped. Matches exactly.
- `tests/Luma.Windows.Tests`: Ran `dotnet test tests/Luma.Windows.Tests/Luma.Windows.Tests.csproj -c Release`.
  - Result: 4 passed, 0 failed, 0 skipped. Matches exactly.
- Total automated unit/integration count: 86 tests.

---

## 3. UI Automation Headless Execution Drift (RCN-3 & QAE-1)

### Finding
In `scripts/Smoke-Test.ps1`, screen capture is performed using `System.Drawing.Graphics.CopyFromScreen`:
```powershell
$graphics.CopyFromScreen([int]$rectangle.X, [int]$rectangle.Y, 0, 0, $bitmap.Size)
```
When executed in non-interactive, virtualized, or background sessions (e.g. background tasks or headless CI without a virtual display), `CopyFromScreen` throws:
`Win32Exception: The handle is invalid`

### Required Action
In Wave 1, QAE must harden `Smoke-Test.ps1` with screen capture error suppression (wrapping `CopyFromScreen` in a try/catch block and filling a fallback solid color when no GDI display handle exists) as already prototyped on `feat/v0.7-tooltips-and-shortcuts`.

---

## 4. Keyboard Shortcuts Drift (RCN-3 & UXD-3)

### Current Implemented Accelerators (`MainWindow.xaml`):
- `Ctrl+C`: Copy
- `Ctrl+X`: Cut
- `Ctrl+V`: Paste
- `Ctrl+A`: Select loaded results
- `Delete`: Delete to Recycle Bin
- `F2`: Rename
- `Ctrl+Wheel`: Zoom/resize thumbnails
- `Double-click`: Open in default app

### Documented and Planned Keyboard Gaps:
- `F1`: Shortcut cheat sheet dialog (implemented in PR 2 on feature branch, pending merge into Wave 2).
- Arrow keys, Home, End, PageUp, PageDown: Not currently mapped explicitly in gallery repeater.
- `Enter`: Open selected item.
- `Space`: Quick preview overlay (task VWR-1).
- `Ctrl+F`: Focus search input.
- `Ctrl+Shift+F`: Toggle filters.
- `F5`: Refresh current source.
- `Esc`: Clear selection or dismiss overlay.
- `Ctrl+Z`: Undo organization actions.

---

## 5. Performance Baselines (RCN-5)

Documented in `docs/PERFORMANCE.md` for 200,000 synthetic catalog records:
- Windows runner:
  - Anchor lookup: 574.92 ms
  - First 120 records and count: 35.09 ms (v0.6.0) / 53.06 ms (v0.5.0) / 8.78 ms (v0.4.0)
  - Next page continuation: 0.70 ms (v0.6.0) / 1.67 ms (v0.5.0) / 0.60 ms (v0.4.0)
  - Month breakdown: 461.89 ms (v0.6.0) / 506.31 ms (v0.5.0) / 245.90 ms (v0.4.0)
- Linux runner:
  - First 120 records: 42.01 ms (v0.5.0) / 7.58 ms (v0.4.0)
  - Next page continuation: 1.51 ms (v0.5.0) / 1.29 ms (v0.4.0)
  - Month breakdown: 967.90 ms (v0.5.0) / 530.85 ms (v0.4.0)
- Peak decoded bitmaps during native smoke: 58 decoded images for 260 items.

These serve as the baseline that `PRF` must track and optimize in Wave 2.
