import XCTest
@testable import AstroCore

final class PipelineTests: XCTestCase {
    var sources: [ChannelKind: ChannelSource] = [:]

    override func setUp() {
        sources = [
            .ha: Synth.source(.ha, scale: 1, seed: 1),
            .oiii: Synth.source(.oiii, scale: 0.4, seed: 2),
            .sii: Synth.source(.sii, scale: 0.25, seed: 3),
            .red: Synth.source(.red, scale: 0.6, seed: 4),
            .green: Synth.source(.green, scale: 0.6, seed: 5),
            .blue: Synth.source(.blue, scale: 0.6, seed: 6),
            .hb: Synth.source(.hb, scale: 0.2, seed: 7),
        ]
    }

    func planes(level: Int = 0) -> [ChannelKind: PlanarImage] { sources.mapValues { $0.pyramid.level(level) } }

    func render(_ settings: CombineSettings, level: Int = 0) throws -> RenderResult {
        let ctx = RenderContext.make(settings: settings.normalization, sources: sources)
        return try Renderer.render(RenderRequest(planes: planes(level: level), analysisPlanes: Renderer.analysisSamples(sources: sources), settings: settings, context: ctx))
    }

    func assertBounded(_ img: RGBImage, file: StaticString = #filePath, line: UInt = #line) {
        for p in img.planes {
            let (lo, hi) = p.minMax()
            XCTAssertGreaterThanOrEqual(lo, 0, file: file, line: line); XCTAssertLessThanOrEqual(hi, 1, file: file, line: line)
            XCTAssertFalse(p.toArray().contains { !$0.isFinite }, file: file, line: line)
        }
    }

    func testMatrixPaletteMapsChannels() throws {
        var s = CombineSettings()
        s.select(palette: Palettes.palette(id: "sho")!)
        s.stretch.method = .none
        s.normalization.method = .none
        s.luminance.enabled = false
        let r = try render(s)
        // With no stretch and no normalisation, R == SII, G == Ha, B == OIII (clamped).
        XCTAssertEqual(r.image.r[10, 10], min(sources[.sii]!.image[10, 10], 1), accuracy: 1e-6)
        XCTAssertEqual(r.image.g[10, 10], min(sources[.ha]!.image[10, 10], 1), accuracy: 1e-6)
        XCTAssertEqual(r.image.b[10, 10], min(sources[.oiii]!.image[10, 10], 1), accuracy: 1e-6)
    }

    func testPreAndPostStretchPathsProduceValidImages() throws {
        for stage in StretchStage.allCases {
            for linked in [true, false] {
                for pal in ["sho", "hoo", "foraxx_sho", "dyn_sho_ratio", "lrgb", "sho_natural"] {
                    var s = CombineSettings()
                    s.select(palette: Palettes.palette(id: pal)!)
                    s.stretchStage = stage
                    s.stretch.linked = linked
                    s.luminance.enabled = true; s.luminance.preset = .ha
                    s.post.saturation = 1.2; s.post.scnrGreen = 0.5; s.post.hueShift = 10
                    let r = try render(s)
                    assertBounded(r.image)
                    XCTAssertEqual(r.image.width, 256)
                    // Something must be visible: the mean is far from 0 and 1.
                    let m = r.image.luma().mean()
                    XCTAssertGreaterThan(m, 0.02, "\(pal) \(stage) linked=\(linked)"); XCTAssertLessThan(m, 0.98)
                    if stage == .postCombine { XCTAssertEqual(r.postStretch?.count, 4) } else { XCTAssertNil(r.postStretch) }
                }
            }
        }
    }

    func testMedianLandsOnTargetBackground() throws {
        var s = CombineSettings()
        s.select(palette: Palettes.palette(id: "monochrome")!)
        s.monoChannels = [.ha]
        s.stretch.linked = false
        s.stretch.targetBackground = 0.3
        let r = try render(s)
        let st = Statistics.compute(image: r.image.r)
        XCTAssertEqual(st.median, 0.3, accuracy: 0.02)
    }

    func testPixelMathPermutationMatchesMatrix() throws {
        var a = CombineSettings(); a.select(palette: Palettes.palette(id: "sho")!)
        var b = a; b.mode = .pixelMath; b.pixelMath = PixelMathProgram(red: "SII", green: "Ha", blue: "OIII")
        let ra = try render(a), rb = try render(b)
        for (p, q) in zip(ra.image.planes, rb.image.planes) { XCTAssertLessThan(p.maxAbsDifference(q), 1e-5) }
    }

    func testMissingChannelsReported() {
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "sho")!)
        let ctx = RenderContext.make(settings: s.normalization, sources: [.ha: sources[.ha]!])
        XCTAssertThrowsError(try Renderer.render(RenderRequest(planes: [.ha: sources[.ha]!.image], settings: s, context: ctx))) { e in
            guard case RenderError.missingChannels(let k) = e else { return XCTFail() }
            XCTAssertEqual(Set(k), [.oiii, .sii])
        }
        XCTAssertEqual(Renderer.missingChannels(settings: s, available: [.ha, .oiii]), [.sii])
    }

    func testPixelMathErrorsSurface() {
        var s = CombineSettings(); s.mode = .pixelMath; s.pixelMath = PixelMathProgram(red: "Ha +", green: "Ha", blue: "Ha")
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        XCTAssertThrowsError(try Renderer.render(RenderRequest(planes: planes(), settings: s, context: ctx))) { e in
            guard case RenderError.pixelMath = e else { return XCTFail("\(e)") }
        }
    }

    func testChannelAdjustments() throws {
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "monochrome")!); s.monoChannels = [.ha]
        let base = try render(s).image.r.mean()
        var adj = ChannelAdjust(); adj.weight = 0.5
        s.setAdjust(.ha, adj)
        XCTAssertEqual(try render(s).image.r.mean(), base * 0.5, accuracy: 1e-3)
        adj = ChannelAdjust(); adj.enabled = false
        s.setAdjust(.ha, adj)
        XCTAssertEqual(try render(s).image.r.mean(), 0, accuracy: 1e-6)
        adj = ChannelAdjust(); adj.stretchBoost = 0.2; s.setAdjust(.ha, adj)
        XCTAssertGreaterThan(try render(s).image.r.mean(), base)
        adj = ChannelAdjust(); adj.blackPoint = 0.2; adj.gamma = 1.5; s.setAdjust(.ha, adj)
        XCTAssertLessThan(try render(s).image.r.mean(), base)
        var lin = s; lin.stretch.method = .none; lin.setAdjust(.ha, ChannelAdjust())
        let linBase = try render(lin).image.r.mean()
        adj = ChannelAdjust(); adj.gain = 3; lin.setAdjust(.ha, adj)
        XCTAssertEqual(try render(lin).image.r.mean(), linBase * 3, accuracy: 0.05)
    }

    func testStripRenderMatchesWholeRender() throws {
        for stage in StretchStage.allCases {
            var s = CombineSettings(); s.select(palette: Palettes.palette(id: "foraxx_sho")!)
            s.stretchStage = stage
            s.luminance.enabled = true; s.luminance.preset = .haMostly
            s.stars.enabled = true
            let ctx = RenderContext.make(settings: s.normalization, sources: sources)
            let whole = try Renderer.render(RenderRequest(planes: planes(), analysisPlanes: Renderer.analysisSamples(sources: sources), settings: s, context: ctx))
            var progress: [Double] = []
            let strips = try Renderer.renderFull(sources: sources, settings: s, context: ctx, postStretch: whole.postStretch, stripPixels: 256 * 50, progress: { progress.append($0) })!
            for (p, q) in zip(whole.image.planes, strips.planes) { XCTAssertLessThan(p.maxAbsDifference(q), 1e-5, "\(stage)") }
            XCTAssertEqual(progress.last!, 1, accuracy: 1e-9)
            XCTAssertGreaterThan(progress.count, 1)
            // Without a fixed post stretch the strip renderer derives it from the analysis samples: same result.
            let derived = try Renderer.renderFull(sources: sources, settings: s, context: ctx, postStretch: nil, stripPixels: 256 * 50)!
            for (p, q) in zip(whole.image.planes, derived.planes) { XCTAssertLessThan(p.maxAbsDifference(q), 1e-5, "\(stage) derived") }
        }
    }

    func testCancellation() throws {
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "sho")!)
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        let r = try Renderer.renderFull(sources: sources, settings: s, context: ctx, postStretch: nil, stripPixels: 256 * 10, isCancelled: { true })
        XCTAssertNil(r)
    }

    func testPreviewLevelIsDownsampleOfFull() throws {
        // For linear (no stretch) matrix combinations the level-1 preview equals the box-filtered full render.
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "sho")!); s.stretch.method = .none; s.luminance.enabled = false
        let full = try render(s, level: 0).image.downsampled2x()
        let half = try render(s, level: 1).image
        for (p, q) in zip(full.planes, half.planes) { XCTAssertLessThan(p.maxAbsDifference(q), 1e-5) }
    }

    func testSettingsRoundTrip() throws {
        var s = CombineSettings()
        s.select(palette: Palettes.palette(id: "foraxx_sho")!)
        var adj = ChannelAdjust(); adj.gain = 1.5; s.setAdjust(.oiii, adj)
        s.normalization.method = .histogramMatch; s.normalization.reference = .oiii
        s.stretch.method = .ghs; s.post.hueShift = -12
        let data = try JSONEncoder().encode(s)
        let back = try JSONDecoder().decode(CombineSettings.self, from: data)
        XCTAssertEqual(back, s)
        var doc = ProjectDocument(); doc.settings = s; doc.channels = [.init(kind: .ha, path: "/tmp/ha.fits")]; doc.rotation = 3
        let d2 = try doc.data()
        let docBack = try JSONDecoder().decode(ProjectDocument.self, from: d2)
        XCTAssertEqual(docBack.settings, s); XCTAssertEqual(docBack.channels.first?.kind, .ha); XCTAssertEqual(docBack.rotation, 3)
    }

    func testPalettesAvailability() {
        let avail = Palettes.available(for: [.ha, .oiii])
        XCTAssertTrue(avail.contains { $0.id == "hoo" })
        XCTAssertFalse(avail.contains { $0.id == "sho" })
        XCTAssertFalse(avail.contains { $0.id == "rgb" })
    }
}

final class DisplayPlaneTests: XCTestCase {
    func testDisplayPlaneFollowsRegion() {
        let img = PlanarImage(width: 400, height: 300, fill: 0.1)
        let full = ChannelSource(kind: .ha, image: img, fileName: "x")
        let whole = full.displayPlane(maxPixels: 400 * 300)
        XCTAssertEqual(whole.width, 400); XCTAssertEqual(whole.height, 300)
        // A region crops the thumbnail at full resolution when it fits the budget...
        let region = PixelRect(x: 40, y: 30, width: 200, height: 100)
        let cropped = ChannelSource(full, region: region)
        let t0 = cropped.displayPlane(maxPixels: 200 * 100)
        XCTAssertEqual(t0.width, 200); XCTAssertEqual(t0.height, 100)
        // ...and picks a coarser level (region-based budget) otherwise.
        let t1 = cropped.displayPlane(maxPixels: 200 * 100 / 2)
        XCTAssertEqual(t1.width, 100); XCTAssertEqual(t1.height, 50)
        // A full-image region behaves like no region.
        let same = ChannelSource(full, region: PixelRect.full(width: 400, height: 300)).displayPlane(maxPixels: 400 * 300)
        XCTAssertEqual(same.width, 400)
    }
}

final class PostProcessTests: XCTestCase {
    func sample() -> RGBImage {
        let img = RGBImage(width: 4, height: 1)
        img.r.pointer.update(from: [0.8, 0.2, 0.5, 0.0], count: 4)
        img.g.pointer.update(from: [0.2, 0.8, 0.5, 0.0], count: 4)
        img.b.pointer.update(from: [0.2, 0.2, 0.5, 1.0], count: 4)
        return img
    }

    func testSaturationZeroGivesGray() {
        let img = sample()
        PostProcess.saturation(img, factor: 0)
        for i in 0..<4 { XCTAssertEqual(img.r.pointer[i], img.g.pointer[i], accuracy: 1e-6); XCTAssertEqual(img.g.pointer[i], img.b.pointer[i], accuracy: 1e-6) }
    }

    func testSCNRReducesGreenOnly() {
        let img = sample()
        PostProcess.scnr(img, amount: 1)
        XCTAssertEqual(img.g.pointer[1], 0.2, accuracy: 1e-6)   // min(0.8, (0.2+0.2)/2)
        XCTAssertEqual(img.g.pointer[0], 0.2, accuracy: 1e-6)   // unchanged (already <= mean)
        XCTAssertEqual(img.r.pointer[1], 0.2, accuracy: 1e-6)
    }

    func testHueShiftFullCircleIsIdentity() {
        let img = sample(), ref = sample()
        PostProcess.hueShift(img, degrees: 360)
        for (p, q) in zip(img.planes, ref.planes) { XCTAssertLessThan(p.maxAbsDifference(q), 1e-4) }
        PostProcess.hueShift(img, degrees: 120)
        XCTAssertGreaterThan(img.planes[0].maxAbsDifference(ref.planes[0]), 0.1)
    }

    func testLuminanceInjection() {
        for method in LuminanceMethod.allCases {
            let img = sample()
            let lum = PlanarImage(width: 4, height: 1, pixels: [0.5, 0.5, 0.5, 0.5])
            PostProcess.injectLuminance(img, lum: lum, amount: 1, method: method)
            let y = img.luma()
            // Saturated colours clip when brightened, so the achieved luminance can fall short.
            for i in 0..<3 { XCTAssertEqual(y.pointer[i], 0.5, accuracy: method == .lab ? 0.12 : 0.06, "\(method) px \(i)") }
            // Hue preserved: the dominant channel stays dominant.
            XCTAssertGreaterThan(img.r.pointer[0], img.g.pointer[0])
            XCTAssertGreaterThan(img.g.pointer[1], img.r.pointer[1])
        }
    }

    func testStarsBlendScreenBrightens() {
        let img = RGBImage(width: 2, height: 1)
        let stars = RGBImage(width: 2, height: 1)
        stars.r.pointer[0] = 0.9; stars.g.pointer[0] = 0.9; stars.b.pointer[0] = 0.9
        var s = StarsBlendSettings(); s.enabled = true; s.useMask = true; s.threshold = 0.1; s.softness = 0.2
        PostProcess.blendStars(img, stars: stars, settings: s)
        XCTAssertGreaterThan(img.r.pointer[0], 0.8)
        XCTAssertEqual(img.r.pointer[1], 0, accuracy: 1e-6)
    }

    func testApplyKeepsRange() {
        let img = sample()
        var s = PostSettings(); s.gamma = 0.7; s.blackPoint = 0.1; s.whitePoint = 0.9; s.vibrance = 0.5; s.saturation = 1.5; s.hueShift = -30
        PostProcess.apply(s, to: img)
        for p in img.planes { let (lo, hi) = p.minMax(); XCTAssertGreaterThanOrEqual(lo, 0); XCTAssertLessThanOrEqual(hi, 1) }
    }
}

final class DualBandTests: XCTestCase {
    func testSplitPresets() {
        let r = PlanarImage(width: 2, height: 1, pixels: [0.8, 0.1]), g = PlanarImage(width: 2, height: 1, pixels: [0.2, 0.6]), b = PlanarImage(width: 2, height: 1, pixels: [0.4, 0.4])
        var s = DualBandSettings()
        let (ha, o3) = DualBand.split(r: r, g: g, b: b, settings: s)
        XCTAssertEqual(ha.toArray(), [0.8, 0.1])
        XCTAssertEqual(o3.toArray(), [0.3, 0.5])
        XCTAssertEqual(s.outputKinds.0, .ha); XCTAssertEqual(s.outputKinds.1, .oiii)
        s.crosstalkSecond = 0.5
        let (_, o3b) = DualBand.split(r: r, g: g, b: b, settings: s)
        XCTAssertEqual(o3b.toArray()[0], 0, "negative clamps to 0")
        XCTAssertEqual(o3b.toArray()[1], 0.45, accuracy: 1e-6)
        s.preset = .siiHb
        XCTAssertEqual(s.outputKinds.0, .sii); XCTAssertEqual(s.outputKinds.1, .hb)
    }
}

/// Runs only when the user's sample data is present.
final class RealDataIntegrationTests: XCTestCase {
    let soul = URL(fileURLWithPath: "/Users/basilio/Pictures/Astrophotography/_Processing/Soul Core/27h No-Flats")
    let lowers = URL(fileURLWithPath: "/Users/basilio/Pictures/Astrophotography/_Processing/LowersNebula/Stacks")

    func testLoadAndRenderLowersTIFFs() throws {
        let files = ["LowersNebula-36h-Ha.tif", "LowersNebula-36h-O3.tif", "LowersNebula-36h-S2.tif"].map { lowers.appendingPathComponent($0) }
        try XCTSkipUnless(files.allSatisfy { FileManager.default.fileExists(atPath: $0.path) }, "sample data not present")
        var sources: [ChannelKind: ChannelSource] = [:]
        for f in files {
            let l = try ImageLoader.load(url: f)
            XCTAssertNotNil(l.detectedKind)
            sources[l.detectedKind!] = ChannelSource(kind: l.detectedKind!, image: l.planes[0], fileName: l.fileName)
        }
        XCTAssertEqual(Set(sources.keys), [.ha, .oiii, .sii])
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "sho")!)
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        let lvl = sources[.ha]!.pyramid.levelIndex(maxPixels: 500_000)
        let r = try Renderer.render(RenderRequest(planes: sources.mapValues { $0.pyramid.level(lvl) }, analysisPlanes: Renderer.analysisSamples(sources: sources), settings: s, context: ctx))
        XCTAssertLessThan(r.millis, 2000)
        XCTAssertGreaterThan(r.image.luma().mean(), 0.05)
    }

    func testSoulCoreFITSHeaderDetection() throws {
        let f = soul.appendingPathComponent("Soul_Core-Sulphur_II-session_1-lpc.fits")
        try XCTSkipUnless(FileManager.default.fileExists(atPath: f.path), "sample data not present")
        let h = try FITSReader.readHeader(url: f)
        XCTAssertEqual(h.string("FILTER"), "Sulphur II")
        XCTAssertEqual(ChannelDetection.detect(fileName: f.lastPathComponent, filterKeyword: h.string("FILTER")), .sii)
        XCTAssertEqual(h.int("NAXIS1"), 7272)
    }
}

final class TolerantDecodingTests: XCTestCase {
    func testMissingAndUnknownKeysFallBackToDefaults() throws {
        let json = """
        {"version": 1, "channels": [{"kind": "Ha", "path": "/x/ha.fits"}],
         "settings": {"paletteID": "hoo", "mode": "matrix", "stars": {"enabled": true}, "stretch": {"method": "ghs", "ghsLogD": 3.5, "bogus": 1},
                      "normalization": {"method": "noSuchMethod"}, "post": {"saturation": 1.5}, "channels": {"OIII": {"gain": 2}}},
         "crop": {"x": 1, "y": 2, "width": 30, "height": 40}}
        """
        let doc = try JSONDecoder().decode(ProjectDocument.self, from: Data(json.utf8))
        XCTAssertEqual(doc.channels.first?.kind, .ha)
        XCTAssertEqual(doc.settings.paletteID, "hoo")
        XCTAssertTrue(doc.settings.stars.enabled)
        XCTAssertEqual(doc.settings.stars.stretch, .auto)            // missing key → default
        XCTAssertEqual(doc.settings.stretch.method, .ghs)
        XCTAssertEqual(doc.settings.stretch.ghsLogD, 3.5)
        XCTAssertEqual(doc.settings.stretch.targetBackground, StretchSettings().targetBackground)
        XCTAssertEqual(doc.settings.normalization.method, NormalizationSettings().method)   // unknown value → default
        XCTAssertEqual(doc.rotation, 0)                                                    // missing → no rotation
        XCTAssertEqual(doc.settings.post.saturation, 1.5)
        XCTAssertEqual(doc.settings.adjust(.oiii).gain, 2)
        XCTAssertEqual(doc.crop, PixelRect(x: 1, y: 2, width: 30, height: 40))
        XCTAssertNil(doc.starsFile)
        let empty = try JSONDecoder().decode(CombineSettings.self, from: Data("{}".utf8))
        XCTAssertEqual(empty, CombineSettings())
        // Round trip of current defaults remains exact.
        let s = CombineSettings()
        XCTAssertEqual(try JSONDecoder().decode(CombineSettings.self, from: try JSONEncoder().encode(s)), s)
    }
}

final class LuminancePresetTests: XCTestCase {
    func testPresetExpressionsAdaptToAvailableChannels() {
        let all: Set<ChannelKind> = [.ha, .oiii, .sii, .luminance]
        XCTAssertEqual(LuminancePreset.ha.expression(available: all, custom: ""), "Ha")
        XCTAssertEqual(LuminancePreset.haMostly.expression(available: all, custom: ""), "0.8*Ha + 0.1*OIII + 0.1*SII")
        // SII missing → weights renormalised over Hα and OIII.
        XCTAssertEqual(LuminancePreset.haMostly.expression(available: [.ha, .oiii], custom: ""), "0.889*Ha + 0.111*OIII")
        XCTAssertEqual(LuminancePreset.haMostly.expression(available: [.ha], custom: ""), "Ha")
        XCTAssertNil(LuminancePreset.haMostly.expression(available: [.red, .green], custom: ""))
        XCTAssertEqual(LuminancePreset.maxLines.expression(available: [.ha, .oiii], custom: ""), "max(Ha, OIII)")
        XCTAssertEqual(LuminancePreset.meanLines.expression(available: [.sii], custom: ""), "SII")
        XCTAssertEqual(LuminancePreset.custom.expression(available: all, custom: " Ha^0.5 "), "Ha^0.5")
        XCTAssertNil(LuminancePreset.custom.expression(available: all, custom: "  "))
        for p in LuminancePreset.allCases where p != .custom {
            if let e = p.expression(available: all, custom: "") { XCTAssertNoThrow(try PixelMath.parse(e), p.rawValue) }
        }
    }

    func testFallbackChainAndRequiredChannels() {
        var l = LuminanceSettings()
        XCTAssertEqual(l.resolvedExpression(available: [.ha, .oiii]), "Ha")
        XCTAssertEqual(l.resolvedExpression(available: [.luminance, .red]), "L")       // Hα missing → L
        XCTAssertNil(l.resolvedExpression(available: [.red, .green, .blue]))
        l.enabled = false
        XCTAssertNil(l.resolvedExpression(available: [.ha]))
        l.enabled = true; l.preset = .custom; l.customExpression = "0.5*Ha + 0.5*Hb"
        XCTAssertEqual(l.requiredChannels(available: [.ha]), [.ha, .hb])
        var s = CombineSettings(); s.luminance = l
        XCTAssertTrue(s.allChannels(available: [.ha, .hb]).contains(.hb))
    }

    func testRendererUsesMixedLuminance() throws {
        let sources: [ChannelKind: ChannelSource] = [.ha: Synth.source(.ha), .oiii: Synth.source(.oiii, scale: 0.4, seed: 2), .sii: Synth.source(.sii, scale: 0.3, seed: 3)]
        let planes = sources.mapValues { $0.image }
        for stage in StretchStage.allCases {
            var s = CombineSettings(); s.select(palette: Palettes.palette(id: "hoo")!); s.stretchStage = stage
            s.luminance.enabled = false
            let ctx = RenderContext.make(settings: s.normalization, sources: sources)
            let samples = Renderer.analysisSamples(sources: sources)
            let plain = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: s, context: ctx))
            s.luminance.enabled = true; s.luminance.preset = .haMostly
            let mixed = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: s, context: ctx))
            XCTAssertGreaterThan(mixed.image.r.maxAbsDifference(plain.image.r), 0.01, "\(stage)")
            for p in mixed.image.planes { let (lo, hi) = p.minMax(); XCTAssertGreaterThanOrEqual(lo, 0); XCTAssertLessThanOrEqual(hi, 1) }
            // Custom expression that references a missing channel surfaces as a render error.
            s.luminance.preset = .custom; s.luminance.customExpression = "Ha + Hb"
            XCTAssertThrowsError(try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: s, context: ctx)))
            s.luminance.customExpression = "Ha +"
            XCTAssertThrowsError(try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: s, context: ctx)))
        }
    }

    func testOldProjectLuminanceKeyMigrates() throws {
        let on = try JSONDecoder().decode(LuminanceSettings.self, from: Data(#"{"source":"L","amount":0.5}"#.utf8))
        XCTAssertTrue(on.enabled); XCTAssertEqual(on.preset, .lum); XCTAssertEqual(on.amount, 0.5)
        let off = try JSONDecoder().decode(LuminanceSettings.self, from: Data(#"{"source":null,"amount":1}"#.utf8))
        XCTAssertFalse(off.enabled)
        let fresh = try JSONDecoder().decode(LuminanceSettings.self, from: Data("{}".utf8))
        XCTAssertEqual(fresh, LuminanceSettings())
    }
}

final class MonoLuminanceTests: XCTestCase {
    func testMonoPalettesIgnoreLuminance() throws {
        let sources: [ChannelKind: ChannelSource] = [.ha: Synth.source(.ha), .oiii: Synth.source(.oiii, scale: 0.4, seed: 2)]
        let planes = sources.mapValues { $0.image }
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "monochrome")!); s.monoChannels = [.oiii]
        s.luminance.enabled = true; s.luminance.preset = .ha
        XCTAssertFalse(s.luminanceApplies)
        XCTAssertFalse(s.allChannels(available: [.ha, .oiii]).contains(.ha))
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        let r = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: Renderer.analysisSamples(sources: sources), settings: s, context: ctx))
        var off = s; off.luminance.enabled = false
        let r2 = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: Renderer.analysisSamples(sources: sources), settings: off, context: ctx))
        XCTAssertLessThan(r.image.r.maxAbsDifference(r2.image.r), 1e-6)
        s.select(palette: Palettes.palette(id: "hoo")!)
        XCTAssertTrue(s.luminanceApplies)
    }
}


final class MonochromeModeTests: XCTestCase {
    func testMonochromeAveragesAllLoadedChannels() throws {
        let sources: [ChannelKind: ChannelSource] = [.ha: Synth.source(.ha), .oiii: Synth.source(.oiii, scale: 0.4, seed: 2), .sii: Synth.source(.sii, scale: 0.3, seed: 3)]
        let planes = sources.mapValues { $0.image }
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "monochrome")!)
        s.stretch.method = .none; s.normalization.method = .none
        XCTAssertEqual(s.mode, .monochrome)
        XCTAssertTrue(s.requiredChannels.isEmpty)
        XCTAssertEqual(s.allChannels(available: [.ha, .oiii, .sii]), [.ha, .oiii, .sii])
        XCTAssertTrue(Palettes.available(for: [.ha]).contains { $0.isMonochrome })
        XCTAssertFalse(Palettes.available(for: []).contains { $0.isMonochrome })
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        let r = try Renderer.render(RenderRequest(planes: planes, settings: s, context: ctx))
        let expected = (planes[.ha]![10, 10] + planes[.oiii]![10, 10] + planes[.sii]![10, 10]) / 3
        XCTAssertEqual(r.image.r[10, 10], min(expected, 1), accuracy: 1e-6)
        XCTAssertEqual(r.image.g[10, 10], r.image.r[10, 10]); XCTAssertEqual(r.image.b[10, 10], r.image.r[10, 10])
        // Weight sliders and the channel subset are honoured.
        var adj = ChannelAdjust(); adj.weight = 0.5; s.setAdjust(.ha, adj)
        s.monoChannels = [.ha, .oiii]
        let r2 = try Renderer.render(RenderRequest(planes: planes, settings: s, context: ctx))
        XCTAssertEqual(r2.image.r[10, 10], min((0.5 * planes[.ha]![10, 10] + planes[.oiii]![10, 10]) / 2, 1), accuracy: 1e-6)
        // Disabled channels drop out of the average.
        adj = ChannelAdjust(); adj.enabled = false; s.setAdjust(.oiii, adj); s.setAdjust(.ha, ChannelAdjust())
        let r3 = try Renderer.render(RenderRequest(planes: planes, settings: s, context: ctx))
        XCTAssertEqual(r3.image.r[10, 10], min(planes[.ha]![10, 10], 1), accuracy: 1e-6)
        // Round trip.
        let back = try JSONDecoder().decode(CombineSettings.self, from: try JSONEncoder().encode(s))
        XCTAssertEqual(back, s)
    }
}

final class ColorBandsAndDetailTests: XCTestCase {
    func testHSLRoundTrip() {
        for (r, g, b) in [(0.8, 0.2, 0.2), (0.1, 0.9, 0.3), (0.2, 0.2, 0.9), (0.5, 0.5, 0.5), (1, 0.6, 0), (0.3, 0.8, 0.8)] as [(Float, Float, Float)] {
            let (h, s, l) = PostProcess.rgbToHSL(r, g, b)
            let (r2, g2, b2) = PostProcess.hslToRGB(h, s, l)
            XCTAssertEqual(r2, r, accuracy: 1e-4); XCTAssertEqual(g2, g, accuracy: 1e-4); XCTAssertEqual(b2, b, accuracy: 1e-4)
        }
        XCTAssertEqual(PostProcess.rgbToHSL(1, 0, 0).0, 0)
        XCTAssertEqual(PostProcess.rgbToHSL(0, 1, 0).0, 120)
        XCTAssertEqual(PostProcess.rgbToHSL(0, 0, 1).0, 240)
    }

    func testBandWeightsPartitionUnity() {
        let centers = ColorBand.allCases.map(\.hue)
        for h in stride(from: 0, to: 360, by: 7) {
            let sum = (0..<centers.count).map { PostProcess.bandWeight(hue: Float(h), index: $0, centers: centers) }.reduce(0, +)
            XCTAssertEqual(sum, 1, accuracy: 1e-4, "hue \(h)")
        }
        XCTAssertEqual(PostProcess.bandWeight(hue: 0, index: 0, centers: centers), 1)
        XCTAssertEqual(PostProcess.bandWeight(hue: 15, index: 0, centers: centers), 0.5, accuracy: 1e-5)
        XCTAssertEqual(PostProcess.bandWeight(hue: 330, index: 0, centers: centers), 0.5, accuracy: 1e-5)
        XCTAssertEqual(PostProcess.bandWeight(hue: 120, index: 0, centers: centers), 0)
    }

    func testSelectiveColourAffectsOnlyItsBand() {
        let img = RGBImage(width: 3, height: 1)
        img.r.pointer.update(from: [0.8, 0.2, 0.5], count: 3)   // red, blue, grey
        img.g.pointer.update(from: [0.2, 0.2, 0.5], count: 3)
        img.b.pointer.update(from: [0.2, 0.9, 0.5], count: 3)
        var s = ColorBandsSettings()
        var a = ColorBandAdjust(); a.saturation = -1; s.set(.reds, a)
        PostProcess.colorBands(img, settings: s)
        XCTAssertEqual(img.r[0, 0], img.g[0, 0], accuracy: 1e-4, "red pixel fully desaturated")
        XCTAssertEqual(img.b[1, 0], 0.9, accuracy: 1e-5, "blue pixel untouched")
        XCTAssertEqual(img.r[2, 0], 0.5, accuracy: 1e-5, "grey untouched")
        var hueShift = ColorBandsSettings(); var ha = ColorBandAdjust(); ha.hue = 60; hueShift.set(.blues, ha)
        let img2 = RGBImage(width: 1, height: 1); img2.r.pointer[0] = 0.2; img2.g.pointer[0] = 0.2; img2.b.pointer[0] = 0.9
        PostProcess.colorBands(img2, settings: hueShift)
        XCTAssertEqual(PostProcess.rgbToHSL(img2.r[0, 0], img2.g[0, 0], img2.b[0, 0]).0, 300, accuracy: 0.5)
        XCTAssertTrue(ColorBandsSettings().isNeutral)
    }

    func testTextureAndClarityDoNotDigHalosAroundStars() {
        // Flat background with a bright star: local contrast must not darken the ring around it.
        let w = 256, h = 256
        let img = RGBImage(width: w, height: h)
        for y in 0..<h { for x in 0..<w {
            let dx = Float(x - 128), dy = Float(y - 128)
            let v = 0.3 + 0.7 * exp(-(dx * dx + dy * dy) / 18)
            img.r[x, y] = v; img.g[x, y] = v; img.b[x, y] = v
        } }
        var d = DetailSettings(); d.texture = 1; d.clarity = 1
        PostProcess.detail(img, settings: d)
        let lum = img.luma()
        var ringMin: Float = 1
        for y in 0..<h { for x in 0..<w {
            let r = ((Float(x - 128) * Float(x - 128)) + (Float(y - 128) * Float(y - 128))).squareRoot()
            if r > 6 && r < 40 { ringMin = min(ringMin, lum[x, y]) }
        } }
        XCTAssertGreaterThan(ringMin, 0.3 - 0.01, "dark halo around the star")
        XCTAssertGreaterThan(lum[128, 128], 0.9)
    }

    func testDetailChangesLocalContrastAndStripsMatch() throws {
        let sources: [ChannelKind: ChannelSource] = [.ha: Synth.source(.ha), .oiii: Synth.source(.oiii, scale: 0.4, seed: 2)]
        let planes = sources.mapValues { $0.image }
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "hoo")!)
        let ctx = RenderContext.make(settings: s.normalization, sources: sources)
        let samples = Renderer.analysisSamples(sources: sources)
        let plain = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: s, context: ctx))
        s.detail.clarity = 0.8; s.detail.dehaze = 0.5
        let det = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: s, context: ctx))
        XCTAssertGreaterThan(det.image.r.maxAbsDifference(plain.image.r), 0.01)
        for p in det.image.planes { let (lo, hi) = p.minMax(); XCTAssertGreaterThanOrEqual(lo, 0); XCTAssertLessThanOrEqual(hi, 1) }
        // Clarity raises the local contrast: the std-dev of the high-pass grows.
        func highPassStd(_ p: PlanarImage) -> Float { let b = PostProcess.pyramidBlur(p, levels: 2); var sum: Float = 0; for i in 0..<p.count { let d = p.pointer[i] - b.pointer[i]; sum += d * d }; return (sum / Float(p.count)).squareRoot() }
        var onlyClarity = s; onlyClarity.detail = DetailSettings(); onlyClarity.detail.clarity = 0.8
        let cl = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: onlyClarity, context: ctx))
        XCTAssertGreaterThan(highPassStd(cl.image.luma()), highPassStd(plain.image.luma()))
        // Strip-wise export equals the whole render.
        let strips = try Renderer.renderFull(sources: sources, settings: s, context: ctx, postStretch: det.postStretch, stripPixels: 256 * 40)!
        for (p, q) in zip(det.image.planes, strips.planes) { XCTAssertLessThan(p.maxAbsDifference(q), 1e-4) }
        XCTAssertEqual(PostProcess.blurLevels(width: 7272, relativeRadius: 0.015), 7)
        XCTAssertEqual(PostProcess.blurLevels(width: 3636, relativeRadius: 0.015), 6)
        // The three scales are ordered: texture < clarity < dehaze.
        XCTAssertLessThan(PostProcess.blurLevels(width: 2000, relativeRadius: PostProcess.textureRadius), PostProcess.blurLevels(width: 2000, relativeRadius: PostProcess.clarityRadius))
        XCTAssertLessThan(PostProcess.blurLevels(width: 2000, relativeRadius: PostProcess.clarityRadius), PostProcess.blurLevels(width: 2000, relativeRadius: PostProcess.dehazeRadius))
        var tex = s; tex.detail = DetailSettings(); tex.detail.texture = 0.8
        let tx = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: samples, settings: tex, context: ctx))
        XCTAssertGreaterThan(highPassStd(tx.image.luma()), highPassStd(plain.image.luma()))
        // Settings round trip with bands.
        var ba = ColorBandAdjust(); ba.hue = 12; s.colorBands.set(.teals, ba)
        XCTAssertEqual(try JSONDecoder().decode(CombineSettings.self, from: try JSONEncoder().encode(s)), s)
    }
}
