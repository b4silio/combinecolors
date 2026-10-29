import XCTest
@testable import AstroCore

final class DustRemovalTests: XCTestCase {
    /// Scene with a gentle gradient, nebula blobs and stars (low noise so residuals are measurable).
    private func scene(width: Int = 420, height: Int = 320, noise: Float = 0.0002) -> PlanarImage {
        Synth.channel(width: width, height: height, background: 0.01, nebula: 0.0015, stars: 0.3, noise: noise, seed: 11, gradient: 0.002)
    }

    /// Multiplies a scene by a soft disc shadow and a donut.
    private func shade(_ truth: PlanarImage, disc: (Float, Float, Float, Float), donut: (Float, Float, Float, Float, Float)?) -> PlanarImage {
        let img = truth.copy()
        for y in 0..<img.height { for x in 0..<img.width {
            let px = Float(x) + 0.5, py = Float(y) + 0.5
            var t: Float = 1
            let d = hypot(px - disc.0, py - disc.1)
            t *= 1 - disc.3 * exp(-pow(d / disc.2, 4))
            if let dn = donut {
                let r = hypot(px - dn.0, py - dn.1)
                t *= 1 - dn.4 * exp(-pow((r - dn.2) / dn.3, 2))
            }
            img[x, y] = truth[x, y] * t
        } }
        return img
    }

    /// Relative error of `a` against `truth` averaged over 8×8 blocks inside a disc, stars excluded.
    private func blockError(_ a: PlanarImage, _ truth: PlanarImage, cx: Float, cy: Float, radius: Float) -> (median: Float, max: Float) {
        var errs: [Float] = []
        var by = 0
        while by + 8 <= a.height {
            var bx = 0
            while bx + 8 <= a.width {
                var sa: Float = 0, st: Float = 0, n = 0
                var inside = true, star = false
                for y in by..<(by + 8) { for x in bx..<(bx + 8) {
                    if hypot(Float(x) + 0.5 - cx, Float(y) + 0.5 - cy) > radius { inside = false }
                    if truth[x, y] > 0.03 { star = true }
                    sa += a[x, y]; st += truth[x, y]; n += 1
                } }
                if inside && !star { errs.append(abs(sa / st - 1)) }
                bx += 8
            }
            by += 8
        }
        errs.sort()
        return (errs.isEmpty ? 0 : errs[errs.count / 2], errs.last ?? 0)
    }

    func testRadialModelRestoresDiscAndDonut() {
        let truth = scene()
        let shaded = shade(truth, disc: (150, 120, 40, 0.15), donut: (310, 210, 28, 7, 0.25))
        let before1 = blockError(shaded, truth, cx: 150, cy: 120, radius: 36)
        let before2 = blockError(shaded, truth, cx: 310, cy: 210, radius: 40)
        XCTAssertGreaterThan(before1.median, 0.1); XCTAssertGreaterThan(before2.max, 0.1)
        let spots = [DustSpot(x: 150, y: 120, radius: 55, model: .radial, smoothness: 3, feather: 0.15), DustSpot(x: 310, y: 210, radius: 48, model: .radial, smoothness: 3, feather: 0.15)]
        let healed = DustRemoval.heal(shaded, spots: spots)
        XCTAssertFalse(healed === shaded)
        let after1 = blockError(healed, truth, cx: 150, cy: 120, radius: 36)
        let after2 = blockError(healed, truth, cx: 310, cy: 210, radius: 40)
        XCTAssertLessThan(after1.median, 0.012, "disc median error"); XCTAssertLessThan(after1.max, 0.03, "disc max error")
        XCTAssertLessThan(after2.median, 0.015, "donut median error"); XCTAssertLessThan(after2.max, 0.04, "donut max error")
        // Nothing changes beyond the spots.
        for y in 0..<truth.height { for x in 0..<truth.width {
            let out1 = hypot(Float(x) + 0.5 - 150, Float(y) + 0.5 - 120) > 55, out2 = hypot(Float(x) + 0.5 - 310, Float(y) + 0.5 - 210) > 48
            if out1 && out2 { XCTAssertEqual(healed[x, y], shaded[x, y], "(\(x), \(y))"); if healed[x, y] != shaded[x, y] { return } }
        } }
    }

    func testStarsUnderTheSpotKeepTheirBrightness() throws {
        let truth = scene()
        // Brightest star inside the disc.
        var best: (Int, Int, Float) = (0, 0, 0)
        for y in 90..<150 { for x in 120..<180 where truth[x, y] > best.2 && hypot(Float(x) - 150, Float(y) - 120) < 28 { best = (x, y, truth[x, y]) } }
        guard best.2 > 0.05 else { throw XCTSkip("no star under the test spot") }
        let shaded = shade(truth, disc: (150, 120, 40, 0.2), donut: nil)
        let healed = DustRemoval.heal(shaded, spots: [DustSpot(x: 150, y: 120, radius: 55)])
        XCTAssertEqual(healed[best.0, best.1] / best.2, 1, accuracy: 0.05, "star peak restored by the same gain as the sky")
    }

    func testSurfaceModelFollowsAnEllipticalMote() {
        let truth = scene()
        let img = truth.copy()
        for y in 0..<img.height { for x in 0..<img.width {
            let dx = (Float(x) + 0.5 - 320) / 36, dy = (Float(y) + 0.5 - 240) / 18
            img[x, y] = truth[x, y] * (1 - 0.2 * exp(-pow(dx * dx + dy * dy, 2)))
        } }
        let radial = DustRemoval.heal(img, spots: [DustSpot(x: 320, y: 240, radius: 62, model: .radial, smoothness: 3, feather: 0.15)])
        // A sharp-edged mote wants little smoothing (the Gaussian blurs the edge of the shadow estimate).
        let surface = DustRemoval.heal(img, spots: [DustSpot(x: 320, y: 240, radius: 62, model: .surface, smoothness: 2.5, feather: 0.15)])
        let er = blockError(radial, truth, cx: 320, cy: 240, radius: 40), es = blockError(surface, truth, cx: 320, cy: 240, radius: 40)
        XCTAssertLessThan(es.median, 0.015); XCTAssertLessThan(es.max, 0.04)
        XCTAssertLessThan(es.max, er.max, "the surface model must beat the radial one on an elongated mote")
    }

    func testFitFindsCentreAndExtent() {
        let truth = scene()
        let shaded = shade(truth, disc: (150, 120, 40, 0.12), donut: (310, 210, 28, 7, 0.25))
        // Click off-centre on the disc with a small starting radius.
        let s1 = DustRemoval.fit(image: shaded, x: 158, y: 113, radius: 15)
        XCTAssertNotNil(s1)
        if let s = s1 {
            XCTAssertEqual(s.x, 150, accuracy: 3); XCTAssertEqual(s.y, 120, accuracy: 3)
            XCTAssertGreaterThan(s.radius, 42); XCTAssertLessThan(s.radius, 62)
        }
        // Donut: click near the centre where the shadow is weak.
        let s2 = DustRemoval.fit(image: shaded, x: 306, y: 214, radius: 20)
        XCTAssertNotNil(s2)
        if let s = s2 {
            XCTAssertEqual(s.x, 310, accuracy: 5); XCTAssertEqual(s.y, 210, accuracy: 5)
            XCTAssertGreaterThan(s.radius, 38); XCTAssertLessThan(s.radius, 56)
        }
        // Nothing on plain sky (without nebulosity: a nebular bump next to the click is a shallow
        // "shadow" to the fit, which only matters for the radius it proposes).
        let plain = Synth.channel(width: 300, height: 200, background: 0.01, nebula: 0, stars: 0.3, noise: 0.0002, seed: 5, gradient: 0.002)
        XCTAssertNil(DustRemoval.fit(image: plain, x: 140, y: 100, radius: 20))
    }

    func testSpotsAtTheBorderAndOutsideAreHandled() {
        let truth = scene()
        let shaded = shade(truth, disc: (12, 160, 30, 0.15), donut: nil)
        let healed = DustRemoval.heal(shaded, spots: [DustSpot(x: 12, y: 160, radius: 42), DustSpot(x: -500, y: 10, radius: 20)])
        XCTAssertLessThan(blockError(healed, truth, cx: 12, cy: 160, radius: 26).median, 0.03)
        let z = PlanarImage(width: 64, height: 64)
        XCTAssertFalse(DustRemoval.heal(inPlace: z, spot: DustSpot(x: 32, y: 32, radius: 10)), "all-zero window has no background")
    }

    func testOrientationMapsSpotsLikePixels() {
        let w = 9, h = 5
        for flip in [false, true] { for rot in 0..<4 {
            let o = Orientation(rotation: rot, flipped: flip)
            let img = PlanarImage(width: w, height: h)
            img[6, 1] = 1
            let out = o.apply(to: img)
            let s = DustSpot(x: 6.5, y: 1.5, radius: 2).applying(o, imageWidth: w, imageHeight: h)
            XCTAssertEqual(out[Int(s.x), Int(s.y)], 1, "orientation \(rot)/\(flip): spot must land on the moved pixel")
            XCTAssertEqual(s.radius, 2)
        } }
        // Step-wise ops agree with the composed orientation.
        var s = DustSpot(x: 6.5, y: 1.5, radius: 2)
        var ww = w, hh = h
        s = s.applying(.flipHorizontal, imageWidth: ww, imageHeight: hh)
        s = s.applying(.rotateClockwise, imageWidth: ww, imageHeight: hh); swap(&ww, &hh)
        s = s.applying(.flipVertical, imageWidth: ww, imageHeight: hh)
        let o = Orientation.identity.applying(.flipHorizontal).applying(.rotateClockwise).applying(.flipVertical)
        let c = DustSpot(x: 6.5, y: 1.5, radius: 2, id: s.id).applying(o, imageWidth: w, imageHeight: h)
        XCTAssertEqual(s, c)
    }

    func testDocumentRoundTripAndDefaults() throws {
        var doc = ProjectDocument()
        doc.dust.spots[.oiii] = [DustSpot(x: 10.5, y: 20.25, radius: 33, model: .radial, smoothness: 6, feather: 0.1)]
        doc.dust.model = .radial
        doc.dust.smoothness = 6
        let back = try JSONDecoder().decode(ProjectDocument.self, from: doc.data())
        XCTAssertEqual(back.dust, doc.dust)
        let old = try JSONDecoder().decode(ProjectDocument.self, from: Data("{\"version\":1}".utf8))
        XCTAssertTrue(old.dust.isEmpty); XCTAssertEqual(old.dust.model, .surface)
        let bare = try JSONDecoder().decode(DustSpot.self, from: Data("{\"x\":1,\"y\":2,\"radius\":3}".utf8))
        XCTAssertEqual(bare.model, .surface); XCTAssertEqual(bare.smoothness, 3); XCTAssertEqual(bare.feather, 0.2)
    }
}

extension DustRemovalTests {
    /// Two touching shadows: each background fit must ignore the other spot or both end up biased.
    func testNeighbouringSpotsAreExcludedFromEachOthersBackground() {
        let truth = Synth.channel(width: 420, height: 320, background: 0.01, nebula: 0, stars: 0.3, noise: 0.0002, seed: 11, gradient: 0.002)
        let img = truth.copy()
        for y in 0..<img.height { for x in 0..<img.width {
            let px = Float(x) + 0.5, py = Float(y) + 0.5
            let t1 = 1 - 0.1 * exp(-pow(hypot(px - 150, py - 160) / 40, 4)), t2 = 1 - 0.1 * exp(-pow(hypot(px - 250, py - 160) / 40, 4))
            img[x, y] = truth[x, y] * t1 * t2
        } }
        let spots = [DustSpot(x: 150, y: 160, radius: 50), DustSpot(x: 250, y: 160, radius: 50)]
        let healed = DustRemoval.heal(img, spots: spots)
        var errs: [Float] = []
        for y in 130..<190 { for x in 120..<280 where truth[x, y] < 0.03 && (hypot(Float(x) + 0.5 - 150, Float(y) + 0.5 - 160) < 35 || hypot(Float(x) + 0.5 - 250, Float(y) + 0.5 - 160) < 35) {
            errs.append(abs(healed[x, y] / truth[x, y] - 1))
        } }
        errs.sort()
        XCTAssertLessThan(errs[errs.count / 2], 0.025, "median error under the pair")
        // Healing one alone, with the other spot not excluded, is clearly worse.
        let alone = img.copy()
        DustRemoval.heal(inPlace: alone, spot: spots[0])
        var e2: [Float] = []
        for y in 130..<190 { for x in 120..<180 where truth[x, y] < 0.03 && hypot(Float(x) + 0.5 - 150, Float(y) + 0.5 - 160) < 35 { e2.append(abs(alone[x, y] / truth[x, y] - 1)) } }
        e2.sort()
        var e1: [Float] = []
        for y in 130..<190 { for x in 120..<180 where truth[x, y] < 0.03 && hypot(Float(x) + 0.5 - 150, Float(y) + 0.5 - 160) < 35 { e1.append(abs(healed[x, y] / truth[x, y] - 1)) } }
        e1.sort()
        XCTAssertLessThan(e1[e1.count / 2], e2[e2.count / 2])
    }
}

extension DustRemovalTests {
    /// With a small smoothing scale the noise must pass through the correction unchanged: the
    /// transmission map keeps the shadow's structure but not the noise, so the grain under the spot
    /// matches the sky around it.
    func testLocalFlatKeepsTheNoiseGrain() {
        let noise: Float = 0.0005
        let truth = Synth.channel(width: 420, height: 320, background: 0.01, nebula: 0, stars: 0.3, noise: noise, seed: 21, gradient: 0.001)
        let img = truth.copy()
        for y in 0..<img.height { for x in 0..<img.width {
            let d = hypot(Float(x) + 0.5 - 200, Float(y) + 0.5 - 160)
            img[x, y] = truth[x, y] * (1 - 0.05 * exp(-pow(d / 50, 4)))
        } }
        let healed = DustRemoval.heal(img, spots: [DustSpot(x: 200, y: 160, radius: 70, model: .surface, smoothness: 1, feather: 0.2)])
        var res: [Float] = [], tru: [Float] = []
        for y in 130..<190 { for x in 170..<230 where truth[x, y] < 0.03 { res.append(healed[x, y] - truth[x, y]); tru.append(truth[x, y]) }
        }
        func std(_ v: [Float]) -> Float { let m = v.reduce(0, +) / Float(v.count); return (v.map { ($0 - m) * ($0 - m) }.reduce(0, +) / Float(v.count)).squareRoot() }
        // Plain σ = 1 smoothing would leave ≈ 28 % of the noise std in the residual (its coarse part).
        XCTAssertLessThan(std(res), 0.12 * noise, "residual against the truth, i.e. the part of the grain the correction ate")
        XCTAssertEqual(std(tru) > 0 ? std(res) / std(tru) : 1, 0, accuracy: 0.12)
    }
}
