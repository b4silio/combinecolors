import Foundation

/// What a preview render reads: the channel sources, the stars layer, the normalisation context and
/// the region of the pyramid level rendered. Two requests with equal keys read the same pixels.
public struct RenderInputKey: Hashable {
    public var sources: [ChannelKind: UUID]
    public var stars: UUID?
    public var context: UUID
    public var level: Int
    public var x: Int
    public var y: Int
    public var width: Int
    public var height: Int

    public init(sources: [ChannelKind: UUID], stars: UUID?, context: UUID, level: Int, x: Int, y: Int, width: Int, height: Int) {
        self.sources = sources; self.stars = stars; self.context = context; self.level = level
        self.x = x; self.y = y; self.width = width; self.height = height
    }
}

/// Keeps the intermediate images of the last preview render so that a change late in the pipeline
/// only re-runs the stages after it. Three intermediates, each valid while its own inputs are unchanged:
///
/// * **combined** — steps 1–4: normalise, stretch, combine, luminance;
/// * **developed** — the above plus colour (bands, global controls, grading) and stars;
/// * **denoised** — the above plus denoise.
///
/// Detail (local contrast, sharpening) runs on a copy of the last one every time. Moving a denoise
/// slider therefore costs denoise + detail, a colour slider costs colour + stars + denoise + detail,
/// and only a change to the channels, the stretch or the combination runs the whole pipeline.
///
/// The cache holds up to three images of the preview size (an intermediate behind a neutral stage is
/// the same object as the one before it, not a copy). Renders with a live correction, or without the
/// detail stage, bypass it. Not thread safe: one render queue owns it. The result image of a render
/// whose detail stage is neutral is the cached intermediate itself — read it, do not write to it.
public final class RenderCache {
    public enum Stage: String { case combine, colour, denoise, detail, nothing }

    private var inputs: RenderInputKey? = nil
    private var combined: (settings: CombineSettings, developed: Renderer.Developed)? = nil
    private var developed: (settings: CombineSettings, image: RGBImage, overview: DetailOverview?)? = nil
    private var denoised: (settings: DenoiseSettings, image: RGBImage)? = nil

    public init() {}

    public func clear() { inputs = nil; combined = nil; developed = nil; denoised = nil }

    /// Settings as far as the combined stage sees them (later stages neutral).
    static func combineSettings(_ s: CombineSettings) -> CombineSettings {
        var c = developSettings(s)
        c.colorBands = ColorBandsSettings(); c.post = PostSettings(); c.grading = ColorGradingSettings(); c.stars = StarsBlendSettings()
        return c
    }

    /// Settings as far as the developed stage sees them (denoise and detail neutral).
    static func developSettings(_ s: CombineSettings) -> CombineSettings {
        var c = s
        c.denoise = DenoiseSettings(); c.detail = DetailSettings()
        return c
    }

    public func render(_ req: RenderRequest, inputs key: RenderInputKey) throws -> RenderResult {
        guard req.applyDetail, req.correction == nil else { return try Renderer.render(req) }
        let t0 = Date()
        let plan = try Renderer.Plan(req)
        let s = plan.settings
        var ranFrom = Stage.nothing

        // 1–4. Combined: reused while the inputs, the settings up to the luminance and the fixed
        //      stretch agree. A request without a fixed stretch derives it from the same inputs, so
        //      it matches whatever the cached render used.
        let combineSettings = RenderCache.combineSettings(s)
        var reuse = key == inputs && combined?.settings == combineSettings
        if reuse, let f = req.fixedPostStretch, f != combined!.developed.postStretch { reuse = false }
        if !reuse {
            let d = try Renderer.combineStage(plan, planes: req.planes, context: req.context, analysisPlanes: req.analysisPlanes, fixedPostStretch: req.fixedPostStretch)
            inputs = key
            combined = (combineSettings, d)
            developed = nil; denoised = nil
            ranFrom = .combine
        }
        let base = combined!.developed

        // 5–6. Developed: colour and stars.
        let developSettings = RenderCache.developSettings(s)
        if developed?.settings != developSettings {
            let img = base.image.copy()
            Renderer.colourStage(img, plan, planes: req.planes, context: req.context, stars: req.stars)
            developed = (developSettings, img, nil)
            denoised = nil
            if ranFrom == .nothing { ranFrom = .colour }
        }
        if !s.detail.isNeutral, developed!.overview == nil {
            developed!.overview = try Renderer.detailOverview(plan, req: req, postStretch: base.postStretch)
        }

        // 7a. Denoised.
        let afterDenoise: RGBImage
        if s.denoise.isNeutral {
            afterDenoise = developed!.image
        } else if let d = denoised, d.settings == s.denoise {
            afterDenoise = d.image
        } else {
            let img = developed!.image.copy()
            Denoise.apply(img, settings: s.denoise)
            denoised = (s.denoise, img)
            afterDenoise = img
            if ranFrom == .nothing { ranFrom = .denoise }
        }

        // 7b. Detail, on a copy: the cached intermediates stay as they are.
        let out: RGBImage
        if s.detail.isNeutral {
            out = afterDenoise
        } else {
            out = afterDenoise.copy()
            PostProcess.detail(out, settings: s.detail, scale: req.scale, overview: developed!.overview)
            if ranFrom == .nothing { ranFrom = .detail }
        }
        var res = RenderResult(image: out, postStretch: base.postStretch, channelStretch: base.channelStretch, millis: Date().timeIntervalSince(t0) * 1000)
        res.ranFrom = ranFrom
        return res
    }
}
