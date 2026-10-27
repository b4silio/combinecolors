import XCTest
@testable import AstroCore

final class NormalizationTests: XCTestCase {
    func testRobustLinearFitRecoversParametersWithOutliers() {
        var rng = Synth.LCG(state: 11)
        var xs: [Float] = [], ys: [Float] = []
        for i in 0..<5000 {
            let x = Float(i) / 5000
            var y = 2.5 * x + 0.1 + 0.001 * rng.gaussian()
            if i % 50 == 0 { y += 1 }   // outliers
            xs.append(x); ys.append(y)
        }
        let fit = Normalizer.robustLinearFit(x: xs, y: ys)
        XCTAssertEqual(fit.a, 2.5, accuracy: 0.01)
        XCTAssertEqual(fit.b, 0.1, accuracy: 0.002)
    }

    func testUniformLUTApplyMatchesEvaluate() {
        let table = (0..<1024).map { Float($0) / 1023 * 2 }
        let lut = UniformLUT(lo: 0.2, hi: 0.8, table: table, slopeBelow: 0.5, slopeAbove: 3)
        let xs: [Float] = [-1, 0, 0.2, 0.3, 0.5, 0.8, 1.0, 5]
        let img = PlanarImage(width: xs.count, height: 1, pixels: xs)
        lut.apply(to: img)
        for (x, y) in zip(xs, img.toArray()) { XCTAssertEqual(y, lut.evaluate(x), accuracy: 1e-4, "x=\(x)") }
        XCTAssertEqual(lut.evaluate(0.5), 1, accuracy: 1e-3)
        XCTAssertEqual(lut.evaluate(1.0), 2 + 3 * 0.2, accuracy: 1e-5)
        XCTAssertEqual(lut.evaluate(0), -0.1, accuracy: 1e-5)
    }

    func makeInputs() -> [NormalizationInput] {
        let ha = Synth.source(.ha, scale: 1, seed: 1)
        let o3 = Synth.source(.oiii, scale: 0.4, seed: 2)
        let s2 = Synth.source(.sii, scale: 0.25, seed: 3)
        return [ha, o3, s2].map(\.normalizationInput)
    }

    func testAllMethodsProduceSensibleTransforms() {
        let inputs = makeInputs()
        let ref = inputs[0]
        for method in NormalizationMethod.allCases {
            var s = NormalizationSettings(); s.method = method
            let t = Normalizer.transforms(settings: s, inputs: inputs)
            XCTAssertEqual(t[.ha], .identity, "\(method)")
            let o3 = t[.oiii]!
            let st = o3.transform(stats: inputs[1].maskedStats)
            switch method {
            case .none:
                XCTAssertEqual(o3, .identity)
            case .backgroundEqualize:
                XCTAssertEqual(st.background, ref.maskedStats.background, accuracy: 1e-4)
                if case .affine(let a, _) = o3 { XCTAssertEqual(a, 1) } else { XCTFail() }
            case .backgroundDispersion:
                XCTAssertEqual(st.background, ref.maskedStats.background, accuracy: 1e-4)
                XCTAssertEqual(st.backgroundSigma, ref.maskedStats.backgroundSigma, accuracy: 1e-4)
            case .backgroundSignal:
                XCTAssertEqual(st.background, ref.maskedStats.background, accuracy: 1e-4)
                XCTAssertEqual(st.percentile(0.995) - st.background, ref.maskedStats.percentile(0.995) - ref.maskedStats.background, accuracy: 1e-3)
            case .noiseWeighted:
                XCTAssertEqual(st.noise, ref.maskedStats.noise, accuracy: 1e-4)
            case .linearFit:
                if case .affine(let a, _) = o3 { XCTAssertEqual(a, 2.5, accuracy: 0.3) } else { XCTFail() }
            case .histogramMatch:
                for q in [0.1, 0.5, 0.9] { XCTAssertEqual(st.percentile(q), ref.maskedStats.percentile(q), accuracy: 0.002, "q=\(q)") }
            }
        }
    }

    func testReferenceSelection() {
        let inputs = makeInputs()
        var s = NormalizationSettings()
        XCTAssertEqual(Normalizer.referenceChannel(settings: s, available: inputs), .ha)
        s.reference = .sii
        XCTAssertEqual(Normalizer.referenceChannel(settings: s, available: inputs), .sii)
        s.reference = .luminance   // not available → automatic
        XCTAssertEqual(Normalizer.referenceChannel(settings: s, available: inputs), .ha)
        XCTAssertEqual(Normalizer.referenceChannel(settings: s, available: Array(inputs[1...])), .oiii)
    }

    func testRenderContextTransformsStats() {
        let ha = Synth.source(.ha), o3 = Synth.source(.oiii, scale: 0.5, seed: 5)
        var s = NormalizationSettings(); s.method = .backgroundDispersion
        let ctx = RenderContext.make(settings: s, sources: [.ha: ha, .oiii: o3])
        XCTAssertEqual(ctx.reference, .ha)
        XCTAssertEqual(ctx.normalizedMaskedStats[.oiii]!.background, ha.maskedStats.background, accuracy: 1e-4)
        XCTAssertGreaterThan(ctx.referenceBackgroundSigma, 0)
    }
}
