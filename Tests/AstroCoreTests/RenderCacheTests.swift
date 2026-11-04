import XCTest
@testable import AstroCore

/// The render cache must give the very same pixels as a render from scratch, whatever stage changed.
final class RenderCacheTests: XCTestCase {
    private func sources() -> [ChannelKind: ChannelSource] {
        var out: [ChannelKind: ChannelSource] = [:]
        for (i, k) in [ChannelKind.ha, .oiii, .sii].enumerated() {
            out[k] = Synth.source(k, seed: UInt64(7 + i), width: 160, height: 120)
        }
        return out
    }

    private func key(_ s: [ChannelKind: ChannelSource], ctx: RenderContext, level: Int = 0) -> RenderInputKey {
        RenderInputKey(sources: s.mapValues(\.id), stars: nil, context: ctx.id, level: level, x: 0, y: 0, width: 160 >> level, height: 120 >> level)
    }

    /// Equal images. Vectorised Accelerate math may differ in the last bit with the buffers'
    /// alignment (a cached plane vs a fresh one), which showed up only under the load of the full
    /// suite: a few 1e-6 at most; anything larger is a real difference.
    private func assertSame(_ a: RGBImage, _ b: RGBImage, _ what: String, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertEqual(a.width, b.width, what, file: file, line: line)
        for (p, q) in zip(a.planes, b.planes) {
            var maxDiff: Float = 0
            for i in 0..<p.count { maxDiff = max(maxDiff, abs(p.pointer[i] - q.pointer[i])) }
            XCTAssertLessThanOrEqual(maxDiff, 1e-5, what, file: file, line: line)
        }
    }

    func testCachedRendersMatchFreshOnesAndReuseTheRightStages() throws {
        let srcs = sources()
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "sho")!)
        s.detail.clarity = 0.4; s.denoise.amount = 0.3; s.post.saturation = 0.2
        let ctx = RenderContext.make(settings: s.normalization, sources: srcs)
        let analysis = Renderer.analysisSamples(sources: srcs)
        let cache = RenderCache()
        let planes = srcs.mapValues { $0.pyramid.level(0) }
        func request(_ s: CombineSettings) -> RenderRequest { RenderRequest(planes: planes, analysisPlanes: analysis, settings: s, context: ctx) }
        func check(_ s: CombineSettings, expect: RenderCache.Stage, _ what: String) throws {
            let cached = try cache.render(request(s), inputs: key(srcs, ctx: ctx))
            let fresh = try Renderer.render(request(s))
            assertSame(cached.image, fresh.image, what)
            XCTAssertEqual(cached.postStretch, fresh.postStretch, what)
            XCTAssertEqual(cached.ranFrom, expect, what)
        }
        try check(s, expect: .combine, "first render runs everything")
        try check(s, expect: .nothing, "same settings: denoise and detail outputs are both kept")
        s.detail.sharpen = 0.8
        try check(s, expect: .detail, "detail change")
        s.denoise.amount = 0.6
        try check(s, expect: .denoise, "denoise change")
        s.grading.setBalance(.midtones, { var t = ToneBalance(); t.cyanRed = 0.3; return t }())
        try check(s, expect: .colour, "grading change")
        s.stretch.targetBackground = 0.3
        try check(s, expect: .combine, "stretch change")
        s.detail = DetailSettings(); s.denoise = DenoiseSettings()
        try check(s, expect: .nothing, "neutral late stages: the developed image is returned as is")
        s.stretchStage = .postCombine
        try check(s, expect: .combine, "post-combine stretch")
        // A fixed post-stretch equal to the derived one is a hit; a different one is not.
        let derived = try cache.render(request(s), inputs: key(srcs, ctx: ctx))
        var fixed = request(s); fixed.fixedPostStretch = derived.postStretch
        XCTAssertEqual(try cache.render(fixed, inputs: key(srcs, ctx: ctx)).ranFrom, .nothing)
        fixed.fixedPostStretch = derived.postStretch!.map { _ in ResolvedStretch.identity }
        let other = try cache.render(fixed, inputs: key(srcs, ctx: ctx))
        XCTAssertEqual(other.ranFrom, .combine)
        assertSame(other.image, try Renderer.render(fixed).image, "fixed stretch differs")
        // Other inputs (another level, another context) start over.
        let ctx2 = RenderContext.make(settings: s.normalization, sources: srcs)
        XCTAssertEqual(try cache.render(request(s), inputs: key(srcs, ctx: ctx2)).ranFrom, .combine)
        let lvl1 = srcs.mapValues { $0.pyramid.level(1) }
        let r1 = try cache.render(RenderRequest(planes: lvl1, analysisPlanes: analysis, settings: s, context: ctx2), inputs: key(srcs, ctx: ctx2, level: 1))
        XCTAssertEqual(r1.ranFrom, .combine)
        XCTAssertEqual(r1.image.width, 80)
    }

    func testOneChannelChangePreparesThatChannelOnly() throws {
        let srcs = sources()
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "sho")!)
        let ctx = RenderContext.make(settings: s.normalization, sources: srcs)
        let analysis = Renderer.analysisSamples(sources: srcs)
        let cache = RenderCache()
        let planes = srcs.mapValues { $0.pyramid.level(0) }
        func check(_ s: CombineSettings, reused: Int, _ what: String) throws {
            let cached = try cache.render(RenderRequest(planes: planes, analysisPlanes: analysis, settings: s, context: ctx), inputs: key(srcs, ctx: ctx))
            let fresh = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: analysis, settings: s, context: ctx))
            assertSame(cached.image, fresh.image, what)
            XCTAssertEqual(cached.ranFrom, .combine, what)
            XCTAssertEqual(cached.reusedChannels, reused, what)
        }
        try check(s, reused: 0, "first render prepares every channel")
        var a = s.adjust(.oiii); a.gain = 1.4; s.setAdjust(.oiii, a)
        try check(s, reused: 0, "gain of one channel with a linked stretch: the stretch of every channel moves")
        s.stretch.linked = false
        try check(s, reused: 0, "unlinked: every channel stretched on its own")
        a.gain = 1.8; s.setAdjust(.oiii, a)
        try check(s, reused: 2, "gain of one channel, unlinked: that channel only")
        s.stretch.linked = true
        a.weight = 0.7; s.setAdjust(.oiii, a)
        try check(s, reused: 0, "back to linked")
        a.weight = 0.6; s.setAdjust(.oiii, a)
        try check(s, reused: 2, "weight of one channel (after the stretch): that channel only")
        s.luminance.amount = 0.5
        try check(s, reused: 3, "luminance amount: every plane reused")
        s.stretch.targetBackground = 0.3
        try check(s, reused: 0, "stretch: every plane prepared again")
        // Post-combine: the linear planes carry the weight; the others stay.
        s.stretchStage = .postCombine
        try check(s, reused: 0, "post-combine: linear planes")
        a.weight = 1.3; s.setAdjust(.oiii, a)
        try check(s, reused: 2, "post-combine weight of one channel")
        s.stretch.targetBackground = 0.25
        try check(s, reused: 3, "post-combine stretch: the linear planes stay")
        // Pixel-math luminance naming a bare channel must not alias the stored plane (it is clamped later).
        s.stretchStage = .preCombine
        s.mode = .pixelMath; s.pixelMath = PixelMathProgram(red: "SII", green: "Ha", blue: "OIII", luminance: "Ha")
        var ha = s.adjust(.ha); ha.weight = 1.5; s.setAdjust(.ha, ha)
        try check(s, reused: 0, "pixel math with a Ha luminance")
        a.weight = 0.9; s.setAdjust(.oiii, a)
        try check(s, reused: 2, "the stored Ha plane (reused) was not clamped by the first render's luminance")
    }

    func testStarsLastReblendOnly() throws {
        var srcs = sources()
        for (i, k) in [ChannelKind.red, .green, .blue].enumerated() { srcs[k] = Synth.source(k, scale: 0.6, seed: UInt64(20 + i), width: 160, height: 120) }
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "sho")!)
        s.denoise.amount = 0.4; s.detail.clarity = 0.5
        s.stars.enabled = true; s.stars.stage = .last
        let ctx = RenderContext.make(settings: s.normalization, sources: srcs)
        let analysis = Renderer.analysisSamples(sources: srcs)
        let cache = RenderCache()
        let planes = srcs.mapValues { $0.pyramid.level(0) }
        func check(_ s: CombineSettings, expect: RenderCache.Stage, _ what: String) throws {
            let cached = try cache.render(RenderRequest(planes: planes, analysisPlanes: analysis, settings: s, context: ctx), inputs: key(srcs, ctx: ctx))
            let fresh = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: analysis, settings: s, context: ctx))
            assertSame(cached.image, fresh.image, what)
            XCTAssertEqual(cached.ranFrom, expect, what)
        }
        try check(s, expect: .combine, "first render")
        s.stars.amount = 0.6
        try check(s, expect: .stars, "star amount: re-blend on the kept detail output")
        s.stars.brightness = 0.5
        try check(s, expect: .stars, "star brightness")
        s.detail.sharpen = 0.7
        try check(s, expect: .detail, "detail: detail + stars")
        s.denoise.amount = 0.2
        try check(s, expect: .denoise, "denoise: denoise + detail + stars")
        s.stars.stage = .beforeDetail
        try check(s, expect: .colour, "stars before detail: part of the developed image")
        s.stars.amount = 0.3
        try check(s, expect: .colour, "a star slider then re-runs from the colour stage")
    }

    func testLiveCorrectionAndStripRendersBypassTheCache() throws {
        let srcs = sources()
        var s = CombineSettings(); s.select(palette: Palettes.palette(id: "sho")!)
        let ctx = RenderContext.make(settings: s.normalization, sources: srcs)
        let cache = RenderCache()
        let planes = srcs.mapValues { $0.pyramid.level(0) }
        var req = RenderRequest(planes: planes, settings: s, context: ctx)
        XCTAssertEqual(try cache.render(req, inputs: key(srcs, ctx: ctx)).ranFrom, .combine)
        req.correction = CorrectionInput(map: CorrectionMap(imageWidth: 160, imageHeight: 120), strength: 1, kind: .ha, rect: nil)
        XCTAssertNil(try cache.render(req, inputs: key(srcs, ctx: ctx)).ranFrom)
        req.correction = nil; req.applyDetail = false
        XCTAssertNil(try cache.render(req, inputs: key(srcs, ctx: ctx)).ranFrom)
        req.applyDetail = true
        XCTAssertEqual(try cache.render(req, inputs: key(srcs, ctx: ctx)).ranFrom, .nothing, "the bypassed renders left the cache alone")
    }
}
