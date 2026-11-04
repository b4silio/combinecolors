import XCTest
import Foundation
@testable import AstroCore

final class StretchTests: XCTestCase {
    func testMTFProperties() {
        for x: Float in [0, 0.1, 0.5, 0.9, 1] { XCTAssertEqual(Stretch.mtf(0.5, x), x, accuracy: 1e-6) }
        for m: Float in [0.01, 0.25, 0.5, 0.75] { XCTAssertEqual(Stretch.mtf(m, m), 0.5, accuracy: 1e-5) }
        XCTAssertEqual(Stretch.mtf(0.25, 0), 0)
        XCTAssertEqual(Stretch.mtf(0.25, 1), 1)
        // MTF(t, v) is the midtones parameter that maps v to t.
        let m = Stretch.mtf(0.25, 0.002)
        XCTAssertEqual(Stretch.mtf(m, 0.002), 0.25, accuracy: 1e-5)
    }

    func testAutoSTFMapsMedianToTarget() {
        let img = Synth.channel(width: 300, height: 200, background: 0.006, nebula: 0.001, stars: 0.3, noise: 0.00005)
        let stats = Statistics.compute(image: img)
        var s = StretchSettings()
        s.targetBackground = 0.25
        let r = Stretch.resolve(s, stats: stats)
        XCTAssertEqual(r.c0, stats.median - 2.8 * stats.madn, accuracy: 1e-7)
        XCTAssertEqual(r.evaluate(stats.median), 0.25, accuracy: 1e-4)
        XCTAssertEqual(r.evaluate(0), 0)
        XCTAssertEqual(r.evaluate(1), 1)
        // Vectorised application matches the scalar reference.
        let copy = img.copy()
        r.apply(to: copy)
        var maxErr: Float = 0
        for i in 0..<img.count { maxErr = max(maxErr, abs(copy.pointer[i] - r.evaluate(img.pointer[i]))) }
        XCTAssertLessThan(maxErr, 1e-4)
    }

    func testLinkedUsesAveragedStats() {
        let a = Statistics.fromSample([0.1, 0.2, 0.3]), b = Statistics.fromSample([0.5, 0.6, 0.7])
        let s = StretchSettings()
        let r = Stretch.resolveLinked(s, stats: [a, b])
        XCTAssertEqual(r.evaluate(0.4), s.targetBackground, accuracy: 1e-4)
    }

    /// Linear stacks of very different depth: the manual strengths are relative to the noise, so the
    /// default of every method puts the sky at a similar, sensible level on both.
    func testManualStrengthsAreRelativeToTheNoise() {
        func stack(sky: Float, noise: Float) -> ChannelStats {
            // Gaussian-ish sky (deterministic) plus a sparse bright tail.
            var v: [Float] = []
            for i in 0..<20000 {
                let u = (Float(i) + 0.5) / 20000
                let z = sqrt(2) * erfinv(2 * u - 1)
                v.append(max(sky + noise * z, 0))
            }
            for i in 0..<200 { v.append(min(sky + Float(i) * 0.004, 1)) }
            return Statistics.fromSample(v)
        }
        let deep = stack(sky: 0.006, noise: 0.0003), noisy = stack(sky: 0.05, noise: 0.01)
        for method in [StretchMethod.mtf, .arcsinh, .ghs, .log] {
            var s = StretchSettings(); s.method = method
            for st in [deep, noisy] {
                let sky = Stretch.resolve(s, stats: st).evaluate(st.median)
                XCTAssertGreaterThan(sky, 0.12, "\(method) sky too dark: \(sky)")
                XCTAssertLessThan(sky, 0.38, "\(method) sky too bright: \(sky)")
            }
            // Brighter with more strength (lower midtones for the MTF).
            var stronger = s
            switch method {
            case .mtf: stronger.mtfMidtonesSigma = 3
            case .arcsinh: stronger.arcsinhStrength = 10
            case .ghs: stronger.ghsStrength = 1
            default: stronger.logStrength = 10
            }
            XCTAssertGreaterThan(Stretch.resolve(stronger, stats: deep).evaluate(deep.median), Stretch.resolve(s, stats: deep).evaluate(deep.median), "\(method)")
        }
    }

    func testGHSPointsArePercentilesOfTheImage() {
        let st = Statistics.fromSample((0..<1001).map { Float($0) / 1000 * 0.02 })
        var s = StretchSettings(); s.method = .ghs; s.shadowsClipping = -6; s.ghsSPPercentile = 50; s.ghsHPPercentile = 100
        let r = Stretch.resolve(s, stats: st)
        let expected = (st.percentile(0.5) - r.c0) / (r.c1 - r.c0)
        XCTAssertEqual(r.ghsSP, expected, accuracy: 1e-5)
        XCTAssertEqual(r.ghsHP, 1)
        s.ghsLPPercentile = 80   // above SP: held at SP
        XCTAssertEqual(Stretch.resolve(s, stats: st).ghsLP, r.ghsSP, accuracy: 1e-6)
    }

    func testOtherMethodsMonotonicAndBounded() {
        let stats = Statistics.fromSample((0..<1000).map { Float($0) / 1000 })
        for method in [StretchMethod.arcsinh, .ghs, .log, .mtf] {
            var s = StretchSettings(); s.method = method; s.ghsStrength = 0.5; s.ghsB = 1.5; s.ghsSPPercentile = 40; s.ghsLPPercentile = 20; s.ghsHPPercentile = 90
            let r = Stretch.resolve(s, stats: stats)
            var prev: Float = -1
            for i in 0...200 {
                let x = Float(i) / 200
                let y = r.evaluate(x)
                XCTAssertGreaterThanOrEqual(y, prev - 1e-5, "\(method) not monotonic at \(x)")
                XCTAssertGreaterThanOrEqual(y, -1e-6); XCTAssertLessThanOrEqual(y, 1 + 1e-5)
                prev = y
            }
            XCTAssertEqual(r.evaluate(0), 0, accuracy: 1e-5)
            XCTAssertEqual(r.evaluate(1), 1, accuracy: 1e-5)
            // Vectorised vs scalar.
            let img = PlanarImage(width: 201, height: 1, pixels: (0...200).map { Float($0) / 200 })
            let ref = img.toArray().map { r.evaluate($0) }
            r.apply(to: img)
            for (a, b) in zip(img.toArray(), ref) { XCTAssertEqual(a, b, accuracy: 2e-4, "\(method)") }
        }
    }

    func testGHSDerivativeContinuityAtProtectionPoints() {
        let D: Float = 50, b: Float = 1, SP: Float = 0.2, LP: Float = 0.1, HP: Float = 0.8
        func f(_ x: Float) -> Float { Stretch.ghs(x, D: D, b: b, SP: SP, LP: LP, HP: HP) }
        let h: Float = 1e-3
        XCTAssertEqual((f(LP + h) - f(LP)) / h, (f(LP) - f(LP - h)) / h, accuracy: 0.5)
        XCTAssertEqual((f(HP + h) - f(HP)) / h, (f(HP) - f(HP - h)) / h, accuracy: 0.5)
        XCTAssertEqual(f(0), 0, accuracy: 1e-6); XCTAssertEqual(f(1), 1, accuracy: 1e-6)
    }

    func testGammaAndNone() {
        var s = StretchSettings(); s.method = .none; s.gamma = 0.5
        let r = Stretch.resolve(s, stats: ChannelStats())
        XCTAssertEqual(r.evaluate(0.25), 0.5, accuracy: 1e-6)
        let img = PlanarImage(width: 2, height: 1, pixels: [0.25, 1])
        r.apply(to: img)
        XCTAssertEqual(img[0, 0], 0.5, accuracy: 1e-6)
    }
}

/// Inverse error function (Giles 2010 single-precision approximation), for synthetic Gaussian samples.
private func erfinv(_ x: Float) -> Float {
    var w = -log((1 - x) * (1 + x)), p: Float
    if w < 5 {
        w -= 2.5
        p = 2.81022636e-08; p = 3.43273939e-07 + p * w; p = -3.5233877e-06 + p * w; p = -4.39150654e-06 + p * w
        p = 0.00021858087 + p * w; p = -0.00125372503 + p * w; p = -0.00417768164 + p * w; p = 0.246640727 + p * w; p = 1.50140941 + p * w
    } else {
        w = sqrt(w) - 3
        p = -0.000200214257; p = 0.000100950558 + p * w; p = 0.00134934322 + p * w; p = -0.00367342844 + p * w
        p = 0.00573950773 + p * w; p = -0.0076224613 + p * w; p = 0.00943887047 + p * w; p = 1.00167406 + p * w; p = 2.83297682 + p * w
    }
    return p * x
}
