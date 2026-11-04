import XCTest
@testable import AstroCore

final class ToneTests: XCTestCase {
    let levels = PostSettings()

    func testNeutralCurveIsIdentityAndLevelsWorkAsBefore() {
        for x in stride(from: Float(0), through: 1, by: 0.05) {
            XCTAssertEqual(PostProcess.toneCurve(x, tone: ToneSettings(), levels: levels), x, accuracy: 1e-6)
        }
        var l = PostSettings(); l.blackPoint = 0.1; l.whitePoint = 0.9; l.gamma = 2
        XCTAssertEqual(PostProcess.toneCurve(0.1, tone: ToneSettings(), levels: l), 0, accuracy: 1e-6)
        XCTAssertEqual(PostProcess.toneCurve(0.5, tone: ToneSettings(), levels: l), 0.25, accuracy: 1e-5)
        XCTAssertEqual(PostProcess.toneCurve(0.95, tone: ToneSettings(), levels: l), 1, accuracy: 1e-6)
    }

    func testControlsMoveTheRightTonesAndStayMonotonic() {
        func curve(_ t: ToneSettings) -> (Float) -> Float { { PostProcess.toneCurve($0, tone: t, levels: self.levels) } }
        var t = ToneSettings()
        for (kp, v) in [(\ToneSettings.brightness, Float(1)), (\.brightness, -1), (\.contrast, 1), (\.contrast, -1),
                        (\.highlights, 1), (\.highlights, -1), (\.shadows, 1), (\.shadows, -1)] {
            t = ToneSettings(); t[keyPath: kp] = v
            let f = curve(t)
            var last: Float = -1
            for x in stride(from: Float(0), through: 1, by: 0.01) { XCTAssertGreaterThanOrEqual(f(x), last - 1e-6); last = f(x) }
            XCTAssertEqual(f(0), 0, accuracy: 1e-5); XCTAssertEqual(f(1), 1, accuracy: 1e-5)
        }
        t = ToneSettings(); t.brightness = 0.5
        XCTAssertGreaterThan(curve(t)(0.5), 0.55)
        t = ToneSettings(); t.contrast = 0.8
        XCTAssertLessThan(curve(t)(0.25), 0.22); XCTAssertGreaterThan(curve(t)(0.75), 0.78)
        t = ToneSettings(); t.shadows = 1
        XCTAssertGreaterThan(curve(t)(0.25), 0.35); XCTAssertEqual(curve(t)(0.75), 0.75, accuracy: 1e-6)
        t = ToneSettings(); t.highlights = -1
        XCTAssertLessThan(curve(t)(0.75), 0.65); XCTAssertEqual(curve(t)(0.25), 0.25, accuracy: 1e-6)
    }

    func testExposureAndDecoding() {
        let img = RGBImage(width: 2, height: 1)
        img.r[0, 0] = 0.2; img.g[0, 0] = 0.3; img.b[0, 0] = 0.6
        var t = ToneSettings(); t.exposure = 1
        PostProcess.applyTone(t, levels: PostSettings(), to: img)
        XCTAssertEqual(img.r[0, 0], 0.4, accuracy: 1e-6)
        XCTAssertEqual(img.b[0, 0], 1, accuracy: 1e-6, "clipped at white")
        let s = try! JSONDecoder().decode(CombineSettings.self, from: Data(#"{"post":{"gamma":1.2}}"#.utf8))
        XCTAssertTrue(s.tone.isNeutral, "projects from before the Contrast block decode neutral")
        XCTAssertEqual(s.post.gamma, 1.2, accuracy: 1e-6)
    }
}
