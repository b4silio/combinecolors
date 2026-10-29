# CombineColors for Windows

A port of the macOS app to Windows: the same processing (FITS / TIFF loading, statistics,
normalisation, stretch, palettes, Pixel Math, luminance, colour bands, denoise, dust spot healing,
crop / rotation, presets, recent sets, project files, export) rewritten in C# on .NET 9, with a
WPF front-end. Project files (`.json`) are interchangeable with the Mac app.

```
windows/
  CombineColors.sln
  AstroCore/            processing library (no UI) — same layout as Sources/AstroCore
    Image/              PlanarImage, Pyramid, PixelRect/AutoCrop, Statistics, StarMask, Vec (SIMD kernels)
    IO/                 FITS reader/writer, TIFF reader/writer, PNG/JPEG via WIC, loader, channel detection
    Processing/         Stretch, Normalization, Palettes, PostProcess, Denoise, Bm3d, DualBand, DustRemoval
    PixelMath/          lexer, parser, vectorised evaluator, programs
    Pipeline/           CombineSettings, Json (tolerant project format), ChannelSource, Renderer, ProjectDocument
  CombineColors/        WPF app (Model: ProjectModel, RenderCoordinator, Preferences, RecentSets; Views)
  CombineColorsCli/     headless renderer (combinecolors-cli.exe), same options as the Mac CLI
  AstroCore.Tests/      xUnit port of the AstroCore test suite (real-data tests use C:\_Astrophotography when present)
  CombineColors.Tests/  model-level app tests (loading, crop, rotation, presets, dust spots, recent sets)
  scripts/publish.ps1   self-contained build → build\CombineColors-<version>-win-x64.zip
```

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
```

Run `CombineColors.exe --prefs <file>` to keep an instance's preferences (recent sets with their
saved presets and dust spots, hidden palettes, collapsed blocks…) apart from the user's, which
live in `%LOCALAPPDATA%\CombineColors\preferences.json`.

## What differs from the Mac build

* **No GPU path.** Metal is replaced by CPU code spread over every core (SIMD kernels in
  `Vec`, `Parallel.For` for the heavy loops). Non-local means and BM3D run on the CPU: BM3D on a
  2.5 MP preview takes a few seconds on a 16-core machine instead of a fraction of a second. A
  DirectX compute port is the obvious next step.
* **TIFF** is read by a built-in reader (8/16/32-bit integer and float, strips or tiles,
  uncompressed / LZW / Deflate / PackBits, predictors, BigTIFF) so values arrive exactly as stored;
  PNG and JPEG go through the Windows Imaging Component. Export writes uncompressed TIFF, PNG and
  JPEG through the same paths.
* **Keyboard**: ⌘ becomes Ctrl (Ctrl+O add images, Ctrl+Shift+O open project, Ctrl+S / Ctrl+Shift+S
  save, Ctrl+E export, Ctrl+Shift+C copy preview, Ctrl+0 fit, Ctrl+1 actual size, Ctrl+R rotate,
  Ctrl+Shift+H / V flips, Ctrl+Z / Ctrl+Y undo / redo). In the preview: wheel zooms around the
  pointer, drag pans, Ctrl / Shift + wheel pans, double-click toggles fit / 1:1, Return / Esc apply
  or cancel crop and dust editing, B toggles the dust "Original" view, Delete removes the selected spot.
* The window uses the standard Windows (light) controls around a dark preview; the layout follows
  the Mac app (channel column · preview · palettes + presets, processing strip underneath).
