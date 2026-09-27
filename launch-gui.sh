#!/bin/bash
DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" >/dev/null 2>&1 && pwd )"

if [ ! -d "$DIR/LibProsperoPkg.app" ] || [ ! -f "$DIR/LibProsperoPkg.app/Contents/MacOS/LibProsperoPkg.Gui.dll" ]; then
    echo "App bundle not found. Building first..."
    "$DIR/build-app.sh"
fi

open "$DIR/LibProsperoPkg.app"
