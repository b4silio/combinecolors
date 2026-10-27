import Foundation
import Accelerate

/// How the per-channel signal levels are equalised before combination.
public enum NormalizationMethod: String, Codable, CaseIterable, Identifiable {
    case none
    case backgroundEqualize
    case linearFit
    case backgroundDispersion
    case backgroundSignal
    case noiseWeighted
    case histogramMatch

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .none: return "None"
        case .backgroundEqualize: return "Background equalise"
        case .linearFit: return "Linear fit"
        case .backgroundDispersion: return "Background + dispersion"
        case .backgroundSignal: return "Background + signal"
        case .noiseWeighted: return "Noise weighted"
        case .histogramMatch: return "Histogram match"
        }
    }

    public var summary: String {
        switch self {
        case .none: return "Channels are used as loaded."
        case .backgroundEqualize: return "Shifts each channel so that its sky background equals the reference background (offset only)."
        case .linearFit: return "Robust iterative linear regression of the reference on each channel (PixInsight LinearFit style). Matches background and overall signal."
        case .backgroundDispersion: return "Matches the sky background and the robust dispersion (MADN) of each channel to the reference."
        case .backgroundSignal: return "Models each channel as background + emission: subtracts the sigma-clipped sky, then scales so the chosen signal percentile of the star-masked image equals the reference."
        case .noiseWeighted: return "Subtracts the background and scales so every channel has the same noise floor. Faint channels keep their lower SNR instead of being amplified."
        case .histogramMatch: return "Maps the cumulative histogram of each channel onto the reference (quantile mapping)."
        }
    }
}

public struct NormalizationSettings: Codable, Equatable, Hashable {
    public var method: NormalizationMethod = .backgroundSignal
    /// Reference channel; nil = automatic (Hα when present, else the strongest signal).
    public var reference: ChannelKind? = nil
    /// Compute statistics on star-masked pixels.
    public var useStarMask: Bool = true
    /// Percentile (0..1) defining the "signal" level for background+signal matching.
    public var signalPercentile: Float = 0.995
    /// Linear-fit rejection threshold (sigma) and iterations.
    public var fitRejectSigma: Float = 2.5
    public var fitIterations: Int = 5
    /// Exclude the extreme tails from histogram matching (fraction, each side).
    public var histogramClip: Float = 0.0005

    public init() {}
}

/// A uniformly sampled look-up table over [lo, hi] with linear extrapolation outside.
public struct UniformLUT: Codable, Equatable {
    public var lo: Float
    public var hi: Float
    public var table: [Float]
    public var slopeBelow: Float
    public var slopeAbove: Float

    public init(lo: Float, hi: Float, table: [Float], slopeBelow: Float, slopeAbove: Float) {
        self.lo = lo; self.hi = hi; self.table = table; self.slopeBelow = slopeBelow; self.slopeAbove = slopeAbove
    }

    public func evaluate(_ x: Float) -> Float {
        if x <= lo { return table[0] + slopeBelow * (x - lo) }
        if x >= hi { return table[table.count - 1] + slopeAbove * (x - hi) }
        let pos = (x - lo) / (hi - lo) * Float(table.count - 1)
        let i = Int(pos)
        let f = pos - Float(i)
        if i + 1 < table.count { return table[i] * (1 - f) + table[i + 1] * f }
        return table[i]
    }

    public func apply(to plane: PlanarImage) {
        let n = plane.count
        let p = plane.pointer
        let out = UnsafeMutablePointer<Float>.allocate(capacity: n)
        defer { out.deallocate() }
        var scale = Float(table.count - 1) / (hi - lo)
        var offset = -lo * scale
        vDSP_vtabi(p, 1, &scale, &offset, table, vDSP_Length(table.count), out, 1, vDSP_Length(n))
        // Extrapolation: below lo and above hi.
        let tmp = UnsafeMutablePointer<Float>.allocate(capacity: n)
        defer { tmp.deallocate() }
        let nn = vDSP_Length(n)
        var negHi = -hi, zero: Float = 0, big = Float.greatestFiniteMagnitude
        vDSP_vsadd(p, 1, &negHi, tmp, 1, nn)            // x - hi
        vDSP_vclip(tmp, 1, &zero, &big, tmp, 1, nn)     // max(x - hi, 0)
        var sa = slopeAbove
        vDSP_vsma(tmp, 1, &sa, out, 1, out, 1, nn)
        var negLo = -lo, negBig = -Float.greatestFiniteMagnitude
        vDSP_vsadd(p, 1, &negLo, tmp, 1, nn)            // x - lo
        vDSP_vclip(tmp, 1, &negBig, &zero, tmp, 1, nn)  // min(x - lo, 0)
        var sb = slopeBelow
        vDSP_vsma(tmp, 1, &sb, out, 1, out, 1, nn)
        p.update(from: out, count: n)
    }
}

/// The transform applied to one channel by the normaliser.
public enum ChannelTransform: Equatable {
    case identity
    case affine(a: Float, b: Float)
    case lut(UniformLUT)

    public func evaluate(_ x: Float) -> Float {
        switch self {
        case .identity: return x
        case .affine(let a, let b): return a * x + b
        case .lut(let l): return l.evaluate(x)
        }
    }

    public func apply(to plane: PlanarImage) {
        switch self {
        case .identity: break
        case .affine(let a, let b): plane.affine(scale: a, offset: b)
        case .lut(let l): l.apply(to: plane)
        }
    }

    /// Statistics after the transform.
    public func transform(stats: ChannelStats) -> ChannelStats {
        switch self {
        case .identity: return stats
        case .affine(let a, let b): return stats.affine(a: a, b: b)
        case .lut(let l):
            // Approximate the local scale from the central slope.
            let s = (l.evaluate(stats.median + stats.madn) - l.evaluate(stats.median - stats.madn)) / max(2 * stats.madn, 1e-12)
            return stats.transformed({ l.evaluate($0) }, scale: s)
        }
    }

    public var description: String {
        switch self {
        case .identity: return "identity"
        case .affine(let a, let b): return String(format: "×%.4f %+.5f", a, b)
        case .lut: return "histogram LUT"
        }
    }
}

/// Everything the normaliser needs to know about one channel.
public struct NormalizationInput {
    public let kind: ChannelKind
    public let image: PlanarImage
    /// Statistics computed on the whole image (zeros excluded).
    public let stats: ChannelStats
    /// Statistics computed with the star mask (falls back to `stats`).
    public let maskedStats: ChannelStats
    public let mask: PlanarImage?
    public let maskLevel: Int
    public let region: PixelRect?

    public init(kind: ChannelKind, image: PlanarImage, stats: ChannelStats, maskedStats: ChannelStats? = nil, mask: PlanarImage? = nil, maskLevel: Int = 0, region: PixelRect? = nil) {
        self.kind = kind; self.image = image; self.stats = stats
        self.maskedStats = maskedStats ?? stats; self.mask = mask; self.maskLevel = maskLevel; self.region = region
    }
}

public enum Normalizer {

    /// Chooses the reference channel.
    public static func referenceChannel(settings: NormalizationSettings, available: [NormalizationInput]) -> ChannelKind? {
        if let r = settings.reference, available.contains(where: { $0.kind == r }) { return r }
        if let ha = available.first(where: { $0.kind == .ha }) { return ha.kind }
        if let l = available.first(where: { $0.kind == .luminance }) { return l.kind }
        return available.max(by: { $0.maskedStats.signalStrength < $1.maskedStats.signalStrength })?.kind
    }

    /// Computes the transform for every channel.
    public static func transforms(settings: NormalizationSettings, inputs: [NormalizationInput]) -> [ChannelKind: ChannelTransform] {
        var out: [ChannelKind: ChannelTransform] = [:]
        for i in inputs { out[i.kind] = .identity }
        guard settings.method != .none, let refKind = referenceChannel(settings: settings, available: inputs),
              let ref = inputs.first(where: { $0.kind == refKind }) else { return out }
        let refStats = settings.useStarMask ? ref.maskedStats : ref.stats
        for input in inputs where input.kind != refKind {
            let st = settings.useStarMask ? input.maskedStats : input.stats
            switch settings.method {
            case .none:
                break
            case .backgroundEqualize:
                out[input.kind] = .affine(a: 1, b: refStats.background - st.background)
            case .backgroundDispersion:
                let a = st.backgroundSigma > 0 ? refStats.backgroundSigma / st.backgroundSigma : 1
                out[input.kind] = .affine(a: a, b: refStats.background - a * st.background)
            case .backgroundSignal:
                let q = Double(settings.signalPercentile)
                let sRef = refStats.percentile(q) - refStats.background
                let sCh = st.percentile(q) - st.background
                let a = sCh > 0 && sRef > 0 ? sRef / sCh : 1
                out[input.kind] = .affine(a: a, b: refStats.background - a * st.background)
            case .noiseWeighted:
                let a = st.noise > 0 && refStats.noise > 0 ? refStats.noise / st.noise : 1
                out[input.kind] = .affine(a: a, b: refStats.background - a * st.background)
            case .linearFit:
                let fit = linearFit(reference: ref, channel: input, settings: settings)
                out[input.kind] = .affine(a: fit.a, b: fit.b)
            case .histogramMatch:
                out[input.kind] = .lut(histogramMatchLUT(source: st, reference: refStats, clip: settings.histogramClip))
            }
        }
        return out
    }

    /// Robust iterative least squares fit `ref ≈ a·channel + b`.
    public static func linearFit(reference: NormalizationInput, channel: NormalizationInput, settings: NormalizationSettings) -> (a: Float, b: Float) {
        guard reference.image.width == channel.image.width && reference.image.height == channel.image.height else { return (1, 0) }
        var opts = StatsOptions()
        opts.maxSamples = 400_000
        opts.region = reference.region
        if settings.useStarMask {
            opts.mask = reference.mask
            opts.maskLevel = reference.maskLevel
        }
        let (xs, ys) = Statistics.pairedSample(channel.image, reference.image, options: opts, maskB: settings.useStarMask ? channel.mask : nil)
        return robustLinearFit(x: xs, y: ys, rejectSigma: settings.fitRejectSigma, iterations: settings.fitIterations)
    }

    /// Iteratively re-weighted least squares with sigma rejection on the residuals.
    public static func robustLinearFit(x: [Float], y: [Float], rejectSigma: Float = 2.5, iterations: Int = 5) -> (a: Float, b: Float) {
        guard x.count == y.count, x.count > 8 else { return (1, 0) }
        var keep = [Bool](repeating: true, count: x.count)
        var a: Double = 1, b: Double = 0
        for _ in 0..<max(1, iterations) {
            var sx = 0.0, sy = 0.0, sxx = 0.0, sxy = 0.0, n = 0.0
            for i in 0..<x.count where keep[i] {
                let xi = Double(x[i]), yi = Double(y[i])
                sx += xi; sy += yi; sxx += xi * xi; sxy += xi * yi; n += 1
            }
            guard n > 8 else { break }
            let den = n * sxx - sx * sx
            guard abs(den) > 1e-30 else { break }
            a = (n * sxy - sx * sy) / den
            b = (sy - a * sx) / n
            // Residual sigma (robust).
            var res = [Float](repeating: 0, count: x.count)
            var cnt = 0
            for i in 0..<x.count where keep[i] { res[cnt] = Float(Double(y[i]) - (a * Double(x[i]) + b)); cnt += 1 }
            res.removeSubrange(cnt...)
            res.withUnsafeMutableBufferPointer { vDSP_vsort($0.baseAddress!, vDSP_Length($0.count), 1) }
            let med = Statistics.percentile(sorted: res, q: 0.5)
            let sigma = 1.4826 * Statistics.medianAbsoluteDeviation(sorted: res, center: med)
            guard sigma > 0 else { break }
            var changed = false
            for i in 0..<x.count {
                let r = Float(Double(y[i]) - (a * Double(x[i]) + b))
                let k = abs(r - med) <= rejectSigma * sigma
                if k != keep[i] { changed = true; keep[i] = k }
            }
            if !changed { break }
        }
        return (Float(a), Float(b))
    }

    /// Quantile mapping from the source distribution onto the reference distribution.
    public static func histogramMatchLUT(source: ChannelStats, reference: ChannelStats, clip: Float = 0.0005, knots: Int = 2048, tableSize: Int = 65536) -> UniformLUT {
        let qLo = Double(clip), qHi = 1 - Double(clip)
        let lo = source.percentile(qLo), hi = source.percentile(qHi)
        guard hi > lo, !source.sortedSample.isEmpty, !reference.sortedSample.isEmpty else {
            return UniformLUT(lo: 0, hi: 1, table: [0, 1], slopeBelow: 1, slopeAbove: 1)
        }
        // Knots in quantile space.
        var sx: [Float] = [], sy: [Float] = []
        sx.reserveCapacity(knots + 1); sy.reserveCapacity(knots + 1)
        for i in 0...knots {
            let q = qLo + (qHi - qLo) * Double(i) / Double(knots)
            sx.append(source.percentile(q)); sy.append(reference.percentile(q))
        }
        // Enforce monotonicity.
        for i in 1..<sx.count { sx[i] = max(sx[i], sx[i - 1]); sy[i] = max(sy[i], sy[i - 1]) }
        var table = [Float](repeating: 0, count: tableSize)
        var k = 0
        for i in 0..<tableSize {
            let x = lo + (hi - lo) * Float(i) / Float(tableSize - 1)
            while k + 1 < sx.count - 1 && sx[k + 1] <= x { k += 1 }
            let x0 = sx[k], x1 = sx[k + 1]
            let t = x1 > x0 ? (x - x0) / (x1 - x0) : 0
            table[i] = sy[k] + (sy[k + 1] - sy[k]) * min(max(t, 0), 1)
        }
        // Slopes for extrapolation: use the outer 1% of the mapping.
        func slope(_ i0: Int, _ i1: Int) -> Float {
            let dx = sx[i1] - sx[i0]
            return dx > 0 ? (sy[i1] - sy[i0]) / dx : 1
        }
        let m = max(1, knots / 100)
        let sb = slope(0, m), sa = slope(knots - m, knots)
        return UniformLUT(lo: lo, hi: hi, table: table, slopeBelow: max(sb, 1e-6), slopeAbove: max(sa, 1e-6))
    }
}
