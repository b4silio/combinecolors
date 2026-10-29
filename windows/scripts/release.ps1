# Publishes the Windows build as a GitHub release asset, the counterpart of scripts/release.sh:
# tags the current VERSION (if the Mac side has not already), pushes, builds the self-contained zip
# with publish.ps1 and attaches it to the GitHub release of that tag, creating the release with the
# matching CHANGELOG section as notes when it does not exist yet. Needs a clean tree and the gh CLI
# (winget install GitHub.cli, or the portable zip unpacked into %LOCALAPPDATA%\Programs\gh; then gh auth login).
#   .\scripts\release.ps1            # release the current VERSION
#   .\scripts\release.ps1 -NoTag     # only build and upload the zip to an existing release
param([switch]$NoTag)
# Native commands (git, gh) write progress to stderr; with a "Stop" preference Windows PowerShell
# would turn that into errors, so exit codes are checked explicitly instead.
$ErrorActionPreference = "Continue"
$root = Split-Path -Parent $PSScriptRoot          # windows\
$repo = Split-Path -Parent $root
Set-Location $repo
function Run { param([string]$What, [scriptblock]$Command) & $Command 2>&1 | ForEach-Object { "$_" }; if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" } }
# gh from PATH, or the portable copy in %LOCALAPPDATA%\Programs\gh (no admin rights needed).
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $portable = Join-Path $env:LOCALAPPDATA "Programs\gh\bin\gh.exe"; if (Test-Path $portable) { $gh = $portable } }
if (-not $gh) { throw "the GitHub CLI (gh) is not installed: winget install GitHub.cli, or unzip the portable gh into %LOCALAPPDATA%\Programs\gh" }
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
    if (-not (git tag --list $tag)) { Run "git tag" { git tag -a $tag -m "CombineColors $version" } }
    Run "git push" { git push origin HEAD $tag }
}
& (Join-Path $root "scripts\publish.ps1")
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
$zip = Join-Path $repo "build\CombineColors-$version-win-x64.zip"
if (-not (Test-Path $zip)) { throw "publish did not produce $zip" }
$null = & $gh release view $tag --json tagName 2>&1
$exists = ($LASTEXITCODE -eq 0)
if ($exists) {
    Run "gh release upload" { & $gh release upload $tag $zip --clobber }
} else {
    Run "gh release create" { & $gh release create $tag $zip --title "CombineColors $version" --notes $notesText }
}
Write-Host "Released $tag with $(Split-Path -Leaf $zip)"
