# Changelog

Versions follow [Semantic Versioning](https://semver.org): a `feat` commit bumps the minor
version, a `fix` / `perf` / `refactor` / `docs` / `build` commit bumps the patch version
(`scripts/bump.sh`). The version lives in `VERSION` and is stamped into the app at build time;
`scripts/release.sh` tags it and publishes the DMG as a GitHub release.

## [1.12.1] – 2026-11-03

- Manual corrections: flow shown as a percentage (0.5–10 %, default 2 %) and the default strength is 0.5 EV

## [1.12.0] – 2026-11-03

- Stars are blended after denoise and detail by default (switchable in the RGB stars block): untouched by them, and a star slider re-blends only
- Dust / Gradient / Manual buttons above the channel column open the editors; while one is open a click on a channel shows it there (the channel menus lost the three entries)
- Fixed: the correction strength and brush defaults were not read back from projects and recent sets, and a recent set did not restore its stars layer

## [1.11.0] – 2026-11-03

- Combine stage ×5 faster: the Lab luminance injection is vectorised (29 → 3 ms at the preview size) and each channel's prepared plane is kept, so a gain or weight change prepares that channel only; the status bar says how many channels were reused

## [1.10.0] – 2026-11-03

- Render cache: moving a Detail, Denoise or colour slider re-runs only the stages after it (Detail 57 → 17 ms, with BM3D on 277 → 17 ms at the preview size); the status bar says what was reused

## [1.9.0] – 2026-11-03

- Manual corrections painted on one channel of a split colour file no longer come back on all three: the map is stored per channel (<image>.<channel>.ccmask.png; older maps are still read); Mac, Windows and Linux
- RGB stars: with Brightest stars to white on, lowering Amount fades the faint stars but keeps the cores white (they used to turn grey)
- Balance block (between Colour and Denoise): Photoshop-style Color Balance (cyan–red, magenta–green, yellow–blue for shadows, midtones and highlights, Preserve luminosity) and Selective Color for the whites, neutrals and blacks (cyan, magenta, yellow, black; Relative or Absolute); Mac, Windows, Linux and the CLI
- Reopening a recent set or a project whose R, G and B came from one colour file gives the three channels their own planes again (they all got the first plane) and saves the set's state; Mac, Windows and Linux
- Detail and RGB stars blocks are one scrolled column again (the two-column layout left the sliders a few pixels wide); Sharpen sits under the Detail scales; Mac, Windows and Linux
- Linux: the interface scales itself on HiDPI / 4K screens that announce no scale (physical size from the X server, 1.5–3× in quarter steps); `--scale <f>` or `COMBINECOLORS_SCALE` force a value

## [1.8.0] – 2026-11-03

- Background-sample gradient removal (Samples shape, à la DBE / GraXpert): automatic sky grid with nebula rejection, thin-plate spline or kriging surface with leave-one-out robust reweighting and cross-validated smoothing, click / drag / delete samples; Mac, Windows, Linux and the CLI
- Manual corrections (channel menu › Manual Corrections…): paint a channel darker or brighter with a soft brush (right-drag sets the radius, ⌥ inverts, undo; the preview shows the result while painting and when changing the strength, Map switches to the red / blue tint, space bar pans in every editor, Flow 0.01–0.2 and Strength 0–1.5 stops, badge under the channel label), applied live as an exposure change and kept next to the image as <image>.ccmask.png
- RGB stars layer: a colour image can be kept whole (Keep as RGB (stars) when dropping it, Load RGB stars…, --stars-file) and blended on top; Screen / Lighten / Add, star brightness and contrast, Brightest stars to white
- Formula read-out: the palette panel shows the combination as PixelMath lines with a copy button (tester request)

## [1.7.0] – 2026-11-02

- XISF input (PixInsight format): monolithic files, UInt8–64 / Float32 / Float64 samples, planar or interleaved, zlib / LZ4 compression with byte shuffling; filter detection from the embedded FITS keywords; Mac, Windows, Linux and the CLI
- Detail: two more local-contrast scales — Micro (0.2 % of the width) and Low freq. (50 %) — for large frames; the Detail block is now two columns
- Zoomed-in preview matches the fit view and the export: the Detail stage (Dehaze, Clarity, Texture) of a cropped render takes its radii, haze and star cap from the whole picture (coarse overview) instead of the visible crop, so zooming no longer changes the look (beta report); Mac, Windows and Linux

## [1.6.1] – 2026-11-02

- Gradient / dust editors and the channel peek show the channel's data with the stretch only (no gain / offset, normalisation, Detail, Denoise or Colour bands): editing a gradient no longer needs Reset all

## [1.6.0] – 2026-11-02

- .NET apps (Windows WPF, Linux Avalonia) and CLI: guided gradient removal with the same models, editor and project format as the Mac app

## [1.5.0] – 2026-11-02

- Guided gradient removal (channel menu › Remove Gradient…): line / ellipse / polynomial guides with a robust profile, optional sectors and residual plane or quadratic, geometry refinement from the data, subtract or divide, protected regions, live preview; CLI --gradient options
- Developer switches -openEditor and -screenshot for UI screenshots without screen-recording permission

## [1.4.0] – 2026-11-02

- Linux: Avalonia UI app (windows/CombineColors.Avalonia) and CLI on the portable net9.0 build of AstroCore (built-in exact PNG codec + SkiaSharp for JPEG, CPU denoise); windows/scripts/publish-linux.sh; the Windows targets compile on macOS / Linux
- Releases: `scripts/release.sh` also builds and attaches the self-contained `CombineColors-<version>-linux-x64.tar.gz` (untar, install libfontconfig1 and the X11 libraries, run `./CombineColors`)
- `justfile` with the build, test, publish and release recipes (`just` lists them; `just release`, `just publish-linux`, `just bump minor "…"`)

## [1.3.0] – 2026-10-29

- Windows: dark interface (Fluent dark theme, independent of the system setting), `--screenshot` developer switch, `windows/scripts/release.ps1` to publish the zip as a GitHub release asset
- macOS: DMG attached to the same 1.3.0 release (includes the 1.0.1 changes below); `scripts/release.sh` uploads to an existing release so both platforms share one tag

## [1.2.0] – 2026-10-29

- Windows: GPU noise reduction through Direct3D 11 compute shaders (non-local means and BM3D, the Metal kernels in HLSL), CPU fallback kept

## [1.1.0] – 2026-10-29

- Windows port (windows/): the processing library, the app (WPF) and the CLI rewritten in C# on .NET 9, with the test suites; project files are interchangeable with the Mac app

## [1.0.1] – 2026-10-29

- README screenshot refreshed with the current layout
- The crop-limits mode (hidden / frame / crop only) is remembered between launches
- bump.sh writes its changelog section correctly

## [1.0.0] – 2026-10-29

First release.

- Load FITS (8/16/32-bit integer, float) and TIFF/PNG stacks by drag & drop; channel detection
  from the FITS `FILTER` keyword or file names; dual-band colour files split into two filters.
- Palettes (SHO, HOO, Foraxx, dynamic mixes, mono…) with pre-computed thumbnails, plus Pixel
  Math with its own expression language.
- Stretch: PixInsight-style auto STF (linked / unlinked), MTF, arcsinh, GHS, log, manual;
  pre-combine or post-combine stage.
- Six normalisation methods (background, linear fit, dispersion, background + signal,
  noise-weighted, histogram match) with star-masked statistics.
- Luminance recipes (Hα, L, weighted mixes, custom expression), RGB star blending.
- Post-processing: colour bands (hue / saturation / lightness per band), saturation, vibrance,
  SCNR, hue shift, black / white point, gamma, texture / clarity / dehaze, sharpening.
- Denoise: starlet wavelets, non-local means, guided filter, and GPU (Metal) non-local means
  and BM3D.
- Dust spot healing on the input channels (multiplicative transmission model, local flat or
  radial profile per spot, fitted from a click).
- Shared crop with auto-trim, rotation and flips, presets with thumbnails, recent channel sets
  that remember their settings, project files, export to FITS / TIFF / PNG / JPEG,
  command-line renderer, copy preview to clipboard.
