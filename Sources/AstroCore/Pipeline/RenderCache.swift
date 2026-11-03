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
/// Detail (local contrast, sharpening) runs on a copy of the last one; when the stars go in last
/// its output is kept too (**detailed**) and a star slider re-blends only. Moving a denoise
/// slider therefore costs denoise + detail, a colour slider costs colour + stars + denoise + detail,
/// and only a change to the channels, the stretch or the combination runs the whole pipeline. Even
/// then the single channels' prepared planes (normalised, stretched, adjusted) are kept per channel
/// (`ChannelPlaneStore`): a gain or weight change on one channel prepares that channel only.
///
/// The cache holds up to four images of the preview size (an intermediate behind a neutral stage is
/// the same object as the one before it, not a copy). Renders with a live correction, or without the
/// detail stage, bypass it. Not thread safe: one render queue owns it. The result image of a render
/// whose detail stage is neutral is the cached intermediate itself — read it, do not write to it.
public final class RenderCache {
    public enum Stage: String { case combine, colour, denoise, detail, stars, nothing }

    private var inputs: RenderInputKey? = nil
    private let channels = ChannelPlaneStore()
    private var combined: (settings: CombineSettings, developed: Renderer.Developed)? = nil
    private var developed: (settings: CombineSettings, image: RGBImage, overview: DetailOverview?)? = nil
    private var denoised: (settings: DenoiseSettings, image: RGBImage)? = nil
    /// The detail stage's output, kept only when the stars go in after it (a star slider re-blends only).
    private var detailed: (settings: DetailSettings, scale: Float, base: RGBImage, image: RGBImage)? = nil

    public init() {}

    public func clear() { inputs = nil; channels.removeAll(); combined = nil; developed = nil; denoised = nil; detailed = nil }

    /// Settings as far as the combined stage sees them (later stages neutral).
    static func combineSettings(_ s: CombineSettings) -> CombineSettings {
        var c = developSettings(s)
        c.colorBands = ColorBandsSettings(); c.post = PostSettings(); c.grading = ColorGradingSettings(); c.stars = StarsBlendSettings()
        return c
    }

    /// Settings as far as the developed stage sees them (denoise and detail neutral, and the stars
    /// when they go in last).
    static func developSettings(_ s: CombineSettings) -> CombineSettings {
        var c = s
        c.denoise = DenoiseSettings(); c.detail = DetailSettings()
        if s.stars.stage == .last { c.stars = StarsBlendSettings() }
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
        var reusedChannels = 0
        if !reuse {
            if key != inputs { channels.removeAll() }
            let d = try Renderer.combineStage(plan, planes: req.planes, context: req.context, analysisPlanes: req.analysisPlanes, fixedPostStretch: req.fixedPostStretch, store: channels)
            reusedChannels = d.reusedChannels
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
            denoised = nil; detailed = nil
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
            denoised = (s.denoise, img); detailed = nil
            afterDenoise = img
            if ranFrom == .nothing { ranFrom = .denoise }
        }

        // 7b. Detail, on a copy: the cached intermediates stay as they are. With the stars last the
        //     output is kept, so that a star slider finds it.
        let starsLast = s.stars.enabled && s.stars.stage == .last
        let afterDetail: RGBImage
        if s.detail.isNeutral {
            afterDetail = afterDenoise
        } else if starsLast, let d = detailed, d.settings == s.detail, d.scale == req.scale, d.base === afterDenoise {
            afterDetail = d.image
        } else {
            let img = afterDenoise.copy()
            PostProcess.detail(img, settings: s.detail, scale: req.scale, overview: developed!.overview)
            detailed = starsLast ? (s.detail, req.scale, afterDenoise, img) : nil
            afterDetail = img
            if ranFrom == .nothing { ranFrom = .detail }
        }

        // 7c. The stars last, on a copy.
        let out: RGBImage
        if starsLast {
            out = afterDetail.copy()
            Renderer.starsStage(out, plan, planes: req.planes, context: req.context, stars: req.stars)
            if ranFrom == .nothing { ranFrom = .stars }
        } else {
            out = afterDetail
        }
        var res = RenderResult(image: out, postStretch: base.postStretch, channelStretch: base.channelStretch, millis: Date().timeIntervalSince(t0) * 1000)
        res.ranFrom = ranFrom
        res.reusedChannels = reusedChannels
        return res
    }
}
