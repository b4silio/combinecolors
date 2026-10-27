import XCTest
@testable import AstroCore

final class StatisticsTests: XCTestCase {
    func testMedianMADPercentiles() {
        let s = Statistics.fromSample([5, 1, 3, 2, 4])
        XCTAssertEqual(s.median, 3)
        XCTAssertEqual(s.mad, 1)
        XCTAssertEqual(s.madn, 1.4826, accuracy: 1e-4)
        XCTAssertEqual(s.min, 1); XCTAssertEqual(s.max, 5)
        XCTAssertEqual(s.percentile(0), 1); XCTAssertEqual(s.percentile(1), 5)
        XCTAssertEqual(s.percentile(0.25), 2)
        let even = Statistics.fromSample([1, 2, 3, 4])
        XCTAssertEqual(even.median, 2.5)
    }

    func testMADTwoPointerMatchesNaive() {
        var rng = Synth.LCG(state: 42)
        for n in [1, 2, 3, 10, 101, 1000] {
            var a = (0..<n).map { _ in rng.gaussian() }
            a.sort()
            let med = Statistics.percentile(sorted: a, q: 0.5)
            let fast = Statistics.medianAbsoluteDeviation(sorted: a, center: med)
            let dev = a.map { abs($0 - med) }.sorted()
            let naive = Statistics.percentile(sorted: dev, q: 0.5)
            XCTAssertEqual(fast, naive, accuracy: 1e-6, "n=\(n)")
        }
    }

    func testZerosAreExcluded() {
        let img = PlanarImage(width: 10, height: 10, fill: 0.5)
        for x in 0..<5 { for y in 0..<10 { img[x, y] = 0 } }
        let s = Statistics.compute(image: img)
        XCTAssertEqual(s.median, 0.5)
        XCTAssertEqual(s.validFraction, 0.5, accuracy: 0.01)
    }

    func testNoiseEstimate() {
        let img = PlanarImage(width: 400, height: 300)
        var rng = Synth.LCG(state: 7)
        for i in 0..<img.count { img.pointer[i] = 0.5 + 0.01 * rng.gaussian() }
        let s = Statistics.compute(image: img)
        XCTAssertEqual(s.noise, 0.01, accuracy: 0.0015)
        XCTAssertEqual(s.madn, 0.01, accuracy: 0.0015)
        XCTAssertEqual(s.background, 0.5, accuracy: 0.001)
        XCTAssertEqual(s.backgroundSigma, 0.01, accuracy: 0.002)
    }

    func testBackgroundIgnoresBrightOutliers() {
        var values = [Float](repeating: 0.1, count: 10_000)
        var rng = Synth.LCG(state: 3)
        for i in values.indices { values[i] += 0.001 * rng.gaussian() }
        for i in 0..<500 { values[i] = 0.9 }   // stars
        let s = Statistics.fromSample(values)
        XCTAssertEqual(s.background, 0.1, accuracy: 0.0005)
        XCTAssertLessThan(s.backgroundSigma, 0.002)
    }

    func testMultiSampleAlignment() {
        let a = PlanarImage(width: 50, height: 40), b = PlanarImage(width: 50, height: 40)
        for i in 0..<a.count { a.pointer[i] = Float(i + 1); b.pointer[i] = Float(2 * (i + 1)) }
        a.pointer[0] = 0  // invalid in a only
        let s = Statistics.multiSample(images: [a, b], maxSamples: 500)
        XCTAssertEqual(s[0].count, s[1].count)
        XCTAssertGreaterThan(s[0].count, 100)
        for (x, y) in zip(s[0], s[1]) { XCTAssertEqual(y, 2 * x) }
    }

    func testHistogram() {
        let img = PlanarImage(width: 10, height: 10, fill: 0.5)
        let h = Statistics.histogram(image: img, bins: 4)
        XCTAssertEqual(h, [0, 0, 100, 0])
    }

    func testStatsAffineTransform() {
        let s = Statistics.fromSample([0.1, 0.2, 0.3, 0.4, 0.5])
        let t = s.affine(a: 2, b: 1)
        XCTAssertEqual(t.median, 1.6, accuracy: 1e-6)
        XCTAssertEqual(t.madn, s.madn * 2, accuracy: 1e-6)
        XCTAssertEqual(t.sortedSample, [1.2, 1.4, 1.6, 1.8, 2.0])
    }

    func testStarMaskCoversStars() {
        let img = Synth.channel(width: 256, height: 192, stars: 0.5, noise: 0.0005)
        let mask = StarMask.build(image: img)
        let cov = StarMask.coverage(mask)
        XCTAssertGreaterThan(cov, 0.01)
        XCTAssertLessThan(cov, 0.5)
        // Brightest pixel must be masked.
        var best = 0
        for i in 0..<img.count where img.pointer[i] > img.pointer[best] { best = i }
        XCTAssertEqual(mask.pointer[best], 1)
    }
}
