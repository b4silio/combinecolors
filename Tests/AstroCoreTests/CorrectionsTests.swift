import XCTest
@testable import AstroCore

final class CorrectionsTests: XCTestCase {
    func testMapResolutionDabAndApplication() throws {
        let m = CorrectionMap(imageWidth: 4096, imageHeight: 3072)
        XCTAssertEqual(m.width, 1024); XCTAssertEqual(m.height, 768)
        XCTAssertTrue(m.isEmpty)
        // A soft dab darkens around its centre, fades out at the radius, nothing beyond.
        m.dab(x: 2048, y: 1536, radius: 400, amount: -0.5, hardness: 0.3)
        XCTAssertFalse(m.isEmpty)
        XCTAssertEqual(m.value(x: 2048, y: 1536), -0.5, accuracy: 0.02)
        XCTAssertLessThan(abs(m.value(x: 2048 + 380, y: 1536)), 0.05)
        XCTAssertEqual(m.value(x: 100, y: 100), 0)
        // Dabs accumulate and clamp at ±1.
        for _ in 0..<5 { m.dab(x: 2048, y: 1536, radius: 400, amount: -0.5, hardness: 0.3) }
        XCTAssertEqual(m.value(x: 2048, y: 1536), -1, accuracy: 0.02)
        // Applied: 2^(strength × map) — the centre darkened by one stop at strength 1, two at strength 2; zero pixels stay zero.
        let img = PlanarImage(width: 4096, height: 3072, fill: 0.4)
        img[10, 10] = 0
        let out = m.applied(to: img, strength: 1)
        XCTAssertEqual(out[2048, 1536], 0.2, accuracy: 0.01)
        XCTAssertEqual(out[100, 100], 0.4, accuracy: 1e-6)
        XCTAssertEqual(out[10, 10], 0)
        let two = m.applied(to: img, strength: 2)
        XCTAssertEqual(two[2048, 1536], 0.1, accuracy: 0.01)
        // The region form (a crop at a pyramid level) agrees with the whole-image one.
        let crop = img.cropped(x: 1800, y: 1300, width: 500, height: 500).downsampled2x()
        m.apply(to: crop, strength: 1, rect: CGRect(x: 1800, y: 1300, width: 500, height: 500))
        XCTAssertEqual(crop[124, 118], out[2048, 1536], accuracy: 0.01)
        // A stroke lays dabs along its path.
        let s = CorrectionMap(imageWidth: 1000, imageHeight: 800)
        s.stroke(from: CGPoint(x: 100, y: 400), to: CGPoint(x: 900, y: 400), radius: 40, amount: 0.3, hardness: 0.5)
        XCTAssertGreaterThan(s.value(x: 500, y: 400), 0.25)
        XCTAssertEqual(s.value(x: 500, y: 600), 0)
        s.clear(); XCTAssertTrue(s.isEmpty)
    }

    func testOrientationAndSidecarRoundTrip() throws {
        let m = CorrectionMap(imageWidth: 800, imageHeight: 600)
        m.dab(x: 100, y: 500, radius: 60, amount: 0.8, hardness: 0.5)   // bottom-left
        let rotated = m.applying(.rotateClockwise)
        XCTAssertEqual(rotated.imageWidth, 600); XCTAssertEqual(rotated.imageHeight, 800)
        XCTAssertEqual(rotated.value(x: 600 - 500, y: 100), 0.8, accuracy: 0.05, "the dab moved with the image")
        let o = Orientation(rotation: 1, flipped: true)
        var oriented = m.applying(.flipHorizontal); oriented = oriented.applying(.rotateClockwise)
        let back = oriented.unoriented(o)
        XCTAssertEqual(back.value(x: 100, y: 500), 0.8, accuracy: 0.05, "unoriented returns to the file's frame")
        // Sidecar: written next to the image, read back with the same values.
        let image = Synth.tempURL("corr.fits")
        try FITSWriter.write(planes: [PlanarImage(width: 800, height: 600, fill: 0.1)], to: image)
        let location = try m.write(for: image, kind: .red)
        XCTAssertEqual(location.lastPathComponent, "corr.fits.R.ccmask.png", "the channel is part of the name")
        let read = try XCTUnwrap(CorrectionMap.read(for: image, kind: .red, imageWidth: 800, imageHeight: 600))
        XCTAssertLessThan(read.plane.maxAbsDifference(m.plane), 1.0 / 65535 * 2)
        XCTAssertNil(CorrectionMap.read(for: image, kind: .green, imageWidth: 800, imageHeight: 600), "another channel of the same file has its own map")
        XCTAssertNil(CorrectionMap.read(for: image, kind: .red, imageWidth: 801, imageHeight: 600), "a map of another size is ignored")
        CorrectionMap.remove(for: image, kind: .red)
        XCTAssertNil(CorrectionMap.read(for: image, kind: .red, imageWidth: 800, imageHeight: 600))
        // A map saved before the channel went into the name is read by every channel, and replaced on the next save.
        let legacy = CorrectionMap.sidecarURL(for: image)
        try FileManager.default.moveItem(at: try m.write(for: image, kind: .green), to: legacy)
        XCTAssertNotNil(CorrectionMap.read(for: image, kind: .red, imageWidth: 800, imageHeight: 600))
        XCTAssertNotNil(CorrectionMap.read(for: image, kind: .blue, imageWidth: 800, imageHeight: 600))
        try m.write(for: image, kind: .red)
        XCTAssertFalse(FileManager.default.fileExists(atPath: legacy.path), "superseded")
        XCTAssertNil(CorrectionMap.read(for: image, kind: .blue, imageWidth: 800, imageHeight: 600))
        CorrectionMap.remove(for: image, kind: .red)
        // Read-only folder: the fallback in Application Support takes it.
        let ro = URL(fileURLWithPath: "/System/Library/nonexistent.fits")
        let fb = try m.write(for: ro, kind: .ha)
        XCTAssertTrue(fb.path.contains("Corrections") && fb.lastPathComponent.hasSuffix(".Ha.ccmask.png"))
        XCTAssertNotNil(CorrectionMap.read(for: ro, kind: .ha, imageWidth: 800, imageHeight: 600))
        CorrectionMap.remove(for: ro, kind: .ha)
        // Settings decode with defaults.
        let old = try JSONDecoder().decode(CorrectionSettings.self, from: Data("{}".utf8))
        XCTAssertEqual(old.brushRadius, 0.04); XCTAssertEqual(old.brushFlow, 0.02); XCTAssertEqual(old.strength(for: .ha), 0.5)
    }

    func testLiveCorrectionInTheRendererMatchesTheCommittedOne() throws {
        let sources: [ChannelKind: ChannelSource] = [.ha: Synth.source(.ha, width: 256, height: 192), .oiii: Synth.source(.oiii, scale: 0.4, seed: 2, width: 256, height: 192)]
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "hoo")!)
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        let map = CorrectionMap(imageWidth: 256, imageHeight: 192)
        map.dab(x: 128, y: 96, radius: 50, amount: 0.7, hardness: 0.4)
        // Live: the renderer applies the map to the Ha plane; committed: the plane itself is corrected.
        var live = RenderRequest(planes: sources.mapValues { $0.image }, settings: s, context: ctx)
        live.correction = CorrectionInput(map: map, strength: 1, kind: .ha, rect: nil)
        let a = try Renderer.render(live)
        var planes = sources.mapValues { $0.image }
        planes[.ha] = map.applied(to: sources[.ha]!.image, strength: 1)
        let b = try Renderer.render(RenderRequest(planes: planes, settings: s, context: ctx))
        for (p, q) in zip(a.image.planes, b.image.planes) { XCTAssertLessThan(p.maxAbsDifference(q), 1e-4) }
        let plain = try Renderer.render(RenderRequest(planes: sources.mapValues { $0.image }, settings: s, context: ctx))
        XCTAssertGreaterThan(a.image.r[128, 96], plain.image.r[128, 96], "brightened where painted (Ha drives red in HOO)")
    }
}

final class BrushSpacingTests: XCTestCase {
    /// A slow drag (one-pixel mouse events) paints as much as one fast drag along the same path.
    func testSlowDragPaintsLikeAFastOne() {
        let slow = CorrectionMap(imageWidth: 400, imageHeight: 200), fast = CorrectionMap(imageWidth: 400, imageHeight: 200)
        var c1 = CorrectionMap.strokeStart(radius: 40)
        var p = CGPoint(x: 100, y: 100)
        slow.stroke(from: p, to: p, radius: 40, amount: 0.02, hardness: 0.3, carry: &c1)
        for _ in 0..<200 {
            let q = CGPoint(x: p.x + 1, y: p.y)
            slow.stroke(from: p, to: q, radius: 40, amount: 0.02, hardness: 0.3, carry: &c1)
            p = q
        }
        var c2 = CorrectionMap.strokeStart(radius: 40)
        fast.stroke(from: CGPoint(x: 100, y: 100), to: CGPoint(x: 100, y: 100), radius: 40, amount: 0.02, hardness: 0.3, carry: &c2)
        fast.stroke(from: CGPoint(x: 100, y: 100), to: CGPoint(x: 300, y: 100), radius: 40, amount: 0.02, hardness: 0.3, carry: &c2)
        XCTAssertEqual(slow.value(x: 200, y: 100), fast.value(x: 200, y: 100), accuracy: 1e-4)
        // One pass at 2 % flow stays gentle (a dozen per cent of the full range at most).
        XCTAssertLessThan(slow.value(x: 200, y: 100), 0.2)
        XCTAssertGreaterThan(slow.value(x: 200, y: 100), 0.05)
    }
}
