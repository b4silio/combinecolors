#!/bin/bash
# Builds the release binary and assembles CombineColors.app in ./build.
#   scripts/make_app.sh              native (arm64) build, quick
#   UNIVERSAL=1 scripts/make_app.sh  arm64 + x86_64 slices, for sharing with Intel Macs too
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${UNIVERSAL:-0}" = "1" ]; then
    swift build -c release --arch arm64 --arch x86_64 --product CombineColors
    BIN=.build/apple/Products/Release
else
    swift build -c release --product CombineColors
    BIN=.build/release
fi
APP=build/CombineColors.app
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN/CombineColors" "$APP/Contents/MacOS/CombineColors"
cp Resources/Info.plist "$APP/Contents/Info.plist"
# Build number = number of commits (CFBundleShortVersionString stays in Resources/Info.plist).
if BUILD=$(git rev-list --count HEAD 2>/dev/null); then
    /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $BUILD" "$APP/Contents/Info.plist" || true
fi
[ -f Resources/AppIcon.icns ] && cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"
# SwiftPM resource bundle (if any)
for b in "$BIN"/*.bundle; do [ -d "$b" ] && cp -R "$b" "$APP/Contents/Resources/"; done
echo "APPL????" > "$APP/Contents/PkgInfo"
codesign --force --deep --sign - "$APP" 2>/dev/null || true
echo "Built $APP ($(lipo -archs "$APP/Contents/MacOS/CombineColors"))"
