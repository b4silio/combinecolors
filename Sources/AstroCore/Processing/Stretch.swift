import Foundation
import Accelerate

public enum StretchMethod: String, Codable, CaseIterable, Identifiable {
    case none
    case autoSTF
    case mtf
    case arcsinh
    case ghs
    case log

    public var id: String { rawValue }
    public var displayName: String {
        switch self {
        case .none: return "None (linear)"
        case .autoSTF: return "Auto STF"
        case .mtf: return "Histogram (MTF)"
        case .arcsinh: return "Arcsinh"
        case .ghs: return "Hyperbolic (GHS)"
        case .log: return "Logarithmic"
        }
    }
}

/// User-facing stretch parameters.
public struct StretchSettings: Codable, Equatable, Hashable {
    public var method: StretchMethod = .autoSTF
    /// Auto STF: where the median lands (PixInsight default 0.25).
    public var targetBackground: Float = 0.2
    /// Auto black point in units of MADN below the median (PixInsight default -2.8).
    public var shadowsClipping: Float = -2.8
    /// Highlight clipping as a percentile in [0,1]; 1 = none.
    public var highlightsPercentile: Float = 1
    /// Use the same transform for all channels (computed from the averaged statistics).
    public var linked: Bool = true
    // Manual MTF (applied in the normalised [0,1] domain after the automatic black point).
    public var mtfShadows: Float = 0
    public var mtfMidtones: Float = 0.5
    public var mtfHighlights: Float = 1
    // Arcsinh
    public var arcsinhStretch: Float = 200
    // GHS
    public var ghsLogD: Float = 5      // D = exp(logD) - 1
    public var ghsB: Float = 2
    public var ghsSP: Float = 0
    public var ghsLP: Float = 0
    public var ghsHP: Float = 1
    // Log
    public var logK: Float = 2000
    /// Output gamma after the stretch (1 = none).
    public var gamma: Float = 1

    public init() {}
}

/// A stretch resolved for one channel: fixed numbers, ready to be applied.
public struct ResolvedStretch: Codable, Equatable {
    public var method: StretchMethod = .none
    /// Input black / white points (linear domain).
    public var c0: Float = 0
    public var c1: Float = 1
    /// MTF midtones balance (for .autoSTF / .mtf).
    public var midtones: Float = 0.5
    public var mtfShadows: Float = 0
    public var mtfHighlights: Float = 1
    public var arcsinhStretch: Float = 200
    public var ghsD: Float = 100
    public var ghsB: Float = 2
    public var ghsSP: Float = 0
    public var ghsLP: Float = 0
    public var ghsHP: Float = 1
    public var logK: Float = 2000
    public var gamma: Float = 1

    public init() {}

    public static let identity = ResolvedStretch()

    // MARK: Scalar reference implementation (used by tests and pixel read-outs).

    public func evaluate(_ x: Float) -> Float {
        if method == .none { return (gamma != 1 && gamma > 0) ? pow(max(x, 0), gamma) : x }
        var u = (x - c0) / max(c1 - c0, 1e-12)
        u = min(max(u, 0), 1)
        var y: Float
        switch method {
        case .none: y = u
        case .autoSTF: y = Stretch.mtf(midtones, u)
        case .mtf:
            let v = min(max((u - mtfShadows) / max(mtfHighlights - mtfShadows, 1e-12), 0), 1)
            y = Stretch.mtf(midtones, v)
        case .arcsinh:
            y = arcsinhStretch > 0 ? asinh(arcsinhStretch * u) / asinh(arcsinhStretch) : u
        case .log:
            y = logK > 0 ? log1p(logK * u) / log1p(logK) : u
        case .ghs:
            y = Stretch.ghs(u, D: ghsD, b: ghsB, SP: ghsSP, LP: ghsLP, HP: ghsHP)
        }
        if gamma != 1 && gamma > 0 { y = pow(max(y, 0), gamma) }
        return y
    }

    /// Applies the stretch in place to a whole plane using Accelerate.
    public func apply(to plane: PlanarImage) {
        if method == .none && gamma == 1 { return }
        let n = vDSP_Length(plane.count)
        let p = plane.pointer
        if method != .none {
            // u = clamp((x - c0) / (c1 - c0), 0, 1)
            var a: Float = 1 / max(c1 - c0, 1e-12)
            var b: Float = -c0 * a
            vDSP_vsmsa(p, 1, &a, &b, p, 1, n)
            var lo: Float = 0, hi: Float = 1
            vDSP_vclip(p, 1, &lo, &hi, p, 1, n)
        }
        switch method {
        case .none: break
        case .autoSTF:
            Stretch.applyMTF(midtones, to: plane)
        case .mtf:
            var a: Float = 1 / max(mtfHighlights - mtfShadows, 1e-12)
            var b: Float = -mtfShadows * a
            vDSP_vsmsa(p, 1, &a, &b, p, 1, n)
            var lo: Float = 0, hi: Float = 1
            vDSP_vclip(p, 1, &lo, &hi, p, 1, n)
            Stretch.applyMTF(midtones, to: plane)
        case .arcsinh:
            guard arcsinhStretch > 0 else { break }
            var s = arcsinhStretch
            vDSP_vsmul(p, 1, &s, p, 1, n)
            var cnt = Int32(plane.count)
            vvasinhf(p, p, &cnt)
            var inv = 1 / asinh(arcsinhStretch)
            vDSP_vsmul(p, 1, &inv, p, 1, n)
        case .log:
            guard logK > 0 else { break }
            var s = logK
            vDSP_vsmul(p, 1, &s, p, 1, n)
            var cnt = Int32(plane.count)
            vvlog1pf(p, p, &cnt)
            var inv = 1 / log1p(logK)
            vDSP_vsmul(p, 1, &inv, p, 1, n)
        case .ghs:
            Stretch.applyGHS(to: plane, D: ghsD, b: ghsB, SP: ghsSP, LP: ghsLP, HP: ghsHP)
        }
        if gamma != 1 && gamma > 0 {
            var lo: Float = 0, hi: Float = 1
            vDSP_vclip(p, 1, &lo, &hi, p, 1, n)
            var g = gamma
            var cnt = Int32(plane.count)
            vvpowsf(p, &g, p, &cnt)
        }
    }
}

public enum Stretch {

    /// PixInsight midtones transfer function.
    @inline(__always) public static func mtf(_ m: Float, _ x: Float) -> Float {
        if x <= 0 { return 0 }
        if x >= 1 { return 1 }
        if m == 0.5 { return x }
        return (m - 1) * x / ((2 * m - 1) * x - m)
    }

    public static func applyMTF(_ m: Float, to plane: PlanarImage) {
        if m == 0.5 { return }
        let n = vDSP_Length(plane.count)
        let p = plane.pointer
        let tmp = UnsafeMutablePointer<Float>.allocate(capacity: plane.count)
        defer { tmp.deallocate() }
        var num = m - 1
        var den = 2 * m - 1, negm = -m
        vDSP_vsmsa(p, 1, &den, &negm, tmp, 1, n)   // tmp = (2m-1)x - m
        vDSP_vsmul(p, 1, &num, p, 1, n)             // p = (m-1)x
        vDSP_vdiv(tmp, 1, p, 1, p, 1, n)            // p = p / tmp
    }

    /// Generalised hyperbolic stretch on the normalised domain [0,1].
    /// T(v) = 1 - (1 + D·b·v)^(-1/b) (b > 0) or 1 - exp(-D·v) (b = 0), mirrored about SP,
    /// with linear continuation below LP and above HP, then normalised to [0,1].
    public static func ghs(_ x: Float, D: Float, b: Float, SP: Float, LP: Float, HP: Float) -> Float {
        guard D > 0 else { return x }
        let lp = min(LP, SP), hp = max(HP, SP)
        func T(_ v: Float) -> Float {
            if b > 0 { return 1 - pow(1 + D * b * v, -1 / b) }
            return 1 - exp(-D * v)
        }
        func dT(_ v: Float) -> Float {
            if b > 0 { return D * pow(1 + D * b * v, -1 / b - 1) }
            return D * exp(-D * v)
        }
        func f(_ t: Float) -> Float { t >= SP ? T(t - SP) : -T(SP - t) }
        func df(_ t: Float) -> Float { dT(abs(t - SP)) }
        func g(_ t: Float) -> Float {
            if t < lp { return f(lp) + df(lp) * (t - lp) }
            if t > hp { return f(hp) + df(hp) * (t - hp) }
            return f(t)
        }
        let g0 = g(0), g1 = g(1)
        let d = g1 - g0
        guard d > 0 else { return x }
        return (g(x) - g0) / d
    }

    public static func applyGHS(to plane: PlanarImage, D: Float, b: Float, SP: Float, LP: Float, HP: Float) {
        guard D > 0 else { return }
        let n = plane.count
        let p = plane.pointer
        let lp = min(LP, SP), hp = max(HP, SP)
        // Pre-compute the normalisation constants with the scalar version.
        let g0 = ghsRaw(0, D: D, b: b, SP: SP, lp: lp, hp: hp), g1 = ghsRaw(1, D: D, b: b, SP: SP, lp: lp, hp: hp)
        let d = g1 - g0
        guard d > 0 else { return }
        // Vectorised core: v = |x - SP| (clamped to the protected range), base = 1 + D b v, T = 1 - base^(-1/b)
        let tmp = UnsafeMutablePointer<Float>.allocate(capacity: n)
        defer { tmp.deallocate() }
        let nn = vDSP_Length(n)
        var negSP = -SP
        vDSP_vsadd(p, 1, &negSP, tmp, 1, nn)            // tmp = x - SP
        vDSP_vabs(tmp, 1, tmp, 1, nn)                   // |x - SP|
        var cnt = Int32(n)
        if b > 0 {
            var a = D * b, one: Float = 1
            vDSP_vsmsa(tmp, 1, &a, &one, tmp, 1, nn)    // 1 + D b v
            var e = -1 / b
            vvpowsf(tmp, &e, tmp, &cnt)                 // (1 + D b v)^(-1/b)
        } else {
            var a = -D
            vDSP_vsmul(tmp, 1, &a, tmp, 1, nn)
            vvexpf(tmp, tmp, &cnt)
        }
        var negOne: Float = -1, one: Float = 1
        vDSP_vsmsa(tmp, 1, &negOne, &one, tmp, 1, nn)   // T = 1 - (...)
        // Piecewise assembly (scalar, cheap arithmetic only).
        let fLP = ghsF(lp, D: D, b: b, SP: SP), dLP = ghsDF(lp, D: D, b: b, SP: SP)
        let fHP = ghsF(hp, D: D, b: b, SP: SP), dHP = ghsDF(hp, D: D, b: b, SP: SP)
        let invd = 1 / d
        for i in 0..<n {
            let x = p[i]
            var v: Float
            if x < lp { v = fLP + dLP * (x - lp) }
            else if x > hp { v = fHP + dHP * (x - hp) }
            else { v = x >= SP ? tmp[i] : -tmp[i] }
            p[i] = (v - g0) * invd
        }
    }

    private static func ghsT(_ v: Float, D: Float, b: Float) -> Float {
        b > 0 ? 1 - pow(1 + D * b * v, -1 / b) : 1 - exp(-D * v)
    }
    private static func ghsDT(_ v: Float, D: Float, b: Float) -> Float {
        b > 0 ? D * pow(1 + D * b * v, -1 / b - 1) : D * exp(-D * v)
    }
    private static func ghsF(_ t: Float, D: Float, b: Float, SP: Float) -> Float {
        t >= SP ? ghsT(t - SP, D: D, b: b) : -ghsT(SP - t, D: D, b: b)
    }
    private static func ghsDF(_ t: Float, D: Float, b: Float, SP: Float) -> Float { ghsDT(abs(t - SP), D: D, b: b) }
    private static func ghsRaw(_ t: Float, D: Float, b: Float, SP: Float, lp: Float, hp: Float) -> Float {
        if t < lp { return ghsF(lp, D: D, b: b, SP: SP) + ghsDF(lp, D: D, b: b, SP: SP) * (t - lp) }
        if t > hp { return ghsF(hp, D: D, b: b, SP: SP) + ghsDF(hp, D: D, b: b, SP: SP) * (t - hp) }
        return ghsF(t, D: D, b: b, SP: SP)
    }

    // MARK: - Resolution from statistics

    /// Auto-STF parameters (PixInsight algorithm) from a median and a normalised MAD.
    public static func autoSTF(median: Float, madn: Float, targetBackground: Float, shadowsClipping: Float) -> (c0: Float, c1: Float, midtones: Float) {
        guard madn > 0 else {
            // Flat image: just map the median to the target background.
            let m = median > 0 && median < 1 ? mtf(targetBackground, median) : 0.5
            return (0, 1, m)
        }
        if median <= 0.5 {
            let c0 = min(max(median + shadowsClipping * madn, 0), 1)
            let c1: Float = 1
            let m = mtf(targetBackground, max(median - c0, 1e-7) / max(c1 - c0, 1e-7))
            return (c0, c1, m)
        } else {
            // Inverted image.
            let c1 = min(max(median - shadowsClipping * madn, 0), 1)
            let c0: Float = 0
            let m = mtf(max(c1 - median, 1e-7) / max(c1 - c0, 1e-7), targetBackground)
            return (c0, c1, m)
        }
    }

    /// Resolve the settings for a channel with the given (already normalised) statistics.
    public static func resolve(_ s: StretchSettings, stats: ChannelStats) -> ResolvedStretch {
        resolve(s, median: stats.median, madn: stats.madn, highlight: stats.percentile(Double(s.highlightsPercentile)), max: stats.max)
    }

    /// Linked resolution: statistics are averaged across channels.
    public static func resolveLinked(_ s: StretchSettings, stats: [ChannelStats]) -> ResolvedStretch {
        guard !stats.isEmpty else { return .identity }
        let med = stats.map(\.median).reduce(0, +) / Float(stats.count)
        let madn = stats.map(\.madn).reduce(0, +) / Float(stats.count)
        let hl = stats.map { $0.percentile(Double(s.highlightsPercentile)) }.max() ?? 1
        let mx = stats.map(\.max).max() ?? 1
        return resolve(s, median: med, madn: madn, highlight: hl, max: mx)
    }

    public static func resolve(_ s: StretchSettings, median: Float, madn: Float, highlight: Float, max: Float) -> ResolvedStretch {
        var r = ResolvedStretch()
        r.method = s.method
        r.gamma = s.gamma
        guard s.method != .none else { return r }
        let auto = autoSTF(median: median, madn: madn, targetBackground: s.targetBackground, shadowsClipping: s.shadowsClipping)
        r.c0 = auto.c0
        r.c1 = s.highlightsPercentile < 1 ? Swift.max(highlight, auto.c0 + 1e-6) : auto.c1
        if s.highlightsPercentile < 1 && s.method == .autoSTF {
            // Re-derive the midtones for the narrower range so the median still lands on target.
            r.midtones = mtf(s.targetBackground, Swift.max(median - r.c0, 1e-7) / Swift.max(r.c1 - r.c0, 1e-7))
        } else {
            r.midtones = auto.midtones
        }
        switch s.method {
        case .mtf:
            r.midtones = s.mtfMidtones
            r.mtfShadows = s.mtfShadows
            r.mtfHighlights = s.mtfHighlights
        case .arcsinh: r.arcsinhStretch = s.arcsinhStretch
        case .ghs:
            r.ghsD = exp(s.ghsLogD) - 1
            r.ghsB = s.ghsB; r.ghsSP = s.ghsSP; r.ghsLP = s.ghsLP; r.ghsHP = s.ghsHP
        case .log: r.logK = s.logK
        default: break
        }
        return r
    }
}
