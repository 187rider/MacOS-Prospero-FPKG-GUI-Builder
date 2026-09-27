# Release Notes — Prospero FPKG GUI Builder for macOS 🍎🎮

---

## [v1.2.0] — 2026-09-28

### 🚀 Native Apple Silicon (M-Series) Self-Contained Runtime
- **Native Mach-O Binary (`arm64`)**: The application bundle (`LibProsperoPkg.app`) now contains a native Mach-O 64-bit executable compiled specifically for Apple Silicon (M1, M2, M3, M4, Pro, Max, and Ultra).
- **ReadyToRun (R2R) AOT Compilation**: All core libraries, PFS filesystem encoders, Kraken decompression routines, and cryptographic engines are pre-compiled into native ARM64 machine instructions. Cold startup time is instantaneous, and CPU efficiency is maximized with hardware SIMD/NEON acceleration.
- **Zero External Dependencies**: End-users do not need .NET SDK, .NET Runtime, or Homebrew installed. The app is completely self-contained—simply double-click `LibProsperoPkg.app` to run.
- **Automated Native Build Pipeline**: Updated `build-app.sh` to automatically detect host architecture (`arm64` vs `x64`) and assemble the self-contained bundle.

### 🌐 Complete English Localization (Unpack & Verify Tab)
- **UI Markup & Labels**: Fully translated Tab 2 ("Unpack & Verify") into English, including input fields, action buttons (*Quick Verify*, *Unpack*, *Browse*), and the detailed package information table (*Title*, *Content ID*, *Game Version*, *SDK Version*, *System Requirements*, *Languages*, *PlayGo Languages*, *DRM Type*, *Container*, *Content Type*, *Package Segments*, *PKG Size*).
- **Frontend & Backend Logging**: All progress status messages, alert prompts, error messages, and console logs in `app.js` and `AppLogic.cs` are now in standard English.
- **Standardized Number Formatting**: Formatted byte sizes using international units (`KiB`, `MiB`, `GiB`, `TiB`) and comma digit separators.

### 🛠️ UI & Workflow Fixes
- **Cancel Button State Reset**: Fixed an issue where the Cancel button remained stuck or reappeared after a build completed. Implemented `resetBuildButtons()` to ensure button states, icons, and visibility are cleanly restored upon build completion, cancellation, or failure.
- **Progress Tracking Integrity**: Prevented progress update completion events from reviving the Cancel button after the packaging task has ended.

---

## [v1.1.0] — 2026-09-27

### 🎮 Critical Fix: 64-Bit Inode Addressing (Large Game Support)
- **Large Game Kernel Mount Fix**: Resolved a critical defect in inner PFS metadata serialization (`ProsperoPs5InnerMetadata.cs`) where 64-bit `DataOffset` values were truncated to 32 bits due to an erroneous write at offset 100.
- **Full File Extent Support**: Guarantees valid kernel mounting on PS5 firmwares (tested up to FW 11.20) for games of any size (> 4 GiB, 20 GiB, 50 GiB, 100+ GiB).
- **Pre-Flight Extent Validation**: Added checked 64-bit bounds validation verifying `DataOffset + DataSize <= innerPfsImageLength` across all generated inodes before packaging finalizes.

### ⚡ Automatic PS5 Native FSELF Conversion
- **Orbis to Prospero Translation**: Detects legacy PS4/Orbis FSELFs (`0x1D3D154F` / `4F 15 3D 1D`), decompresses them back to pure ELFs, and re-signs them into native PS5 FSELFs (`0xEEF51454` / `54 14 F5 EE`).
- **Section Table Sanitization**: Automatically detects and repairs truncated ELF section header tables from dumping tools.

### 🛡️ Smart Metadata & License Quarantine
- **Firmware Auto-Backporting**: Automatically patches `requiredSystemSoftwareVersion` in `param.json` to FW 1.00 (`0x0100000000000000`) to eliminate `CE-100005-6` and `CE-107880-4` installation errors.
- **Safe License Handling (`SceSysQuarantine`)**: Temporarily stages aside conflicting retail license files (`license.dat`, `license.info`) and stale PlayGo tables during packaging so clean debug RIF licenses are generated, restoring all original files untouched upon completion.

---

## [v1.0.0] — 2026-09-26

### 🎉 Initial Release
- Modern macOS dark-theme GUI powered by Photino.NET and WebKit (`WKWebView`).
- Support for building PS5 Application (`PS5GD`), Patch (`PS5GP`), and Additional Content (`PS5AC`) packages.
- Real-time build console with timestamped logging and progress tracking.
- Package Inspection tab with header parsing, segment table inspection, and entry listing.
