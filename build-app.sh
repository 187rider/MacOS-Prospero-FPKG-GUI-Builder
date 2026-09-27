#!/bin/bash
set -e

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
cd "$DIR"

# Detect architecture (arm64 for Apple Silicon M1-M4, x64 for Intel)
ARCH="$(uname -m)"
if [ "$ARCH" = "arm64" ]; then
    RID="osx-arm64"
else
    RID="osx-x64"
fi

echo "Building LibProsperoPkg native self-contained bundle for $ARCH ($RID)..."
rm -rf gui/publish
dotnet publish gui/gui.csproj -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -o gui/publish

mkdir -p LibProsperoPkg.app/Contents/MacOS
mkdir -p LibProsperoPkg.app/Contents/Resources

rm -rf LibProsperoPkg.app/Contents/MacOS/*
cp -R gui/publish/* LibProsperoPkg.app/Contents/MacOS/

cat << 'EOF' > LibProsperoPkg.app/Contents/Info.plist
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>LibProsperoPkg.Gui</string>
    <key>CFBundleIdentifier</key>
    <string>com.prospero.pkg.gui</string>
    <key>CFBundleName</key>
    <string>LibProsperoPkg</string>
    <key>CFBundleDisplayName</key>
    <string>LibProsperoPkg</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>1.2.0</string>
    <key>CFBundleVersion</key>
    <string>1.2.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

chmod +x LibProsperoPkg.app/Contents/MacOS/LibProsperoPkg.Gui

echo "Build complete! LibProsperoPkg.app is a native self-contained $ARCH application."
