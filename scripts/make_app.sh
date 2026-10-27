#!/bin/bash
# Builds the release binary and assembles CombineColors.app in ./build.
set -euo pipefail
cd "$(dirname "$0")/.."
swift build -c release --product CombineColors
APP=build/CombineColors.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/release/CombineColors "$APP/Contents/MacOS/CombineColors"
cp Resources/Info.plist "$APP/Contents/Info.plist"
[ -f Resources/AppIcon.icns ] && cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
# SwiftPM resource bundle (if any)
for b in .build/release/*.bundle; do [ -d "$b" ] && cp -R "$b" "$APP/Contents/Resources/"; done
echo "APPL????" > "$APP/Contents/PkgInfo"
codesign --force --deep --sign - "$APP" 2>/dev/null || true
echo "Built $APP"
