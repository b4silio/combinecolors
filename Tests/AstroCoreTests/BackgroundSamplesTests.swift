import XCTest
@testable import AstroCore

final class BackgroundSamplesTests: XCTestCase {
    static let w = 480, h = 360
    static let sky: Float = 0.01, noise: Float = 0.0003

    /// Flat sky + noise + a nebula blob + stars; `flat` is the truth without background structure.
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

    /// Light pollution: a tilted plane (10 noise across the frame) plus, when asked, a compact glow
    /// in the top-right corner (a street lamp, the Moon just outside the frame).
    private func background(_ x: Int, _ y: Int, glow: Bool = true) -> Float {
        let fx = Float(x) / Float(Self.w), fy = Float(y) / Float(Self.h)
        var v = 0.003 * fx - 0.002 * fy
        if glow { let dx = fx - 1, dy = fy; v += 0.004 * exp(-(dx * dx + dy * dy) / (2 * 0.22 * 0.22)) }
        return v
    }

    private func add(_ a: PlanarImage, _ f: (Int, Int) -> Float) -> PlanarImage {
        let out = PlanarImage(width: a.width, height: a.height)
        for y in 0..<a.height { for x in 0..<a.width { out[x, y] = a[x, y] + f(x, y) } }
        return out
    }

    /// RMS of a − b over the sky (stars and nebula left out) after removing the mean difference, in noise units.
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

    func testGridLeavesOutTheNebulaAndCoversTheSky() {
        let (flat, _, _) = scene()
        let shaded = add(flat) { x, y in self.background(x, y, glow: false) }
        let s = GradientRemoval.samples(image: shaded)
        let radius = BackgroundSamples.defaultRadius(width: Self.w, height: Self.h)
        let grid = BackgroundSamples.grid(samples: s, perRow: 12, radius: radius, tolerance: 2.5)
        XCTAssertGreaterThan(grid.count, 50, "most of the 12 × 9 grid is sky")
        XCTAssertFalse(grid.contains { hypot($0.x - 200, $0.y - 160) < 80 }, "no sample on the nebula core or its bright skirt")
        // The gradient's bright and dark ends are kept: the sky model follows a planar gradient.
        XCTAssertTrue(grid.contains { $0.x > 400 && $0.y < 60 }, "the bright end of the gradient is still sky")
        XCTAssertTrue(grid.contains { $0.x < 80 && $0.y > 300 })
        // A compact corner glow is not planar: the grid drops it at the default tolerance (the user
        // adds samples there, or raises the tolerance), but keeps the rest.
        let glowing = add(flat) { x, y in self.background(x, y, glow: true) }
        let gs = GradientRemoval.samples(image: glowing)
        let g2 = BackgroundSamples.grid(samples: gs, perRow: 12, radius: radius, tolerance: 2.5)
        XCTAssertGreaterThan(g2.count, 45)
        XCTAssertFalse(g2.contains { $0.x > 430 && $0.y < 40 })
        let g3 = BackgroundSamples.grid(samples: gs, perRow: 12, radius: radius, tolerance: 60)
        XCTAssertTrue(g3.contains { $0.x > 430 && $0.y < 40 }, "a large tolerance keeps the glow")
        // A disc on padding gives no level; a disc on sky does.
        let padded = shaded.copy()
        for y in 0..<Self.h { for x in 0..<60 { padded[x, y] = 0 } }
        let ps = GradientRemoval.samples(image: padded)
        XCTAssertNil(BackgroundSamples.measure(samples: ps, x: 20, y: 100, radius: radius))
        XCTAssertNotNil(BackgroundSamples.measure(samples: ps, x: 300, y: 100, radius: radius))
    }

    func testSplineAndKrigingRecoverASmoothGradientAndKeepTheNebula() {
        let (flat, neb, stars) = scene()
        let shaded = add(flat) { x, y in self.background(x, y) }
        let before = skyResidualSigma(shaded, flat, stars: stars, exclude: neb)
        XCTAssertGreaterThan(before, 3)
        // The grid (tolerance 2.5) plus the samples a user would add in the glowing corner.
        let s = GradientRemoval.samples(image: shaded)
        let radius = BackgroundSamples.defaultRadius(width: Self.w, height: Self.h)
        let points = BackgroundSamples.grid(samples: s, perRow: 14, radius: radius, tolerance: 2.5)
            + [BackgroundSample(x: 462, y: 18), BackgroundSample(x: 430, y: 18), BackgroundSample(x: 462, y: 50), BackgroundSample(x: 400, y: 18), BackgroundSample(x: 462, y: 85), BackgroundSample(x: 425, y: 50)]
        for surface in SampleSurface.allCases {
            let spec = GradientSpec(gesture: .samples(), mode: .subtract, samples: points, surface: surface, sampleSmoothing: 0.5, autoSmoothing: false, gridPerRow: 14, gridTolerance: 2.5)
            guard let (corrected, fit) = GradientRemoval.correct(shaded, spec: spec) else { return XCTFail("no \(surface) fit") }
            let err = skyResidualSigma(corrected, flat, stars: stars, exclude: neb)
            XCTAssertLessThan(err, 1.2, "\(surface): sky flat to the noise (\(err)σ)")
            // The nebula is kept: under it and around it the correction is right to the noise level
            // (the surface bridges the hole the nebula leaves in the grid from samples on its faint skirt).
            var dc: Float = 0, dr: Float = 0, dcn = 0, drn = 0
            for y in 0..<Self.h { for x in 0..<Self.w {
                let d = hypot(Float(x) - 200, Float(y) - 160)
                if d < 10 { dc += corrected[x, y] - flat[x, y]; dcn += 1 } else if d > 150, d < 160 { dr += corrected[x, y] - flat[x, y]; drn += 1 }
            } }
            XCTAssertLessThan(abs(dc / Float(dcn)), 2.5 * Self.noise, "\(surface): the nebula centre keeps its brightness to the noise")
            XCTAssertLessThan(abs(dr / Float(drn)), 3 * Self.noise, "\(surface): the nebula's surroundings keep their level")
            XCTAssertGreaterThan(fit.sampleResults.count, 60)
            XCTAssertLessThan(fit.residualSigma, 2)
        }
    }

    func testSamplesAboveTheSkyAreRejectedByTheFit() {
        let (flat, neb, stars) = scene()
        let shaded = add(flat) { x, y in self.background(x, y, glow: false) }
        let s = GradientRemoval.samples(image: shaded)
        let radius = BackgroundSamples.defaultRadius(width: Self.w, height: Self.h)
        var points = BackgroundSamples.grid(samples: s, perRow: 12, radius: radius, tolerance: 2.5)
        // A user drops samples on the nebula: the asymmetric reweighting drops them instead of lifting the surface.
        let onNebula = [BackgroundSample(x: 200, y: 160), BackgroundSample(x: 215, y: 150)]
        points += onNebula
        guard let surface = BackgroundSamples.fit(points, radius: radius, samples: s, surface: .spline, smoothing: nil) else { return XCTFail("no fit") }
        for p in onNebula {
            let r = surface.results.first { $0.id == p.id }!
            XCTAssertTrue(r.isRejected, "sample on the nebula rejected (weight \(r.weight))")
        }
        let used = surface.results.filter { !$0.isRejected }.count
        XCTAssertGreaterThan(used, points.count - 6)
        // The two samples changed nothing: the surface is the one fitted without them.
        guard let alone = BackgroundSamples.fit(Array(points.dropLast(2)), radius: radius, samples: s, surface: .spline, smoothing: nil) else { return XCTFail("no fit") }
        XCTAssertEqual(surface.value(x: 200, y: 160), alone.value(x: 200, y: 160), accuracy: 0.5 * Self.noise, "samples on the nebula do not lift the surface")
        XCTAssertLessThan(surface.value(x: 200, y: 160), (surface.results.first { $0.id == onNebula[0].id }!.value ?? 1) - 0.5 * 0.004)
        _ = neb; _ = stars
    }

    func testSamplesSpecRoundTripAndOrientation() throws {
        var spec = GradientSpec(gesture: .samples(), mode: .divide, sampleRadius: 40, surface: .kriging, sampleSmoothing: 0.3, gridPerRow: 10, gridTolerance: 3)
        spec.samples = [BackgroundSample(x: 10, y: 20), BackgroundSample(x: 470, y: 350)]
        var settings = GradientSettings(); settings.specs[.ha] = spec; settings.surface = .kriging; settings.gridPerRow = 10
        let back = try JSONDecoder().decode(GradientSettings.self, from: try JSONEncoder().encode(settings))
        XCTAssertEqual(back, settings)
        let rotated = spec.applying(Orientation.Op.rotateClockwise, imageWidth: 480, imageHeight: 360)
        XCTAssertEqual(rotated.samples[0].x, 340); XCTAssertEqual(rotated.samples[0].y, 10)
        let flipped = spec.applying(Orientation.Op.flipHorizontal, imageWidth: 480, imageHeight: 360)
        XCTAssertEqual(flipped.samples[1].x, 10)
        // Old documents without the new keys decode to the defaults.
        let old = #"{"gesture":{"shape":"polynomial"},"mode":"subtract"}"#
        let o = try JSONDecoder().decode(GradientSpec.self, from: Data(old.utf8))
        XCTAssertEqual(o.surface, .spline); XCTAssertTrue(o.samples.isEmpty); XCTAssertEqual(o.gridPerRow, 14)
    }
}
