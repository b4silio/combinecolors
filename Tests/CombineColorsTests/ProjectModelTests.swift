import XCTest
import AstroCore
@testable import CombineColors

@MainActor
final class ProjectModelTests: XCTestCase {
    var dir: URL!
    /// Isolated preferences: the models under test must never touch the user's recent sets
    /// (their saved presets and dust spots live there). One fixed suite, wiped before and after.
    static let defaultsSuite = "ch.basilio.combinecolors.tests"
    var defaults: UserDefaults!

    override func setUp() async throws {
        dir = FileManager.default.temporaryDirectory.appendingPathComponent("CombineColorsModelTests-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defaults = Self.isolatedDefaults()
    }

    override func tearDown() async throws {
        try? FileManager.default.removeItem(at: dir)
        Self.discardIsolatedDefaults()
    }

    static func isolatedDefaults() -> UserDefaults {
        UserDefaults.standard.removePersistentDomain(forName: defaultsSuite)
        return UserDefaults(suiteName: defaultsSuite)!
    }

    static func discardIsolatedDefaults() {
        UserDefaults.standard.removePersistentDomain(forName: defaultsSuite)
        try? FileManager.default.removeItem(at: FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Preferences/\(defaultsSuite).plist"))
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
        let m = ProjectModel(defaults: defaults)
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
        let m = ProjectModel(defaults: defaults)
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

    /// A split colour file is one path behind three channels: the recent set and the project must
    /// give R, G and B their own planes again (not the first plane three times), and the set's
    /// state is still auto-saved.
    func testSplitColorFileReopensWithThreePlanes() async throws {
        let m = ProjectModel(defaults: defaults)
        let url = dir.appendingPathComponent("rgb.fits")
        try FITSWriter.write(planes: [PlanarImage(width: 50, height: 40, fill: 0.1), PlanarImage(width: 50, height: 40, fill: 0.2), PlanarImage(width: 50, height: 40, fill: 0.3)], to: url)
        m.loadFiles([url])
        await wait { m.pendingColorFile != nil }
        m.assignColorFileAsRGB(m.pendingColorFile!)
        await wait { m.sources.count == 3 && !m.isLoading && !m.recentSets.isEmpty }
        XCTAssertEqual(m.recentSets[0].entries.map(\.kind), [.blue, .green, .red].sorted { $0.rawValue < $1.rawValue })
        m.settings.post.saturation = 1.4
        await wait(timeout: 5) { m.recentSets[0].state?.settings.post.saturation == 1.4 }
        XCTAssertEqual(m.recentSets[0].state?.settings.post.saturation, 1.4, "state saved for a one-file set")
        let project = dir.appendingPathComponent("rgb.json")
        try m.saveProject(to: project)
        // Recent set.
        let m2 = ProjectModel(defaults: defaults)
        m2.openRecentSet(m2.recentSets[0])
        await wait { m2.sources.count == 3 && !m2.isLoading }
        XCTAssertEqual(m2.sources[.red]!.image[5, 5], 0.1, accuracy: 1e-5)
        XCTAssertEqual(m2.sources[.green]!.image[5, 5], 0.2, accuracy: 1e-5)
        XCTAssertEqual(m2.sources[.blue]!.image[5, 5], 0.3, accuracy: 1e-5)
        XCTAssertEqual(m2.settings.post.saturation, 1.4)
        // Project file.
        let m3 = ProjectModel(defaults: defaults)
        m3.openProject(from: project)
        await wait { m3.sources.count == 3 && !m3.isLoading }
        XCTAssertEqual(m3.sources[.green]!.image[5, 5], 0.2, accuracy: 1e-5)
        XCTAssertEqual(m3.sources[.blue]!.image[5, 5], 0.3, accuracy: 1e-5)
    }

    func testRecentSetRestoresCorrectionStrengthAndStarsLayer() async throws {
        let m = try await loadedModel()
        // A painted correction at strength 2 on Hα, and an RGB stars layer.
        m.beginCorrectionEditing(.ha)
        m.correctionStrength = 2
        m.paintCorrection(from: CGPoint(x: 60, y: 60), to: CGPoint(x: 90, y: 80), invert: false)
        m.commitCorrectionEditing()
        await wait { m.hasCorrection(.ha) && m.correctionEditing == nil }
        let stars = dir.appendingPathComponent("stars.fits")
        try FITSWriter.write(planes: [PlanarImage(width: 200, height: 150, fill: 0.2), PlanarImage(width: 200, height: 150, fill: 0.3), PlanarImage(width: 200, height: 150, fill: 0.4)], to: stars)
        m.loadStarsLayer(from: stars)
        await wait { m.starsLayer != nil && !m.isLoading }
        XCTAssertEqual(m.activeLayer, .background, "the RGB stars go to the Stars tab without showing it")
        XCTAssertEqual(Set(m.layerSources(.stars).keys), [.red, .green, .blue])
        XCTAssertTrue(m.settings.stars.enabled)
        await wait(timeout: 5) { m.recentSets.first?.state?.starsLayer?.channels.count == 3 && m.recentSets.first?.state?.corrections.strength[.ha] == 2 }
        XCTAssertEqual(m.recentSets.first?.state?.corrections.strength[.ha], 2, "strength saved with the set")
        XCTAssertEqual(Set(m.recentSets.first?.state?.starsLayer?.channels.map(\.path) ?? []), [stars.path], "Stars tab saved with the set")
        // Reopened from the recent set: both are back.
        let m2 = ProjectModel(defaults: defaults)
        m2.openRecentSet(m2.recentSets[0])
        await wait(timeout: 5) { m2.sources.count == 3 && !m2.isLoading && m2.starsLayer != nil }
        XCTAssertEqual(Set(m2.layerSources(.stars).values.compactMap { $0.url?.path }), [stars.path])
        XCTAssertNotNil(m2.starsLayer)
        XCTAssertEqual(m2.corrections.strength(for: .ha), 2)
        XCTAssertTrue(m2.hasCorrection(.ha))
        // And the editor tools: the buttons open on the first channel, a click on a slot switches, a second tool applies the first.
        m2.beginEditing(.dust)
        XCTAssertEqual(m2.editingTool, .dust); XCTAssertEqual(m2.editingChannel, .ha)
        m2.switchEditingChannel(.oiii)
        XCTAssertEqual(m2.dustEditing, .oiii)
        m2.beginEditing(.gradient)
        XCTAssertEqual(m2.editingTool, .gradient); XCTAssertEqual(m2.gradientEditing, .oiii); XCTAssertNil(m2.dustEditing)
        m2.cancelGradientEditing()
        XCTAssertNil(m2.editingTool)
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
        // Cancel without a crop keeps a draft (the toolbar fields may still be bound to it).
        m.cancelCropEditing()
        XCTAssertFalse(m.cropEditing); XCTAssertNil(m.crop); XCTAssertNotNil(m.cropDraft)
        m.beginCropEditing()
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
        let m = ProjectModel(defaults: defaults)
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
        let m2 = ProjectModel(defaults: defaults)
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

    /// Straightening with the crop: the picture turns under the upright crop on Apply, coordinates
    /// follow, the project keeps the angle, and going back to 0 gives the untouched channels back.
    func testFineRotationWithTheCrop() async throws {
        let m = try await loadedModel()
        let haBefore = m.sources[.ha]!.image
        m.beginDustEditing(.ha)
        m.addDustSpot(at: CGPoint(x: 150, y: 40))
        let spot = m.dustDraft!.spots(for: .ha)[0]
        m.commitDustEditing()
        await wait { !m.isLoading && m.context != nil && m.dust.spots(for: .ha).count == 1 }
        let healedBefore = m.sources[.ha]!.image
        // Turning while editing changes nothing until Apply; the constraint keeps the crop inside.
        m.beginCropEditing()
        m.cropDraft = PixelRect(x: 0, y: 0, width: 200, height: 150)
        m.setCropAngle(10)
        XCTAssertEqual(m.orientation.angle, 0)
        XCTAssertTrue(m.sources[.ha]!.image === healedBefore)
        let draft = m.cropDraft!
        XCTAssertLessThan(draft.width, 200); XCTAssertLessThan(draft.height, 150)
        XCTAssertEqual(Double(draft.width) / Double(draft.height), 200.0 / 150, accuracy: 0.03, "the crop keeps its shape")
        m.setCropAngle(0)
        XCTAssertEqual(m.cropDraft, PixelRect(x: 0, y: 0, width: 200, height: 150), "the crop grows back with the angle")
        m.setCropAngle(10)
        m.commitCropEditing()
        await wait { m.orientation.angle == 10 && !m.isLoading && m.context != nil && m.sources[.ha]!.image !== healedBefore }
        XCTAssertEqual(m.orientation, Orientation(rotation: 0, flipped: false, angle: 10))
        XCTAssertEqual(m.crop, draft)
        XCTAssertEqual(m.sources[.ha]?.region, draft)
        XCTAssertEqual(m.imageSize, CGSize(width: 200, height: 150), "same canvas")
        // The spot turned with the picture; the centre pixel stays (it is the pivot).
        let turned = m.dust.spots(for: .ha)[0]
        let e = FineRotation.rotate(x: spot.x, y: spot.y, degrees: 10, width: 200, height: 150)
        XCTAssertEqual(turned.x, e.x, accuracy: 1e-9); XCTAssertEqual(turned.y, e.y, accuracy: 1e-9)
        XCTAssertEqual(m.sources[.ha]!.image[100, 75], haBefore[100, 75], accuracy: 2e-3)
        XCTAssertEqual(m.sources[.ha]!.image[0, 0], 0, "a corner comes in empty")
        XCTAssertEqual(m.projectDocument().angle, 10)
        // Reopening the project turns the files the same way.
        let url = dir.appendingPathComponent("turned.json")
        try m.saveProject(to: url)
        let m2 = ProjectModel(defaults: defaults)
        m2.openProject(from: url)
        await wait { m2.sources.count == 3 && !m2.isLoading && m2.context != nil }
        XCTAssertEqual(m2.orientation.angle, 10)
        var maxDiff: Float = 0
        for i in 0..<m2.sources[.oiii]!.image.count { maxDiff = max(maxDiff, abs(m2.sources[.oiii]!.image.pointer[i] - m.sources[.oiii]!.image.pointer[i])) }
        XCTAssertEqual(maxDiff, 0)
        // A quarter turn keeps the fine angle; back to 0 gives the untouched (quarter-turned) channel.
        m.rotateClockwise()
        await wait { m.rotation == 1 && !m.isLoading && m.context != nil }
        XCTAssertEqual(m.orientation.angle, 10)
        m.beginCropEditing()
        m.setCropAngle(0)
        m.commitCropEditing()
        await wait { m.orientation.angle == 0 && !m.isLoading && m.context != nil }
        XCTAssertTrue(m.unrotatedPlanes.isEmpty)
        let raw = haBefore.rotated90CW()
        let o = m.originalImages[.oiii]!, rawO = try ImageLoader.load(url: dir.appendingPathComponent("x-OIII.fits")).planes[0].rotated90CW()
        XCTAssertEqual(o.toArray(), rawO.toArray(), "resampled once from the untouched data, so nothing is lost")
        XCTAssertEqual(m.sources[.ha]!.image[20, 20], raw[20, 20], accuracy: 1e-6)
    }

    /// Any angle: whole quarter turns become exact ones (the canvas turns sideways), the rest a fine turn.
    func testTurningAllTheWayRound() async throws {
        let m = try await loadedModel()
        let raw = try ImageLoader.load(url: dir.appendingPathComponent("x-OIII.fits")).planes[0]
        XCTAssertEqual(ProjectModel.split(95).quarters, 1); XCTAssertEqual(ProjectModel.split(95).rest, 5, accuracy: 1e-12)
        XCTAssertEqual(ProjectModel.split(-100).quarters, 3); XCTAssertEqual(ProjectModel.split(-100).rest, -10, accuracy: 1e-12)
        XCTAssertEqual(ProjectModel.split(180).quarters, 2)
        m.beginCropEditing()
        m.setCropAngle(270)
        XCTAssertEqual(m.cropAngleDraft, -90, "kept in −180…180")
        m.setCropAngle(180)
        // A half turn: exact, the same canvas.
        XCTAssertEqual(m.cropEditBounds, CGRect(x: 0, y: 0, width: 200, height: 150))
        m.cropDraft = PixelRect(x: 0, y: 0, width: 200, height: 150)
        m.commitCropEditing()
        await wait { m.orientation.rotation == 2 && !m.isLoading && m.context != nil }
        XCTAssertEqual(m.orientation, Orientation(rotation: 2, flipped: false, angle: 0))
        XCTAssertEqual(m.originalImages[.oiii]!.toArray(), raw.rotated90CW(times: 2).toArray())
        XCTAssertNil(m.crop)
        // 95°: one exact quarter turn (sideways canvas) plus 5°; the crop drawn on the sideways canvas lands in it.
        m.beginCropEditing()
        m.cropConstrain = false
        m.setCropAngle(95)
        let b = m.cropEditBounds
        XCTAssertEqual(b, CGRect(x: 25, y: -25, width: 150, height: 200))
        m.cropDraft = PixelRect(x: 45, y: 0, width: 100, height: 120)
        m.commitCropEditing()
        await wait { m.orientation.rotation == 3 && !m.isLoading && m.context != nil }
        XCTAssertEqual(m.orientation, Orientation(rotation: 3, flipped: false, angle: 5))
        XCTAssertEqual(m.imageSize, CGSize(width: 150, height: 200))
        XCTAssertEqual(m.crop, PixelRect(x: 20, y: 25, width: 100, height: 120))
        // Resampled once from the untouched (now three-quarter-turned) plane.
        let expected = raw.rotated90CW(times: 3).rotated(byDegrees: 5)
        XCTAssertEqual(m.originalImages[.oiii]!.toArray(), expected.toArray())
    }

    /// Bright spots (star halos) in the dust editor: the kind of new spots, kept per spot, lowers
    /// the glow under it on Apply.
    func testBrightSpotsInTheDustEditor() async throws {
        let m = try await loadedModel()
        let before = m.sources[.oiii]!.image
        m.beginDustEditing(.oiii)
        m.dustDraft?.polarity = .bright
        m.addDustSpot(at: CGPoint(x: 100, y: 75))
        let spot = try XCTUnwrap(m.dustDraft?.spots(for: .oiii).first)
        XCTAssertEqual(spot.polarity, .bright)
        m.commitDustEditing()
        await wait { !m.isLoading && m.context != nil && m.sources[.oiii]!.image !== before }
        XCTAssertEqual(m.dust.spots(for: .oiii).first?.polarity, .bright)
        XCTAssertLessThan(m.sources[.oiii]!.image[100, 75], before[100, 75], "the glow at the centre is taken down")
        XCTAssertEqual(m.projectDocument().dust.spots(for: .oiii).first?.polarity, .bright)
    }

    func testCropConstraintFitsInsideTheTurnedFrame() {
        for angle in [-30.0, -5, 4, 12, 45] {
            let r = ProjectModel.constrained(PixelRect(x: 10, y: 10, width: 180, height: 130), angle: angle, width: 200, height: 150)
            let t = angle * .pi / 180
            for (x, y) in [(r.x, r.y), (r.maxX, r.y), (r.maxX, r.maxY), (r.x, r.maxY)] {
                let dx = Double(x) - 100, dy = Double(y) - 75
                XCTAssertLessThanOrEqual(abs(cos(t) * dx + sin(t) * dy), 100.5, "\(angle)°")
                XCTAssertLessThanOrEqual(abs(-sin(t) * dx + cos(t) * dy), 75.5, "\(angle)°")
            }
        }
        XCTAssertEqual(ProjectModel.constrained(PixelRect(x: 1, y: 2, width: 30, height: 40), angle: 0, width: 200, height: 150), PixelRect(x: 1, y: 2, width: 30, height: 40))
    }

    func testFlips() async throws {
        let m = try await loadedModel()
        let haBefore = m.sources[.ha]!.image
        m.cropDraft = PixelRect(x: 10, y: 20, width: 100, height: 80); m.cropEditing = true; m.commitCropEditing()
        await wait { m.sources[.ha]?.region == m.crop && !m.isLoading && m.context != nil }
        m.flipHorizontal()
        await wait { m.orientation.flipped && !m.isLoading && m.context != nil }
        XCTAssertEqual(m.imageSize, CGSize(width: 200, height: 150))
        XCTAssertEqual(m.crop, PixelRect(x: 200 - 110, y: 20, width: 100, height: 80))
        XCTAssertEqual(m.sources[.ha]!.image[199 - 10, 20], haBefore[10, 20])
        m.flipVertical()
        await wait { m.orientation == Orientation(rotation: 2, flipped: false) && !m.isLoading && m.context != nil }
        XCTAssertEqual(m.crop, PixelRect(x: 90, y: 150 - 100, width: 100, height: 80))
        XCTAssertEqual(m.sources[.ha]!.image[189, 129], haBefore[10, 20])
        XCTAssertEqual(m.projectDocument().rotation, 2); XCTAssertFalse(m.projectDocument().flipped)
        // A channel added now gets the same 180° turn.
        let url = try makeChannel("x-Hb.fits", kind: .hb, scale: 0.2, seed: 4)
        m.loadFiles([url])
        await wait { m.sources[.hb] != nil && !m.isLoading }
        let raw = try ImageLoader.load(url: url).planes[0]
        XCTAssertEqual(m.sources[.hb]!.image[189, 129], raw[10, 20])
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
        let m2 = ProjectModel(defaults: defaults)
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
        let m = ProjectModel(defaults: defaults)
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
        let base = ProjectModelTests()
        try await base.setUp()
        let defaults = base.defaults!
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
        let base = ProjectModelTests()
        try await base.setUp()
        let defaults = base.defaults!
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
        // Finishing stages must not reach the single-channel view (the gradient / dust editors use it too).
        m.settings.detail.clarity = 1.5; m.settings.denoise.amount = 0.8; var band = ColorBandAdjust(); band.saturation = 0.5; m.settings.colorBands.bands[.reds] = band
        var adj = m.settings.adjust(.oiii); adj.gain = 2; adj.offsetSigma = 1; m.settings.setAdjust(.oiii, adj)
        m.peekChannel = .oiii
        XCTAssertEqual(m.displayedSettings.adjust(.oiii), ChannelAdjust()); XCTAssertEqual(m.displayedSettings.normalization.method, .none)
        var keptStretch = m.settings.stretch; keptStretch.linked = false   // auto-STF is shown unlinked (one channel)
        XCTAssertEqual(m.displayedSettings.stretch, keptStretch, "the stretch is the one thing kept")
        XCTAssertEqual(m.displayedSettings.detail, DetailSettings()); XCTAssertEqual(m.displayedSettings.denoise, DenoiseSettings())
        XCTAssertEqual(m.displayedSettings.colorBands, ColorBandsSettings()); XCTAssertEqual(m.displayedSettings.post, PostSettings())
        XCTAssertEqual(m.settings.detail.clarity, 1.5, "the real settings are untouched")
        m.peekChannel = nil
        await base.wait { m.frame?.settings.mode == .matrix }
        XCTAssertEqual(m.frame?.settings.mode, .matrix)
        try? await base.tearDown()
    }
}

extension ProjectModelTests {
    func testDustSpotEditing() async throws {
        let m = try await loadedModel()
        let original = m.sources[.ha]!.image
        XCTAssertTrue(m.originalImages[.ha] === original, "without spots the original is the channel image itself")
        // Live preview while editing, dropped on cancel.
        m.beginDustEditing(.ha)
        XCTAssertEqual(m.dustEditing, .ha); XCTAssertNotNil(m.dustDraft)
        XCTAssertEqual(m.displayedSettings.mode, .monochrome)
        m.addDustSpot(at: CGPoint(x: 60, y: 70))
        XCTAssertEqual(m.dustDraft?.spots(for: .ha).count, 1)
        XCTAssertNotNil(m.selectedDustSpot)
        await wait { m.sources[.ha]!.image !== original }
        XCTAssertTrue(m.sources[.ha]!.image !== original, "the preview heals a copy")
        m.cancelDustEditing()
        XCTAssertNil(m.dustEditing); XCTAssertNil(m.dustDraft); XCTAssertTrue(m.dust.isEmpty)
        await wait { m.sources[.ha]!.image === original && !m.isLoading }
        XCTAssertTrue(m.sources[.ha]!.image === original, "cancel restores the applied state")
        // Commit: the spot is healed on every channel it was copied to; pixels far away are untouched.
        m.beginDustEditing(.ha)
        m.addDustSpot(at: CGPoint(x: 60, y: 70))
        m.copyDustSpotsToAllChannels()
        XCTAssertEqual(m.dustDraft?.spots(for: .oiii).count, 1)
        XCTAssertEqual(m.dustDraft?.spots(for: .sii).count, 1)
        let spot = m.dustDraft!.spots(for: .ha)[0]
        m.commitDustEditing()
        XCTAssertNil(m.dustEditing)
        await wait { !m.isLoading && m.sources[.ha]!.image !== original && m.sources[.oiii]!.image !== m.originalImages[.oiii]! && m.context != nil }
        XCTAssertEqual(m.dust.spots(for: .ha), [spot])
        XCTAssertEqual(m.sources[.ha]!.image[195, 145], original[195, 145])
        XCTAssertEqual(m.sources[.oiii]!.image[195, 145], m.originalImages[.oiii]![195, 145])
        XCTAssertEqual(m.projectDocument().dust, m.dust)
        // Save / open keeps the spots and heals on load.
        let url = dir.appendingPathComponent("dust.json")
        try m.saveProject(to: url)
        let m2 = ProjectModel(defaults: defaults)
        m2.openProject(from: url)
        await wait { m2.sources.count == 3 && !m2.isLoading && m2.context != nil }
        XCTAssertEqual(m2.dust, m.dust)
        var maxDiff: Float = 0
        for i in 0..<m2.sources[.ha]!.image.count { maxDiff = max(maxDiff, abs(m2.sources[.ha]!.image.pointer[i] - m.sources[.ha]!.image.pointer[i])) }
        XCTAssertEqual(maxDiff, 0, "the reopened project heals the same spots")
        // Moving the channel takes its spots along; rotating maps them; removing drops them.
        m.reassign(.ha, to: .luminance)
        XCTAssertEqual(m.dust.spots(for: .luminance), [spot]); XCTAssertTrue(m.dust.spots(for: .ha).isEmpty)
        XCTAssertNotNil(m.originalImages[.luminance]); XCTAssertNil(m.originalImages[.ha])
        m.rotateClockwise()
        await wait { !m.isLoading && m.imageSize?.width == 150 }
        let r = m.dust.spots(for: .luminance)[0]
        XCTAssertEqual(r.x, 150 - spot.y, accuracy: 1e-9); XCTAssertEqual(r.y, spot.x, accuracy: 1e-9); XCTAssertEqual(r.id, spot.id)
        XCTAssertEqual(m.sources[.luminance]!.image.width, 150)
        m.removeChannel(.luminance)
        XCTAssertTrue(m.dust.spots(for: .luminance).isEmpty); XCTAssertNil(m.originalImages[.luminance])
    }

    func testGradientEditing() async throws {
        let m = try await loadedModel()
        let original = m.sources[.ha]!.image
        // Live preview while editing, dropped on cancel.
        m.beginGradientEditing(.ha)
        XCTAssertEqual(m.gradientEditing, .ha); XCTAssertNotNil(m.gradientDraft); XCTAssertNil(m.editedGradientSpec)
        XCTAssertEqual(m.displayedSettings.mode, .monochrome)
        m.gradientDraft?.refine = false   // the geometry search is slow in debug builds; covered by GradientRemovalTests
        m.setGradientGesture(.ellipse(center: CGPoint(x: 100, y: 75), a: 120, b: 90))
        XCTAssertEqual(m.editedGradientSpec?.gesture.shape, .ellipse)
        XCTAssertEqual(m.editedGradientSpec?.mode, .subtract, "new models take the defaults")
        await wait(timeout: 30) { m.sources[.ha]!.image !== original && m.gradientFit != nil }
        XCTAssertTrue(m.sources[.ha]!.image !== original, "the preview corrects a copy")
        XCTAssertEqual(m.gradientFit?.kind, .ha)
        // The background view shows the model alone (smooth: far less variance than the channel) with its own statistics.
        let corrected = m.sources[.ha]!.image, channelNoise = m.sources[.ha]!.stats.noise
        m.gradientView = .background
        await wait(timeout: 30) { m.sources[.ha]!.image !== corrected }
        let bg = m.sources[.ha]!.image
        XCTAssertTrue(bg !== corrected && bg !== original)
        // Smooth: the pixel-to-pixel differences of the model are far below the channel's noise grain.
        var hp: Float = 0, hpc: Float = 0
        for i in 1..<bg.count { hp += abs(bg.pointer[i] - bg.pointer[i - 1]); hpc += abs(corrected.pointer[i] - corrected.pointer[i - 1]) }
        XCTAssertLessThan(hp, hpc * 0.2, "the model is smooth")
        XCTAssertLessThan(m.sources[.ha]!.stats.noise, channelNoise * 0.5, "the model source gets its own statistics (a smooth model has almost no noise)")
        m.gradientView = .corrected
        await wait(timeout: 30) { m.sources[.ha]!.image !== bg }
        // Remove model: the preview goes back to the untouched channel before Apply.
        m.clearGradient()
        XCTAssertNil(m.editedGradientSpec)
        await wait(timeout: 10) { m.sources[.ha]!.image === original }
        XCTAssertTrue(m.sources[.ha]!.image === original, "Remove model previews the channel untouched")
        m.setGradientGesture(.ellipse(center: CGPoint(x: 100, y: 75), a: 120, b: 90))
        await wait(timeout: 30) { m.sources[.ha]!.image !== original }
        m.gradientProtectMode = true
        m.addGradientRegion(at: CGPoint(x: 100, y: 75))
        XCTAssertEqual(m.gradientRegions.count, 1); XCTAssertNotNil(m.selectedGradientRegion)
        m.removeSelectedGradientRegion()
        XCTAssertTrue(m.gradientRegions.isEmpty)
        m.cancelGradientEditing()
        XCTAssertNil(m.gradientEditing); XCTAssertNil(m.gradientDraft); XCTAssertTrue(m.gradient.isEmpty)
        await wait { m.sources[.ha]!.image === original && !m.isLoading }
        XCTAssertTrue(m.sources[.ha]!.image === original, "cancel restores the applied state")
        // Commit: the model copied to every channel is applied; a divide-mode spec round-trips.
        m.beginGradientEditing(.ha)
        m.setGradientGesture(.ellipse(center: CGPoint(x: 100, y: 75), a: 120, b: 90))
        var spec = m.editedGradientSpec!
        spec.mode = .divide; spec.refine = false   // the applied defaults (refine on) came back with the new draft
        m.updateGradientSpec(spec)
        XCTAssertEqual(m.gradientDraft?.mode, .divide, "edited parameters become the defaults for new models")
        m.copyGradientToAllChannels()
        XCTAssertEqual(m.gradientDraft?.spec(for: .oiii), spec); XCTAssertEqual(m.gradientDraft?.spec(for: .sii), spec)
        m.commitGradientEditing()
        XCTAssertNil(m.gradientEditing)
        await wait(timeout: 30) { !m.isLoading && m.sources[.ha]!.image !== original && m.sources[.oiii]!.image !== m.originalImages[.oiii]! && m.context != nil }
        XCTAssertEqual(m.gradient.spec(for: .ha), spec)
        XCTAssertEqual(m.projectDocument().gradient, m.gradient)
        // Save / open re-applies the same models.
        let url = dir.appendingPathComponent("gradient.json")
        try m.saveProject(to: url)
        let m2 = ProjectModel(defaults: defaults)
        m2.openProject(from: url)
        await wait(timeout: 30) { m2.sources.count == 3 && !m2.isLoading && m2.context != nil }
        XCTAssertEqual(m2.gradient, m.gradient)
        guard let reopened = m2.sources[.ha]?.image else { return XCTFail("reopened project not loaded: sources \(m2.sources.keys) loading \(m2.isLoading) context \(m2.context != nil) error \(m2.errorMessage ?? "-")") }
        var maxDiff: Float = 0
        for i in 0..<reopened.count { maxDiff = max(maxDiff, abs(reopened.pointer[i] - m.sources[.ha]!.image.pointer[i])) }
        XCTAssertEqual(maxDiff, 0, "the reopened project applies the same models")
        // Reopening the editor on an applied model and removing it previews the untouched channel.
        let applied = m.sources[.ha]!.image
        m.beginGradientEditing(.ha)
        XCTAssertNotNil(m.editedGradientSpec)
        await wait(timeout: 30) { m.gradientFit != nil }
        m.clearGradient()
        await wait(timeout: 10) { m.sources[.ha]!.image === original }
        XCTAssertTrue(m.sources[.ha]!.image === original, "Remove model on an applied model shows the original")
        XCTAssertNotEqual(applied[100, 75], original[100, 75])
        m.cancelGradientEditing()
        await wait(timeout: 30) { !m.isLoading && m.sources[.ha]!.image !== original }
        // Moving the channel takes its model along; rotating maps it; removing drops it.
        m.reassign(.ha, to: .luminance)
        XCTAssertEqual(m.gradient.spec(for: .luminance), spec); XCTAssertNil(m.gradient.spec(for: .ha))
        m.rotateClockwise()
        await wait { !m.isLoading && m.imageSize?.width == 150 }
        let r = m.gradient.spec(for: .luminance)!.gesture
        XCTAssertEqual(r.x, 150 - 75, accuracy: 1e-9); XCTAssertEqual(r.y, 100, accuracy: 1e-9); XCTAssertEqual(r.angle, .pi / 2, accuracy: 1e-9)
        m.removeChannel(.luminance)
        XCTAssertNil(m.gradient.spec(for: .luminance))
        // Opening the dust editor closes the gradient editor and vice versa.
        m.beginGradientEditing(.oiii)
        m.beginDustEditing(.oiii)
        XCTAssertNil(m.gradientEditing); XCTAssertEqual(m.dustEditing, .oiii)
        m.beginGradientEditing(.oiii)
        XCTAssertNil(m.dustEditing); XCTAssertEqual(m.gradientEditing, .oiii)
        m.cancelGradientEditing()
        await wait { !m.isLoading }
    }

    /// A peek left behind (the slot menu swallows the gesture end) must not override the edited
    /// channel, and switching channels re-previews the one shown (its draft may have changed
    /// through Copy to all while another channel was on screen).
    /// The corrections editor renders with its own context (normalisation off): the live map must
    /// still reach the preview, so a darkening stroke shows before Apply (and Original hides it).
    func testCorrectionStrokeShowsLiveInTheEditor() async throws {
        let m = try await loadedModel()
        m.viewport = Viewport(visibleRect: CGRect(x: 0, y: 0, width: 200, height: 150), scale: 1)
        m.beginCorrectionEditing(.ha)
        XCTAssertEqual(m.correctionEditing, .ha)
        await wait { !m.isLoading && m.frame?.settings.mode == .monochrome && m.frame?.rect.width == 200 }
        func mean(_ f: PreviewFrame) -> Float {
            var sum: Float = 0
            for y in 60..<90 { for x in 85..<115 { sum += f.rgb.r[x, y] } }
            return sum / 900
        }
        let before = m.frame!
        m.correctionBrushRadius = 40
        m.correctionBrushFlow = 1
        m.correctionStrength = 3
        m.correctionDarken = true
        m.beginCorrectionStroke()
        m.paintCorrection(from: CGPoint(x: 90, y: 75), to: CGPoint(x: 110, y: 75), invert: false)
        await wait { m.frame!.cgImage !== before.cgImage }
        let painted = m.frame!
        XCTAssertLessThan(mean(painted), mean(before) * 0.7, "the stroke darkens the preview before Apply")
        m.correctionBypass = true
        await wait { m.frame!.cgImage !== painted.cgImage }
        XCTAssertEqual(mean(m.frame!), mean(before), accuracy: 1e-4, "Original shows the channel without the draft")
        m.cancelCorrectionEditing()
        XCTAssertNil(m.correctionEditing)
    }

    func testDustEditorOverridesStuckPeekAndRepreviewsOnSwitch() async throws {
        let m = try await loadedModel()
        m.peekChannel = .sii
        m.beginDustEditing(.ha)
        XCTAssertNil(m.peekChannel)
        XCTAssertEqual(m.displayedSettings.monoChannels, [.ha])
        m.addDustSpot(at: CGPoint(x: 60, y: 70))
        m.copyDustSpotsToAllChannels()
        let oiii = m.sources[.oiii]!.image
        m.peekChannel = .sii
        m.switchDustChannel(.oiii)
        XCTAssertNil(m.peekChannel)
        XCTAssertEqual(m.displayedSettings.monoChannels, [.oiii])
        await wait { m.sources[.oiii]!.image !== oiii }
        XCTAssertTrue(m.sources[.oiii]!.image !== oiii, "the copied spot is previewed on the channel switched to")
        m.viewport = Viewport(visibleRect: CGRect(x: 0, y: 0, width: 200, height: 150), scale: 1)
        await wait { m.frame != nil }
        let pb = NSPasteboard(name: NSPasteboard.Name("CombineColorsTests"))
        XCTAssertTrue(m.copyPreviewToPasteboard(pb))
        XCTAssertTrue(pb.canReadObject(forClasses: [NSImage.self], options: nil), "the preview is on the pasteboard as an image")
        let whole = m.previewImageForCopy!
        XCTAssertEqual(whole.width, m.frame!.cgImage.width)
        m.crop = PixelRect(x: 20, y: 10, width: 100, height: 50)
        await wait { !m.isLoading && m.frame != nil }
        let cropped = m.previewImageForCopy!
        let scale = CGFloat(m.frame!.cgImage.width) / m.frame!.rect.width
        XCTAssertEqual(CGFloat(cropped.width), 100 * scale, accuracy: 1.5, "the copy covers the crop only")
        XCTAssertEqual(CGFloat(cropped.height), 50 * scale, accuracy: 1.5)
        pb.releaseGlobally()
        m.cancelDustEditing()
        await wait { !m.isLoading && m.sources[.oiii]!.image === m.originalImages[.oiii]! }
    }
}
