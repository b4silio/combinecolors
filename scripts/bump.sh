#!/bin/bash
# Bumps VERSION and adds a changelog entry (no commit; include both files in the feat/fix commit).
#   scripts/bump.sh minor "Dust spot healing on the input channels"
#   scripts/bump.sh patch "Copy preview honours the crop"
# Several entries: repeat the text argument. Conventional commits: feat → minor, everything else → patch.
set -euo pipefail
cd "$(dirname "$0")/.."
kind=${1:?usage: bump.sh major|minor|patch "entry" ["entry"…]}; shift
[ $# -ge 1 ] || { echo "at least one changelog entry needed" >&2; exit 1; }
IFS=. read -r major minor patch < VERSION
case "$kind" in
    major) major=$((major + 1)); minor=0; patch=0 ;;
    minor) minor=$((minor + 1)); patch=0 ;;
    patch) patch=$((patch + 1)) ;;
    *) echo "unknown bump kind: $kind" >&2; exit 1 ;;
esac
new="$major.$minor.$patch"
echo "$new" > VERSION
section=$(mktemp)
{ echo "## [$new] – $(date +%Y-%m-%d)"; echo; for e in "$@"; do echo "- $e"; done; echo; } > "$section"
# Insert the new section after the intro paragraph (before the first "## [").
awk -v f="$section" '
    !done && /^## \[/ { while ((getline l < f) > 0) print l; close(f); done = 1 }
    { print }
' CHANGELOG.md > CHANGELOG.md.tmp && mv CHANGELOG.md.tmp CHANGELOG.md
rm -f "$section"
grep -q "^## \[$new\]" CHANGELOG.md || { echo "changelog entry not written" >&2; exit 1; }
echo "VERSION $new"
