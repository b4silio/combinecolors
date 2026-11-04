import Foundation
import Accelerate

public enum RenderError: Error, LocalizedError {
    case missingChannels([ChannelKind])
    case pixelMath(PixelMathError)
    case noOutput

    public var errorDescription: String? {
        switch self {
        case .missingChannels(let k): return "Missing channels: " + k.map(\.displayName).joined(separator: ", ")
        case .pixelMath(let e): return e.errorDescription
        case .noOutput: return "Nothing to render."
        }
    }
}

/// Normalisation state derived from the loaded channels and the normalisation settings.
public struct RenderContext {
    /// Identity of this context for render caches: every `make` yields a new one.
    public let id = UUID()
    public var transforms: [ChannelKind: ChannelTransform] = [:]
    /// Whole-image statistics after normalisation.
    public var normalizedStats: [ChannelKind: ChannelStats] = [:]
    /// Star-masked statistics after normalisation.
    public var normalizedMaskedStats: [ChannelKind: ChannelStats] = [:]
    public var reference: ChannelKind? = nil
    public var referenceBackgroundSigma: Float = 0

    public init() {}

    public static func make(settings: NormalizationSettings, sources: [ChannelKind: ChannelSource]) -> RenderContext {
        var ctx = RenderContext()
        let inputs = sources.values.sorted { $0.kind.rawValue < $1.kind.rawValue }.map(\.normalizationInput)
        ctx.transforms = Normalizer.transforms(settings: settings, inputs: inputs)
        ctx.reference = Normalizer.referenceChannel(settings: settings, available: inputs)
        for (k, s) in sources {
            let t = ctx.transforms[k] ?? .identity
            ctx.normalizedStats[k] = t.transform(stats: s.stats)
            ctx.normalizedMaskedStats[k] = t.transform(stats: s.maskedStats)
        }
        if let r = ctx.reference, let s = ctx.normalizedMaskedStats[r] { ctx.referenceBackgroundSigma = s.backgroundSigma }
        else { ctx.referenceBackgroundSigma = ctx.normalizedMaskedStats.values.first?.backgroundSigma ?? 0 }
        return ctx
    }
}

public struct RenderRequest {
    public var planes: [ChannelKind: PlanarImage]
    /// Full-resolution samples (1×N planes, identical positions in every channel) used to derive
    /// post-combine stretch parameters independently of the preview resolution.
    public var analysisPlanes: [ChannelKind: PlanarImage]?
    public var settings: CombineSettings
    public var context: RenderContext
    /// When set, these resolved stretches are used for the post-combine stage instead of being derived.
    public var fixedPostStretch: [ResolvedStretch]?
    /// Apply denoise and detail inside this call (false while rendering strips).
    public var applyDetail: Bool = true
    /// Rendered pixels per full-resolution pixel (1 at full size, 0.5 at pyramid level 1, …);
    /// pixel-based radii such as the sharpening radius are scaled by it.
    public var scale: Float = 1
    /// The whole picture at a coarse pyramid level (every channel of `planes`) when `planes` is a crop
    /// of it: the detail stage takes its star cap and large-radius blurs from there, so a zoomed-in
    /// preview agrees with the whole picture and the export. `overviewRect` is the region covered by
    /// `planes`, in overview pixels.
    public var overview: [ChannelKind: PlanarImage]? = nil
    public var overviewRect: CGRect? = nil
    /// RGB stars from a stars layer (the same region as `planes`); without it the R, G and B
    /// channels serve as stars.
    public var stars: StarsInput? = nil
    /// A hand-painted correction applied live to one channel (the editor's preview); the channels
    /// themselves carry their committed corrections.
    public var correction: CorrectionInput? = nil

    public init(planes: [ChannelKind: PlanarImage], analysisPlanes: [ChannelKind: PlanarImage]? = nil, settings: CombineSettings, context: RenderContext, fixedPostStretch: [ResolvedStretch]? = nil, applyDetail: Bool = true, scale: Float = 1,
                overview: [ChannelKind: PlanarImage]? = nil, overviewRect: CGRect? = nil) {
        self.planes = planes; self.analysisPlanes = analysisPlanes; self.settings = settings; self.context = context; self.fixedPostStretch = fixedPostStretch; self.applyDetail = applyDetail; self.scale = scale
        self.overview = overview; self.overviewRect = overviewRect
    }
}

public struct RenderResult {
    public let image: RGBImage
    /// The per-output-channel stretch used in post-combine mode (nil for pre-combine).
    public let postStretch: [ResolvedStretch]?
    /// Per-channel stretch used in pre-combine mode.
    public let channelStretch: [ChannelKind: ResolvedStretch]
    public let millis: Double
    /// Set by `RenderCache`: the first stage that ran for this render (what was reused is before it).
    public var ranFrom: RenderCache.Stage? = nil
    /// Set by `RenderCache`: channels whose prepared planes (normalised, stretched, adjusted) were reused
    /// by a combine stage that had to run.
    public var reusedChannels: Int = 0
}

/// The planes of single channels ready for the combination — normalised, stretched and adjusted
/// (steps 1–2) — kept between renders by `RenderCache` so that a change to one channel, or none,
/// leaves the others' planes as they are. Every entry carries what made it. Planes handed out are
/// shared: readers only.
public final class ChannelPlaneStore {
    struct Key: Equatable {
        var adjust: ChannelAdjust
        var stage: StretchStage
        /// The stretch applied (pre-combine); nil in the post-combine stage, where the plane stays linear.
        var stretch: ResolvedStretch?
    }
    struct Entry {
        var key: Key
        var plane: PlanarImage
        /// Statistics of the plane; nil when the render did not need them (computed on demand).
        var stats: ChannelStats?
    }
    var entries: [ChannelKind: Entry] = [:]
    public init() {}
    public func removeAll() { entries = [:] }
}

public enum Renderer {

    /// Channels the settings need that are not loaded.
    public static func missingChannels(settings: CombineSettings, available: Set<ChannelKind>) -> [ChannelKind] {
        settings.requiredChannels.subtracting(available).sorted { $0.rawValue < $1.rawValue }
    }

    public static func render(_ req: RenderRequest) throws -> RenderResult {
        let t0 = Date()
        let plan = try Plan(req)
        let main = try develop(plan, planes: req.planes, context: req.context, analysisPlanes: req.analysisPlanes, fixedPostStretch: req.fixedPostStretch,
                               stars: req.stars, correction: req.correction)
        let combined = main.image
        // 7. Denoise and local contrast (non point-wise; the strip renderer applies them on the whole
        //    image), then the stars when they go in last.
        if req.applyDetail {
            let overview = try detailOverview(plan, req: req, postStretch: main.postStretch)
            Denoise.apply(combined, settings: plan.settings.denoise, scale: req.scale)
            PostProcess.detail(combined, settings: plan.settings.detail, scale: req.scale, overview: overview)
            if plan.settings.stars.enabled, plan.settings.stars.stage == .last {
                starsStage(combined, plan, planes: req.planes, context: req.context, stars: req.stars)
            }
        }
        return RenderResult(image: combined, postStretch: main.postStretch, channelStretch: main.channelStretch, millis: Date().timeIntervalSince(t0) * 1000)
    }

    /// What a render request resolves to before any pixel is touched: the channels it uses and the
    /// parsed luminance expression. Throws for missing channels or a bad expression.
    struct Plan {
        let settings: CombineSettings
        let available: Set<ChannelKind>
        let needed: Set<ChannelKind>
        let lumExpr: Expr?

        init(_ req: RenderRequest) throws {
            let s = req.settings
            let available = Set(req.planes.keys)
            let missing = missingChannels(settings: s, available: available)
            guard missing.isEmpty else { throw RenderError.missingChannels(missing) }
            if s.mode == .monochrome && s.monochromeChannels(available: available).isEmpty { throw RenderError.noOutput }
            guard !req.planes.isEmpty else { throw RenderError.noOutput }
            settings = s
            self.available = available
            needed = s.allChannels(available: available).intersection(available)
            lumExpr = try (s.luminanceApplies ? s.luminance.resolvedExpression(available: available) : nil).map { src in
                do { return try PixelMath.parse(src) } catch let e as PixelMathError { throw RenderError.pixelMath(PixelMathError(message: "Luminance: " + e.message, position: e.position)) }
            }
        }
    }

    /// The whole picture developed the same way (same stretch) for the detail stage of a cropped
    /// render; nil when the detail stage is neutral or the request carries no overview.
    static func detailOverview(_ plan: Plan, req: RenderRequest, postStretch: [ResolvedStretch]?) throws -> DetailOverview? {
        guard !plan.settings.detail.isNeutral, let ov = req.overview, let rect = req.overviewRect, plan.needed.allSatisfy({ ov[$0] != nil }) else { return nil }
        let o = try develop(plan, planes: ov, context: req.context, analysisPlanes: nil, fixedPostStretch: postStretch,
                            correction: req.correction.map { CorrectionInput(map: $0.map, strength: $0.strength, kind: $0.kind, rect: nil) })
        return DetailOverview(image: o.image, rect: rect)
    }

    struct Developed {
        var image: RGBImage
        var postStretch: [ResolvedStretch]?
        var channelStretch: [ChannelKind: ResolvedStretch]
        /// Channels whose prepared planes came from the `ChannelPlaneStore`.
        var reusedChannels: Int = 0
    }

    /// Steps 1–6 — normalise, stretch, combine, luminance, colour, stars: everything up to the
    /// non-point-wise detail stage — for one set of planes (the render itself, or its overview).
    static func develop(_ plan: Plan, planes reqPlanes: [ChannelKind: PlanarImage], context: RenderContext, analysisPlanes: [ChannelKind: PlanarImage]?,
                        fixedPostStretch: [ResolvedStretch]?, stars: StarsInput? = nil, correction: CorrectionInput? = nil) throws -> Developed {
        let d = try combineStage(plan, planes: reqPlanes, context: context, analysisPlanes: analysisPlanes, fixedPostStretch: fixedPostStretch, correction: correction)
        colourStage(d.image, plan, planes: reqPlanes, context: context, stars: stars)
        return d
    }

    /// Steps 1–4 — normalise, stretch, combine, luminance — the point-wise work that depends on the
    /// channels, the stretch and the combination only. The image comes back unclamped. With a
    /// `store`, a channel's prepared plane is taken from it when the same settings made it, and the
    /// planes prepared here are put in it (for the planes of one and the same region only).
    static func combineStage(_ plan: Plan, planes reqPlanes: [ChannelKind: PlanarImage], context: RenderContext, analysisPlanes: [ChannelKind: PlanarImage]?,
                             fixedPostStretch: [ResolvedStretch]?, correction: CorrectionInput? = nil, store: ChannelPlaneStore? = nil) throws -> Developed {
        let s = plan.settings, available = plan.available, needed = plan.needed, lumExpr = plan.lumExpr
        guard let anyPlane = reqPlanes.values.first else { throw RenderError.noOutput }
        let w = anyPlane.width, h = anyPlane.height
        let store = correction == nil ? store : nil
        var reused = 0

        // 1. Normalised linear channels (+ gain, pedestal). A live correction goes on the plane
        //    before everything else, as the committed ones do. With a store the statistics come
        //    first (from the context, no pixel work) and a plane is prepared only when needed.
        var linear: [ChannelKind: PlanarImage] = [:]
        var linStats: [ChannelKind: ChannelStats] = [:]
        func prepare(_ k: ChannelKind) -> (PlanarImage, ChannelStats) {
            var plane = reqPlanes[k]!
            if let c = correction, c.kind == k {
                plane = plane.copy()
                c.map.apply(to: plane, strength: c.strength, rect: c.rect)
            }
            return prepareLinear(k, plane: plane, settings: s, context: context)
        }
        for k in needed {
            if store != nil, let st = linearStats(k, settings: s, context: context) { linStats[k] = st; continue }
            let (p, st) = prepare(k)
            linear[k] = p; linStats[k] = st
        }
        var channelStretch: [ChannelKind: ResolvedStretch] = [:]
        var combined: RGBImage
        var lumPlane: PlanarImage? = nil
        var postStretch: [ResolvedStretch]? = nil

        switch s.stretchStage {
        case .preCombine:
            // 2. Stretch every channel on its own.
            var stretched: [ChannelKind: PlanarImage] = [:]
            var stStats: [ChannelKind: ChannelStats] = [:]
            let stretchKinds = s.mode == .monochrome ? s.monochromeChannels(available: available) : s.requiredChannels
            let linkedStretch: ResolvedStretch? = s.stretch.linked
                ? Stretch.resolveLinked(s.stretch, stats: stretchKinds.compactMap { linStats[$0] })
                : nil
            let wantStats = usesChannelStats(s)
            for k in needed {
                var r: ResolvedStretch
                let adj = s.adjust(k)
                if let l = linkedStretch, stretchKinds.contains(k), adj.stretchBoost == 0 {
                    r = l
                } else {
                    var ss = s.stretch
                    ss.targetBackground = min(max(ss.targetBackground + adj.stretchBoost, 0.01), 0.9)
                    r = Stretch.resolve(ss, stats: linStats[k]!)
                }
                channelStretch[k] = r
                let key = ChannelPlaneStore.Key(adjust: adj, stage: .preCombine, stretch: r)
                func stats() -> ChannelStats { postStretchAdjustedStats(adj, linStats[k]!.transformed({ r.evaluate($0) }, scale: 1)) }
                if let store, var e = store.entries[k], e.key == key {
                    stretched[k] = e.plane
                    reused += 1
                    if wantStats {
                        if e.stats == nil { e.stats = stats(); store.entries[k] = e }
                        stStats[k] = e.stats
                    }
                    continue
                }
                let p = linear[k] ?? prepare(k).0
                r.apply(to: p)
                applyPostStretchAdjust(adj, to: p)
                stretched[k] = p
                let st: ChannelStats? = wantStats ? stats() : nil
                if let st { stStats[k] = st }
                store?.entries[k] = ChannelPlaneStore.Entry(key: key, plane: p, stats: st)
            }
            combined = try combine(stretched, stats: stStats, settings: s, width: w, height: h, lum: &lumPlane)
            if lumPlane == nil, let e = lumExpr {
                lumPlane = try evaluateLuminance(e, planes: stretched, width: w, height: h)
            }
        case .postCombine:
            // Weight acts as a linear multiplier here.
            for k in needed {
                let adj = s.adjust(k)
                let key = ChannelPlaneStore.Key(adjust: adj, stage: .postCombine, stretch: nil)
                if let store, let e = store.entries[k], e.key == key, let st = e.stats {
                    linear[k] = e.plane; linStats[k] = st
                    reused += 1
                    continue
                }
                let p = linear[k] ?? prepare(k).0
                var st = linStats[k]!
                if adj.weight != 1 { p.affine(scale: adj.weight, offset: 0); st = st.affine(a: adj.weight, b: 0) }
                linear[k] = p; linStats[k] = st
                store?.entries[k] = ChannelPlaneStore.Entry(key: key, plane: p, stats: st)
            }
            var lumLinear: PlanarImage? = nil
            combined = try combine(linear, stats: usesChannelStats(s) ? linStats : [:], settings: s, width: w, height: h, lum: &lumLinear)
            if lumLinear == nil, let e = lumExpr {
                lumLinear = try evaluateLuminance(e, planes: linear, width: w, height: h)
            }
            // 3. Stretch parameters: fixed (cached), derived from the resolution-independent analysis
            //    samples, or — as a last resort — from this very render.
            //    Entries 0…2 are R, G, B; entry 3 (when present) is the luminance.
            var resolved: [ResolvedStretch] = []
            if let fixed = fixedPostStretch, fixed.count >= 3, (lumLinear == nil || fixed.count >= 4) {
                resolved = fixed
            } else {
                var source = combined
                var lumSource = lumLinear
                if let analysis = analysisPlanes, needed.allSatisfy({ analysis[$0] != nil }) {
                    var aLinear: [ChannelKind: PlanarImage] = [:]
                    for k in needed {
                        let (p, _) = prepareLinear(k, plane: analysis[k]!, settings: s, context: context)
                        let wgt = s.adjust(k).weight
                        if wgt != 1 { p.affine(scale: wgt, offset: 0) }
                        aLinear[k] = p
                    }
                    var aLum: PlanarImage? = nil
                    let aw = analysis.values.first!.width, ah = analysis.values.first!.height
                    source = try combine(aLinear, stats: [:], settings: s, width: aw, height: ah, lum: &aLum)
                    if aLum == nil, let e = lumExpr { aLum = try evaluateLuminance(e, planes: aLinear, width: aw, height: ah) }
                    lumSource = aLum
                }
                resolved = resolvePostStretch(source, settings: s.stretch)
                if let ls = lumSource {
                    resolved.append(Stretch.resolve(s.stretch, stats: Statistics.fromSample(subsample(ls))))
                }
            }
            postStretch = resolved
            for (i, p) in combined.planes.enumerated() { resolved[i].apply(to: p) }
            // Luminance: mixed in the linear domain, then its own auto stretch.
            if let ll = lumLinear {
                let r = resolved.count >= 4 ? resolved[3] : Stretch.resolve(s.stretch, stats: Statistics.fromSample(subsample(ll)))
                r.apply(to: ll)
                lumPlane = ll
            }
        }

        // 4. Luminance injection.
        if let lum = lumPlane, s.luminance.amount > 0 {
            lum.clamp(min: 0, max: 1)
            PostProcess.injectLuminance(combined, lum: lum, amount: s.luminance.amount, method: s.luminance.method)
        }

        return Developed(image: combined, postStretch: postStretch, channelStretch: channelStretch, reusedChannels: reused)
    }

    /// Statistics of a channel's normalised linear plane (+ gain, pedestal) from the context alone;
    /// nil when the context has none for it (then the plane itself is needed, see `prepareLinear`).
    static func linearStats(_ k: ChannelKind, settings s: CombineSettings, context: RenderContext) -> ChannelStats? {
        guard var st = context.normalizedStats[k] else { return nil }
        let adj = s.adjust(k)
        let offset = adj.offsetSigma * context.referenceBackgroundSigma
        if adj.gain != 1 || offset != 0 { st = st.affine(a: adj.gain, b: offset) }
        return st
    }

    /// Steps 5–6 on the combined image, in place: colour post-processing and the RGB stars.
    static func colourStage(_ combined: RGBImage, _ plan: Plan, planes reqPlanes: [ChannelKind: PlanarImage], context: RenderContext, stars: StarsInput?) {
        let s = plan.settings
        let w = combined.width, h = combined.height
        // 5. Colour post-processing in the order of the blocks: selective colour bands, the colour
        //    controls, the Photoshop-style grading (Color Balance, Selective Color on whites /
        //    neutrals / blacks), then the tone controls (Contrast).
        combined.clamp01()
        PostProcess.colorBands(combined, settings: s.colorBands)
        PostProcess.apply(s.post, to: combined)
        PostProcess.grade(combined, settings: s.grading)
        PostProcess.applyTone(s.tone, levels: s.post, to: combined)

        // 6. RGB stars, when they go in before denoise and detail.
        if s.stars.enabled, s.stars.stage == .beforeDetail {
            starsStage(combined, plan, planes: reqPlanes, context: context, stars: stars)
        }
        combined.clamp01()
    }

    /// The RGB stars blended in, in place: from the stars layer, else from the R, G and B channels.
    static func starsStage(_ combined: RGBImage, _ plan: Plan, planes reqPlanes: [ChannelKind: PlanarImage], context: RenderContext, stars: StarsInput?) {
        let s = plan.settings
        let w = combined.width, h = combined.height
        if let st = stars, st.planes.count == 3, st.planes[0].width == w, st.planes[0].height == h {
            let img = starsImage(planes: st.planes.map { $0.copy() }, stats: st.stats, settings: s, processed: st.processed)
            PostProcess.blendStars(combined, stars: img, settings: s.stars)
        } else if let r = reqPlanes[.red], let g = reqPlanes[.green], let b = reqPlanes[.blue] {
            let img = starsImage(r: r, g: g, b: b, settings: s, context: context)
            PostProcess.blendStars(combined, stars: img, settings: s.stars)
        }
        combined.clamp01()
    }

    // MARK: - Pieces

    /// Evaluates a luminance expression over the given planes; a plane returned unchanged is copied.
    static func evaluateLuminance(_ e: Expr, planes: [ChannelKind: PlanarImage], width w: Int, height h: Int) throws -> PlanarImage {
        var env = PixelMathEnvironment(width: w, height: h)
        for (k, p) in planes { env.set(k, plane: p) }
        do {
            let out = try PixelMath.evaluateToPlane(e, env: env)
            return planes.values.contains { $0 === out } ? out.copy() : out
        } catch let err as PixelMathError { throw RenderError.pixelMath(PixelMathError(message: "Luminance: " + err.message, position: err.position)) }
    }

    static func usesChannelStats(_ s: CombineSettings) -> Bool {
        guard s.mode == .pixelMath else { return false }
        let src = [s.pixelMath.red, s.pixelMath.green, s.pixelMath.blue, s.pixelMath.luminance].joined(separator: " ").lowercased()
        return ["cmed", "cmean", "cmin", "cmax", "cmad", "cbg", "cnoise", "cpct"].contains { src.contains($0) }
    }

    /// Copy + normalise + gain/pedestal.
    static func prepareLinear(_ k: ChannelKind, plane: PlanarImage, settings s: CombineSettings, context: RenderContext) -> (PlanarImage, ChannelStats) {
        let p = plane.copy()
        let t = context.transforms[k] ?? .identity
        t.apply(to: p)
        var st = context.normalizedStats[k] ?? Statistics.fromSample(subsample(p))
        let adj = s.adjust(k)
        let offset = adj.offsetSigma * context.referenceBackgroundSigma
        if adj.gain != 1 || offset != 0 {
            p.affine(scale: adj.gain, offset: offset)
            st = st.affine(a: adj.gain, b: offset)
        }
        if !adj.enabled {
            var z: Float = 0
            vDSP_vfill(&z, p.pointer, 1, vDSP_Length(p.count))
        }
        return (p, st)
    }

    static func applyPostStretchAdjust(_ adj: ChannelAdjust, to p: PlanarImage) {
        if adj.blackPoint != 0 {
            let a = 1 / max(1 - adj.blackPoint, 1e-3)
            p.affine(scale: a, offset: -adj.blackPoint * a)
            p.clamp(min: 0, max: 1)
        }
        if adj.gamma != 1 && adj.gamma > 0 {
            p.clamp(min: 0, max: 1)
            var g = adj.gamma; var n = Int32(p.count)
            vvpowsf(p.pointer, &g, p.pointer, &n)
        }
        if adj.weight != 1 { p.affine(scale: adj.weight, offset: 0) }
    }

    static func postStretchAdjustedStats(_ adj: ChannelAdjust, _ st: ChannelStats) -> ChannelStats {
        var s = st
        if adj.blackPoint != 0 {
            let a = 1 / max(1 - adj.blackPoint, 1e-3)
            s = s.affine(a: a, b: -adj.blackPoint * a)
        }
        if adj.gamma != 1 && adj.gamma > 0 { s = s.transformed({ pow(max($0, 0), adj.gamma) }, scale: 1) }
        if adj.weight != 1 { s = s.affine(a: adj.weight, b: 0) }
        return s
    }

    static func combine(_ planes: [ChannelKind: PlanarImage], stats: [ChannelKind: ChannelStats], settings s: CombineSettings, width w: Int, height h: Int, lum: inout PlanarImage?) throws -> RGBImage {
        let n = vDSP_Length(w * h)
        switch s.mode {
        case .monochrome:
            let kinds = s.monochromeChannels(available: Set(planes.keys)).filter { s.adjust($0).enabled }
            let gray = PlanarImage(width: w, height: h)
            guard !kinds.isEmpty else { return RGBImage(r: gray, g: gray.copy(), b: gray.copy()) }
            var k: Float = 1 / Float(kinds.count)
            for kind in kinds { vDSP_vsma(planes[kind]!.pointer, 1, &k, gray.pointer, 1, gray.pointer, 1, n) }
            return RGBImage(r: gray, g: gray.copy(), b: gray.copy())
        case .matrix:
            let out = RGBImage(width: w, height: h)
            for (o, dst) in out.planes.enumerated() {
                for (k, p) in planes {
                    var wgt = s.matrix.weight(k, o)
                    guard wgt != 0 else { continue }
                    vDSP_vsma(p.pointer, 1, &wgt, dst.pointer, 1, dst.pointer, 1, n)
                }
            }
            return out
        case .pixelMath:
            let compiled: PixelMathProgram.Compiled
            do { compiled = try s.pixelMath.compile() } catch let e as PixelMathError { throw RenderError.pixelMath(e) }
            var env = PixelMathEnvironment(width: w, height: h)
            for (k, p) in planes { env.set(k, plane: p, stats: stats[k]) }
            do {
                let r = try PixelMath.evaluateToPlane(compiled.red, env: env)
                let g = try PixelMath.evaluateToPlane(compiled.green, env: env)
                let b = try PixelMath.evaluateToPlane(compiled.blue, env: env)
                // Expressions may return the very same plane object (e.g. "OIII"); copy to keep planes independent.
                func own(_ p: PlanarImage) -> PlanarImage { planes.values.contains { $0 === p } ? p.copy() : p }
                if let l = compiled.luminance { lum = own(try PixelMath.evaluateToPlane(l, env: env)) }
                return RGBImage(r: own(r), g: own(g), b: own(b))
            } catch let e as PixelMathError { throw RenderError.pixelMath(e) }
        }
    }

    static func resolvePostStretch(_ img: RGBImage, settings st: StretchSettings) -> [ResolvedStretch] {
        let stats = img.planes.map { Statistics.fromSample(subsample($0)) }
        if st.linked {
            let r = Stretch.resolveLinked(st, stats: stats)
            return [r, r, r]
        }
        return stats.map { Stretch.resolve(st, stats: $0) }
    }

    static func subsample(_ p: PlanarImage, max: Int = 300_000) -> [Float] {
        var o = StatsOptions(); o.maxSamples = max
        return Statistics.sample(image: p, options: o).0
    }

    /// A rendered (processed) stars image blended onto `background` in place, as the Combine stars
    /// block does it: brightness, contrast, white point, saturation, mask and blend mode of `s.stars`.
    /// `stats` are the stars image's statistics (for the white point).
    public static func blendProcessedStars(onto background: RGBImage, stars: RGBImage, stats: [ChannelStats], settings s: CombineSettings) {
        let img = starsImage(planes: stars.planes.map { $0.copy() }, stats: stats, settings: s, processed: true)
        PostProcess.blendStars(background, stars: img, settings: s.stars)
        background.clamp01()
    }

    /// Stars from the R, G and B channels (normalised like the channels).
    static func starsImage(r: PlanarImage, g: PlanarImage, b: PlanarImage, settings s: CombineSettings, context: RenderContext) -> RGBImage {
        var planes: [PlanarImage] = []
        var stats: [ChannelStats] = []
        for (k, p) in [(ChannelKind.red, r), (.green, g), (.blue, b)] {
            let c = p.copy()
            (context.transforms[k] ?? .identity).apply(to: c)
            planes.append(c)
            stats.append(context.normalizedStats[k] ?? Statistics.fromSample(subsample(c)))
        }
        return starsImage(planes: planes, stats: stats, settings: s)
    }

    /// The star layer ready to blend: stretched when linear, scaled to white when asked, then the
    /// brightness (stops) and contrast (spread around the mid-tone). `planes` are consumed.
    static func starsImage(planes: [PlanarImage], stats: [ChannelStats], settings s: CombineSettings, processed: Bool = false) -> RGBImage {
        // Already-stretched star images (bright stars reach high values) must not be stretched again.
        let looksStretched = (stats.map { $0.percentile(0.999) }.max() ?? 0) > 0.15
        var doStretch: Bool
        switch s.stars.stretch {
        case .auto: doStretch = !looksStretched
        case .none: doStretch = false
        case .autoSTF: doStretch = true
        }
        if processed { doStretch = false }
        // The layer's brightest level (whole-image statistics, so a crop agrees with the export).
        var peak = stats.map { $0.percentile(0.9999) }.max() ?? 1
        if doStretch {
            var st = StretchSettings()
            st.method = .autoSTF
            st.targetBackground = 0.1
            let resolved = Stretch.resolveLinked(st, stats: stats)
            for p in planes { resolved.apply(to: p) }
            peak = resolved.evaluate(peak)
        }
        var gain = pow(2, s.stars.brightness)
        if s.stars.normalize, peak > 1e-4 { gain /= peak }
        let spread = pow(2, s.stars.contrast)
        if gain != 1 || spread != 1 {
            // v' = (v·gain − 0.5)·spread + 0.5
            let a = gain * spread, b = 0.5 - 0.5 * spread
            for p in planes { p.affine(scale: a, offset: b); p.clamp(min: 0, max: 1) }
        }
        return RGBImage(r: planes[0], g: planes[1], b: planes[2])
    }

    /// Builds the 1×N analysis sample planes for a set of sources.
    public static func analysisSamples(sources: [ChannelKind: ChannelSource], maxSamples: Int = 400_000) -> [ChannelKind: PlanarImage] {
        let kinds = sources.keys.sorted { $0.rawValue < $1.rawValue }
        let region = sources.values.first?.region
        let arrays = Statistics.multiSample(images: kinds.map { sources[$0]!.image }, maxSamples: maxSamples, region: region)
        var out: [ChannelKind: PlanarImage] = [:]
        for (k, a) in zip(kinds, arrays) where !a.isEmpty { out[k] = PlanarImage(width: a.count, height: 1, pixels: a) }
        return out
    }

    // MARK: - Full resolution, strip-wise

    /// Renders the full-resolution image in horizontal strips to bound memory.
    /// `postStretch` should come from a preview render so the result matches what was shown.
    public static func renderFull(sources: [ChannelKind: ChannelSource], settings: CombineSettings, context: RenderContext,
                                  postStretch: [ResolvedStretch]?, region: PixelRect? = nil, stripPixels: Int = 4_000_000,
                                  stars: StarsLayer? = nil,
                                  progress: ((Double) -> Void)? = nil, isCancelled: (() -> Bool)? = nil) throws -> RGBImage? {
        guard let any = sources.values.first else { throw RenderError.noOutput }
        let crop = region?.clamped(width: any.width, height: any.height) ?? PixelRect.full(width: any.width, height: any.height)
        let w = crop.width, h = crop.height
        let rows = max(1, min(h, stripPixels / w))
        let out = RGBImage(width: w, height: h)
        var y = 0
        let analysisPlanes = analysisSamples(sources: sources)
        var fixed = postStretch
        if fixed == nil && settings.stretchStage == .postCombine {
            // Derive once from the analysis planes.
            let probe = try render(RenderRequest(planes: analysisPlanes, analysisPlanes: nil, settings: settings, context: context, applyDetail: false))
            fixed = probe.postStretch
        }
        while y < h {
            if isCancelled?() == true { return nil }
            let hh = min(rows, h - y)
            var planes: [ChannelKind: PlanarImage] = [:]
            for (k, s) in sources { planes[k] = s.image.cropped(x: crop.x, y: crop.y + y, width: w, height: hh) }
            var req = RenderRequest(planes: planes, analysisPlanes: nil, settings: settings, context: context, fixedPostStretch: fixed, applyDetail: false)
            if let st = stars, st.width == any.width, st.height == any.height { req.stars = st.input(level: 0, x: crop.x, y: crop.y + y, width: w, height: hh) }
            let res = try render(req)
            for (src, dst) in zip(res.image.planes, out.planes) {
                (dst.pointer + y * w).update(from: src.pointer, count: w * hh)
            }
            y += hh
            progress?(Double(y) / Double(h))
        }
        if isCancelled?() == true { return nil }
        Denoise.apply(out, settings: settings.denoise)
        PostProcess.detail(out, settings: settings.detail, scale: 1)
        if settings.stars.enabled, settings.stars.stage == .last {
            // The stars last, strip by strip again (point-wise, so the strips agree with a whole render).
            let plan = try Plan(RenderRequest(planes: sources.mapValues { $0.image }, settings: settings, context: context))
            y = 0
            while y < h {
                if isCancelled?() == true { return nil }
                let hh = min(rows, h - y)
                let strip = RGBImage(r: out.r.cropped(x: 0, y: y, width: w, height: hh), g: out.g.cropped(x: 0, y: y, width: w, height: hh), b: out.b.cropped(x: 0, y: y, width: w, height: hh))
                var planes: [ChannelKind: PlanarImage] = [:]
                for k in [ChannelKind.red, .green, .blue] { if let src = sources[k] { planes[k] = src.image.cropped(x: crop.x, y: crop.y + y, width: w, height: hh) } }
                var input: StarsInput? = nil
                if let st = stars, st.width == any.width, st.height == any.height { input = st.input(level: 0, x: crop.x, y: crop.y + y, width: w, height: hh) }
                starsStage(strip, plan, planes: planes, context: context, stars: input)
                for (src, dst) in zip(strip.planes, out.planes) { (dst.pointer + y * w).update(from: src.pointer, count: w * hh) }
                y += hh
            }
        }
        return out
    }
}
