#!/bin/bash
set -e

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
cd "$DIR"

# Detect architecture (arm64 for Apple Silicon M1-M4, x64 for Intel)
ARCH="$(uname -m)"
if [ "$ARCH" = "arm64" ]; then
    RID="osx-arm64"
    PLAT="macOS-arm64"
else
    RID="osx-x64"
    PLAT="macOS-x64"
fi

echo "Building LibProsperoPkg native self-contained bundle for $ARCH ($RID)..."
rm -rf gui/publish
dotnet publish gui/gui.csproj -c Release -r "$RID" --self-contained -p:PublishReadyToRun=true -o gui/publish

mkdir -p LibProsperoPkg.app/Contents/MacOS
mkdir -p LibProsperoPkg.app/Contents/Resources

rm -rf LibProsperoPkg.app/Contents/MacOS/*
cp -R gui/publish/* LibProsperoPkg.app/Contents/MacOS/

if [ -f "gui/Resources/AppIcon.icns" ]; then
    cp gui/Resources/AppIcon.icns LibProsperoPkg.app/Contents/Resources/AppIcon.icns
fi

if [ -d "gui/Resources/fakelib" ]; then
    mkdir -p LibProsperoPkg.app/Contents/Resources/fakelib
    cp -R gui/Resources/fakelib/* LibProsperoPkg.app/Contents/Resources/fakelib/
fi

if [ -d "gui/Resources/sounds" ]; then
    mkdir -p LibProsperoPkg.app/Contents/Resources/sounds
    cp -R gui/Resources/sounds/* LibProsperoPkg.app/Contents/Resources/sounds/
fi

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
    <string>2.0.0</string>
    <key>CFBundleVersion</key>
    <string>2.0.0</string>
    <key>CFBundleIconFile</key>
    <string>AppIcon</string>
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
</dict>
</plist>
EOF

chmod +x LibProsperoPkg.app/Contents/MacOS/LibProsperoPkg.Gui

echo "Cleaning extended attributes and ad-hoc codesigning LibProsperoPkg.app..."
xattr -cr LibProsperoPkg.app
codesign --force --deep --sign - LibProsperoPkg.app
codesign --verify --deep --strict --verbose=2 LibProsperoPkg.app

echo "Creating DMG installer..."
DMG_FILE="LibProsperoPkg-v2.0.0-$PLAT.dmg"
ZIP_FILE="LibProsperoPkg-v2.0.0-$PLAT.app.zip"

rm -rf dmg_staging "$DMG_FILE" "$ZIP_FILE"
mkdir -p dmg_staging
cp -R LibProsperoPkg.app dmg_staging/
ln -s /Applications dmg_staging/Applications
hdiutil create -volname "LibProsperoPkg" -srcfolder dmg_staging -ov -format UDZO "$DMG_FILE"
rm -rf dmg_staging

echo "Creating zip archive of .app bundle..."
zip -r -y "$ZIP_FILE" LibProsperoPkg.app

echo "Build complete! Output:"
echo "  - App Bundle: LibProsperoPkg.app"
echo "  - DMG Installer: $DMG_FILE"
echo "  - Zip Bundle: $ZIP_FILE"
