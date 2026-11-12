# Luma Packaging, Signing, and Installer Strategy

Role: PKG (Packaging and Release Engineer)
Date: 2026-11-11
Target Release: v0.7.0

---

## 1. Distribution Model Analysis

Luma provides two official distribution channels for Windows users:

### 1.1. Portable Unsigned ZIP (Primary Milestone Channel)
- **Target:** Users who prefer zero installation, portable USB drive workflows, or evaluation without modifying system shortcuts.
- **Payload:** Self-contained `win-x64` output with bundled .NET 8 runtime and Windows App SDK DLLs.
- **Behavior:** Unpack anywhere writable and double-click `Luma.App.exe`. User data is stored in `%LOCALAPPDATA%\Luma`.
- **Limitation:** Windows SmartScreen displays an initial "Unknown Publisher" warning until global reputation is accumulated.

### 1.2. Per-User Lightweight Installer (New in v0.7.0)
- **Target:** Everyday users who want a native Start Menu shortcut, optional desktop icon, and clean Add/Remove Programs integration.
- **Technology Choice:** Inno Setup (Script: `scripts/Luma-Installer.iss`).
  - **Why Inno Setup:**
    1. **Zero Admin Privileges Required:** Installs cleanly into `%LOCALAPPDATA%\Programs\Luma`. Does not prompt for UAC elevation.
    2. **Compact & Fast:** Produces a single compressed setup EXE (~45-55 MB).
    3. **Atomic Upgrades & Clean Uninstall:** Safely updates binaries in place without touching user catalogs or settings in `%LOCALAPPDATA%\Luma`.
    4. **Completely Scriptable:** Can be compiled in CI or locally via `iscc scripts/Luma-Installer.iss`.

---

## 2. Code Signing and SmartScreen Reputation

### 2.1. Signing Options
1. **Azure Trusted Signing (Recommended for Production):**
   - Microsoft's cloud-based code signing service.
   - Cost: ~$9.99/month for identity validation.
   - Benefit: Immediate trust integration with Windows SmartScreen, fast reputation ramp, no hardware HSM token required.
2. **Standard Organization Validation (OV) Certificate:**
   - Traditional certificate provided by DigiCert, Sectigo, or Certum.
   - Cost: ~$200 - $400 / year + physical USB token.
   - Requires manual signing step during release packaging.
3. **Self-Signed Certificate (Development & Internal Testing):**
   - Cost: Free.
   - Requires manual installation into `Cert:\LocalMachine\Root` on the target test machine to suppress warnings.

### 2.2. SmartScreen Policy
- Unsigned binaries show "Windows protected your PC". Users must click "More info" -> "Run anyway".
- The README and user guide clearly document this behavior so users understand it is an expected artifact of an unsigned open-source release.

---

## 3. In-App Update Strategy

In accordance with Section 5 (Agent 7):
- Luma does not perform silent or forced automatic background updates in Phase 4.
- An explicit, user-initiated "Check for updates" button is provided in Settings.
- When clicked, Luma opens the official verified GitHub Releases page in the user's default browser.

