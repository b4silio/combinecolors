# CombineColors — Implementation Plan

Native macOS app (SwiftUI + Accelerate) to combine monochrome astrophotography
channels (FITS / TIFF, 16 & 32 bit) into colour images: fixed palettes (SHO,
HOO, …), dynamic palettes (Foraxx, …) and free-form Pixel Math, with real-time
preview, per-channel normalisation methods and adjustable stretching.

## Architecture

```
Sources/
  AstroCore/            pure Swift + Accelerate library, no UI
    Image/              PlanarImage (float32 planes), pyramid, statistics, histogram, star mask
    IO/                 FITS reader/writer, TIFF/PNG/JPEG via ImageIO, channel-name detection
    Processing/         stretch functions (STF/MTF/arcsinh/GHS/log), LUTs, normalisation,
                        matrix palettes, post-processing (saturation, SCNR, hue, luminance),
                        dual-band split, RGB star blending
    PixelMath/          lexer, parser, AST, vectorised evaluator, preset formulas
    Pipeline/           CombineSettings (Codable) + Renderer (settings -> RGB image)
  CombineColors/        SwiftUI app (document model, preview engine, inspector, editors, export)
  combinecolors-cli/    headless renderer (batch / scripting / test images)
Tests/AstroCoreTests/   XCTest suite
scripts/make_app.sh     builds release binary and assembles CombineColors.app
```

Design decisions:

* **Fidelity-first preview.** All per-channel transforms (normalisation, stretch) are
  derived from statistics of the *full-resolution* image (sub-sampled), turned into
  65 536-entry LUTs, then applied on a mip-pyramid level sized for the viewport.
  Point-wise operations commute with box down-sampling, so the preview is the exact
  down-sampled final result for linear ops and a very close approximation for the
  non-linear ones. Zooming past 1:1 renders crops of the full-resolution data.
* **Latest-wins render queue.** Parameter changes coalesce; stale renders are skipped.
* **Both stretch orders** are available: stretch each channel *before* combining
  (default, mirrors the usual narrowband workflow) or combine linear and stretch
  *after* (linked or unlinked STF).
* **Normalisation menu** (see literature notes in README): none, background equalise,
  linear fit (robust iterative regression to a reference channel, PixInsight
  LinearFit style), background/dispersion matching, background/signal matching
  (background from sigma-clipped sky, signal from a high percentile of the
  star-masked image), noise-weighted (equal noise floor), histogram matching.
  Statistics may be computed with or without a star mask (switch).
* **Palettes** are 3×N matrices (editable) or Pixel-Math formulas; every preset
  gets a pre-computed thumbnail (whole image or chosen region).

## Phases (all implemented)

1. Core image model + IO: PlanarImage, FITS read/write (BITPIX 8/16/32/-32/-64,
   BZERO/BSCALE, NAXIS3), TIFF/PNG via ImageIO (8/16/32-bit), writers, channel detection.
2. Statistics: median/MAD/percentiles/noise/background (sigma-clipped), zero-padding mask,
   pyramid, histogram, star mask.
3. Stretch: MTF, auto STF (PixInsight algorithm, linked/unlinked), arcsinh, GHS, log,
   manual histogram; LUT generation and vDSP lookup.
4. Normalisation methods (above), reference selection, star-masked statistics.
5. Palettes + Pixel Math (lexer/parser/evaluator, functions, presets: Foraxx, dynamic SHO/HOO…).
6. Pipeline + post-processing (saturation, SCNR, hue, gamma, luminance injection,
   RGB star blend) + CLI; render real data to PNG for visual validation.
7. SwiftUI app: drag & drop, channel strip, zoom/pan preview, palette grid with thumbnails,
   parameters inspector with real-time preview, pixel math editor, dual-band splitter,
   export (FITS/TIFF16/TIFF32/PNG/JPEG), project save/load, histogram, pixel readout.
8. Packaging (make_app.sh), README, performance pass, full test run.
9. Shared crop (added on request): PixelRect, region-aware statistics, auto-trim of
   registration borders, crop overlay/editing on the preview, cropped thumbnails and export.
10. Model tests (Tests/CombineColorsTests) driving the app model headlessly: loading,
    detection, mismatch rejection, pending files, reassign/remove, crop flow, auto-crop,
    project round trip, export, A/B snapshot + undo, hover read-out, dual-band insertion.

11. Luminance recipes (Hα default, L, weighted mixes, max/mean of lines, custom expression),
    resolution-independent luminance stretch in the post-combine stage.
12. Recent channel sets (UserDefaults MRU, File menu + start screen), scroll-safe drop-downs.

## Iteration log (keep this current — every change lands here)

Each entry: what was added or corrected, where it lives, how it is verified.
Reproducing the project means implementing phases 1–8 plus everything below.

### Features added after the first build

| # | Feature | Where | Verified by |
|---|---------|-------|-------------|
| F1 | Shared crop: one rectangle for all channels, drawn/handled on the preview or typed in; **Auto trim** of empty registration borders; overlay dims outside the crop; drives statistics, thumbnails, export | `Image/PixelRect.swift` (PixelRect, AutoCrop), region-aware `Statistics`, `ChannelSource(_, region:)`, `Renderer.renderFull(region:)`, `PreviewView` crop layers, `InspectorView.cropSection` | CropTests, ProjectModelTests.testCropFlow/testAutoCrop, screenshots |
| F2 | Headless model tests (test target on the executable) | `Tests/CombineColorsTests` | `swift test` |
| F3 | Launch with files / "Open With" / `--args` (window must still open: `applicationShouldOpenUntitledFile`) | `App.swift` AppDelegate | manual launch |
| F4 | CLI `--crop`, `--save-project`, `--lum`, `--stars` | `combinecolors-cli/main.swift` | CLI runs on real data |
| F5 | Star image stretch detection (`StarsStretchMode` auto/none/autoSTF): already-stretched star-only images are not re-stretched | `Settings.swift`, `Renderer.starsImage` | Lowers stars render |
| F6 | Already-stretched channel detection → stretch set to none with a status hint | `ProjectModel.sourcesChanged` | Lowers TIFF launch |
| F7 | A/B snapshot compare (Store A / Show A / Restore A), Reset all settings | `ProjectModel.snapshotA/showingSnapshot`, `ContentView.previewToolbar` | PeekTests/Snapshot test, screenshots |
| F8 | Lenient (forward/backward compatible) decoding of every settings struct; channel maps encoded as JSON objects (`ChannelKind: CodingKeyRepresentable`) | `Pipeline/TolerantCoding.swift` | TolerantDecodingTests |
| F9 | Luminance recipes: on by default with Hα; presets Hα, L, 0.8/0.1/0.1, 0.7/0.3 HOO, 0.5/0.25/0.25, 0.5 L + 0.5 Hα, max/mean of lines, custom expression; missing channels dropped and weights renormalised, Hα → L fallback; post-combine luminance stretch derived from analysis samples (resolution independent) | `LuminancePreset`, `LuminanceSettings` in `Settings.swift`, `Renderer.evaluateLuminance`, `InspectorView.luminanceSection` | LuminancePresetTests, strip-vs-whole test |
| F10 | Recent channel sets: a group of files loaded together is one MRU entry (File ▸ Open Recent Set, start screen) | `Model/RecentSets.swift`, `ProjectModel.openRecentSet` | RecentSetsTests |
| F11 | Inspector split in two panes with independent scrolling (palettes above, controls below, draggable divider) | `InspectorView` (VSplitView) | screenshot |
| F12 | Hide palettes: eye button per thumbnail, Favourites / All switch, persisted in UserDefaults | `ProjectModel.hiddenPaletteIDs/paletteFilter`, `PaletteGridView` | PaletteVisibilityTests |
| F13 | Mono palettes skip luminance injection (would all look identical); then replaced by one **Monochrome** palette = weighted average of every loaded channel (Weight sliders, disabled channels dropped), `CombineMode.monochrome` + `monoChannels` subset | `Palettes.swift`, `Settings.swift`, `Renderer.combine` | MonoLuminanceTests, MonochromeModeTests |
| F15 | Colour bands (Camera Raw "HSL / selective colour"): per band (reds, oranges, yellows, greens, teals, blues, magentas) hue ±60°, saturation ±1, lightness ±1; band weights interpolate linearly between centres, fade for neutral pixels; applied after luminance, before the global Colour block | `ColorBand*` in `Settings.swift`, `PostProcess.colorBands`, `InspectorView.colorBandsSection` + `ColorBandRow`/`MiniSlider` | ColorBandsAndDetailTests |
| F16 | Detail: clarity (mid-tone local contrast, radius 1.5 % of width) and dehaze (large-scale haze subtraction, radius 12 %), pyramid blur with radii relative to the image width so levels/export agree; applied last; strip export renders strips without it then applies it to the assembled image | `DetailSettings`, `PostProcess.detail/pyramidBlur`, `RenderRequest.applyDetail`, `Renderer.renderFull` | ColorBandsAndDetailTests (strips == whole) |
| F17 | Inspector as an accordion: one block open at a time (Palette keeps its own state), compact headers with tinted bar + divider, order Palette · Channels · Stretch · Normalisation · Luminance · Colour bands · Colour · Detail · RGB stars · Crop · Output; vertical split removed | `SectionBox` (bound expansion), `InspectorView.body` | screenshot |
| F18 | Presets bar under the preview (replaces Store A / Show A): Save preset captures settings + thumbnail; click applies, active one highlighted; rename / update / delete via context menu; saved in the project file | `SettingsPreset`, `ProjectDocument.presets`, `ProjectModel.savePreset…`, `PresetBarView` | ProjectModelTests.testPresetsAndUndo, screenshot |
| F19 | Compact channel strip: 80 pt tiles, channel identified by a coloured frame + small badge instead of dot + label, slot menu as an overlay icon on the thumbnail, filename/stats moved to the tooltip; empty slots 96 pt wide with a dashed frame in the channel colour; strip height 142 → 90 pt | `ChannelSlotView` | screenshot |
| F20 | New window layout: channel column on the left (with the always-on histogram underneath), preview in the middle, palettes + preset row on the right, processing blocks in a horizontal strip at the bottom; each block has a vertical title that collapses/expands it independently (collapsed set remembered); Output block removed (HQ toggle in the preview toolbar, Export in the File menu ⌘E and toolbar, Reset all in the palette pane); "Save preset" removed from the preview toolbar | `ContentView`, `ControlsStripView` + `ControlBlock`, `PaletteGridView`, `ChannelStripView` | screenshot |
| F21 | Palette preview zoom slider (70–220 pt thumbnails, remembered) | `PaletteGridView` | screenshot |
| F22 | Rotate 90° clockwise (toolbar, Crop block, View menu ⌘R): rotates every channel, the crop and the thumbnails; statistics and star masks rebuilt; channels added later are rotated the same way; stored in the project | `PlanarImage.rotated90CW` (vImage), `PixelRect.rotated90CW`, `ProjectModel.rotateClockwise`, `ProjectDocument.rotation` | PlanarImageTests.testRotated90CW, ProjectModelTests.testRotateClockwise, PipelineTests (document round-trip) |
| F23 | Peek switching: keep the mouse button down on a channel slot and move over other slots to view each in turn | `ChannelStripView` column gesture | — |
| F24 | Colour-file dialog on drop: RGB or Dual-band (filter pair picker, live previews of both lines, Advanced weights/cross-talk, save split FITS); the standalone "Split dual-band…" entry points are gone | `ColorFileSheet` | — |
| F25 | Compact controls strip: no parameter summary in block headers, strip 204 pt tall with a gap under the preview, narrower blocks (slider labels 70 pt, value fields 46 pt), Stretch "Order" menu with short names ("Stretch first" / "Combine first"), Luminance and Detail texts trimmed; Colour bands shows one component at a time (Hue / Sat / Light segmented switch, value to the right of each slider); Crop block removed — Auto trim, Limits and Clear live on the preview toolbar and the crop numbers appear under Apply/Cancel while editing; right-click (or ⌃-click) on any slider resets it to its default | `ControlBlock`, `SliderRow` + `RightClickReset`, `ControlsStripView`, `ColorBandRow`/`MiniSlider`, `ContentView.previewToolbar`, `StretchStage.shortName` | screenshot |
| F14 | Press-and-hold a channel thumbnail → main preview shows that channel alone (grayscale, own auto-stretch), released → normal view; implemented as a temporary monochrome settings override (`peekSettings`) | `ProjectModel.peekChannel`, `ChannelStripView` gesture | PeekTests, screenshot |

### Corrections

| # | Problem | Fix |
|---|---------|-----|
| C1 | Preview layer drew over the channel strip | `layer.masksToBounds = true` on the zoom view |
| C2 | Launching with file arguments opened no window (SwiftUI treats them as documents) | `applicationShouldOpenUntitledFile` / `applicationShouldHandleReopen` return true |
| C3 | Nested sheet from the colour-file dialog never appeared | present the dual-band sheet from `ContentView` after a short delay |
| C4 | Project files saved by an older build failed to decode after a new field was added | lenient decoding (F8) |
| C5 | Toolbar controls with `.help()` tooltips ignored synthetic clicks (testing only, but labels are clearer anyway) | text labels on the preview toolbar |
| C6 | Numbers interpolated into SwiftUI `Text` picked up locale grouping ("6'104") | `Text(verbatim:)` / `String()` |
| C7 | Trackpad scrolling over menu-style `Picker`s changed their value | `MenuPicker` (Menu-based) for every drop-down in scrollable panes |
| C8 | "From" picker label wrapped onto two lines | `.fixedSize()` on the label |
| C9 | Status bar jumped between one and two lines while hovering (pixel read-out wrapped) | fixed 24 pt height, single truncating line, fixed-format read-out string |
| C10 | Unlinked per-channel auto-STF cancels normalisation and linear gain | default changed to linked; explanatory note in the Stretch section |
| C11 | Post-combine stretch parameters drifted with zoom level / strip | derived from full-resolution analysis samples (`Renderer.analysisSamples`) |
| C12 | `AutoCrop` over-trimmed sides touched by another side's border | grow-back pass after shrinking |
| C13 | Natural palette needed Hβ and was never available | Hβ term removed |
| C14 | Crop Apply/Cancel keyboard shortcuts lived only in the (possibly collapsed) Crop block | shortcuts also on the preview-toolbar buttons |
| C15 | Channel-strip thumbnails kept showing the whole frame after a crop | `ChannelSource.displayPlane` honours the source region (level chosen from the region size); `ProjectModel` rebuilds slot thumbnails on crop change and on load with an existing crop (DisplayPlaneTests, testCropFlow) |
| C16 | After a rotation, clicking a palette preview selected the one below: the portrait thumbnail drawn with `.fill` overflowed its cell (clipped visually, not for hit-testing) and the later cell caught the click | thumbnails are `allowsHitTesting(false)`, cells `clipped()` with an explicit content shape (palette grid and preset row) |
| C17 | Plain scrolling panned the preview; zooming needed ⌘ | scroll zooms around the pointer (pinch too); ⌘/⌥-scroll or dragging pans |

### Process rule

Whenever a feature or correction is made, add it to the tables above in the same
commit (see the project memory note).

## Status

* 98 tests pass (`swift test`): core, pipeline, luminance presets, monochrome, colour bands/detail, rotation, model flows, presets, recent sets, palette visibility, peek.
* Verified on the Soul Core FITS set (6 × 7272×5478 float32) and the Lowers Nebula
  16-bit TIFF set: loading ≈ 1 s for six channels, preview renders 1–30 ms, full-resolution
  Foraxx export ≈ 0.5 s, auto-crop 20–30 ms.
* UI verified by screenshots (launch, project load with crop, palette grid, histogram).

## Tests

* FITS: synthetic files for each BITPIX, BZERO/BSCALE, NAXIS3, ROWORDER; round trip write/read.
* TIFF: write 16-bit & 32-bit float, read back exact values.
* Channel detection from FITS FILTER keyword and file-name patterns.
* Stats: median/MAD vs known arrays, zero-padding exclusion, noise estimate on Gaussian noise.
* Pyramid: 2×2 box average correctness, odd sizes.
* Stretch: MTF identity at m=0.5, MTF(m, m)=0.5, STF reproduces reference numbers,
  arcsinh/GHS monotonic and bounded, LUT vs direct evaluation error < 1e-3.
* Normalisation: linear fit recovers synthetic a,b; background equalise aligns medians;
  noise weighting equalises sigma; histogram matching aligns percentiles.
* Pixel Math: precedence, unary, `~`, `^`, functions, variables aliases, error reporting,
  Foraxx formulas vs hand-computed values.
* Palettes: matrix mapping (SHO/HOO), pixel-math and matrix equivalence for pure permutations.
* Pipeline: end-to-end render on synthetic channels, pre/post stretch paths, post ops bounds.
* Dual-band split: synthetic OSC image splits into expected planes.
* Integration (skipped when data absent): load user FITS/TIFF, render every palette to PNG.
