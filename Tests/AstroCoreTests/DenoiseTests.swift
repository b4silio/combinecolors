import XCTest
@testable import AstroCore

final class DenoiseTests: XCTestCase {
    /// Flat sky with Gaussian noise, a soft nebula gradient and a star.
    private func scene(noise: Float, seed: UInt64 = 1) -> (RGBImage, RGBImage) {
        let w = 128, h = 96
        var rng = SeededRNG(seed: seed)
        let clean = RGBImage(width: w, height: h), noisy = RGBImage(width: w, height: h)
        for y in 0..<h { for x in 0..<w {
            let dx = Float(x - 40), dy = Float(y - 48)
            let v = 0.25 + 0.15 * Float(x) / Float(w) + 0.7 * exp(-(dx * dx + dy * dy) / 6)
            let n = rng.gaussian() * noise
            clean.r[x, y] = v; clean.g[x, y] = v; clean.b[x, y] = v
            noisy.r[x, y] = v + n; noisy.g[x, y] = v + n + rng.gaussian() * noise * 0.5; noisy.b[x, y] = v + n
        } }
        noisy.clamp01()
        return (clean, noisy)
    }

    private func rms(_ a: PlanarImage, _ b: PlanarImage, where keep: (Int, Int) -> Bool) -> Float {
        var sum: Float = 0, n = 0
        for y in 0..<a.height { for x in 0..<a.width where keep(x, y) { let d = a[x, y] - b[x, y]; sum += d * d; n += 1 } }
        return (sum / Float(max(n, 1))).squareRoot()
    }

    func testNoiseEstimate() {
        let (_, noisy) = scene(noise: 0.02)
        let s = Denoise.noiseSigma(noisy.luma())
        XCTAssertEqual(s, 0.02, accuracy: 0.006)
    }

    func testEveryMethodReducesNoiseAndKeepsTheStar() {
        let (clean, noisy) = scene(noise: 0.02)
        let background: (Int, Int) -> Bool = { x, y in (x - 40) * (x - 40) + (y - 48) * (y - 48) > 100 }
        let before = rms(noisy.luma(), clean.luma(), where: background)
        for m in DenoiseMethod.allCases {
            let img = RGBImage(r: noisy.r.copy(), g: noisy.g.copy(), b: noisy.b.copy())
            var d = DenoiseSettings(); d.method = m; d.amount = 0.8; d.protectBright = 0.5
            Denoise.apply(img, settings: d)
            let after = rms(img.luma(), clean.luma(), where: background)
            XCTAssertLessThan(after, before * 0.6, "\(m) should cut the background noise")
            XCTAssertEqual(img.luma()[40, 48], noisy.luma()[40, 48], accuracy: 0.03, "\(m) must keep the star peak")
            for p in img.planes { let (lo, hi) = p.minMax(); XCTAssertGreaterThanOrEqual(lo, 0); XCTAssertLessThanOrEqual(hi, 1) }
        }
    }

    func testColourSmoothingKeepsLuminance() {
        let (_, noisy) = scene(noise: 0.02)
        let img = RGBImage(r: noisy.r.copy(), g: noisy.g.copy(), b: noisy.b.copy())
        var d = DenoiseSettings(); d.color = 1
        Denoise.apply(img, settings: d)
        XCTAssertLessThan(img.luma().maxAbsDifference(noisy.luma()), 0.01)
        // The green-only noise (chroma) is reduced.
        func chromaStd(_ i: RGBImage) -> Float {
            var s: Float = 0; for k in 0..<i.r.count { let d = i.g.pointer[k] - i.r.pointer[k]; s += d * d }; return (s / Float(i.r.count)).squareRoot()
        }
        XCTAssertLessThan(chromaStd(img), chromaStd(noisy) * 0.5)
    }

    func testNeutralAndDecoding() throws {
        XCTAssertTrue(DenoiseSettings().isNeutral)
        var d = DenoiseSettings(); d.color = 0.3; XCTAssertFalse(d.isNeutral)
        let decoded = try JSONDecoder().decode(DenoiseSettings.self, from: Data("{\"amount\": 0.4, \"method\": \"guided\"}".utf8))
        XCTAssertEqual(decoded.amount, 0.4); XCTAssertEqual(decoded.method, .guided); XCTAssertEqual(decoded.layers, 3)
        let old = try JSONDecoder().decode(CombineSettings.self, from: Data("{}".utf8))
        XCTAssertTrue(old.denoise.isNeutral)
    }

    func testDenoiseStripsMatchWholeRender() throws {
        let sources: [ChannelKind: ChannelSource] = [.ha: Synth.source(.ha), .oiii: Synth.source(.oiii, scale: 0.4, seed: 2)]
        let planes = sources.mapValues { $0.image }
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "hoo")!)
        s.denoise.amount = 0.7; s.denoise.color = 0.5
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        let samples = Renderer.analysisSamples(sources: sources)
        let whole = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: s, context: ctx))
        let strips = try Renderer.renderFull(sources: sources, settings: s, context: ctx, postStretch: whole.postStretch, stripPixels: 256 * 40)!
        for (p, q) in zip(whole.image.planes, strips.planes) { XCTAssertLessThan(p.maxAbsDifference(q), 1e-4) }
    }
}

/// Deterministic generator for the synthetic scenes.
struct SeededRNG: RandomNumberGenerator {
    var state: UInt64
    init(seed: UInt64) { state = seed &* 0x9E3779B97F4A7C15 | 1 }
    mutating func next() -> UInt64 {
        state ^= state << 13; state ^= state >> 7; state ^= state << 17
        return state
    }
    mutating func gaussian() -> Float {
        let u1 = max(Float(next() % 1_000_000) / 1_000_000, 1e-6), u2 = Float(next() % 1_000_000) / 1_000_000
        return (-2 * log(u1)).squareRoot() * cos(2 * .pi * u2)
    }
}
