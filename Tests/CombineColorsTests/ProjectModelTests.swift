import XCTest
import AstroCore
@testable import CombineColors

@MainActor
final class ProjectModelTests: XCTestCase {
    var dir: URL!

    override func setUp() async throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("CombineColorsModelTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: dir)
    }

    func makeChannel(_ name: String, kind: ChannelKind, scale: Float, seed: UInt64, width: Int = 200, height: Int = 150) throws -> URL {
        let img = PlanarImage(width: width, height: height)
        var state = seed
        func rnd() -> Float { state = state &* 6364136223846793005 &+ 1442695040888963407; return Float((state >> 33) & 0xFFFF) / 65535 }
        for y in 0..<height { for x in 0..<width {
            let dx = Float(x - width / 2), dy = Float(y - height / 2)
            img[x, y] = scale * (0.01 + 0.02 * exp(-(dx * dx + dy * dy) / 800) + 0.001 * (rnd() - 0.5))
        } }
        let url = dir.appendingPathComponent(name)
        try FITSWriter.write(planes: [img], to: url, extraCards: [("FILTER", kind.rawValue, nil)])
        return url
    }

    /// Spins the main run loop until the condition holds or the timeout elapses.
    func wait(timeout: TimeInterval = 10, for condition: @escaping () -> Bool) async {
        let deadline = Date().addingTimeInterval(timeout)
        while !condition() && Date() < deadline {
            RunLoop.main.run(mode: .default, before: Date().addingTimeInterval(0.02))
            await Task.yield()
        }
    }

    func loadedModel() async throws -> ProjectModel {
        let m = ProjectModel()
        let urls = [try makeChannel("x-Ha.fits", kind: .ha, scale: 1, seed: 1),
                    try makeChannel("x-OIII.fits", kind: .oiii, scale: 0.4, seed: 2),
                    try makeChannel("x-SII.fits", kind: .sii, scale: 0.3, seed: 3)]
        m.loadFiles(urls)
        await wait { m.sources.count == 3 && !m.isLoading && m.context != nil }
        return m
    }

    func testLoadDetectsChannelsAndRenders() async throws {
        let m = try await loadedModel()
        XCTAssertEqual(Set(m.sources.keys), [.ha, .oiii, .sii])
        XCTAssertEqual(m.imageSize, CGSize(width: 200, height: 150))
        XCTAssertEqual(m.slotThumbnails.count, 3)
        m.viewport = Viewport(visibleRect: CGRect(x: 0, y: 0, width: 200, height: 150), scale: 1)
        await wait { m.frame != nil }
        XCTAssertNotNil(m.frame)
        XCTAssertEqual(m.frame?.level, 0)
        XCTAssertEqual(m.frame?.rgb.width, 200)
        XCTAssertNil(m.renderError)
        await wait { m.thumbnails.count >= Palettes.available(for: m.availableKinds).count }
        XCTAssertGreaterThanOrEqual(m.thumbnails.count, Palettes.available(for: m.availableKinds).count)
        XCTAssertEqual(m.histogram.count, 3)
    }

    func testMismatchedSizeIsRejected() async throws {
        let m = try await loadedModel()
        let bad = try makeChannel("y-Hb.fits", kind: .hb, scale: 1, seed: 9, width: 100, height: 80)
        m.loadFiles([bad])
        await wait { m.errorMessage != nil }
        XCTAssertTrue(m.errorMessage?.contains("does not match") ?? false)
        XCTAssertEqual(m.sources.count, 3)
    }

    func testUnknownFileGoesToPending() async throws {
        let m = try await loadedModel()
        let img = PlanarImage(width: 200, height: 150, fill: 0.02)
        let url = dir.appendingPathComponent("mystery.fits")
        try FITSWriter.write(planes: [img], to: url)
        m.loadFiles([url])
        await wait { !m.pendingFiles.isEmpty }
        XCTAssertEqual(m.pendingFiles.count, 1)
        m.assignPending(m.pendingFiles[0], to: .luminance)
        await wait { m.sources[.luminance] != nil && !m.isLoading }
        XCTAssertNotNil(m.sources[.luminance])
        XCTAssertTrue(m.pendingFiles.isEmpty)
    }

    func testColorFileAsRGB() async throws {
        let m = ProjectModel()
        let p = PlanarImage(width: 50, height: 40, fill: 0.3)
        let url = dir.appendingPathComponent("stars.fits")
        try FITSWriter.write(planes: [p, p, p], to: url)
        m.loadFiles([url])
        await wait { m.pendingColorFile != nil }
        XCTAssertNotNil(m.pendingColorFile)
        m.assignColorFileAsRGB(m.pendingColorFile!)
        await wait { m.sources.count == 3 && !m.isLoading }
        XCTAssertEqual(Set(m.sources.keys), [.red, .green, .blue])
    }

    func testReassignAndRemove() async throws {
        let m = try await loadedModel()
        m.reassign(.sii, to: .hb)
        XCTAssertNil(m.sources[.sii]); XCTAssertEqual(m.sources[.hb]?.kind, .hb)
        m.reassign(.hb, to: .ha)   // swap
        XCTAssertEqual(m.sources[.ha]?.fileName, "x-SII.fits")
        XCTAssertEqual(m.sources[.hb]?.fileName, "x-Ha.fits")
        m.removeChannel(.hb)
        XCTAssertNil(m.sources[.hb])
        XCTAssertEqual(m.sources.count, 2)
        m.removeAll()
        XCTAssertTrue(m.sources.isEmpty)
        XCTAssertNil(m.frame)
    }

    func testPaletteFallbackWhenChannelsMissing() async throws {
        let m = try await loadedModel()
        m.select(palette: Palettes.palette(id: "sho")!)
        m.removeChannel(.sii)
        // SHO needs SII: the model must switch to a palette it can render.
        XCTAssertTrue(Renderer.missingChannels(settings: m.settings, available: m.availableKinds).isEmpty)
        XCTAssertNotEqual(m.settings.paletteID, "sho")
    }

    func testCropFlow() async throws {
        let m = try await loadedModel()
        m.beginCropEditing()
        XCTAssertTrue(m.cropEditing)
        XCTAssertNotNil(m.cropDraft)
        m.cropDraft = PixelRect(x: 10, y: 20, width: 100, height: 80)
        m.commitCropEditing()
        XCTAssertFalse(m.cropEditing)
        XCTAssertEqual(m.crop, PixelRect(x: 10, y: 20, width: 100, height: 80))
        await wait { m.sources[.ha]?.region == m.crop && !m.isLoading && m.context != nil }
        XCTAssertEqual(m.sources[.ha]?.region, m.crop)
        XCTAssertTrue(m.sources[.ha]!.pyramid === m.baseSources[.ha]!.pyramid, "crop must not copy the pyramid")
        // Slot thumbnails follow the crop (5:4 instead of 4:3).
        for k in [ChannelKind.ha, .oiii, .sii] {
            XCTAssertEqual(m.slotThumbnails[k]?.width, 100, "\(k)"); XCTAssertEqual(m.slotThumbnails[k]?.height, 80, "\(k)")
        }
        // Full crop clears it.
        m.cropDraft = PixelRect(x: 0, y: 0, width: 200, height: 150)
        m.cropEditing = true
        m.commitCropEditing()
        XCTAssertNil(m.crop)
        await wait { m.sources[.ha]?.region == nil && !m.isLoading }
        XCTAssertEqual(m.slotThumbnails[.ha]?.width, 200)
        m.cancelCropEditing()
        m.clearCrop()
        XCTAssertNil(m.crop)
    }

    func testAutoCropOnPaddedData() async throws {
        let m = ProjectModel()
        let img = PlanarImage(width: 120, height: 100, fill: 0.02)
        for y in 0..<100 { for x in 0..<15 { img[x, y] = 0 } }
        let url = dir.appendingPathComponent("padded-Ha.fits")
        try FITSWriter.write(planes: [img], to: url)
        m.loadFiles([url])
        await wait { m.sources.count == 1 && !m.isLoading }
        m.autoCrop()
        await wait { m.crop != nil }
        XCTAssertEqual(m.crop, PixelRect(x: 15, y: 0, width: 105, height: 100))
    }

    func testProjectRoundTrip() async throws {
        let m = try await loadedModel()
        m.select(palette: Palettes.palette(id: "foraxx_sho")!)
        m.settings.post.saturation = 1.7
        m.crop = PixelRect(x: 5, y: 5, width: 150, height: 100)
        let url = dir.appendingPathComponent("project.json")
        try m.saveProject(to: url)
        XCTAssertEqual(m.projectURL, url)
        let m2 = ProjectModel()
        m2.openProject(from: url)
        await wait { m2.sources.count == 3 && !m2.isLoading && m2.context != nil }
        XCTAssertEqual(m2.settings, m.settings)
        XCTAssertEqual(m2.crop, m.crop)
        XCTAssertEqual(Set(m2.sources.keys), [.ha, .oiii, .sii])
        XCTAssertEqual(m2.sources[.ha]?.region, m.crop)
    }

    func testExportWritesFile() async throws {
        let m = try await loadedModel()
        m.crop = PixelRect(x: 0, y: 0, width: 100, height: 75)
        await wait { m.sources[.ha]?.region != nil && m.context != nil && !m.isLoading }
        for (fmt, ext) in [(RasterWriter.Format.tiff16, "tif"), (.fits32, "fits"), (.png8, "png")] {
            let url = dir.appendingPathComponent("out.\(ext)")
            var done = false
            var err: Error? = nil
            m.export(to: url, format: fmt, linear: fmt == .fits32) { e in err = e; done = true }
            await wait { done }
            XCTAssertNil(err)
            let back = try ImageLoader.load(url: url)
            XCTAssertEqual(back.width, 100); XCTAssertEqual(back.height, 75); XCTAssertEqual(back.planes.count, 3)
        }
        XCTAssertNil(m.exportProgress)
    }

    func testRotateClockwise() async throws {
        let m = try await loadedModel()
        let haBefore = m.sources[.ha]!.image
        m.cropDraft = PixelRect(x: 10, y: 20, width: 100, height: 80); m.cropEditing = true; m.commitCropEditing()
        await wait { m.sources[.ha]?.region == m.crop && !m.isLoading && m.context != nil }
        m.rotateClockwise()
        await wait { m.imageSize == CGSize(width: 150, height: 200) && !m.isLoading && m.context != nil }
        XCTAssertEqual(m.rotation, 1)
        XCTAssertEqual(m.imageSize, CGSize(width: 150, height: 200))
        // The crop follows the rotation: (x 10, y 20, 100×80) in 200×150 → (x 150−100, y 10, 80×100).
        XCTAssertEqual(m.crop, PixelRect(x: 50, y: 10, width: 80, height: 100))
        XCTAssertEqual(m.sources[.ha]?.region, m.crop)
        // Pixels moved as a clockwise turn: source (x, y) → (H−1−y, x).
        let haAfter = m.sources[.ha]!.image
        XCTAssertEqual(haAfter[149 - 20, 10], haBefore[10, 20])
        XCTAssertEqual(m.slotThumbnails[.ha]?.width, 80); XCTAssertEqual(m.slotThumbnails[.ha]?.height, 100)
        // Saved in the project; three more turns bring the original orientation back.
        XCTAssertEqual(m.projectDocument().rotation, 1)
        for _ in 0..<3 {
            m.rotateClockwise()
            await wait { !m.isLoading && m.context != nil }
        }
        XCTAssertEqual(m.rotation, 0)
        XCTAssertEqual(m.imageSize, CGSize(width: 200, height: 150))
        XCTAssertEqual(m.crop, PixelRect(x: 10, y: 20, width: 100, height: 80))
        XCTAssertEqual(m.sources[.ha]!.image[10, 20], haBefore[10, 20])
        // A channel added while rotated is rotated the same way.
        m.rotateClockwise()
        await wait { m.rotation == 1 && !m.isLoading && m.context != nil }
        let url = try makeChannel("x-Hb.fits", kind: .hb, scale: 0.2, seed: 4)
        m.loadFiles([url])
        await wait { m.sources[.hb] != nil && !m.isLoading }
        XCTAssertEqual(m.sources[.hb]?.width, 150); XCTAssertEqual(m.sources[.hb]?.height, 200)
    }

    func testPresetsAndUndo() async throws {
        let m = try await loadedModel()
        let um = UndoManager()
        m.undoManager = um
        m.viewport = Viewport(visibleRect: CGRect(x: 0, y: 0, width: 200, height: 150), scale: 1)
        await wait { m.frame != nil }
        let original = m.settings
        let a = m.savePreset()
        XCTAssertEqual(a.name, "Preset 1")
        XCTAssertNotNil(a.thumbnailPNG)
        XCTAssertNotNil(m.presetThumbnail(a))
        XCTAssertEqual(m.activePresetID, a.id)
        m.settings.post.hueShift = 30
        XCTAssertTrue(um.canUndo)
        XCTAssertNil(m.activePresetID)
        let b = m.savePreset(name: "Warm")
        XCTAssertEqual(m.presets.count, 2)
        m.applyPreset(a)
        XCTAssertEqual(m.settings, original)
        m.applyPreset(b)
        XCTAssertEqual(m.settings.post.hueShift, 30)
        m.renamePreset(b, to: "Warmer")
        XCTAssertEqual(m.presets[1].name, "Warmer")
        m.settings.post.saturation = 2
        m.updatePreset(b)
        XCTAssertEqual(m.presets[1].settings.post.saturation, 2)
        // Presets travel with the project.
        let url = dir.appendingPathComponent("presets.json")
        try m.saveProject(to: url)
        let m2 = ProjectModel()
        m2.openProject(from: url)
        await wait { m2.presets.count == 2 }
        XCTAssertEqual(m2.presets.map(\.name), ["Preset 1", "Warmer"])
        XCTAssertNotNil(m2.presetThumbnail(m2.presets[0]))
        m.deletePreset(a)
        XCTAssertEqual(m.presets.count, 1)
        um.undo()
        await wait { m.settings.post.saturation != 2 }
        XCTAssertNotEqual(m.settings.post.saturation, 2)
    }

    func testHoverReadout() async throws {
        let m = try await loadedModel()
        m.viewport = Viewport(visibleRect: CGRect(x: 0, y: 0, width: 200, height: 150), scale: 1)
        await wait { m.frame != nil }
        m.updateHover(imagePoint: CGPoint(x: 100, y: 75))
        XCTAssertEqual(m.hover?.values.count, 3)
        XCTAssertNotNil(m.hover?.rgb)
        m.updateHover(imagePoint: CGPoint(x: -1, y: 0))
        XCTAssertNil(m.hover)
    }

    func testDualBandAddsChannels() async throws {
        let m = ProjectModel()
        let r = PlanarImage(width: 60, height: 40, fill: 0.5), g = PlanarImage(width: 60, height: 40, fill: 0.2), b = PlanarImage(width: 60, height: 40, fill: 0.3)
        let (ha, o3) = DualBand.split(r: r, g: g, b: b, settings: DualBandSettings())
        m.addSplitChannels(first: (.ha, ha), second: (.oiii, o3), fileName: "osc.fits")
        await wait { m.sources.count == 2 && !m.isLoading }
        XCTAssertEqual(m.sources[.ha]?.stats.median, 0.5)
        XCTAssertEqual(m.sources[.oiii]?.stats.median ?? 0, 0.25, accuracy: 1e-6)
    }
}

@MainActor
final class RecentSetsTests: XCTestCase {
    func testRecordAndReopenSets() async throws {
        let defaults = UserDefaults(suiteName: "RecentSetsTests-\(UUID().uuidString)")!
        let base = ProjectModelTests()
        try await base.setUp()
        defer { Task { try? await base.tearDown() } }
        let ha = try base.makeChannel("set-Ha.fits", kind: .ha, scale: 1, seed: 1)
        let o3 = try base.makeChannel("set-OIII.fits", kind: .oiii, scale: 0.5, seed: 2)
        let m = ProjectModel(defaults: defaults)
        XCTAssertTrue(m.recentSets.isEmpty)
        m.loadFiles([ha, o3])
        await base.wait { m.sources.count == 2 && !m.isLoading && !m.recentSets.isEmpty }
        XCTAssertEqual(m.recentSets.count, 1)
        XCTAssertEqual(m.recentSets[0].entries.map(\.kind), [.ha, .oiii])
        XCTAssertEqual(m.recentSets[0].name, "set")
        // Adding a channel updates the same entry (new path set → new entry at front, old one kept).
        let s2 = try base.makeChannel("set-SII.fits", kind: .sii, scale: 0.3, seed: 3)
        m.loadFiles([s2])
        await base.wait { m.sources.count == 3 && !m.isLoading && m.recentSets.count == 2 }
        XCTAssertEqual(m.recentSets[0].entries.count, 3)
        // Settings, crop and presets are auto-saved with the set (debounced) and restored on reopening.
        m.settings.post.saturation = 1.7
        m.cropDraft = PixelRect(x: 10, y: 20, width: 100, height: 80); m.cropEditing = true; m.commitCropEditing()
        m.savePreset(name: "look")
        await base.wait(timeout: 5) { m.recentSets[0].state?.settings.post.saturation == 1.7 && m.recentSets[0].state?.crop != nil && m.recentSets[0].state?.presets.count == 1 }
        XCTAssertEqual(m.recentSets[0].state?.crop, PixelRect(x: 10, y: 20, width: 100, height: 80))
        XCTAssertTrue(m.recentSets[0].state?.channels.isEmpty ?? false, "no paths inside the state")
        // Reopening the first set replaces the loaded channels and brings back the state it was saved with.
        let first = m.recentSets[1]
        m.openRecentSet(first)
        await base.wait { m.sources.count == 2 && !m.isLoading }
        XCTAssertEqual(Set(m.sources.keys), [.ha, .oiii])
        XCTAssertEqual(m.recentSets[0].paths, first.paths, "reopened set moves to the front")
        XCTAssertEqual(m.recentSets.count, 2)
        XCTAssertEqual(m.settings.post.saturation, 1, "the two-channel set was saved with default settings")
        XCTAssertNil(m.crop)
        // Now reopen the three-channel set: its state comes back.
        let three = m.recentSets[1]
        XCTAssertEqual(three.state?.settings.post.saturation, 1.7)
        m.openRecentSet(three)
        await base.wait { m.sources.count == 3 && !m.isLoading && m.crop != nil }
        XCTAssertEqual(m.settings.post.saturation, 1.7)
        XCTAssertEqual(m.crop, PixelRect(x: 10, y: 20, width: 100, height: 80))
        XCTAssertEqual(m.presets.map(\.name), ["look"])
        XCTAssertEqual(m.recentSets[0].id, three.id, "same set keeps its identity")
        // Persisted across model instances.
        let m2 = ProjectModel(defaults: defaults)
        XCTAssertEqual(m2.recentSets.count, 2)
        // Missing files are reported and the entry dropped.
        try FileManager.default.removeItem(at: ha)
        m2.openRecentSet(m2.recentSets[0])
        XCTAssertNotNil(m2.errorMessage)
        XCTAssertEqual(m2.recentSets.count, 1)
        m2.clearRecentSets()
        XCTAssertTrue(m2.recentSets.isEmpty)
        XCTAssertEqual(RecentSet.makeName(entries: [.init(kind: .ha, path: "/a/b/M31_Ha.fits"), .init(kind: .oiii, path: "/a/b/M31_OIII.fits")]), "M31")
        XCTAssertEqual(RecentSet.makeName(entries: [.init(kind: .ha, path: "/x/Nebula/h.fits"), .init(kind: .oiii, path: "/x/Nebula/o.fits")]), "Nebula")
    }
}

@MainActor
final class PaletteVisibilityTests: XCTestCase {
    func testHideAndFilterPersist() async throws {
        let defaults = UserDefaults(suiteName: "PaletteVisibilityTests-\(UUID().uuidString)")!
        let base = ProjectModelTests()
        try await base.setUp()
        let m = ProjectModel(defaults: defaults)
        m.loadFiles([try base.makeChannel("v-Ha.fits", kind: .ha, scale: 1, seed: 1), try base.makeChannel("v-OIII.fits", kind: .oiii, scale: 0.5, seed: 2)])
        await base.wait { m.sources.count == 2 && !m.isLoading }
        let all = Palettes.available(for: m.availableKinds)
        XCTAssertEqual(m.visiblePalettes.count, all.count)
        m.setPalette("hoo", hidden: true)
        m.setPalette("hho", hidden: true)
        XCTAssertEqual(m.visiblePalettes.count, all.count - 2)
        XCTAssertFalse(m.visiblePalettes.contains { $0.id == "hoo" })
        m.paletteFilter = .all
        XCTAssertEqual(m.visiblePalettes.count, all.count)
        XCTAssertTrue(m.isPaletteHidden("hoo"))
        m.setPalette("hoo", hidden: false)
        XCTAssertFalse(m.isPaletteHidden("hoo"))
        let m2 = ProjectModel(defaults: defaults)
        XCTAssertEqual(m2.hiddenPaletteIDs, ["hho"])
        XCTAssertEqual(m2.paletteFilter, .all)
        try? await base.tearDown()
    }
}


@MainActor
final class PeekTests: XCTestCase {
    func testPeekShowsSingleChannelThenRestores() async throws {
        let base = ProjectModelTests()
        try await base.setUp()
        let m = try await base.loadedModel()
        m.select(palette: Palettes.palette(id: "sho")!)
        m.viewport = Viewport(visibleRect: CGRect(x: 0, y: 0, width: 200, height: 150), scale: 1)
        await base.wait { m.frame != nil }
        m.peekChannel = .oiii
        XCTAssertEqual(m.displayedSettings.mode, .monochrome)
        XCTAssertEqual(m.displayedSettings.monoChannels, [.oiii])
        XCTAssertEqual(m.settings.mode, .matrix, "peeking must not touch the real settings")
        await base.wait { m.frame?.settings.mode == .monochrome }
        let f = m.frame!
        XCTAssertEqual(f.rgb.r.toArray(), f.rgb.b.toArray(), "peek frame is grayscale")
        m.peekChannel = nil
        await base.wait { m.frame?.settings.mode == .matrix }
        XCTAssertEqual(m.frame?.settings.mode, .matrix)
        try? await base.tearDown()
    }
}
