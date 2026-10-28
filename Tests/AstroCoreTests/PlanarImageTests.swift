import XCTest
@testable import AstroCore

final class PlanarImageTests: XCTestCase {
    func testDownsampleAveragesBlocks() {
        let img = PlanarImage(width: 4, height: 2, pixels: [1, 3, 5, 7, 2, 4, 6, 8])
        let d = img.downsampled2x()
        XCTAssertEqual(d.width, 2); XCTAssertEqual(d.height, 1)
        XCTAssertEqual(d[0, 0], 2.5, accuracy: 1e-6)
        XCTAssertEqual(d[1, 0], 6.5, accuracy: 1e-6)
    }

    func testDownsampleOddSizesReplicateEdges() {
        let img = PlanarImage(width: 3, height: 3, pixels: [1, 2, 3, 4, 5, 6, 7, 8, 9])
        let d = img.downsampled2x()
        XCTAssertEqual(d.width, 2); XCTAssertEqual(d.height, 2)
        XCTAssertEqual(d[0, 0], 3, accuracy: 1e-6)          // (1+2+4+5)/4
        XCTAssertEqual(d[1, 0], 4.5, accuracy: 1e-6)        // (3+3+6+6)/4
        XCTAssertEqual(d[1, 1], 9, accuracy: 1e-6)          // (9+9+9+9)/4
    }

    func testCropAndFlip() {
        let img = PlanarImage(width: 4, height: 3, pixels: (0..<12).map(Float.init))
        let c = img.cropped(x: 1, y: 1, width: 2, height: 2)
        XCTAssertEqual(c.toArray(), [5, 6, 9, 10])
        img.flipVertically()
        XCTAssertEqual(img.toArray().prefix(4).map { $0 }, [8, 9, 10, 11])
        let clamped = img.cropped(x: -5, y: -5, width: 100, height: 100)
        XCTAssertEqual(clamped.width, 4); XCTAssertEqual(clamped.height, 3)
    }

    func testPyramidLevels() {
        let img = PlanarImage(width: 1000, height: 600, fill: 0.5)
        let p = Pyramid(base: img, minSize: 64)
        XCTAssertEqual(p.levels.map(\.width), [1000, 500, 250, 125, 63])
        XCTAssertEqual(p.levelIndex(maxPixels: 200_000), 1)
        XCTAssertEqual(p.levelIndex(maxPixels: 100_000), 2)
        XCTAssertEqual(p.levelIndex(forScale: 1), 0)
        XCTAssertEqual(p.levelIndex(forScale: 0.5), 1)
        XCTAssertEqual(p.levelIndex(forScale: 0.3), 1)
        XCTAssertEqual(p.levelIndex(forScale: 0.25), 2)
        XCTAssertEqual(p.level(3).mean(), 0.5, accuracy: 1e-6)
    }

    func testResize() {
        let img = PlanarImage(width: 64, height: 32, fill: 0.25)
        let r = img.resized(width: 16, height: 8)
        XCTAssertEqual(r.width, 16)
        XCTAssertEqual(r.mean(), 0.25, accuracy: 1e-5)
    }

    func testRotated90CW() {
        // 3×2 image:  0 1 2       rotated CW (2×3):  3 0
        //             3 4 5                          4 1
        //                                            5 2
        let img = PlanarImage(width: 3, height: 2, pixels: [0, 1, 2, 3, 4, 5])
        let r = img.rotated90CW()
        XCTAssertEqual(r.width, 2); XCTAssertEqual(r.height, 3)
        XCTAssertEqual(r.toArray(), [3, 0, 4, 1, 5, 2])
        // Four turns give the original back.
        let back = r.rotated90CW().rotated90CW().rotated90CW()
        XCTAssertEqual(back.toArray(), img.toArray())
        // A rectangle follows the pixels it covers.
        let rect = PixelRect(x: 1, y: 0, width: 2, height: 1)   // pixels 1,2
        let rr = rect.rotated90CW(imageWidth: 3, imageHeight: 2)
        XCTAssertEqual(rr, PixelRect(x: 1, y: 1, width: 1, height: 2))
        XCTAssertEqual([r[rr.x, rr.y], r[rr.x, rr.y + 1]], [1, 2])
    }
}
