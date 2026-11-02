import XCTest
@testable import AstroCore

final class GradientRemovalTests: XCTestCase {
    static let w = 480, h = 360
    static let sky: Float = 0.01, noise: Float = 0.0003

    /// Flat sky + Gaussian noise + a nebula blob (centred at `nebula`) + a grid of stars; the
    /// returned `flat` has no background structure (what a perfect correction gives back, up to a
    /// constant), `nebula` is the blob alone.
    private func scene(seed: UInt64 = 7, nebula nb: (x: Float, y: Float, s: Float, amp: Float)? = (200, 160, 60, 0.004)) -> (flat: PlanarImage, nebula: PlanarImage, stars: [(Int, Int)]) {
        let w = Self.w, h = Self.h
        let flat = PlanarImage(width: w, height: h), neb = PlanarImage(width: w, height: h)
        var rng = Synth.LCG(state: seed)
        var stars: [(Int, Int)] = []
        for sy in stride(from: 24, to: h, by: 48) { for sx in stride(from: 24, to: w, by: 48) { stars.append((sx + Int(rng.next() * 20) - 10, sy + Int(rng.next() * 20) - 10)) } }
        for y in 0..<h { for x in 0..<w {
            var v = Self.sky + Self.noise * rng.gaussian()
            if let nb {
                let dx = Float(x) - nb.x, dy = Float(y) - nb.y
                let n = nb.amp * exp(-(dx * dx + dy * dy) / (2 * nb.s * nb.s))
                neb[x, y] = n; v += n
            }
            for (sx, sy) in stars {
                let dx = Float(x - sx), dy = Float(y - sy), d2 = dx * dx + dy * dy
                if d2 < 30 { v += 0.2 * exp(-d2 / 2.5) }
            }
            flat[x, y] = v
        } }
        return (flat, neb, stars)
    }

    /// Elliptical vignetting (centre off the frame centre, rotated) as a transmission map, 1 at the
    /// centre: flat inside, falling steeply past the knee — the shape of mechanical vignetting,
    /// which no low-degree polynomial follows.
    private func vignetting(cx: Float = 200, cy: Float = 160, a: Float = 300, b: Float = 240, angle: Float = 0.2, depth: Float = 0.35) -> PlanarImage {
        let t = PlanarImage(width: Self.w, height: Self.h)
        let ct = cos(angle), st = sin(angle)
        for y in 0..<Self.h { for x in 0..<Self.w {
            let dx = Float(x) + 0.5 - cx, dy = Float(y) + 0.5 - cy
            let xr = (dx * ct + dy * st) / a, yr = (-dx * st + dy * ct) / b
            let r = (xr * xr + yr * yr).squareRoot()
            t[x, y] = max(0.3, 1 - depth * (1 - exp(-pow(r / 0.7, 6))))
        } }
        return t
    }

    private func multiply(_ a: PlanarImage, _ t: PlanarImage, plane: (Float, Float) = (0, 0)) -> PlanarImage {
        let out = PlanarImage(width: a.width, height: a.height)
        for y in 0..<a.height { for x in 0..<a.width { out[x, y] = a[x, y] * t[x, y] + plane.0 * Float(x) / Float(a.width) + plane.1 * Float(y) / Float(a.height) } }
        return out
    }

    /// RMS over the sky (stars and the nebula left out) of `a − b` after removing their mean
    /// difference, in units of the pixel noise.
    private func skyResidualSigma(_ a: PlanarImage, _ b: PlanarImage, stars: [(Int, Int)], exclude: PlanarImage? = nil) -> Float {
        var sum: Double = 0, sq: Double = 0, n = 0
        var nearStar = [Bool](repeating: false, count: a.width * a.height)
        for (sx, sy) in stars { for dy in -6...6 { for dx in -6...6 {
            let x = sx + dx, y = sy + dy
            if x >= 0, y >= 0, x < a.width, y < a.height { nearStar[y * a.width + x] = true }
        } } }
        for y in stride(from: 2, to: a.height - 2, by: 3) { for x in stride(from: 2, to: a.width - 2, by: 3) {
            if nearStar[y * a.width + x] { continue }
            if let ex = exclude, ex[x, y] > Self.noise * 0.5 { continue }
            let d = Double(a[x, y] - b[x, y]); sum += d; sq += d * d; n += 1
        } }
        let mean = sum / Double(n)
        return Float((sq / Double(n) - mean * mean).squareRoot()) / Self.noise
    }

    // MARK: Accuracy against the truth

    func testEllipseGuideBeatsThePolynomialOnVignetting() {
        let (flat, neb, stars) = scene()
        let t = vignetting()
        let shaded = multiply(flat, t, plane: (0.0015, -0.0008))
        let protect = [GradientRegion(x: 200, y: 160, a: 110, b: 110)]
        let before = skyResidualSigma(shaded, flat, stars: stars, exclude: neb)
        XCTAssertGreaterThan(before, 2.5, "the synthetic vignetting is far above the noise")
        // Guide drawn roughly right (centre 35 px off, round, no angle), refined.
        let spec = GradientSpec(gesture: .ellipse(center: CGPoint(x: 230, y: 180), a: 280, b: 280), mode: .subtract, residual: .plane, refine: true)
        guard let (guided, fit) = GradientRemoval.correct(shaded, spec: spec, protect: protect) else { return XCTFail("no fit") }
        let poly = GradientSpec(gesture: .polynomial(), mode: .subtract, residual: .none, degree: 4, refine: false)
        guard let (polyCorrected, _) = GradientRemoval.correct(shaded, spec: poly, protect: protect) else { return XCTFail("no polynomial fit") }
        // Compare against the truth with the nebula multiplied by the vignetting (subtract mode leaves it).
        let truth = PlanarImage(width: Self.w, height: Self.h)
        for y in 0..<Self.h { for x in 0..<Self.w { truth[x, y] = flat[x, y] - neb[x, y] + neb[x, y] * t[x, y] } }
        let guidedErr = skyResidualSigma(guided, truth, stars: stars, exclude: neb)
        let polyErr = skyResidualSigma(polyCorrected, truth, stars: stars, exclude: neb)
        XCTAssertLessThan(guidedErr, 1.2, "the guided model leaves the sky at the noise level")
        XCTAssertLessThan(guidedErr, polyErr * 0.8, "the guided model is closer to the truth than a 4th-degree polynomial (\(guidedErr) vs \(polyErr))")
        // The refinement found the centre and the axis ratio.
        XCTAssertEqual(fit.gesture.x, 200, accuracy: 25); XCTAssertEqual(fit.gesture.y, 160, accuracy: 25)
        XCTAssertEqual(fit.gesture.b / fit.gesture.a, 240 / 300, accuracy: 0.1)
        XCTAssertLessThan(fit.residualSigma, 1.5)
    }

    func testLineGuideRecoversAOneDimensionalGradient() {
        let (flat, neb, stars) = scene()
        let shaded = PlanarImage(width: Self.w, height: Self.h)
        let dx: Float = 0.92, dy: Float = 0.39   // direction of the gradient
        for y in 0..<Self.h { for x in 0..<Self.w {
            let u = (Float(x) * dx + Float(y) * dy) / 520
            shaded[x, y] = flat[x, y] + 0.006 / (1 + exp(-(u - 0.55) * 12)) + 0.001 * u
        } }
        // Drawn 15° off the true direction; the refinement turns it.
        let spec = GradientSpec(gesture: .line(from: CGPoint(x: 80, y: 220), to: CGPoint(x: 380, y: 220)), mode: .subtract, residual: .none, refine: true)
        guard let (corrected, fit) = GradientRemoval.correct(shaded, spec: spec, protect: [GradientRegion(x: 200, y: 160, a: 110, b: 110)]) else { return XCTFail("no fit") }
        let err = skyResidualSigma(corrected, flat, stars: stars, exclude: neb)
        XCTAssertLessThan(err, 1.1, "sky flat to the noise after the line model")
        let angle = atan2(fit.gesture.y2 - fit.gesture.y, fit.gesture.x2 - fit.gesture.x)
        XCTAssertEqual(angle, Double(atan2(dy, dx)), accuracy: 0.05, "the direction was refined to the gradient's")
        // Without refinement the wrong direction leaves a residual.
        var fixed = spec; fixed.refine = false
        let (uncorrected, _) = GradientRemoval.correct(shaded, spec: fixed)!
        XCTAssertGreaterThan(skyResidualSigma(uncorrected, flat, stars: stars, exclude: neb), err * 1.5)
    }

    func testProtectedRegionKeepsTheNebula() {
        let (flat, neb, stars) = scene(nebula: (240, 180, 70, 0.005))
        let t = vignetting(cx: 240, cy: 180)
        let shaded = multiply(flat, t)
        func nebulaLeft(_ img: PlanarImage) -> Float {
            // Excess at the nebula centre over the sky 250 px away, relative to the truth.
            var c: Float = 0, s: Float = 0, cn = 0, sn = 0
            for y in 0..<Self.h { for x in 0..<Self.w {
                let d = hypot(Float(x) - 240, Float(y) - 180)
                if d < 15 { c += img[x, y]; cn += 1 } else if d > 170, d < 185, x > 10, x < Self.w - 10 { s += img[x, y]; sn += 1 }
            } }
            return (c / Float(cn) - s / Float(sn)) / 0.005
        }
        let spec = GradientSpec(gesture: .ellipse(center: CGPoint(x: 240, y: 180), a: 300, b: 240), mode: .divide, residual: .none, refine: false)
        let unprotected = GradientRemoval.correct(shaded, spec: spec)!.image
        let protected = GradientRemoval.correct(shaded, spec: spec, protect: [GradientRegion(x: 240, y: 180, a: 160, b: 160)])!.image
        XCTAssertGreaterThan(nebulaLeft(protected), 0.85, "the protected nebula keeps its brightness")
        XCTAssertGreaterThan(nebulaLeft(protected), nebulaLeft(unprotected) + 0.1, "without protection the model eats part of the nebula")
        _ = stars
    }

    func testDivideRestoresStarFluxAndKeepsZeroPadding() {
        let (flat, _, stars) = scene(nebula: nil)
        let t = vignetting()
        let shaded = multiply(flat, t)
        // Zero padding on the left.
        for y in 0..<Self.h { for x in 0..<20 { shaded[x, y] = 0 } }
        let spec = GradientSpec(gesture: .ellipse(center: CGPoint(x: 200, y: 160), a: 300, b: 240, angle: 0.2), mode: .divide, residual: .none, refine: false)
        let out = GradientRemoval.correct(shaded, spec: spec)!.image
        func flux(_ img: PlanarImage, _ s: (Int, Int)) -> Float {
            var sum: Float = 0, bg: Float = 0, n = 0
            for dy in -4...4 { for dx in -4...4 { sum += img[s.0 + dx, s.1 + dy] } }
            for dy in -9...9 { for dx in -9...9 where max(abs(dx), abs(dy)) > 6 { bg += img[s.0 + dx, s.1 + dy]; n += 1 } }
            return sum - 81 * bg / Float(n)
        }
        let edge = stars.filter { $0.0 > 420 && $0.1 > 300 }.first!, centre = stars.min { hypot(Float($0.0 - 200), Float($0.1 - 160)) < hypot(Float($1.0 - 200), Float($1.1 - 160)) }!
        XCTAssertLessThan(flux(shaded, edge) / flux(shaded, centre), 0.8, "the corner star is dimmed by the vignetting")
        XCTAssertEqual(flux(out, edge) / flux(out, centre), 1, accuracy: 0.12, "divide mode gives the corner star its flux back")
        for y in stride(from: 0, to: Self.h, by: 7) { XCTAssertEqual(out[5, y], 0) }
        XCTAssertGreaterThan(out[25, 100], 0)
    }

    func testSectorsFollowAsymmetricVignetting() {
        let (flat, neb, stars) = scene()
        let t = vignetting()
        // Tilted fall-off: deeper on one side.
        for y in 0..<Self.h { for x in 0..<Self.w {
            let phi = atan2(Float(y) - 160, Float(x) - 200)
            t[x, y] = max(0.2, t[x, y] - 0.12 * (1 - t[x, y]) / 0.35 * cos(phi - 0.5))
        } }
        let shaded = multiply(flat, t)
        let protect = [GradientRegion(x: 200, y: 160, a: 110, b: 110)]
        let truth = PlanarImage(width: Self.w, height: Self.h)
        for y in 0..<Self.h { for x in 0..<Self.w { truth[x, y] = flat[x, y] - neb[x, y] + neb[x, y] * t[x, y] } }
        var spec = GradientSpec(gesture: .ellipse(center: CGPoint(x: 200, y: 160), a: 300, b: 240, angle: 0.2), mode: .subtract, residual: .none, refine: false)
        let round = skyResidualSigma(GradientRemoval.correct(shaded, spec: spec, protect: protect)!.image, truth, stars: stars, exclude: neb)
        spec.sectors = 8
        let sectored = skyResidualSigma(GradientRemoval.correct(shaded, spec: spec, protect: protect)!.image, truth, stars: stars, exclude: neb)
        XCTAssertLessThan(sectored, round * 0.8, "eight sectors follow the tilt the round profile cannot (\(sectored) vs \(round))")
        XCTAssertLessThan(sectored, 1.3)
    }

    func testSamplesLeaveOutStarsZerosAndProtectedCells() {
        let (flat, _, _) = scene(nebula: nil)
        for y in 0..<Self.h { for x in 0..<32 { flat[x, y] = 0 } }
        let s = GradientRemoval.samples(image: flat, protect: [GradientRegion(x: 400, y: 240, a: 100, b: 80)])
        XCTAssertGreaterThan(s.count, 5_000)
        XCTAssertTrue(s.x.allSatisfy { $0 > 24 }, "zero padding gives no samples")
        for i in 0..<s.count { XCTAssertFalse(GradientRegion(x: 400, y: 240, a: 100, b: 80).contains(x: Double(s.x[i]), y: Double(s.y[i]))) }
        // Cell medians of 16 pixels: noise ≈ 1.25 σ / 4.
        XCTAssertEqual(s.noise, Self.noise * 1.25 / 4, accuracy: Self.noise * 0.12)
        var sorted = s.v; sorted.sort()
        XCTAssertLessThan(Statistics.percentile(sorted: sorted, q: 0.999), Self.sky + 6 * Self.noise, "stars are masked out of the samples")
    }

    func testTooSmallOrDegenerateInputsGiveNoFit() {
        let tiny = PlanarImage(width: 12, height: 12, fill: 1)
        XCTAssertNil(GradientRemoval.correct(tiny, spec: GradientSpec(gesture: .polynomial())))
        let (flat, _, _) = scene(nebula: nil)
        XCTAssertNil(GradientRemoval.correct(flat, spec: GradientSpec(gesture: .line(from: CGPoint(x: 10, y: 10), to: CGPoint(x: 10, y: 10)))), "a zero-length line has no direction")
        XCTAssertTrue(GradientRemoval.correct(flat, kind: .ha, settings: GradientSettings()) === flat, "no model: the same instance")
    }

    // MARK: Geometry

    func testGesturesAndRegionsFollowTheOrientation() {
        let w = 90, h = 60
        let g = GradientGesture.ellipse(center: CGPoint(x: 20.5, y: 10.5), a: 30, b: 12, angle: 0.4)
        let l = GradientGesture.line(from: CGPoint(x: 5, y: 7), to: CGPoint(x: 60, y: 40))
        let r = GradientRegion(x: 33, y: 21, a: 9, b: 4)
        let img = PlanarImage(width: w, height: h)
        // A marker pixel and a second point: after any orientation the guide coordinate of the mapped points is unchanged.
        let probes: [(Float, Float)] = [(70.5, 50.5), (12.5, 33.5), (44.5, 5.5)]
        for (px, py) in probes { img[Int(px), Int(py)] = 1 }
        for rotation in 0..<4 { for flipped in [false, true] {
            let o = Orientation(rotation: rotation, flipped: flipped)
            let oriented = o.apply(to: img)
            let og = g.applying(o, imageWidth: w, imageHeight: h), ol = l.applying(o, imageWidth: w, imageHeight: h), orr = r.applying(o, imageWidth: w, imageHeight: h)
            for (px, py) in probes {
                // Find the marker in the oriented image.
                var found: (Int, Int)? = nil
                for y in 0..<oriented.height { for x in 0..<oriented.width where oriented[x, y] == 1 {
                    // Several markers: pick the one that maps from this probe by checking the others are elsewhere.
                    let q = (Float(x) + 0.5, Float(y) + 0.5)
                    let (u0, _) = GradientRemoval.coordinate(g, x: px, y: py), (u1, _) = GradientRemoval.coordinate(og, x: q.0, y: q.1)
                    let (v0, _) = GradientRemoval.coordinate(l, x: px, y: py), (v1, _) = GradientRemoval.coordinate(ol, x: q.0, y: q.1)
                    if abs(u0 - u1) < 1e-3 && abs(v0 - v1) < 1e-3 { found = (x, y) }
                } }
                XCTAssertNotNil(found, "probe \((px, py)) under rotation \(rotation) flipped \(flipped) keeps its guide coordinates")
                let inside0 = r.contains(x: Double(px), y: Double(py))
                if let f = found { XCTAssertEqual(orr.contains(x: Double(f.0) + 0.5, y: Double(f.1) + 0.5), inside0) }
            }
        } }
        // Step-wise ops compose like the orientation.
        var s = g, w2 = w, h2 = h
        s = s.applying(.flipHorizontal, imageWidth: w2, imageHeight: h2)
        s = s.applying(.rotateClockwise, imageWidth: w2, imageHeight: h2); swap(&w2, &h2)
        let once = g.applying(Orientation(rotation: 1, flipped: true), imageWidth: w, imageHeight: h)
        XCTAssertEqual(s.x, once.x, accuracy: 1e-9); XCTAssertEqual(s.y, once.y, accuracy: 1e-9); XCTAssertEqual(s.angle, once.angle, accuracy: 1e-9)
    }

    func testShapeConversionKeepsThePlace() {
        let l = GradientGesture.line(from: CGPoint(x: 10, y: 20), to: CGPoint(x: 40, y: 60))
        let e = l.converted(to: .ellipse)
        XCTAssertEqual(e.shape, .ellipse); XCTAssertEqual(e.x, 10); XCTAssertEqual(e.a, 50, accuracy: 1e-9); XCTAssertEqual(e.angle, atan2(40, 30), accuracy: 1e-9)
        let back = e.converted(to: .line)
        XCTAssertEqual(back.x2, 40, accuracy: 1e-9); XCTAssertEqual(back.y2, 60, accuracy: 1e-9)
        XCTAssertEqual(l.converted(to: .polynomial).shape, .polynomial)
    }

    // MARK: Persistence

    func testSettingsRoundTripAndOldDocuments() throws {
        var g = GradientSettings()
        g.specs[.ha] = GradientSpec(gesture: .ellipse(center: CGPoint(x: -200, y: 3000), a: 1500, b: 1200, angle: 0.3), mode: .divide, residual: .quadratic, sectors: 8, smoothness: 2, degree: 4, refine: false)
        g.specs[.oiii] = GradientSpec(gesture: .line(from: CGPoint(x: 1, y: 2), to: CGPoint(x: 300, y: 400)))
        g.protect = [GradientRegion(x: 100, y: 120, a: 50, b: 40)]
        g.shape = .line
        var doc = ProjectDocument()
        doc.gradient = g
        let data = try doc.data()
        let back = try JSONDecoder().decode(ProjectDocument.self, from: data)
        XCTAssertEqual(back.gradient, g)
        // Documents written before the feature have no "gradient" key.
        let old = try JSONDecoder().decode(ProjectDocument.self, from: Data("{\"version\":1,\"channels\":[]}".utf8))
        XCTAssertTrue(old.gradient.isEmpty); XCTAssertEqual(old.gradient.mode, .subtract)
        // A bare spec with only a gesture gets the defaults.
        let bare = try JSONDecoder().decode(GradientSpec.self, from: Data("{\"gesture\":{\"shape\":\"line\",\"x2\":10}}".utf8))
        XCTAssertEqual(bare.gesture.shape, .line); XCTAssertEqual(bare.gesture.x2, 10); XCTAssertEqual(bare.residual, .plane); XCTAssertTrue(bare.refine)
    }
}
