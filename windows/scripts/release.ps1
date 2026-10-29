# Publishes the Windows build as a GitHub release asset, the counterpart of scripts/release.sh:
# tags the current VERSION (if the Mac side has not already), pushes, builds the self-contained zip
# with publish.ps1 and attaches it to the GitHub release of that tag, creating the release with the
# matching CHANGELOG section as notes when it does not exist yet. Needs a clean tree and the gh CLI
# (winget install GitHub.cli, then gh auth login).
#   .\scripts\release.ps1            # release the current VERSION
#   .\scripts\release.ps1 -NoTag     # only build and upload the zip to an existing release
param([switch]$NoTag)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot          # windows\
$repo = Split-Path -Parent $root
Set-Location $repo
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "the GitHub CLI (gh) is not installed: winget install GitHub.cli" }
$version = (Get-Content "VERSION" -Raw).Trim()
$tag = "v$version"
if (git status --porcelain) { throw "commit or stash your changes first" }
# Release notes: the CHANGELOG section of this version.
$lines = Get-Content "CHANGELOG.md"
$notes = @(); $on = $false
foreach ($l in $lines) {
    if ($l -match "^## \[$([regex]::Escape($version))\]") { $on = $true; continue }
    if ($on -and $l -match "^## \[") { break }
    if ($on) { $notes += $l }
}
$notesText = ($notes -join "`n").Trim()
if (-not $notesText) { throw "no CHANGELOG section for $version" }
if (-not $NoTag) {
    if (-not (git tag --list $tag)) { git tag -a $tag -m "CombineColors $version" }
    git push origin HEAD $tag
}
& (Join-Path $root "scripts\publish.ps1")
$zip = Join-Path $repo "build\CombineColors-$version-win-x64.zip"
if (-not (Test-Path $zip)) { throw "publish did not produce $zip" }
gh release view $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    gh release upload $tag $zip --clobber
} else {
    gh release create $tag $zip --title "CombineColors $version" --notes $notesText
}
if ($LASTEXITCODE -ne 0) { throw "gh failed" }
Write-Host "Released $tag with $(Split-Path -Leaf $zip)"
