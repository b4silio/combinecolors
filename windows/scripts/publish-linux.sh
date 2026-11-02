#!/bin/bash
# Builds self-contained Linux releases of the Avalonia app and the CLI (from any OS with the .NET 9 SDK)
# as build/CombineColors-<version>-linux-<arch>.tar.gz (version from the repository's VERSION file).
#   windows/scripts/publish-linux.sh            # linux-x64 and linux-arm64
#   windows/scripts/publish-linux.sh linux-x64  # one architecture
# Runtime needs on the Linux side: X11 (or XWayland), libfontconfig1, libice6, libsm6 (Debian / Ubuntu:
#   sudo apt install libfontconfig1 libice6 libsm6 libx11-6 libxext6 libxi6 libxrandr2 libxcursor1 libxrender1).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"          # windows/
repo="$(dirname "$root")"
version=$(tr -d '[:space:]' < "$repo/VERSION")
rids=("$@"); [ ${#rids[@]} -gt 0 ] || rids=(linux-x64 linux-arm64)
for rid in "${rids[@]}"; do
    out="$repo/build/CombineColors-$version-$rid"
    rm -rf "$out"; mkdir -p "$out"
    dotnet publish "$root/CombineColors.Avalonia/CombineColors.Avalonia.csproj" -c Release -r "$rid" --self-contained true -p:DebugType=none -o "$out"
    dotnet publish "$root/CombineColorsCli/CombineColorsCli.csproj" -f net9.0 -c Release -r "$rid" --self-contained true -p:DebugType=none -o "$out"
    cp "$repo/README.md" "$out/README.md"; cp "$root/README.md" "$out/README-dotnet.md"; cp "$repo/CHANGELOG.md" "$out/CHANGELOG.md"
    tar -C "$repo/build" -czf "$out.tar.gz" "$(basename "$out")"
    echo "Built $out.tar.gz ($(du -h "$out.tar.gz" | cut -f1))"
done
