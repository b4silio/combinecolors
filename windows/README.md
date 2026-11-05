# CombineColors for Windows and Linux (.NET)

A port of the macOS app to Windows and Linux: the same processing (FITS / TIFF loading, statistics,
normalisation, stretch, palettes, Pixel Math, luminance, colour bands, denoise, dust spot healing, guided gradient removal,
crop / rotation, presets, recent sets, project files, export) rewritten in C# on .NET 9, with a
WPF front-end on Windows and an Avalonia UI front-end on Linux. Project files (`.json`) are
interchangeable with the Mac app. The native SwiftUI app remains the macOS build.

```
windows/
  CombineColors.sln
  AstroCore/            processing library (no UI) — same layout as Sources/AstroCore; builds for
                        net9.0-windows (WIC, Direct3D) and plain net9.0 (PngCodec + SkiaSharp, CPU only)
    Image/              PlanarImage, Pyramid, PixelRect/AutoCrop, Statistics, StarMask, Vec (SIMD kernels)
    IO/                 FITS reader/writer, TIFF reader/writer, PngCodec (exact 8/16-bit PNG), RasterIO
                        (PNG/JPEG via WIC on Windows, PngCodec + SkiaSharp elsewhere), loader, channel detection
    Processing/         Stretch, Normalization, Palettes, PostProcess, Denoise, Bm3d, DualBand, DustRemoval, GradientRemoval,
                        GpuDenoise (D3D11, Windows) / GpuDenoise.Portable (stub)
    PixelMath/          lexer, parser, vectorised evaluator, programs
    StarNet/            StarNet2 integration: finding / downloading / running the program, star removal of a file
    Pipeline/           CombineSettings, Json (tolerant project format), ChannelSource, Renderer, ProjectDocument
  CombineColors/        WPF app (Model: ProjectModel, RenderCoordinator, Preferences, RecentSets; Views)
  CombineColors.Avalonia/ Avalonia UI app for Linux (also runs on macOS / Windows): same Model and Views,
                        built in code against Avalonia instead of WPF (see "Linux" below)
  CombineColorsCli/     headless renderer (combinecolors-cli), same options as the Mac CLI; net9.0 + net9.0-windows
  AstroCore.Tests/      xUnit port of the AstroCore test suite (real-data tests use C:\_Astrophotography when present)
  CombineColors.Tests/  model-level app tests (loading, crop, rotation, presets, dust spots, gradient removal, recent sets,
                        Background / Stars tabs)
  scripts/release.ps1   tags, pushes, builds the zip and attaches it to the GitHub release (gh CLI)
  scripts/publish.ps1   self-contained build → build\CombineColors-<version>-win-x64.zip
  scripts/publish-linux.sh  self-contained Linux build (x64 + arm64) → build/CombineColors-<version>-linux-<arch>.tar.gz
```

## Releasing

```powershell
.scripts
elease.ps1          # tag v<VERSION>, push, build the zip, create or update the GitHub release
```

The Mac `scripts/release.sh` and this script share the tag: whichever runs first creates the
release (notes from the CHANGELOG section), the other attaches its asset. `release.sh` also builds
and attaches the Linux x64 tarball (`scripts/publish-linux.sh`). Run `release.ps1 -NoTag`
to only upload the zip to an existing release. Needs the GitHub CLI (`winget install GitHub.cli`,
then `gh auth login`).

## Building

Requirements: Windows 10/11 x64, [.NET 9 SDK](https://dotnet.microsoft.com/download) (Visual
Studio 2022 with the .NET desktop workload also works; open `CombineColors.sln`).

```powershell
cd windows
dotnet build -c Release                     # library, app, CLI, tests
dotnet test                                 # ~140 tests, about 30 s
dotnet run --project CombineColors -c Release
.\scripts\publish.ps1                       # single-folder self-contained app + zip in ..\build
```

The CLI mirrors the Mac one:

```powershell
.\CombineColorsCli\bin\Release\net9.0-windows\combinecolors-cli.exe palettes
.\CombineColorsCli\bin\Release\net9.0-windows\combinecolors-cli.exe info stack-Ha.fits
.\CombineColorsCli\bin\Release\net9.0-windows\combinecolors-cli.exe render --out out --palette all --crop auto --norm backgroundSignal --linked --max 2000000 Soul_Core-*.fits
# Star removal with StarNet2 (writes stack-Ha_bg.fits and stack-Ha_stars.fits next to the file, or in --out <dir>)
.\CombineColorsCli\bin\Release\net9.0-windows\combinecolors-cli.exe starnet --download stack-Ha.fits
```

`starnet` runs the free [StarNet2](https://starnetastro.com/cli-tools/starnet/) command-line program
(by Mikita Misiura, never bundled) on each file: linear data through StarNet2's linear mode with the
Auto STF values (`--target`, `--shadows`), or `--method appStretch --stretch <method>` to stretch here
and invert the stretch afterwards; already-stretched data are processed as they are. Without
`--starnet <path\to\starnet2.exe>` it takes the program the app uses (remembered in
`%LOCALAPPDATA%\CombineColors\starnet.json`), then `%LOCALAPPDATA%\CombineColors\StarNet2`,
`Program Files\StarNet2\bin` (the official installer) and the `PATH`. `--download` fetches the
official portable package for this platform from the starnetastro.com release feed (SHA-256
checked) into `%LOCALAPPDATA%\CombineColors\StarNet2\<version>` (`--install-root <dir>` elsewhere,
not remembered); `--install <zip>` unpacks a package downloaded by hand. Using StarNet2 means
accepting its license (`LICENSE.txt` in the package, path printed by the command).

**StarNet2 and the graphics card (Windows).** StarNet2's Windows package runs ONNX Runtime 1.24 with
DirectML but does not ship `DirectML.dll`, so Windows' own copy is loaded — version 1.8 on Windows 10 /
11, too old for that runtime: the warm-up fails ("Resize … The parameter is incorrect") and StarNet2
falls back to the CPU. The Remove Stars window checks which DirectML StarNet2 would load and, when it is
older than 1.15.4, offers to download Microsoft's redistributable `DirectML.dll` (Microsoft.AI.DirectML
1.15.4 from nuget.org: only the x64 file, ≈ 9 MB read out of the 200 MB package with HTTP range
requests, SHA-256 checked) into the StarNet2 folder, where Windows loads it first. Nothing else changes;
deleting the file undoes it. "Not now" turns the offer into a button under Options. The CLI does the
same with `starnet --directml` (without files it only installs). StarNet2's own README also allows an
ONNX Runtime CUDA build in its folder (NVIDIA only, ≈ 1.5 GB of CUDA / cuDNN libraries); the app does not
offer that. Loading StarNet2's weights in another inference engine is not allowed by its license.

Run `CombineColors.exe --prefs <file>` to keep an instance's preferences (recent sets with their
saved presets and dust spots, hidden palettes, collapsed blocks…) apart from the user's, which
live in `%LOCALAPPDATA%\CombineColors\preferences.json`.

## What differs from the Mac build

* **GPU through Direct3D 11 compute shaders** instead of Metal: the non-local means and BM3D
  kernels are the Metal shaders translated to HLSL (shader model 5, compiled from source at first
  use, float atomics emulated with compare-exchange), driven through the Vortice bindings on the
  adapter with the most memory. Everything else runs on the CPU with SIMD kernels (`Vec`) and
  `Parallel.For`; the CPU ports of non-local means and BM3D remain as the fallback when no GPU is
  usable (or when `GpuDenoise.Enabled` is off).
* **TIFF** is read by a built-in reader (8/16/32-bit integer and float, strips or tiles,
  uncompressed / LZW / Deflate / PackBits, predictors, BigTIFF) so values arrive exactly as stored;
  PNG and JPEG go through the Windows Imaging Component. Export writes uncompressed TIFF, PNG and
  JPEG through the same paths.
* **Keyboard**: ⌘ becomes Ctrl (Ctrl+O add images, Ctrl+Shift+O open project, Ctrl+S / Ctrl+Shift+S
  save, Ctrl+W close image, Ctrl+E export, Ctrl+Shift+C copy preview, Ctrl+1 / Ctrl+2 Background / Stars tab,
  Ctrl+0 fit, Ctrl+R rotate, Ctrl+Shift+H / V flips, Ctrl+Z / Ctrl+Y undo / redo). In the preview: wheel zooms around the
  pointer, drag pans, Ctrl / Shift + wheel pans, 0 fit, 1 actual size, double-click fits first and goes to 1:1 from the fitted view, Return / Esc apply
  or cancel crop, dust and gradient editing, B toggles the "Original" view (M the gradient editor's "Background" view, P its
  Protect mode), Delete removes the selected spot or protected region. Tab hides the side panes and the
  crop / zoom toolbars (View ▸ Hide Side Panels).
* The interface is dark only (the .NET 9 Fluent theme in dark mode, independent of the Windows
  setting); the layout follows the Mac app (channel column · preview · palettes + channel mix +
  presets, processing strip underneath), with the 1.16–1.19 features: Channel mix pane, Tab to hide the
  side panes, movable Pixel Math / Matrix windows, crop straightening (Angle slider, quarter turns, drag
  in the band around the crop, Constrain to image, Level) and bright spots with Max radius in the dust editor.
* Developer switches for UI screenshots: `--prefs <file>`, `--screenshot <png>` (`--screenshot-delay <s>`),
  `--open-layer stars`, `--hide-panes`, `--open-editor crop:<deg>` / `dust:<channel>` / `gradient:<channel>`
  and `--editor-action apply` / `spots:bright:x,y;…`.

## Linux

`CombineColors.Avalonia/` is the Linux front-end: the WPF app's Model layer and view code ported to
[Avalonia UI](https://avaloniaui.net) 11 (Fluent dark theme, Skia rendering). The processing library
is the same `AstroCore`, built for plain `net9.0`:

* **PNG / JPEG**: PNG is read and written by the exact built-in `PngCodec` (8/16-bit gray, RGB and
  RGBA, non-interlaced; other variants fall back to SkiaSharp at 8 bits); JPEG goes through SkiaSharp.
  The Windows build keeps the Windows Imaging Component.
* **GPU denoise** is Windows only (Direct3D 11); on Linux non-local means and BM3D run on the CPU
  (`GpuDenoise.Portable.cs` is the stub that makes the callers take the CPU paths).
* **Not yet on Linux**: copying the preview to the clipboard (Avalonia has no portable image
  clipboard), native menus (the menu bar lives inside the window), app icon packaging. Otherwise the
  Linux front-end follows the Mac app (1.19.1): Background / Stars tabs, star removal, Channel mix,
  Tab to hide the side panes, movable Pixel Math / Matrix windows, crop straightening, bright spots,
  like the Windows front-end.
* **HiDPI / 4K screens**: Avalonia scales the interface from `Xft.dpi` or the `AVALONIA_*` / `QT_*`
  scale variables; without any of them a 4K monitor would get scale 1 and 11 px text. The app
  therefore asks the X server for the screen's pixel and physical size before starting and sets
  the scale itself (quarter steps from 1.5× up to 3×, physical sizes over 90 cm or outside 50–400 dpi are
  treated as bogus and a ≥ 2000-px-tall screen gets 2×; `QT_AUTO_SCREEN_SCALE_FACTOR` and a 96 dpi
  `Xft.dpi` on such a screen do not stop it). **View ▸ Interface Size** (Automatic, 100–300 %) saves
  a fixed size, applied on the next start (the app offers to restart); `--scale 1.5` or
  `COMBINECOLORS_SCALE=1.5` override everything; `GDK_SCALE` is honoured too. The choice is printed on stderr.
* The interface follows the Windows app, so the keyboard shortcuts above apply (Ctrl); Tab hides the
  side panes. The same `--prefs <file>`, `--screenshot <png>` (plus `--screenshot-delay <s>`) developer
  switches exist, plus `--hide-panes`, `--open-editor crop:<deg>` / `dust:<channel>` and
  `--editor-action apply` / `spots:bright:x,y;…` for screenshots;
  preferences live in `~/.local/share/CombineColors/preferences.json`.

Building and running (any OS with the .NET 9 SDK; no Linux machine is needed to build):

```bash
cd windows
dotnet build CombineColors.Avalonia -c Release
dotnet run --project CombineColors.Avalonia -c Release            # also runs on macOS and Windows
dotnet test AstroCore.Tests -f net9.0                            # the engine's tests on the portable build
scripts/publish-linux.sh                                           # self-contained tar.gz for linux-x64 and linux-arm64
```

On the Linux machine the self-contained build needs only X11 (or XWayland) and fontconfig:
`sudo apt install libfontconfig1 libice6 libsm6 libx11-6 libxext6 libxi6 libxrandr2 libxcursor1 libxrender1`,
then `./CombineColors` (or `./combinecolors-cli …`). A headless check without a desktop:
`xvfb-run -a ./CombineColors --screenshot out.png --screenshot-delay 20 *.fits`.

The Windows targets (`net9.0-windows`, WPF) also compile on macOS / Linux thanks to
`EnableWindowsTargeting` in `Directory.Build.props`; running them still needs Windows.
