# Changelog

Versions follow [Semantic Versioning](https://semver.org): a `feat` commit bumps the minor
version, a `fix` / `perf` / `refactor` / `docs` / `build` commit bumps the patch version
(`scripts/bump.sh`). The version lives in `VERSION` and is stamped into the app at build time;
`scripts/release.sh` tags it and publishes the DMG as a GitHub release.

## [1.2.0] – 2026-10-29

- Windows: GPU noise reduction through Direct3D 11 compute shaders (non-local means and BM3D, the Metal kernels in HLSL), CPU fallback kept

## [1.1.0] – 2026-10-29

- Windows port (windows/): the processing library, the app (WPF) and the CLI rewritten in C# on .NET 9, with the test suites; project files are interchangeable with the Mac app

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
