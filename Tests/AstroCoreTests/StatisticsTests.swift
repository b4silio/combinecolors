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

final class CropTests: XCTestCase {
    func testRegionRestrictsStatistics() {
        let img = PlanarImage(width: 100, height: 100, fill: 0.2)
        for y in 0..<100 { for x in 0..<50 { img[x, y] = 0.8 } }
        var o = StatsOptions(); o.region = PixelRect(x: 50, y: 0, width: 50, height: 100)
        XCTAssertEqual(Statistics.compute(image: img, options: o).median, 0.2)
        o.region = PixelRect(x: 0, y: 0, width: 50, height: 100)
        XCTAssertEqual(Statistics.compute(image: img, options: o).median, 0.8)
        XCTAssertEqual(Statistics.compute(image: img).median, 0.2, accuracy: 0.31) // mixed
        let m = Statistics.multiSample(images: [img], maxSamples: 1000, region: PixelRect(x: 0, y: 0, width: 50, height: 100))
        XCTAssertTrue(m[0].allSatisfy { $0 == 0.8 })
        XCTAssertNil(PixelRect(x: 200, y: 0, width: 10, height: 10).clamped(width: 100, height: 100))
        XCTAssertEqual(PixelRect(x: -5, y: -5, width: 20, height: 20).clamped(width: 100, height: 100), PixelRect(x: 0, y: 0, width: 15, height: 15))
        XCTAssertEqual(PixelRect(x: 3, y: 5, width: 10, height: 10).scaled(level: 2), PixelRect(x: 0, y: 1, width: 4, height: 3))
    }

    func testAutoCropFindsPaddedBorders() {
        let a = Synth.channel(width: 200, height: 150), b = Synth.channel(width: 200, height: 150, seed: 9)
        // Zero borders: a has 10 px left / 5 px top, b has 7 px right / 12 px bottom.
        for y in 0..<150 { for x in 0..<10 { a[x, y] = 0 } ; for x in 193..<200 { b[x, y] = 0 } }
        for x in 0..<200 { for y in 0..<5 { a[x, y] = 0 } ; for y in 138..<150 { b[x, y] = 0 } }
        let r = AutoCrop.detect(images: [a, b])!
        XCTAssertEqual(r, PixelRect(x: 10, y: 5, width: 183, height: 133))
        XCTAssertEqual(AutoCrop.detect(images: [Synth.channel(width: 50, height: 40)]), PixelRect.full(width: 50, height: 40))
    }

    func testChannelSourceRegionAndRenderFullCrop() throws {
        let ha = Synth.source(.ha), o3 = Synth.source(.oiii, scale: 0.5, seed: 2)
        let crop = PixelRect(x: 20, y: 10, width: 100, height: 80)
        let sources: [ChannelKind: ChannelSource] = [.ha: ChannelSource(ha, region: crop), .oiii: ChannelSource(o3, region: crop)]
        XCTAssertEqual(sources[.ha]!.region, crop)
        XCTAssertTrue(sources[.ha]!.pyramid === ha.pyramid)
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "hoo")!)
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        let full = try Renderer.renderFull(sources: sources, settings: s, context: ctx, postStretch: nil)!
        let cropped = try Renderer.renderFull(sources: sources, settings: s, context: ctx, postStretch: nil, region: crop, stripPixels: 100 * 7)!
        XCTAssertEqual(cropped.width, 100); XCTAssertEqual(cropped.height, 80)
        for (p, q) in zip(full.planes, cropped.planes) { XCTAssertLessThan(p.cropped(crop).maxAbsDifference(q), 1e-6) }
        let renamed = ChannelSource(ha, kind: .sii)
        XCTAssertEqual(renamed.kind, .sii); XCTAssertEqual(renamed.stats, ha.stats)
    }
}
