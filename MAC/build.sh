#!/bin/bash
# Builds Poltergeist.app into ./build
set -euo pipefail
cd "$(dirname "$0")"

swift build -c release
BIN="$(swift build -c release --show-bin-path)/Poltergeist"

APP="build/Poltergeist.app"
rm -rf build/Polt.app build/Poltergeist.app
mkdir -p "$APP/Contents/MacOS"
cp "$BIN" "$APP/Contents/MacOS/Poltergeist"
cp Info.plist "$APP/Contents/Info.plist"
mkdir -p "$APP/Contents/Resources"
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
# Prefer the stable local identity (see make-signing-cert.sh) so macOS keeps granted permissions across rebuilds.
if security find-certificate -c "Poltergeist Local" >/dev/null 2>&1; then
    codesign --force --sign "Poltergeist Local" "$APP"
else
    echo "Note: no stable signing identity; run ./make-signing-cert.sh once to stop repeated permission prompts."
    codesign --force --sign - "$APP"
fi
# Make the Finder "Upload with Poltergeist" service show up without a logout.
/System/Library/CoreServices/pbs -update >/dev/null 2>&1 || true
echo "Built $APP"
