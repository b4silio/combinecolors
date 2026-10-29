#!/bin/bash
# Packages build/CombineColors.app into build/CombineColors-<version>.dmg for sharing
# (universal binary; runs scripts/make_app.sh first unless SKIP_BUILD=1).
set -euo pipefail
cd "$(dirname "$0")/.."
[ "${SKIP_BUILD:-0}" = "1" ] || UNIVERSAL=1 scripts/make_app.sh
APP=build/CombineColors.app
VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$APP/Contents/Info.plist")
DMG="build/CombineColors-$VERSION.dmg"
STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
cp -R "$APP" "$STAGE/"
ln -s /Applications "$STAGE/Applications"
cat > "$STAGE/How to open.txt" <<'TXT'
CombineColors — combine narrowband / RGB astrophotography channels

Install: drag CombineColors onto the Applications folder, then eject this disk image.

First launch: the app is not notarised by Apple (no developer account), so macOS
will say it cannot verify it. Click "Done", then open
System Settings > Privacy & Security, scroll down to the message about
CombineColors and click "Open Anyway". This is needed once.

Alternative from Terminal (removes the download quarantine flag):
    xattr -dr com.apple.quarantine /Applications/CombineColors.app

Requires macOS 14 or later; Apple silicon and Intel Macs.
Drop FITS or TIFF files (one per channel) onto the window to begin.
TXT
rm -f "$DMG"
hdiutil create -volname "CombineColors $VERSION" -srcfolder "$STAGE" -ov -format UDZO -imagekey zlib-level=9 "$DMG" > /dev/null
echo "Wrote $DMG ($(du -h "$DMG" | cut -f1))"
