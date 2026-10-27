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

    public init(planes: [ChannelKind: PlanarImage], analysisPlanes: [ChannelKind: PlanarImage]? = nil, settings: CombineSettings, context: RenderContext, fixedPostStretch: [ResolvedStretch]? = nil) {
        self.planes = planes; self.analysisPlanes = analysisPlanes; self.settings = settings; self.context = context; self.fixedPostStretch = fixedPostStretch
    }
}

public struct RenderResult {
    public let image: RGBImage
    /// The per-output-channel stretch used in post-combine mode (nil for pre-combine).
    public let postStretch: [ResolvedStretch]?
    /// Per-channel stretch used in pre-combine mode.
    public let channelStretch: [ChannelKind: ResolvedStretch]
    public let millis: Double
}

public enum Renderer {

    /// Channels the settings need that are not loaded.
    public static func missingChannels(settings: CombineSettings, available: Set<ChannelKind>) -> [ChannelKind] {
        settings.requiredChannels.subtracting(available).sorted { $0.rawValue < $1.rawValue }
    }

    public static func render(_ req: RenderRequest) throws -> RenderResult {
        let t0 = Date()
        let s = req.settings
        let available = Set(req.planes.keys)
        let missing = missingChannels(settings: s, available: available)
        guard missing.isEmpty else { throw RenderError.missingChannels(missing) }
        guard let anyPlane = req.planes.values.first else { throw RenderError.noOutput }
        let w = anyPlane.width, h = anyPlane.height

        // 1. Normalised linear channels (+ gain, pedestal).
        var linear: [ChannelKind: PlanarImage] = [:]
        var linStats: [ChannelKind: ChannelStats] = [:]
        let needed = s.allChannels.intersection(available)
        for k in needed {
            let (p, st) = prepareLinear(k, plane: req.planes[k]!, settings: s, context: req.context)
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
            let linkedStretch: ResolvedStretch? = s.stretch.linked
                ? Stretch.resolveLinked(s.stretch, stats: s.requiredChannels.compactMap { linStats[$0] })
                : nil
            for k in needed {
                let p = linear[k]!
                var r: ResolvedStretch
                let adj = s.adjust(k)
                if let l = linkedStretch, s.requiredChannels.contains(k), adj.stretchBoost == 0 {
                    r = l
                } else {
                    var ss = s.stretch
                    ss.targetBackground = min(max(ss.targetBackground + adj.stretchBoost, 0.01), 0.9)
                    r = Stretch.resolve(ss, stats: linStats[k]!)
                }
                channelStretch[k] = r
                r.apply(to: p)
                applyPostStretchAdjust(adj, to: p)
                stretched[k] = p
                if usesChannelStats(s) {
                    var st = linStats[k]!.transformed({ r.evaluate($0) }, scale: 1)
                    st = postStretchAdjustedStats(adj, st)
                    stStats[k] = st
                }
            }
            combined = try combine(stretched, stats: stStats, settings: s, width: w, height: h, lum: &lumPlane)
            if let l = s.luminance.source, lumPlane == nil, let p = stretched[l] { lumPlane = p }
        case .postCombine:
            // Weight acts as a linear multiplier here.
            for k in needed {
                let wgt = s.adjust(k).weight
                if wgt != 1 { linear[k]!.affine(scale: wgt, offset: 0); linStats[k] = linStats[k]!.affine(a: wgt, b: 0) }
            }
            var lumLinear: PlanarImage? = nil
            combined = try combine(linear, stats: usesChannelStats(s) ? linStats : [:], settings: s, width: w, height: h, lum: &lumLinear)
            // 3. Stretch the combined image.
            let resolved: [ResolvedStretch]
            if let fixed = req.fixedPostStretch, fixed.count == 3 {
                resolved = fixed
            } else if let analysis = req.analysisPlanes, needed.allSatisfy({ analysis[$0] != nil }) {
                var aLinear: [ChannelKind: PlanarImage] = [:]
                for k in needed where analysis[k] != nil {
                    let (p, _) = prepareLinear(k, plane: analysis[k]!, settings: s, context: req.context)
                    let wgt = s.adjust(k).weight
                    if wgt != 1 { p.affine(scale: wgt, offset: 0) }
                    aLinear[k] = p
                }
                var dummy: PlanarImage? = nil
                let aw = analysis.values.first!.width, ah = analysis.values.first!.height
                let aCombined = try combine(aLinear, stats: [:], settings: s, width: aw, height: ah, lum: &dummy)
                resolved = resolvePostStretch(aCombined, settings: s.stretch)
            } else {
                resolved = resolvePostStretch(combined, settings: s.stretch)
            }
            postStretch = resolved
            for (i, p) in combined.planes.enumerated() { resolved[i].apply(to: p) }
            // Luminance: own auto stretch.
            if let l = s.luminance.source, let p = linear[l] {
                let r = Stretch.resolve(s.stretch, stats: linStats[l]!)
                channelStretch[l] = r
                r.apply(to: p)
                lumPlane = p
            }
            if let ll = lumLinear {
                let st = Statistics.fromSample(subsample(ll))
                let r = Stretch.resolve(s.stretch, stats: st)
                r.apply(to: ll)
                lumPlane = ll
            }
        }

        // 4. Luminance injection.
        if let lum = lumPlane, s.luminance.amount > 0 {
            lum.clamp(min: 0, max: 1)
            PostProcess.injectLuminance(combined, lum: lum, amount: s.luminance.amount, method: s.luminance.method)
        }

        // 5. Colour post-processing.
        combined.clamp01()
        PostProcess.apply(s.post, to: combined)

        // 6. RGB stars.
        if s.stars.enabled, let r = req.planes[.red], let g = req.planes[.green], let b = req.planes[.blue] {
            let stars = starsImage(r: r, g: g, b: b, settings: s, context: req.context)
            PostProcess.blendStars(combined, stars: stars, settings: s.stars)
        }
        combined.clamp01()
        return RenderResult(image: combined, postStretch: postStretch, channelStretch: channelStretch, millis: Date().timeIntervalSince(t0) * 1000)
    }

    // MARK: - Pieces

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
                if let l = compiled.luminance { lum = try PixelMath.evaluateToPlane(l, env: env) }
                // Expressions may return the very same plane object (e.g. "OIII"); copy to keep planes independent.
                func own(_ p: PlanarImage) -> PlanarImage { planes.values.contains { $0 === p } ? p.copy() : p }
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

    static func starsImage(r: PlanarImage, g: PlanarImage, b: PlanarImage, settings s: CombineSettings, context: RenderContext) -> RGBImage {
        var planes: [PlanarImage] = []
        var stats: [ChannelStats] = []
        for (k, p) in [(ChannelKind.red, r), (.green, g), (.blue, b)] {
            let c = p.copy()
            (context.transforms[k] ?? .identity).apply(to: c)
            planes.append(c)
            stats.append(context.normalizedStats[k] ?? Statistics.fromSample(subsample(c)))
        }
        // Already-stretched star images (bright stars reach high values) must not be stretched again.
        let looksStretched = (stats.map { $0.percentile(0.999) }.max() ?? 0) > 0.15
        let doStretch: Bool
        switch s.stars.stretch {
        case .auto: doStretch = !looksStretched
        case .none: doStretch = false
        case .autoSTF: doStretch = true
        }
        if doStretch {
            var st = StretchSettings()
            st.method = .autoSTF
            st.targetBackground = 0.1
            let resolved = Stretch.resolveLinked(st, stats: stats)
            for p in planes { resolved.apply(to: p) }
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
            let probe = try render(RenderRequest(planes: analysisPlanes, analysisPlanes: nil, settings: settings, context: context))
            fixed = probe.postStretch
        }
        while y < h {
            if isCancelled?() == true { return nil }
            let hh = min(rows, h - y)
            var planes: [ChannelKind: PlanarImage] = [:]
            for (k, s) in sources { planes[k] = s.image.cropped(x: crop.x, y: crop.y + y, width: w, height: hh) }
            let res = try render(RenderRequest(planes: planes, analysisPlanes: nil, settings: settings, context: context, fixedPostStretch: fixed))
            for (src, dst) in zip(res.image.planes, out.planes) {
                (dst.pointer + y * w).update(from: src.pointer, count: w * hh)
            }
            y += hh
            progress?(Double(y) / Double(h))
        }
        return out
    }
}
