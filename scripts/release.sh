#!/bin/bash
# Tags the current VERSION, pushes, builds the universal DMG and publishes it as a GitHub release
# with the matching CHANGELOG section as notes. Needs a clean tree and the gh CLI.
set -euo pipefail
cd "$(dirname "$0")/.."
version=$(cat VERSION)
tag="v$version"
[ -z "$(git status --porcelain)" ] || { echo "commit or stash your changes first" >&2; exit 1; }
notes=$(awk -v v="$version" '
    $0 ~ "^## \\[" v "\\]" { on = 1; next }
    on && /^## \[/ { exit }
    on { print }
' CHANGELOG.md)
[ -n "$notes" ] || { echo "no CHANGELOG section for $version" >&2; exit 1; }
git rev-parse -q --verify "refs/tags/$tag" > /dev/null || git tag -a "$tag" -m "CombineColors $version"
git push origin HEAD "$tag"
scripts/make_dmg.sh
dmg="build/CombineColors-$version.dmg"
# The Windows side (windows/scripts/release.ps1) may have created the release first: then only attach the DMG.
if gh release view "$tag" > /dev/null 2>&1; then
    gh release upload "$tag" "$dmg" --clobber
else
    gh release create "$tag" "$dmg" --title "CombineColors $version" --notes "$notes"
fi
echo "Released $tag"
