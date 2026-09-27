#!/bin/bash
set -e

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"
cd "$DIR"

echo "Building LibProsperoPkg macOS GUI..."
dotnet publish gui/gui.csproj -c Release -o gui/publish

mkdir -p LibProsperoPkg.app/Contents/MacOS
mkdir -p LibProsperoPkg.app/Contents/Resources

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

cat << 'EOF' > LibProsperoPkg.app/Contents/MacOS/LibProsperoPkg.Gui
#!/bin/bash
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Detect Homebrew dotnet or system dotnet
if [ -z "$DOTNET_ROOT" ]; then
    if [ -d "/opt/homebrew/Cellar/dotnet" ]; then
        LATEST_DOTNET=$(ls -d /opt/homebrew/Cellar/dotnet/*/libexec 2>/dev/null | tail -n 1)
        if [ -n "$LATEST_DOTNET" ]; then
            export DOTNET_ROOT="$LATEST_DOTNET"
        fi
    elif [ -d "/usr/local/share/dotnet" ]; then
        export DOTNET_ROOT="/usr/local/share/dotnet"
    fi
fi

export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"
exec dotnet "$DIR/LibProsperoPkg.Gui.dll" "$@"
EOF

chmod +x LibProsperoPkg.app/Contents/MacOS/LibProsperoPkg.Gui

echo "Build complete! LibProsperoPkg.app is ready."
