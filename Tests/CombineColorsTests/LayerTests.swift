import XCTest
import AstroCore
@testable import CombineColors

/// Background / Stars tabs: two sets of channels with their own settings, the Stars tab rendered
/// and blended by the Combine stars block of the Background tab.
extension ProjectModelTests {
    func starChannels() throws -> [URL] {
        [try makeChannel("s-Ha.fits", kind: .ha, scale: 0.5, seed: 11),
         try makeChannel("s-OIII.fits", kind: .oiii, scale: 0.3, seed: 12),
         try makeChannel("s-SII.fits", kind: .sii, scale: 0.2, seed: 13)]
    }

    func twoTabModel() async throws -> ProjectModel {
        let m = try await loadedModel()
        m.settings.stretch.targetBackground = 0.3
        m.switchLayer(to: .stars)
        XCTAssertEqual(m.activeLayer, .stars)
        XCTAssertTrue(m.sources.isEmpty)
        m.loadFiles(try starChannels())
        await wait { m.sources.count == 3 && !m.isLoading && m.context != nil }
        return m
    }

    func testStarsTabKeepsItsOwnChannelsAndSettings() async throws {
        let m = try await twoTabModel()
        XCTAssertEqual(Set(m.sources.values.map { $0.fileName }), ["s-Ha.fits", "s-OIII.fits", "s-SII.fits"])
        XCTAssertEqual(m.settings.normalization.method, .none, "a fresh Stars tab gets star settings")
        XCTAssertEqual(m.settings.stretch.targetBackground, ProjectModel.StarsDefaults.targetBackground)
        XCTAssertFalse(m.settings.luminance.enabled)
        m.settings.post.saturation = 1.7
        m.viewport = Viewport(visibleRect: CGRect(x: 0, y: 0, width: 200, height: 150), scale: 1)
        await wait { m.frame != nil }

        m.switchLayer(to: .background)
        await wait { m.context != nil && m.frame != nil }
        XCTAssertEqual(Set(m.sources.values.map { $0.fileName }), ["x-Ha.fits", "x-OIII.fits", "x-SII.fits"])
        XCTAssertEqual(m.settings.stretch.targetBackground, 0.3, "the Background settings are untouched")
        XCTAssertEqual(m.settings.post.saturation, 1, accuracy: 1e-6)
        XCTAssertEqual(m.layerSources(.stars).count, 3)
        XCTAssertFalse(m.settings.stars.useMask, "stars-only tabs are blended without the luminance mask")

        m.switchLayer(to: .stars)
        XCTAssertEqual(m.settings.post.saturation, 1.7, accuracy: 1e-6)
        XCTAssertEqual(Set(m.sources.values.map { $0.fileName }), ["s-Ha.fits", "s-OIII.fits", "s-SII.fits"])
        // Not while an editor is open.
        m.beginDustEditing(.ha)
        XCTAssertFalse(m.canSwitchLayer)
        m.switchLayer(to: .background)
        XCTAssertEqual(m.activeLayer, .stars)
        m.cancelDustEditing()
    }

    func testCombineBlendsTheRenderedStarsTab() async throws {
        let m = try await loadedModel()
        XCTAssertFalse(m.hasStars, "nothing to combine without channels on the Stars tab")
        m.settings.stars.enabled = true
        m.viewport = Viewport(visibleRect: CGRect(x: 0, y: 0, width: 200, height: 150), scale: 1)
        await wait { m.frame != nil }
        XCTAssertNil(m.blendStars)
        XCTAssertFalse(m.frame?.settings.stars.enabled ?? true, "no R, G, B fallback: the stars are off until the Stars tab has channels")

        m.switchLayer(to: .stars)
        m.loadFiles(try starChannels())
        await wait { m.sources.count == 3 && !m.isLoading && m.context != nil }
        m.switchLayer(to: .background)
        await wait { m.starsLayer != nil && !m.starsCompositeBusy }
        let layer = try XCTUnwrap(m.starsLayer)
        XCTAssertTrue(layer.processed)
        XCTAssertEqual(layer.width, 200)
        await wait { m.frame?.settings.stars.enabled == true }
        XCTAssertTrue(m.frame?.settings.stars.enabled ?? false)
        let combined = m.frame!

        // A change on the Stars tab is picked up when coming back.
        m.switchLayer(to: .stars)
        m.settings.stretch.targetBackground = 0.3
        m.switchLayer(to: .background)
        await wait { m.starsLayer !== layer && !m.starsCompositeBusy }
        XCTAssertFalse(m.starsLayer === layer)
        await wait { m.frame?.cgImage !== combined.cgImage && m.frame?.settings.stars.enabled == true }

        // Emptying the Stars tab turns the combination off.
        m.removeStarsLayer()
        XCTAssertFalse(m.hasStars)
        XCTAssertFalse(m.settings.stars.enabled)
        XCTAssertNil(m.starsLayer)
    }

    func testProjectKeepsTheStarsTab() async throws {
        let m = try await twoTabModel()
        m.settings.post.saturation = 1.4
        m.switchLayer(to: .background)
        m.settings.stars.enabled = true
        let url = dir.appendingPathComponent("p.json")
        try m.saveProject(to: url)
        let doc = try ProjectDocument.load(from: url)
        XCTAssertEqual(doc.channels.count, 3)
        XCTAssertEqual(doc.starsLayer?.channels.count, 3)
        XCTAssertEqual(doc.starsLayer?.settings.post.saturation ?? 0, 1.4, accuracy: 1e-6)
        XCTAssertNil(doc.starsFile)

        let m2 = ProjectModel(defaults: defaults)
        m2.openProject(from: url)
        await wait { m2.sources.count == 3 && m2.layerSources(.stars).count == 3 && !m2.isLoading && m2.starsLayer != nil }
        XCTAssertEqual(m2.activeLayer, .background)
        XCTAssertEqual(Set(m2.layerSources(.stars).values.map(\.fileName)), ["s-Ha.fits", "s-OIII.fits", "s-SII.fits"])
        XCTAssertNotNil(m2.starsLayer)
        m2.switchLayer(to: .stars)
        XCTAssertEqual(m2.settings.post.saturation, 1.4, accuracy: 1e-6, "the Stars tab keeps its own settings")
        m2.removeAll()
        XCTAssertEqual(m2.activeLayer, .background)
        XCTAssertTrue(m2.layerSources(.stars).isEmpty)
    }

    func testOldProjectStarsFileBecomesTheStarsTab() async throws {
        let ha = try makeChannel("x-Ha.fits", kind: .ha, scale: 1, seed: 1)
        let stars = dir.appendingPathComponent("rgb-stars.fits")
        try FITSWriter.write(planes: [PlanarImage(width: 200, height: 150, fill: 0.2), PlanarImage(width: 200, height: 150, fill: 0.3), PlanarImage(width: 200, height: 150, fill: 0.4)], to: stars)
        var doc = ProjectDocument()
        doc.channels = [.init(kind: .ha, path: ha.path)]
        doc.starsFile = stars.path
        doc.settings.stars.enabled = true
        let url = dir.appendingPathComponent("old.json")
        try doc.data().write(to: url)
        let m = ProjectModel(defaults: defaults)
        m.openProject(from: url)
        await wait { m.layerSources(.stars).count == 3 && !m.isLoading && m.starsLayer != nil }
        XCTAssertEqual(Set(m.layerSources(.stars).keys), [.red, .green, .blue])
        XCTAssertEqual(m.layerState(.stars).settings.stretch.method, .none, "an RGB stars image that looks stretched is not stretched again")
        XCTAssertEqual(m.activeLayer, .background)
    }

    func testRotationTurnsBothTabs() async throws {
        let m = try await twoTabModel()
        m.switchLayer(to: .background)
        m.rotateClockwise()
        await wait { !m.isLoading && m.imageSize == CGSize(width: 150, height: 200) }
        XCTAssertEqual(m.layerSources(.stars).values.first?.width, 150)
        XCTAssertEqual(m.layerSources(.stars).values.first?.height, 200)
        m.switchLayer(to: .stars)
        await wait { m.context != nil && !m.isLoading }
        XCTAssertEqual(m.imageSize, CGSize(width: 150, height: 200))
    }

    func testStarsTabRefusesAnotherSize() async throws {
        let m = try await loadedModel()
        m.switchLayer(to: .stars)
        m.loadFiles([try makeChannel("small-Ha.fits", kind: .ha, scale: 1, seed: 5, width: 100, height: 80)])
        await wait { m.errorMessage != nil && !m.isLoading }
        XCTAssertNotNil(m.errorMessage)
        XCTAssertTrue(m.sources.isEmpty)
    }
}

extension ProjectModelTests {
    func testStarsTabShowsTheCombinationAndKeepsTheView() async throws {
        let m = try await twoTabModel()
        let view = Viewport(visibleRect: CGRect(x: 40, y: 30, width: 80, height: 60), scale: 2)
        m.viewport = view
        await wait { m.frame != nil && !m.isRendering }
        func mean(_ f: PreviewFrame) -> Float { f.rgb.planes.map { $0.mean() }.reduce(0, +) / 3 }
        let alone = mean(m.frame!)
        let fit = m.fitRequest
        // The Combine checkbox works from the Stars tab: the preview shows the stars over the background.
        XCTAssertTrue(m.hasStars)
        var c = m.combineStars
        c.enabled = true
        m.combineStars = c
        XCTAssertTrue(m.layerState(.background).settings.stars.enabled, "the setting belongs to the Background tab")
        await wait { m.backgroundImage != nil && !m.backgroundCompositeBusy }
        XCTAssertNotNil(m.backgroundImage)
        await wait { m.frame.map(mean) ?? 0 > alone + 0.02 }
        XCTAssertGreaterThan(mean(m.frame!), alone + 0.02, "the background is under the stars")
        // Off again: stars alone.
        c.enabled = false
        m.combineStars = c
        await wait { abs((m.frame.map(mean) ?? 0) - alone) < 1e-4 }
        XCTAssertEqual(mean(m.frame!), alone, accuracy: 1e-4)
        // Switching tabs keeps zoom and pan.
        m.switchLayer(to: .background)
        XCTAssertEqual(m.fitRequest, fit)
        XCTAssertEqual(m.viewport, view)
    }
}

extension ProjectModelTests {
    func testCloseImageResetAllAndDefault() async throws {
        let m = try await twoTabModel()
        m.switchLayer(to: .background)
        m.settings.post.saturation = 1.8
        m.settings.tone.contrast = 0.4
        let palette = m.settings.paletteID
        m.savePreset(name: "p")

        m.resetAllProcessing()
        XCTAssertEqual(m.settings.stretch.method, .none)
        XCTAssertEqual(m.settings.normalization.method, .none)
        XCTAssertFalse(m.settings.luminance.enabled)
        XCTAssertTrue(m.settings.tone.isNeutral)
        XCTAssertEqual(m.settings.post.saturation, 1, accuracy: 1e-6)
        XCTAssertEqual(m.settings.paletteID, palette, "the palette stays")
        XCTAssertEqual(m.presets.count, 1, "presets stay")

        m.applyDefaultSettings()
        XCTAssertEqual(m.settings.stretch.method, .autoSTF)
        XCTAssertEqual(m.settings, m.defaultSettings())
        m.switchLayer(to: .stars)
        m.applyDefaultSettings()
        XCTAssertEqual(m.settings.normalization.method, .none, "the Stars tab's default is the star settings")
        m.switchLayer(to: .background)

        // Crop, rotation and flips are locked while an editor is open.
        m.beginDustEditing(.ha)
        m.beginCropEditing()
        XCTAssertFalse(m.cropEditing)
        m.rotateClockwise()
        XCTAssertFalse(m.isLoading)
        XCTAssertEqual(m.imageSize, CGSize(width: 200, height: 150))
        m.cancelDustEditing()

        m.projectURL = dir.appendingPathComponent("x.json")
        m.closeImage()
        XCTAssertTrue(m.sources.isEmpty)
        XCTAssertFalse(m.hasStars)
        XCTAssertEqual(m.activeLayer, .background)
        XCTAssertTrue(m.presets.isEmpty)
        XCTAssertNil(m.projectURL)
        XCTAssertEqual(m.settings, CombineSettings())
        XCTAssertNil(m.frame)
    }
}

extension ProjectModelTests {
    /// The samples shape stays selected when the editor switches to a channel without a model or
    /// after Remove model: Generate (or a grid parameter) must then create the model and its grid
    /// (user report 2026-11-04: no points at all on the Pleiades Hα).
    func testGenerateGridOnAChannelWithoutModel() async throws {
        let m = ProjectModel(defaults: defaults)
        m.loadFiles([try makeChannel("g-Ha.fits", kind: .ha, scale: 1, seed: 1, width: 360, height: 240),
                     try makeChannel("g-OIII.fits", kind: .oiii, scale: 0.4, seed: 2, width: 360, height: 240)])
        await wait { m.sources.count == 2 && !m.isLoading && m.context != nil }
        m.beginGradientEditing(.oiii)
        m.setGradientShape(.samples)
        await wait(timeout: 20) { !m.gradientSamples.isEmpty }
        XCTAssertFalse(m.gradientSamples.isEmpty)
        m.switchGradientChannel(.ha)
        XCTAssertEqual(m.gradientShape, .samples)
        XCTAssertNil(m.editedGradientSpec, "the other channel has no model yet")
        m.regenerateGradientGrid()
        await wait(timeout: 20) { !m.gradientSamples.isEmpty }
        XCTAssertFalse(m.gradientSamples.isEmpty, "Generate creates the model and its grid")
        m.clearGradient()
        XCTAssertTrue(m.gradientSamples.isEmpty)
        m.regenerateGradientGrid()
        await wait(timeout: 20) { !m.gradientSamples.isEmpty }
        XCTAssertFalse(m.gradientSamples.isEmpty, "and again after Remove model")
        m.cancelGradientEditing()
    }
}
