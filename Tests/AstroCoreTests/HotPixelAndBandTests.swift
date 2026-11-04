import XCTest
@testable import AstroCore

final class HotPixelAndBandTests: XCTestCase {
    /// Background with noise, one isolated coloured spike and one small star (Gaussian, σ 1.2 px).
    func scene() -> RGBImage {
        let w = 64, h = 64
        var rng = Synth.LCG(state: 7)
        let img = RGBImage(width: w, height: h)
        for p in img.planes { for i in 0..<(w * h) { p.pointer[i] = 0.1 + 0.005 * rng.gaussian() } }
        img.r[20, 20] = 0.9                       // red hot pixel
        for y in 40..<50 { for x in 40..<50 {
            let d2 = Float((x - 45) * (x - 45) + (y - 45) * (y - 45))
            let v = 0.8 * exp(-d2 / (2 * 1.2 * 1.2))
            for p in img.planes { p[x, y] += v }
        } }
        return img
    }

    func testHotPixelsGoStarsStay() {
        let img = scene()
        let star = img.g[45, 45], starR = img.r[45, 45]
        Denoise.removeHotPixels(img, amount: 0.5)
        XCTAssertEqual(img.r[20, 20], 0.1, accuracy: 0.02, "the spike takes its neighbours' level")
        XCTAssertEqual(img.g[45, 45], star, accuracy: 1e-6, "the star core is kept")
        XCTAssertEqual(img.r[45, 45], starR, accuracy: 1e-6)
    }

    func testHotPixelsOnlyAtFullResolution() {
        var d = DenoiseSettings()
        d.hotPixels = 0.5
        XCTAssertFalse(d.isNeutral)
        let reduced = scene()
        Denoise.apply(reduced, settings: d, scale: 0.5)
        XCTAssertEqual(reduced.r[20, 20], 0.9, accuracy: 1e-6, "below 1:1 the filter does not run")
        let full = scene()
        Denoise.apply(full, settings: d, scale: 1)
        XCTAssertEqual(full.r[20, 20], 0.1, accuracy: 0.02)
        // Old projects decode with the filter off.
        let decoded = try! JSONDecoder().decode(DenoiseSettings.self, from: Data(#"{"amount":0.3}"#.utf8))
        XCTAssertEqual(decoded.hotPixels, 0)
    }

    func testBandSoftnessWidensAndSmoothsTheBands() {
        let centers = ColorBand.allCases.map(\.hue)
        let teal = ColorBand.allCases.firstIndex(of: .teals)!
        // Softness 0 is the original partition of unity.
        for h in stride(from: Float(0), to: 360, by: 7) {
            let sum = (0..<centers.count).reduce(Float(0)) { $0 + PostProcess.bandWeight(hue: h, index: $1, centers: centers) }
            XCTAssertEqual(sum, 1, accuracy: 1e-4)
        }
        XCTAssertEqual(PostProcess.bandWeight(hue: 120, index: teal, centers: centers), 0, accuracy: 1e-6, "teal ends at the greens' centre")
        XCTAssertGreaterThan(PostProcess.bandWeight(hue: 120, index: teal, centers: centers, softness: 1), 0.2, "soft teal reaches into the greens")
        XCTAssertEqual(PostProcess.bandWeight(hue: 180, index: teal, centers: centers, softness: 1), 1, accuracy: 1e-6)
        var last: Float = 2
        for h in stride(from: Float(180), through: 300, by: 5) {
            let w = PostProcess.bandWeight(hue: h, index: teal, centers: centers, softness: 0.6)
            XCTAssertLessThanOrEqual(w, last + 1e-6)
            last = w
        }
        // An edit on the teals now moves a green pixel too.
        var s = ColorBandsSettings()
        var a = ColorBandAdjust(); a.saturation = -1
        s.set(.teals, a)
        let hard = RGBImage(width: 1, height: 1), soft = RGBImage(width: 1, height: 1)
        for img in [hard, soft] { img.r[0, 0] = 0.2; img.g[0, 0] = 0.7; img.b[0, 0] = 0.35 }   // hue ≈ 138°
        PostProcess.colorBands(hard, settings: s)
        s.softness = 1
        PostProcess.colorBands(soft, settings: s)
        XCTAssertLessThan(abs(soft.g[0, 0] - soft.r[0, 0]), abs(hard.g[0, 0] - hard.r[0, 0]), "the softer band desaturates the green more")
    }
}
