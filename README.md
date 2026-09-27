# Prospero FPKG GUI Builder for macOS 🍎🎮

A high-performance, modern native GUI application for building PlayStation 5 Fake Packages (**FPKG**) on macOS. Built with .NET and [Photino.NET](https://tryphotino.io/) for a lightweight, native WebKit-powered interface.

---

## 🌟 Key Features & Patches

- **Full 64-bit Inode Addressing (Large Game Fix)**:
  - Fixes the critical bug in inner PFS metadata serialization where offsets were truncated to 32 bits.
  - Guarantees seamless kernel mounting for games of any size (> 4 GiB, 20 GiB, 50 GiB, 100+ GiB) on PS5 firmwares (tested up to FW 11.20).
- **Automatic PS5 Native FSELF Conversion**:
  - Automatically detects legacy PS4 Orbis FSELFs (`0x1D3D154F` / `4F 15 3D 1D`), decompresses them back to pure ELFs, and re-signs them into native PS5 FSELFs (`0xEEF51454` / `54 14 F5 EE`).
  - Ensures all binaries (`eboot.bin`, `prx/*.prx`, `sce_module/*.prx`, `about/right.sprx`) conform strictly to native PS5 format.
- **Pre-Flight Extent Validation**:
  - Every build runs runtime checked 64-bit validation across all serialized inodes. Prevents creating corrupted `.pkg` files if any file extent exceeds the image boundary.
- **OnionHEN & kstuff Compatibility**:
  - Packages with `PlaintextNoAuth` mode mount out of the box with OnionHEN / kstuff.
- **Modern Dark UI**:
  - Drag-and-drop support, real-time packaging progress, compression ratios, and log view.

---

## 📋 Prerequisites

- **macOS**: macOS 11.0 (Big Sur) or higher (Apple Silicon `arm64` or Intel `x64`).
- **.NET SDK**: .NET 9.0 or 10.0.
  ```bash
  brew install dotnet-sdk
  ```

---

## 🚀 Quick Start

### 1. Build and Launch the macOS App Bundle

Simply run the launch script:
```bash
git clone https://github.com/187rider/MacOS-Prospero-FPKG-GUI-Builder.git
cd MacOS-Prospero-FPKG-GUI-Builder
chmod +x *.sh
./launch-gui.sh
```
*Note: If the application bundle has not been built yet, `./launch-gui.sh` will automatically run `./build-app.sh` first to compile and assemble `LibProsperoPkg.app`.*

### 2. Run Directly from Terminal

You can also run the GUI directly using the .NET CLI:
```bash
dotnet run --project gui/gui.csproj
```

---

## 🛠️ How to Build a PS5 FPKG

1. **Launch the Application**: Open `LibProsperoPkg.app` or run `./launch-gui.sh`.
2. **Select Game Folder**: Choose the root folder containing the dumped game files (must contain `eboot.bin` and the `sce_sys/` directory with `param.json`).
3. **Select Output Directory**: Choose where the finalized `.pkg` will be saved.
4. **Configure Options**:
   - **Outer Image Mode**: Select `PLAINTEXT_NOAUTH` (recommended for PS5 HEN / kstuff).
   - **Compression**: Kraken compression is enabled by default.
5. **Build**: Click **"Build FPKG"**. The real-time log will stream NAPS compression, outer PFS signing, and container assembly.

---

## 🧪 Testing & Validation

A comprehensive regression test suite is included in `tests/LibProsperoPkg.Tests`:
```bash
dotnet test tests/LibProsperoPkg.Tests/LibProsperoPkg.Tests.csproj
```
Verifies:
- 64-bit data offset preservation across all critical boundaries (`0xFFFFFFFF`, `0x100000000`, `0x4EC400000`, `0xFFFFFFFFF`).
- Proper zeroing of reserved tail fields without displacing inode layout.
- Round-trip validation and boundary overflow rejection.

---

## 📜 Credits & License

- Built on top of `LibProsperoPkg` and `Photino.NET`.
- Developed for the PlayStation 5 homebrew community.
