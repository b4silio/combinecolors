import XCTest
@testable import AstroCore

final class MetalDenoiseTests: XCTestCase {
    /// Noisy sky with a gradient, a faint nebula blob and a star (luminance plane).
    private func scene(w: Int = 160, h: Int = 120, noise: Float = 0.02, seed: UInt64 = 3) -> (PlanarImage, PlanarImage) {
        var rng = SeededRNG(seed: seed)
        let clean = PlanarImage(width: w, height: h), noisy = PlanarImage(width: w, height: h)
        for y in 0..<h { for x in 0..<w {
            let dx = Float(x - 50), dy = Float(y - 60), nx = Float(x - 110), ny = Float(y - 50)
            let v = 0.25 + 0.03 * Float(x) / Float(w) + 0.7 * exp(-(dx * dx + dy * dy) / 6) + 0.1 * exp(-(nx * nx + ny * ny) / 300)
            clean[x, y] = v
            noisy[x, y] = min(max(v + rng.gaussian() * noise, 0), 1)
        } }
        return (clean, noisy)
    }

    private func rms(_ a: PlanarImage, _ b: PlanarImage, background: Bool) -> Float {
        var s: Float = 0, n = 0
        for y in 0..<a.height { for x in 0..<a.width where ((x - 50) * (x - 50) + (y - 60) * (y - 60) > 100) == background {
            let d = a[x, y] - b[x, y]; s += d * d; n += 1
        } }
        return (s / Float(max(n, 1))).squareRoot()
    }

    override func tearDown() { MetalDenoise.tileSize = 1536; MetalDenoise.enabled = true; MetalDenoise.wienerStage = true }

    func testGPUNonLocalMeansMatchesTheCPU() throws {
        try XCTSkipUnless(MetalDenoise.isAvailable, "no Metal device")
        let (_, noisy) = scene()
        let sigma = Denoise.noiseSigma(noisy)
        let cpu = Denoise.nonLocalMeans(noisy, sigma: sigma, h: 1.2 * sigma, search: 5)
        let gpu = try XCTUnwrap(MetalDenoise.nonLocalMeans(noisy, sigma: sigma, h: 1.2 * sigma, search: 5))
        // Identical away from the borders (the CPU path extends edges slightly differently).
        let inner = gpu.cropped(x: 8, y: 8, width: gpu.width - 16, height: gpu.height - 16)
        XCTAssertLessThan(inner.maxAbsDifference(cpu.cropped(x: 8, y: 8, width: cpu.width - 16, height: cpu.height - 16)), 2e-4)
        XCTAssertLessThan(gpu.maxAbsDifference(cpu), 0.01)
        // Tiling with a tiny tile gives the same result as one tile.
        MetalDenoise.tileSize = 64
        let tiled = try XCTUnwrap(MetalDenoise.nonLocalMeans(noisy, sigma: sigma, h: 1.2 * sigma, search: 5))
        XCTAssertLessThan(tiled.maxAbsDifference(gpu), 1e-5)
    }

    func testBM3DReducesNoiseKeepsStarAndTilesSeamlessly() throws {
        try XCTSkipUnless(MetalDenoise.isAvailable, "no Metal device")
        let (clean, noisy) = scene()
        let sigma = Denoise.noiseSigma(noisy)
        let out = try XCTUnwrap(MetalDenoise.bm3d(noisy, sigma: sigma, amount: 1, search: 16))
        XCTAssertLessThan(rms(out, clean, background: true), rms(noisy, clean, background: true) * 0.4, "BM3D should cut the background noise by more than half")
        XCTAssertEqual(out[50, 60], clean[50, 60], accuracy: 0.05, "star peak kept")
        XCTAssertEqual(out[110, 50], clean[110, 50], accuracy: 0.02, "faint nebula kept")
        let (lo, hi) = out.minMax(); XCTAssertGreaterThan(lo, -0.01); XCTAssertLessThan(hi, 1.01)
        MetalDenoise.tileSize = 64
        let tiled = try XCTUnwrap(MetalDenoise.bm3d(noisy, sigma: sigma, amount: 1, search: 16))
        XCTAssertLessThan(tiled.maxAbsDifference(out), 1e-3)
    }

    func testBM3DKeepsStructureBetterThanTheOtherMethods() throws {
        try XCTSkipUnless(MetalDenoise.isAvailable, "no Metal device")
        let (clean, noisy) = scene(noise: 0.03)
        let sigma = Denoise.noiseSigma(noisy)
        let bm3d = try XCTUnwrap(MetalDenoise.bm3d(noisy, sigma: sigma, amount: 1, search: 16))
        let wav = Denoise.wavelets(noisy, sigma: sigma, amount: 1, layers: 3)
        let nlm = Denoise.nonLocalMeans(noisy, sigma: sigma, h: 1.5 * sigma, search: 5)
        // Around the star BM3D is the most faithful; on the flat sky it is within reach of the wavelets.
        XCTAssertLessThan(rms(bm3d, clean, background: false), rms(nlm, clean, background: false))
        XCTAssertLessThan(rms(bm3d, clean, background: false), rms(wav, clean, background: false) * 0.5)
        XCTAssertLessThan(rms(bm3d, clean, background: true), rms(wav, clean, background: true) * 1.4)
        // The Wiener stage keeps structure at least as well as the hard-thresholding estimate and
        // does not undo its noise reduction (on white noise both are limited by the window size).
        MetalDenoise.wienerStage = false
        let hard = try XCTUnwrap(MetalDenoise.bm3d(noisy, sigma: sigma, amount: 1, search: 16))
        MetalDenoise.wienerStage = true
        XCTAssertLessThanOrEqual(rms(bm3d, clean, background: false), rms(hard, clean, background: false) * 1.02)
        XCTAssertLessThan(rms(bm3d, clean, background: true), rms(hard, clean, background: true) * 1.15)
    }

    func testApplyUsesBM3DAndFallsBackWithoutMetal() {
        let (_, noisy) = scene()
        let img = RGBImage(r: noisy.copy(), g: noisy.copy(), b: noisy.copy())
        var d = DenoiseSettings(); d.method = .bm3d; d.amount = 0.8
        Denoise.apply(img, settings: d)
        XCTAssertLessThan(Denoise.noiseSigma(img.luma()), Denoise.noiseSigma(noisy) * 0.7)
        MetalDenoise.enabled = false
        let img2 = RGBImage(r: noisy.copy(), g: noisy.copy(), b: noisy.copy())
        Denoise.apply(img2, settings: d)
        XCTAssertLessThan(Denoise.noiseSigma(img2.luma()), Denoise.noiseSigma(noisy) * 0.7)
    }
}
