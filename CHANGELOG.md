# Changelog

Versions follow [Semantic Versioning](https://semver.org): a `feat` commit bumps the minor
version, a `fix` / `perf` / `refactor` / `docs` / `build` commit bumps the patch version
(`scripts/bump.sh`). The version lives in `VERSION` and is stamped into the app at build time;
`scripts/release.sh` tags it and publishes the DMG as a GitHub release.

## [1.19.1] – 2026-11-05

- Fix: Sharpen was a high-frequency boost (it brightened bright areas and fine texture). It now deconvolves (Richardson–Lucy against a Gaussian of the radius, damped on noise) by default, with a corrected unsharp mask as the other choice; both are kept from making rings around stars
- Linux catches up with the macOS app: star removal with StarNet2 (Tools menu; downloads the Linux package), Background / Stars tabs (Ctrl+1 / Ctrl+2), Contrast block, Balance switch, block resets, Combine stars, hot pixels, band softness, Default preset, Close Image (Ctrl+W); and the 1.16–1.19.1 features — Channel mix under the palettes, Tab hides the side panels, movable Pixel Math / Matrix windows, Hα orange / SII red, data-relative stretches, straighten / turn in the crop editor (any angle, drag around the crop, Constrain to image, Level), bright spots with Max radius, the new Sharpen
- Linux: View ▸ Interface Size (Automatic, 100–300 %, applied on restart) for screens where the automatic scale is wrong; the automatic choice no longer gives up on 4K screens when QT_AUTO_SCREEN_SCALE_FACTOR or a 96 dpi Xft.dpi is set
- Windows: the shared engine brings the data-relative stretches (Stretch block shows only the method's parameters, Highlights as % clipped, absolute values read-out), the Sharpen method switch and the fixed sharpening, Hα orange / SII red; and the Windows app catches up with 1.16–1.19.1 — Channel mix under the palettes, Tab hides the side panes (View ▸ Hide Side Panels), movable Pixel Math / Matrix windows, straighten / turn in the crop editor (any angle, quarter-turn buttons, drag around the crop, Constrain to image, Level), bright spots with Max radius in the dust editor
- Windows: StarNet2 can use the graphics card. Its package loads Windows' built-in DirectML 1.8, too old for its ONNX Runtime, so it ran on the CPU; the Remove Stars window now offers to download Microsoft's DirectML 1.15.4 (≈ 9 MB from nuget.org, checksum verified) into the StarNet2 folder (CLI: `starnet --directml`)

## [1.19.0] – 2026-11-05

- Bright spots size themselves conservatively: a Max radius (default 1 % of the long side; slider in the dust editor, right-drag on the preview, shown as a dotted circle at the pointer) caps the automatic fit, which also stops at a couple of half-widths of the glow

## [1.18.0] – 2026-11-05

- Bright spots in the dust editor (Dark spots / Bright spots switch): removes star halos left behind by star removal — fitted like a dust shadow, the glow above the surrounding sky subtracted; drawn as orange dotted circles; CLI --dust-polarity
- macOS only at first (Linux from 1.19.1); the Windows build heals bright spots of a project but cannot add them

## [1.17.0] – 2026-11-05

- Straighten / turn in the crop editor: the picture turns under the upright crop (Angle slider and field, any angle, quarter-turn buttons, drag in the band around the crop with a rotate cursor; ⇧ whole degrees, quarter turns catch the pointer); a fine grid while turned; Constrain to image keeps empty corners out of the crop; Auto fits the largest crop inside the turned picture
- On Apply whole quarter turns are exact and the rest (±45°) is resampled once from the untouched data (Lanczos-3, clamped against ringing, ≈ 0.1 s per 40 MP channel); dust spots, gradient guides, samples, protected regions and painted maps turn with the picture; the angle is stored in projects and recent sets
- Stretch block labels widened (Highlights (%) no longer truncated)
- macOS only at first (Linux from 1.19.1); the Windows build opens turned projects but has no angle control

## [1.16.0] – 2026-11-04

- Channel mix: the channel weights moved from the processing strip to the right pane, under the palettes (one compact row per channel; the chevron opens gain, pedestal and the per-channel stretch tweaks)
- Tab hides the side panes and the crop / zoom toolbars, leaving the preview and the processing strip (View ▸ Hide Side Panels); every launch starts with them shown
- Manual stretches are set relative to the data's noise, so their sliders cover the useful range on any linear stack: MTF midtones, arcsinh and log strength, GHS stretch; GHS SP / LP / HP are percentiles of the image (50 % ≈ the sky); the absolute PixInsight-style values are shown under the block. Projects saved with manual stretch values get the new defaults
- The Stretch block shows only the parameters of the chosen method (Target bg for Auto STF only); Highlights is the share of pixels clipped to white (0–2 %) and no longer burns the image
- Pixel Math and Matrix editors open as movable panels instead of dialogs over the preview, so edits are seen live
- No window toolbar (Add Images ⌘O and Export ⌘E stay in the File menu): a lower title bar
- Hα is shown in orange and SII in red
- These changes were in the macOS app only (Linux caught up in 1.19.1; Windows has the stretch changes and the colours)

## [1.15.1] – 2026-11-04

- Fix: Generate produced no gradient points on a channel without a model (after switching channels in the gradient editor, or after Remove model): the samples model is now created on Generate or when a grid parameter changes
- Fix: painted corrections scale only the signal above the channel's sky level by default ("Above the sky"); on linear data the old exposure-style correction also scaled the sky pedestal, which the stretch magnified (0.5 EV darkened the sky to black). "Exposure" keeps the old behaviour (macOS toolbar switch; Windows / Linux use the sky-relative mode)
- Denoise runs before Detail again (better quality on the denoised image); the render cache keeps both outputs, so a detail slider re-runs the detail only
- Windows: the app catches up with 1.10–1.15 — star removal with StarNet2 (Remove Stars… / Tools menu: downloads the official Windows package on first use, license shown and accepted in the app; CLI `starnet` command), Background and Stars tabs with the Combine stars block (Ctrl+1 / Ctrl+2), Contrast block (and `--exposure`, `--brightness`, `--contrast`, `--highlights`, `--shadows` in the CLI), colour band Softness, Denoise Hot pixels, Balance switch (Color Balance ±50 / Selective Color), reset icon on every block, Default button, Reset all removes all processing, File ▸ Close Image (Ctrl+W), double-click fits first, crop and rotation locked while an editor is open, even brush spacing; the preview now uses the render cache

## [1.15.0] – 2026-11-04

- Detail runs before Denoise (blocks and pipeline in the same order; the render cache keeps both outputs, so a denoise slider re-runs the denoise only)
- ⌘W closes the image (File ▸ Close Image) instead of the window and the app
- Fix: the gradient sample grid covers vignetted frames without flats (it kept only the corner samples): the sky envelope adds radial / quadratic terms when a plane cannot explain the sky, and the tolerance follows the model's misfit when it is far above the noise

## [1.14.0] – 2026-11-04

- Background and Stars tabs: two sets of channels with their own settings; the Combine stars block (formerly RGB stars) blends the Stars tab rendered with its settings; Remove Stars fills both tabs, Load RGB stars fills the Stars tab without switching; projects and recent sets keep both tabs
- The BG / Stars tabs sit under the editor and Remove Stars buttons; switching keeps zoom and pan
- Combine BG and Stars also on the Stars tab: its preview shows the stars over the rendered Background tab, and the blend controls work from there
- Colour bands: Softness slider (wider, S-shaped band fall-off and a gentler fade from grey)
- Denoise: Hot pixels slider (isolated single-pixel spikes replaced by their neighbours' median; full resolution only)
- Fix: ⌘1 / ⌘2 pressed on the tab already shown no longer reaches the preview as "1" (1:1 zoom)
- Fix: save panels add the extension (.fits, …) when the name is typed without it
- File ▸ Close Image clears both tabs, crop, settings, presets and the project
- Default button (left of Save preset) restores the palette's default look; Reset all now removes all processing (stretch, normalisation and luminance included)
- Contrast block after Balance (the colour controls stay together): exposure, brightness, contrast, highlights, shadows, black / white point, gamma; the Colour block keeps saturation, vibrance, SCNR and hue ("Classic SHO look" removed)
- Balance block: a Color Balance / Selective Color switch shows one tool at a time; Color Balance sliders span −50…50
- Every processing block has a reset icon at its top right
- Double-click on the preview fits first, then goes to 1:1 from the fitted view
- Fix: crop, rotation and flips are locked while an editor (dust, gradient, corrections) is open
- Fix: the corrections brush spaces its dabs along the path across mouse events (a slow drag piled up dabs and painted far stronger than the flow)

## [1.13.0] – 2026-11-04

- Star removal with StarNet2: Remove Stars… downloads the official StarNet2 package on first use (license shown and accepted in the app), runs it on the channel files, writes <name>_bg and <name>_stars and switches the channels to the starless files; linear data stay linear (StarNet2 linear mode with the project's Auto STF values, or the project's stretch undone afterwards); CLI starnet command

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
