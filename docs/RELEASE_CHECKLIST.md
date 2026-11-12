# Luma Release Checklist

Mandatory quality gate before cutting any release candidate (`vX.Y.Z-rcN`) or final release (`vX.Y.Z`). Never ship without recorded evidence for each box below.

## 1. Automated Verification (after final code edit)
- [x] Zero em-dashes (`\u2014`) in code comments, UI strings, workflows, or release notes (`git ls-files` verified 0 hits).
- [x] Core tests pass (`dotnet test tests/Luma.Tests/Luma.Tests.csproj -c Release`): **129 passed, 0 failed, 0 skipped**.
- [x] Windows integration tests pass (`dotnet test tests/Luma.Windows.Tests/Luma.Windows.Tests.csproj -c Release`): **8 passed, 0 failed, 0 skipped**.
- [x] Large-library benchmarks updated (`dotnet run --project tests/Luma.Benchmarks/Luma.Benchmarks.csproj -c Release`) and `docs/PERFORMANCE.md` matches measured numbers (`100k`: 153.91 ms first page + count; `500k`: 562.23 ms first page + count, 18.84 ms next page).
- [x] Clean self-contained Release publish (`dotnet publish src/Luma.App/Luma.App.csproj -c Release -r win-x64 -p:Platform=x64 -o artifacts/Luma-win-x64`) contains `Luma.App.exe`, `Assets/Luma.ico`, and `MainWindow.xbf`.
- [x] Native desktop UIA smoke test (`scripts/Smoke-Test.ps1`) passed against published `Luma.App.exe` (`1147.0s`, exit code 0; local run used unattended lock-screen fallback for OS Clipboard/context menu while CI enforces strict checks).
- [ ] GitHub Actions `build.yml` workflow green on the exact commit SHA to be released (ready to verify upon push).

## 2. Findings & Exit Gate
- [x] Zero open `S1` or `S2` findings in `docs/assessment/FINDINGS.md` (`F-001`..`F-031` resolved and verified).
- [x] All `S3` items resolved or consciously deferred in `docs/DECISIONS.md` (with explicit `[ASK]` items flagged for owner confirmation).
- [x] Upgrade test from previous released catalog schema (`v0.6.0` -> current) verified green (`StabilizationTests` + `UpgradeTests`).
- [x] Crash/kill/corruption recovery and Bangla/Unicode/long-path (>260 char) torture tests verified green (`StabilizationTests` + `Luma.Windows.Tests`).

## 3. Manual Physical-Hardware Signoff (`docs/QA_ACCEPTANCE.md` / `TESTING_HANDOFF.md`)
- [ ] Owner ran 10-minute verification script on a physical USB external drive (unplug -> offline browse -> replug under a changed drive letter -> in-place reconnect).
- [ ] Keyboard-only traversal, Windows Narrator spot-check, Light/Dark contrast, and 125% to 200% DPI scaling reviewed.

## 4. Packaging, Provenance, and Release Copy
- [x] Version metadata (`FileVersion` 0.7.0.0, `ProductVersion`, `Luma - Local media gallery`) matches cleanly (`artifacts/qa/executable-metadata.txt`).
- [x] Release notes (`releases/v0.7.0-rc1.md`, `releases/v0.7.0.md`) written in plain user language, stating unsigned SmartScreen warning and RC1 `SHA256SUMS.txt` digest.
- [x] `CHANGELOG.md`, `README.md`, `dev-log.md`, and `context.md` updated and consistent.
- [x] Provenance workflow `.github/workflows/release.yml` verifies exact commit SHA, CI run ID, artifact ID, and SHA-256 checksum before publishing.
- [ ] Pre-release legal/trust `[ASK]` approvals (`LICENSE`, signing, installer, telemetry, final public tag) confirmed with owner.

