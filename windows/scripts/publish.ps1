# Builds a self-contained, single-folder Windows release of the app and the CLI and zips it as
# build\CombineColors-<version>-win-x64.zip (version from the repository's VERSION file).
#   .\scripts\publish.ps1
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot          # windows\
$repo = Split-Path -Parent $root
$version = (Get-Content (Join-Path $repo "VERSION") -Raw).Trim()
$out = Join-Path $repo "build\CombineColors-$version-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null
foreach ($proj in @("CombineColors\CombineColors.csproj", "CombineColorsCli\CombineColorsCli.csproj")) {
    dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:DebugType=none -o $out
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $proj" }
}
Copy-Item (Join-Path $repo "README.md") (Join-Path $out "README.md")
Copy-Item (Join-Path $root "README.md") (Join-Path $out "README-windows.md")
Copy-Item (Join-Path $repo "CHANGELOG.md") (Join-Path $out "CHANGELOG.md")
$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$out\*" -DestinationPath $zip
Write-Host "Built $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
