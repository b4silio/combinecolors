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

## Status

* 65 core tests + 13 model tests pass (`swift test`).
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
