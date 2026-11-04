import XCTest
import Foundation
@testable import AstroCore

final class FineRotationTests: XCTestCase {
    /// A smooth picture with a few Gaussian blobs ("stars").
    func blobs(width: Int = 200, height: Int = 150, at points: [(Double, Double)], sigma: Double = 2.5) -> PlanarImage {
        let img = PlanarImage(width: width, height: height)
        for y in 0..<height { for x in 0..<width {
            var v = 0.05 + 0.02 * Double(x) / Double(width)
            for (px, py) in points {
                let dx = Double(x) + 0.5 - px, dy = Double(y) + 0.5 - py
                v += exp(-(dx * dx + dy * dy) / (2 * sigma * sigma))
            }
            img[x, y] = Float(v)
        } }
        return img
    }

    /// Intensity-weighted centre of the pixels above `threshold` within `r` of (`x`, `y`).
    func centroid(_ img: PlanarImage, near x: Double, _ y: Double, r: Int = 8, threshold: Float = 0.2) -> (Double, Double) {
        var sx = 0.0, sy = 0.0, sw = 0.0
        for j in max(0, Int(y) - r)..<min(img.height, Int(y) + r) { for i in max(0, Int(x) - r)..<min(img.width, Int(x) + r) {
            let v = Double(max(img[i, j] - threshold, 0))
            sx += v * (Double(i) + 0.5); sy += v * (Double(j) + 0.5); sw += v
        } }
        return (sx / sw, sy / sw)
    }

    func testZeroAngleIsACopy() {
        let img = blobs(at: [(50, 40)])
        let r = img.rotated(byDegrees: 0)
        XCTAssertFalse(r === img)
        XCTAssertEqual(r.toArray(), img.toArray())
    }

    /// The pixels move where `FineRotation.rotate` sends their coordinates (clockwise on screen).
    func testStarsLandWhereThePointsGo() {
        let stars = [(60.0, 40.0), (150.0, 100.0), (100.0, 75.0)]
        let img = blobs(at: stars)
        for deg in [7.5, -20.0, 33.0] {
            let r = img.rotated(byDegrees: deg)
            for (x, y) in stars {
                let (ex, ey) = FineRotation.rotate(x: x, y: y, degrees: deg, width: 200, height: 150)
                let (cx, cy) = centroid(r, near: ex, ey)
                XCTAssertEqual(cx, ex, accuracy: 0.15, "\(deg)° star \(x),\(y)")
                XCTAssertEqual(cy, ey, accuracy: 0.15, "\(deg)° star \(x),\(y)")
            }
        }
        // Clockwise on screen (y down): a point right of the centre moves down.
        let p = FineRotation.rotate(x: 150, y: 75, degrees: 10, width: 200, height: 150)
        XCTAssertGreaterThan(p.y, 75)
        // Corners that come in from outside are zero.
        XCTAssertEqual(img.rotated(byDegrees: 30)[0, 0], 0)
    }

    func testTurnAndBackKeepsTheMiddleAndNoRinging() {
        let img = blobs(at: [(100, 75), (80, 60), (120, 90)], sigma: 1.5)
        let back = img.rotated(byDegrees: 12).rotated(byDegrees: -12)
        var worst: Float = 0
        for y in 40..<110 { for x in 50..<150 { worst = max(worst, abs(back[x, y] - img[x, y])) } }
        XCTAssertLessThan(worst, 0.12, "two Lanczos passes keep the stars")
        // No value outside the input's range (the clamp stops the Lanczos overshoot).
        let r = img.rotated(byDegrees: 17)
        let (lo, hi) = (img.toArray().min()!, img.toArray().max()!)
        for v in r.toArray() where v != 0 { XCTAssertGreaterThanOrEqual(v, lo - 1e-6); XCTAssertLessThanOrEqual(v, hi + 1e-6) }
    }

    func testOrientationCarriesTheAngle() throws {
        let o = Orientation(rotation: 1, flipped: false, angle: 3.5)
        XCTAssertEqual(o.applying(.rotateClockwise).angle, 3.5)
        XCTAssertEqual(o.applying(.flipHorizontal).angle, -3.5)
        XCTAssertEqual(o.applying(.flipVertical).angle, -3.5)
        XCTAssertFalse(o.isIdentity)
        XCTAssertEqual(o.withoutAngle, Orientation(rotation: 1))
        // Old documents (no angle) still decode; the angle round-trips.
        let old = try JSONDecoder().decode(Orientation.self, from: Data(#"{"rotation": 2, "flipped": true}"#.utf8))
        XCTAssertEqual(old, Orientation(rotation: 2, flipped: true))
        let back = try JSONDecoder().decode(Orientation.self, from: JSONEncoder().encode(o))
        XCTAssertEqual(back, o)
        var doc = ProjectDocument()
        doc.orientation = o
        let reread = try JSONDecoder().decode(ProjectDocument.self, from: doc.data())
        XCTAssertEqual(reread.orientation, o)
        // A flip after a fine turn is the mirrored turn: the pixels agree with flip-then-turn(−θ).
        let img = blobs(at: [(60, 40), (140, 110)])
        let a = img.rotated(byDegrees: 9).flippedHorizontally()
        let b = img.flippedHorizontally().rotated(byDegrees: -9)
        var worst: Float = 0
        for y in 20..<130 { for x in 30..<170 { worst = max(worst, abs(a[x, y] - b[x, y])) } }
        XCTAssertLessThan(worst, 1e-3)
    }

    func testThingsWithCoordinatesTurnWithThePicture() {
        let spot = DustSpot(x: 150, y: 40, radius: 12)
        let s = spot.rotated(byDegrees: 20, imageWidth: 200, imageHeight: 150)
        let e = FineRotation.rotate(x: 150, y: 40, degrees: 20, width: 200, height: 150)
        XCTAssertEqual(s.x, e.x, accuracy: 1e-9); XCTAssertEqual(s.y, e.y, accuracy: 1e-9); XCTAssertEqual(s.radius, 12)
        XCTAssertEqual(s.id, spot.id)
        let g = GradientGesture.ellipse(center: CGPoint(x: 100, y: 75), a: 50, b: 30)
        let gr = g.rotated(byDegrees: 30, imageWidth: 200, imageHeight: 150)
        XCTAssertEqual(gr.angle, g.angle + .pi / 6, accuracy: 1e-12)
        XCTAssertEqual(gr.x, 100, accuracy: 1e-9); XCTAssertEqual(gr.y, 75, accuracy: 1e-9)
        // A protected region grows to the bounding box of its turned ellipse (never protects less).
        let r = GradientRegion(x: 100, y: 75, a: 40, b: 10).rotated(byDegrees: 90, imageWidth: 200, imageHeight: 150)
        XCTAssertEqual(r.a, 10, accuracy: 1e-9); XCTAssertEqual(r.b, 40, accuracy: 1e-9)
        let r45 = GradientRegion(x: 100, y: 75, a: 40, b: 10).rotated(byDegrees: 45, imageWidth: 200, imageHeight: 150)
        XCTAssertGreaterThan(r45.a, 10); XCTAssertGreaterThan(r45.b, 10)
        // A painted map turns its paint along and back again.
        let m = CorrectionMap(imageWidth: 200, imageHeight: 150)
        m.dab(x: 100, y: 75, radius: 20, amount: 1, hardness: 0.5)
        let o = Orientation(rotation: 1, flipped: true, angle: 8)
        let there = m.oriented(o), back = there.unoriented(o)
        XCTAssertEqual(back.imageWidth, 200); XCTAssertEqual(back.imageHeight, 150)
        let centre = back.plane[back.plane.width / 2, back.plane.height / 2], orig = m.plane[m.plane.width / 2, m.plane.height / 2]
        XCTAssertEqual(centre, orig, accuracy: 0.05)
    }
}
