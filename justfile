# CombineColors task runner (https://just.systems). `just` lists the recipes.
# The .NET recipes expect the .NET 9 SDK (`~/.dotnet` is added to PATH when `dotnet` is not found).

set shell := ["bash", "-euo", "pipefail", "-c"]
export PATH := env_var("HOME") + "/.dotnet:" + env_var("PATH")

default:
    @just --list --unsorted

# ---- macOS (native SwiftUI app) --------------------------------------------------------------

# Build everything (library, app binary, CLI) in release mode
build:
    swift build -c release

# Run the Swift test suite
test:
    swift test

# Assemble build/CombineColors.app (native arch); `just app universal=1` adds the x86_64 slice
app universal="0":
    UNIVERSAL={{universal}} scripts/make_app.sh

# Build the universal DMG in build/
dmg:
    scripts/make_dmg.sh

# ---- .NET (Windows WPF app, Linux Avalonia app, CLI) -------------------------------------------

# Build the portable .NET targets (AstroCore, Avalonia app, CLI)
dotnet-build:
    dotnet build windows/CombineColors.Avalonia/CombineColors.Avalonia.csproj -c Release
    dotnet build windows/CombineColorsCli/CombineColorsCli.csproj -f net9.0 -c Release

# Run the AstroCore tests on the portable net9.0 build
dotnet-test:
    dotnet test windows/AstroCore.Tests/AstroCore.Tests.csproj -f net9.0 -c Release

# Run the Avalonia app (works on macOS, Linux and Windows); extra args are passed through
avalonia *args:
    dotnet run --project windows/CombineColors.Avalonia -c Release -- {{args}}

# Compile-check the Windows WPF targets (runs on macOS / Linux too, cannot execute them there)
windows-check:
    dotnet build windows/CombineColors/CombineColors.csproj -c Release
    dotnet build windows/CombineColors.Tests/CombineColors.Tests.csproj -c Release

# Self-contained Linux tarballs in build/ (default: linux-x64 and linux-arm64)
publish-linux *rids:
    windows/scripts/publish-linux.sh {{rids}}

# ---- Versioning and releases -------------------------------------------------------------------

# Bump VERSION and add a CHANGELOG entry: `just bump minor "entry"` (feat) or `just bump patch "entry"`
bump kind +entries:
    scripts/bump.sh {{kind}} {{entries}}

# Tag VERSION, push, build the DMG and the Linux x64 tarball, publish the GitHub release
release:
    scripts/release.sh

# Everything that should be green before a release
check: test dotnet-test windows-check
