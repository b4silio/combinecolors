import Foundation
import Accelerate
import CoreGraphics

/// The whole picture at a coarse resolution, for the detail stage of a cropped render (see `PostProcess.detail`).
public struct DetailOverview {
    public let image: RGBImage
    /// Region of the cropped render inside `image`, in overview pixels (fractional).
    public let rect: CGRect
    public init(image: RGBImage, rect: CGRect) { self.image = image; self.rect = rect }
}

/// Colour post-processing on stretched [0,1] RGB data. Everything is point-wise.
public enum PostProcess {

    /// The Colour block: hue, vibrance, saturation, SCNR (the levels are in `applyTone`).
    public static func apply(_ s: PostSettings, to img: RGBImage) {
        if s.hueShift != 0 { hueShift(img, degrees: s.hueShift) }
        if s.vibrance != 0 { vibrance(img, amount: s.vibrance) }
        if s.saturation != 1 { saturation(img, factor: s.saturation) }
        if s.scnrGreen > 0 { scnr(img, amount: s.scnrGreen) }
        img.clamp01()
    }

    /// The Contrast block, per channel: exposure (gain), black / white point, shadows, highlights,
    /// contrast, brightness, then gamma — one 4096-entry curve after the gain.
    public static func applyTone(_ t: ToneSettings, levels l: PostSettings, to img: RGBImage) {
        guard !t.isNeutral || !l.levelsAreNeutral else { return }
        img.clamp01()
        if t.exposure != 0 {
            let g = pow(2, t.exposure)
            for p in img.planes { p.affine(scale: g, offset: 0) }
            img.clamp01()
        }
        let curveNeutral = t.brightness == 0 && t.contrast == 0 && t.highlights == 0 && t.shadows == 0 && l.levelsAreNeutral
        guard !curveNeutral else { return }
        let n = 4096
        let lut = (0..<n).map { toneCurve(Float($0) / Float(n - 1), tone: t, levels: l) }
        let last = Float(n - 1)
        for p in img.planes {
            let ptr = p.pointer
            DispatchQueue.concurrentPerform(iterations: max(1, min(16, p.count / 65536))) { c in
                let chunks = max(1, min(16, p.count / 65536))
                let start = c * p.count / chunks, end = (c + 1) * p.count / chunks
                for i in start..<end {
                    let x = min(max(ptr[i], 0), 1) * last
                    let k = min(Int(x), n - 2)
                    let f = x - Float(k)
                    ptr[i] = lut[k] + f * (lut[k + 1] - lut[k])
                }
            }
        }
    }

    /// The tone curve on [0, 1] (exposure excluded): levels, shadows / highlights bumps (sine
    /// lobes on [0, ½] and [½, 1], so black, mid-tone and white stay put and the curve stays
    /// monotonic), contrast (smoothstep S-curve, or its inverse for negative values), brightness (midtones
    /// transfer function), gamma.
    public static func toneCurve(_ x0: Float, tone t: ToneSettings, levels l: PostSettings) -> Float {
        var x = (x0 - l.blackPoint) / max(l.whitePoint - l.blackPoint, 1e-4)
        x = min(max(x, 0), 1)
        if t.shadows != 0, x < 0.5 { x += 0.15 * t.shadows * sin(2 * .pi * x) }
        if t.highlights != 0, x > 0.5 { x += 0.15 * t.highlights * sin(2 * .pi * (x - 0.5)) }
        if t.contrast > 0 {
            let s = x * x * (3 - 2 * x)
            x += t.contrast * (s - x)
        } else if t.contrast < 0 {
            // Inverse S: flatter mid-tones, black and white kept.
            let s = x * x * (3 - 2 * x)
            x += -t.contrast * (x - s)
        }
        if t.brightness != 0 {
            // MTF with the mid-tones balance moved from ½: positive brightens.
            x = Stretch.mtf(0.5 - 0.4 * t.brightness, min(max(x, 0), 1))
        }
        x = min(max(x, 0), 1)
        if l.gamma != 1 && l.gamma > 0 { x = pow(x, l.gamma) }
        return x
    }

    /// Luma-preserving saturation: c' = Y + f (c - Y).
    public static func saturation(_ img: RGBImage, factor: Float) {
        let y = img.luma()
        let n = vDSP_Length(y.count)
        var f = factor, oneMinus = 1 - factor
        for p in img.planes {
            // p = f*p + (1-f)*y
            vDSP_vsmsma(p.pointer, 1, &f, y.pointer, 1, &oneMinus, p.pointer, 1, n)
        }
    }

    /// Boosts saturation mostly where it is low.
    public static func vibrance(_ img: RGBImage, amount: Float) {
        let n = img.width * img.height
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer
        for i in 0..<n {
            let mx = max(r[i], g[i], b[i]), mn = min(r[i], g[i], b[i])
            let sat = mx > 1e-6 ? (mx - mn) / mx : 0
            let f = 1 + amount * (1 - sat)
            let y = 0.2126 * r[i] + 0.7152 * g[i] + 0.0722 * b[i]
            r[i] = y + f * (r[i] - y); g[i] = y + f * (g[i] - y); b[i] = y + f * (b[i] - y)
        }
    }

    /// SCNR "average neutral": G' = (1-a) G + a min(G, (R+B)/2).
    public static func scnr(_ img: RGBImage, amount: Float) {
        let n = vDSP_Length(img.width * img.height)
        let tmp = PlanarImage(width: img.width, height: img.height)
        vDSP_vadd(img.r.pointer, 1, img.b.pointer, 1, tmp.pointer, 1, n)
        var half: Float = 0.5
        vDSP_vsmul(tmp.pointer, 1, &half, tmp.pointer, 1, n)
        vDSP_vmin(tmp.pointer, 1, img.g.pointer, 1, tmp.pointer, 1, n)
        var a = amount, oneMinus = 1 - amount
        vDSP_vsmsma(tmp.pointer, 1, &a, img.g.pointer, 1, &oneMinus, img.g.pointer, 1, n)
    }

    /// Rotates hue in YIQ space.
    public static func hueShift(_ img: RGBImage, degrees: Float) {
        let t = degrees * .pi / 180
        let c = cos(t), s = sin(t)
        // RGB -> YIQ
        let toYIQ: [Float] = [0.299, 0.587, 0.114, 0.596, -0.274, -0.322, 0.211, -0.523, 0.312]
        let fromYIQ = invert3(toYIQ)
        // Rotation of (I,Q)
        let rot: [Float] = [1, 0, 0, 0, c, -s, 0, s, c]
        let m = mul3(fromYIQ, mul3(rot, toYIQ))
        applyMatrix(img, m)
    }

    static func invert3(_ m: [Float]) -> [Float] {
        let a = m.map(Double.init)
        let det = a[0] * (a[4] * a[8] - a[5] * a[7]) - a[1] * (a[3] * a[8] - a[5] * a[6]) + a[2] * (a[3] * a[7] - a[4] * a[6])
        let inv: [Double] = [
            (a[4] * a[8] - a[5] * a[7]), -(a[1] * a[8] - a[2] * a[7]), (a[1] * a[5] - a[2] * a[4]),
            -(a[3] * a[8] - a[5] * a[6]), (a[0] * a[8] - a[2] * a[6]), -(a[0] * a[5] - a[2] * a[3]),
            (a[3] * a[7] - a[4] * a[6]), -(a[0] * a[7] - a[1] * a[6]), (a[0] * a[4] - a[1] * a[3]),
        ]
        return inv.map { Float($0 / det) }
    }

    static func mul3(_ a: [Float], _ b: [Float]) -> [Float] {
        var o = [Float](repeating: 0, count: 9)
        for i in 0..<3 { for j in 0..<3 { for k in 0..<3 { o[i * 3 + j] += a[i * 3 + k] * b[k * 3 + j] } } }
        return o
    }

    /// out = M · [r g b]ᵀ (row-major 3×3).
    public static func applyMatrix(_ img: RGBImage, _ m: [Float]) {
        let n = vDSP_Length(img.width * img.height)
        let src = img.planes.map { $0.copy() }
        for (o, dst) in img.planes.enumerated() {
            var w0 = m[o * 3], w1 = m[o * 3 + 1], w2 = m[o * 3 + 2]
            vDSP_vsmul(src[0].pointer, 1, &w0, dst.pointer, 1, n)
            vDSP_vsma(src[1].pointer, 1, &w1, dst.pointer, 1, dst.pointer, 1, n)
            vDSP_vsma(src[2].pointer, 1, &w2, dst.pointer, 1, dst.pointer, 1, n)
        }
    }

    // MARK: - Selective colour (HSL bands)

    /// Per-band hue/saturation/lightness. Band weights interpolate linearly between the band
    /// centres around the hue circle (widened and S-shaped with `softness`) and fade out for
    /// nearly neutral pixels.
    public static func colorBands(_ img: RGBImage, settings s: ColorBandsSettings) {
        guard !s.isNeutral else { return }
        let bands = ColorBand.allCases
        let centers = bands.map(\.hue)
        let adj = bands.map { s.adjust($0) }
        let n = img.width * img.height
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer
        for i in 0..<n {
            var (h, sat, l) = rgbToHSL(r[i], g[i], b[i])
            guard sat > 1e-4 else { continue }
            // Band weights.
            var dh: Float = 0, ds: Float = 0, dl: Float = 0
            for k in 0..<bands.count {
                let a = adj[k]
                if a.isNeutral { continue }
                let w = bandWeight(hue: h, index: k, centers: centers, softness: s.softness)
                if w <= 0 { continue }
                dh += w * a.hue; ds += w * a.saturation; dl += w * a.lightness
            }
            if dh == 0 && ds == 0 && dl == 0 { continue }
            // Softer bands also fade in more gradually from grey.
            let t = min(sat / (0.25 + 0.25 * s.softness), 1)
            let f = t * t * (3 - 2 * t)          // fade for nearly grey pixels
            h = fmodf(h + f * dh + 360, 360)
            sat = min(max(sat * (1 + f * ds), 0), 1)
            let dlf = f * dl
            l = dlf >= 0 ? l + dlf * 0.5 * (1 - l) : l + dlf * 0.5 * l
            (r[i], g[i], b[i]) = hslToRGB(h, sat, min(max(l, 0), 1))
        }
    }

    /// Weight of band `index` at `hue` (degrees): 1 at its centre, falling to 0 at the
    /// neighbouring centres (piecewise linear, a partition of unity) at softness 0; with softness
    /// the reach grows up to 2× and the fall-off turns into a smoothstep.
    static func bandWeight(hue: Float, index: Int, centers: [Float], softness: Float = 0) -> Float {
        let c = centers[index]
        let prev = centers[(index + centers.count - 1) % centers.count]
        let next = centers[(index + 1) % centers.count]
        let widen = 1 + min(max(softness, 0), 1)
        let toPrev = fmodf(c - prev + 360, 360) * widen, toNext = fmodf(next - c + 360, 360) * widen
        let d = fmodf(hue - c + 540, 360) - 180      // signed distance in (−180, 180]
        let lin: Float
        if d >= 0 { lin = toNext > 0 ? max(0, 1 - d / toNext) : 0 }
        else { lin = toPrev > 0 ? max(0, 1 + d / toPrev) : 0 }
        guard softness > 0 else { return lin }
        let smooth = lin * lin * (3 - 2 * lin)
        return lin + min(softness, 1) * (smooth - lin)
    }

    @inline(__always) static func rgbToHSL(_ r: Float, _ g: Float, _ b: Float) -> (Float, Float, Float) {
        let mx = max(r, g, b), mn = min(r, g, b)
        let l = (mx + mn) / 2
        let d = mx - mn
        guard d > 1e-6 else { return (0, 0, l) }
        let s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn)
        var h: Float
        if mx == r { h = (g - b) / d + (g < b ? 6 : 0) }
        else if mx == g { h = (b - r) / d + 2 }
        else { h = (r - g) / d + 4 }
        return (h * 60, min(s, 1), l)
    }

    @inline(__always) static func hslToRGB(_ h: Float, _ s: Float, _ l: Float) -> (Float, Float, Float) {
        guard s > 0 else { return (l, l, l) }
        let q = l < 0.5 ? l * (1 + s) : l + s - l * s
        let p = 2 * l - q
        func f(_ t0: Float) -> Float {
            var t = t0
            if t < 0 { t += 1 } else if t > 1 { t -= 1 }
            if t < 1 / 6 { return p + (q - p) * 6 * t }
            if t < 1 / 2 { return q }
            if t < 2 / 3 { return p + (q - p) * (2 / 3 - t) * 6 }
            return p
        }
        let hk = h / 360
        return (f(hk + 1 / 3), f(hk), f(hk - 1 / 3))
    }

    // MARK: - Local contrast (clarity / dehaze)

    /// Large-radius blur by box-pyramid down-sampling and bilinear up-sampling.
    /// `levels` halves the size that many times, i.e. a radius of about 2^levels pixels.
    static func pyramidBlur(_ p: PlanarImage, levels: Int) -> PlanarImage {
        var cur = p
        var n = 0
        while n < levels && cur.width > 4 && cur.height > 4 { cur = cur.downsampled2x(); n += 1 }
        return n == 0 ? p.copy() : cur.resized(width: p.width, height: p.height)
    }

    /// Number of pyramid levels giving a radius of `relativeRadius × width` pixels.
    public static func blurLevels(width: Int, relativeRadius: Float) -> Int {
        max(1, Int((log2(max(Float(width) * relativeRadius, 1))).rounded()))
    }

    public static let microRadius: Float = 0.002
    public static let textureRadius: Float = 0.006
    public static let clarityRadius: Float = 0.06
    public static let dehazeRadius: Float = 0.25
    public static let lowFrequencyRadius: Float = 0.5

    /// Star cap of a luminance: its 99th percentile, at least 0.15.
    static func starCap(_ y: PlanarImage) -> Float {
        var opts = StatsOptions(); opts.maxSamples = 200_000
        var sample = Statistics.sample(image: y, options: opts).0
        sample.sort()
        return max(Statistics.percentile(sorted: sample, q: 0.99), 0.15)
    }

    /// Luminance with the stars clipped off at `cap` (see `starCap`): the blurs are taken from this
    /// copy so bright stars do not dig dark halos around themselves.
    static func starCapped(_ y: PlanarImage, cap: Float? = nil) -> PlanarImage {
        var lo: Float = 0, hi = cap ?? starCap(y)
        let out = PlanarImage(width: y.width, height: y.height)
        vDSP_vclip(y.pointer, 1, &lo, &hi, out.pointer, 1, vDSP_Length(y.count))
        return out
    }

    /// Local contrast at one scale: `t += amount × midtoneWeight × (t − blur)`, with the blur
    /// taken from the star-capped image and darkening limited to `darkFactor` of the lightening
    /// (both measures against halos around stars).
    static func localContrast(_ t: UnsafeMutablePointer<Float>, count n: Int, blur: PlanarImage, amount: Float, darkFactor: Float) {
        let bp = blur.pointer
        for i in 0..<n {
            let l = t[i]
            let w = 4 * l * (1 - l)           // mid-tone weight
            var delta = amount * w * (l - bp[i])
            if delta < 0 { delta *= darkFactor }
            t[i] = min(max(l + delta, 0), 1)
        }
    }

    /// Separable Gaussian blur (σ in pixels, edges extended).
    public static func gaussianBlur(_ p: PlanarImage, sigma: Float) -> PlanarImage {
        let r = max(1, Int((3 * sigma).rounded(.up)))
        var k = (0...(2 * r)).map { exp(-Float(($0 - r) * ($0 - r)) / (2 * sigma * sigma)) }
        let sum = k.reduce(0, +)
        for i in k.indices { k[i] /= sum }
        let tmp = PlanarImage(width: p.width, height: p.height), out = PlanarImage(width: p.width, height: p.height)
        var src = vImage_Buffer(data: p.pointer, height: vImagePixelCount(p.height), width: vImagePixelCount(p.width), rowBytes: p.width * 4)
        var mid = vImage_Buffer(data: tmp.pointer, height: vImagePixelCount(p.height), width: vImagePixelCount(p.width), rowBytes: p.width * 4)
        var dst = vImage_Buffer(data: out.pointer, height: vImagePixelCount(p.height), width: vImagePixelCount(p.width), rowBytes: p.width * 4)
        let flags = vImage_Flags(kvImageEdgeExtend)
        k.withUnsafeBufferPointer { kp in
            vImageConvolve_PlanarF(&src, &mid, nil, 0, 0, kp.baseAddress!, 1, UInt32(2 * r + 1), 0, flags)
            vImageConvolve_PlanarF(&mid, &dst, nil, 0, 0, kp.baseAddress!, UInt32(2 * r + 1), 1, 0, flags)
        }
        return out
    }

    /// Noise of a luminance: median absolute deviation of its high-pass (`y − blur`), as a σ.
    static func highPassNoise(_ y: UnsafeMutablePointer<Float>, _ blur: UnsafeMutablePointer<Float>, count n: Int) -> Float {
        let step = max(1, n / 200_000)
        var sample = stride(from: 0, to: n, by: step).map { abs(y[$0] - blur[$0]) }
        sample.sort()
        return Statistics.percentile(sorted: sample, q: 0.5) / 0.6745
    }

    /// Weight of the sharpening at a high-pass value `d` for a noise-relative `threshold`: a smooth
    /// ramp from 0 at half the threshold to 1 at the threshold (1 everywhere without masking).
    @inline(__always) static func maskWeight(_ d: Float, threshold: Float) -> Float {
        guard threshold > 0 else { return 1 }
        let w = min(max((abs(d) - 0.5 * threshold) / (0.5 * threshold), 0), 1)
        return w * w * (3 - 2 * w)
    }

    /// Local minimum and maximum of `p` over a square of side `2 r + 1` (overshoot limits).
    static func localRange(_ p: PlanarImage, radius r: Int) -> (min: PlanarImage, max: PlanarImage) {
        let side = vImagePixelCount(2 * max(r, 1) + 1)
        let lo = PlanarImage(width: p.width, height: p.height), hi = PlanarImage(width: p.width, height: p.height)
        var src = vImage_Buffer(data: p.pointer, height: vImagePixelCount(p.height), width: vImagePixelCount(p.width), rowBytes: p.width * 4)
        var dl = vImage_Buffer(data: lo.pointer, height: vImagePixelCount(p.height), width: vImagePixelCount(p.width), rowBytes: p.width * 4)
        var dh = vImage_Buffer(data: hi.pointer, height: vImagePixelCount(p.height), width: vImagePixelCount(p.width), rowBytes: p.width * 4)
        vImageMin_PlanarF(&src, &dl, nil, 0, 0, side, side, vImage_Flags(kvImageEdgeExtend))
        vImageMax_PlanarF(&src, &dh, nil, 0, 0, side, side, vImage_Flags(kvImageEdgeExtend))
        return (lo, hi)
    }

    /// Sharpens the luminance `t` (in place; `source` is a copy of it) by undoing a Gaussian blur of
    /// σ = `sigma` pixels of `t`.
    ///
    /// * Deconvolution: Richardson–Lucy, `est ← est · G ⊛ (y / G ⊛ est)`, ⌈10 × amount⌉ iterations
    ///   (the last one blended so the amount is continuous). The update is damped where the
    ///   residual `y − G ⊛ est` is within about a noise σ (more with `masking`; Starck-style), so
    ///   flat noisy sky is not "deconvolved" into grain.
    /// * Unsharp mask: `t += amount × (t − G ⊛ t)`, masked the same way.
    ///
    /// The result stays within the local range of the input (over about 1.5 σ) — no dark rings
    /// around stars, no bright fringes — except that deconvolution may raise the local maxima
    /// themselves (tightening stars is what it is for). Pixels at zero (padding) stay zero.
    static func sharpen(_ t: UnsafeMutablePointer<Float>, count n: Int, source: PlanarImage, sigma: Float, amount: Float, masking: Float = 0,
                        method: SharpenMethod = .deconvolution) {
        let y = source.pointer
        let blurred = gaussianBlur(source, sigma: sigma)
        // Deconvolution always damps a little (residuals within about one noise σ are noise, and
        // undoing them only breeds grain); Masking raises that, as it does for the unsharp mask.
        let floor: Float = method == .deconvolution ? 1.2 : 0
        let k = max(masking * 4, floor)
        let noise = highPassNoise(y, blurred.pointer, count: n)
        let threshold = k * noise
        let out = PlanarImage(width: source.width, height: source.height)
        let o = out.pointer
        switch method {
        case .unsharpMask:
            let bp = blurred.pointer
            for i in 0..<n {
                let d = y[i] - bp[i]
                o[i] = y[i] + amount * d * maskWeight(d, threshold: threshold)
            }
        case .deconvolution:
            let iterations = max(1, Int((amount * 10).rounded(.up)))
            let est = source.copy()
            let e = est.pointer
            let ratio = PlanarImage(width: source.width, height: source.height)
            let rp = ratio.pointer
            for k in 0..<iterations {
                let conv = k == 0 ? blurred : gaussianBlur(est, sigma: sigma)
                let cp = conv.pointer
                for i in 0..<n {
                    let c = cp[i]
                    guard c > 1e-6, y[i] > 0 else { rp[i] = 1; continue }
                    let q = y[i] / c
                    rp[i] = 1 + (q - 1) * maskWeight(y[i] - c, threshold: threshold)
                }
                let corr = gaussianBlur(ratio, sigma: sigma)
                let gp = corr.pointer
                for i in 0..<n { e[i] = max(e[i] * min(max(gp[i], 0.2), 5), 0) }
            }
            // The last iteration counts in part: amount 0.25 = 2.5 iterations' worth.
            let f = amount * 10 / Float(iterations)
            for i in 0..<n { o[i] = y[i] + (e[i] - y[i]) * f }
        }
        let (lo, hi) = localRange(source, radius: max(1, Int((1.5 * sigma).rounded(.up))))
        let lp = lo.pointer, hp = hi.pointer
        // Deconvolution may raise a peak above its surroundings (tightening stars is what it is
        // for), but nothing else rises above the local maximum, so no bright ring forms around a
        // star either; the unsharp mask stays within the local range everywhere.
        let deconv = method == .deconvolution
        let minContrast = max(4 * noise, 0.01)
        for i in 0..<n {
            guard y[i] != 0 else { t[i] = 0; continue }
            // A peak: the upper part of a neighbourhood with real contrast (not flat sky).
            let contrast = hp[i] - lp[i]
            let peak = deconv && contrast > minContrast && y[i] - lp[i] >= 0.6 * contrast
            t[i] = min(max(o[i], lp[i], 0), peak ? 1 : min(hp[i], 1))
        }
    }

    /// Local contrast at five scales — low frequency (50 % of the width), dehaze (25 %, haze
    /// subtraction), clarity (6 %), texture (0.6 %), micro-structures (0.2 %), applied coarse to fine —
    /// plus sharpening, on the luminance and propagated to RGB by ratio. The local-contrast radii are
    /// relative to the width of the *whole* image so every pyramid level, a zoomed-in crop and the
    /// export agree; the sharpening radius is in full-resolution pixels and scaled by `scale`
    /// (rendered px per full-res px).
    ///
    /// `img` may be a crop of the picture (a zoomed-in preview). Then `overview` — the whole picture at
    /// a coarse resolution plus the crop's position in it — provides the star cap and the large-radius
    /// blurs: its luminance goes through the same stages, each blur is taken from it and resampled
    /// over the crop, so the crop sees the haze and the local means of the whole picture, not of
    /// itself. Without an overview the image is taken to be the whole picture.
    public static func detail(_ img: RGBImage, settings d: DetailSettings, scale: Float = 1, overview: DetailOverview? = nil) {
        guard !d.isNeutral else { return }
        let n = img.width * img.height
        let y = img.luma()
        let target = y.copy()
        let t = target.pointer, yp = y.pointer
        let ov = overview.flatMap { o -> (luma: PlanarImage, rect: CGRect)? in
            o.rect.width > 0 && o.rect.height > 0 && o.image.width > 0 ? (o.image.luma(), o.rect) : nil
        }
        /// Width of the whole picture in pixels of `img`.
        let fullWidth = ov.map { Double($0.luma.width) * Double(img.width) / $0.rect.width } ?? Double(img.width)

        /// One local-contrast stage: `apply(t, n, blur)` on the target, and on the overview so the
        /// following stages see the same picture. The blur radius is `relativeRadius × full width`;
        /// it comes from the overview when it spans at least two overview pixels, from the target
        /// itself otherwise (a small radius against a much larger crop).
        func stage(relativeRadius: Float, _ apply: (UnsafeMutablePointer<Float>, Int, PlanarImage) -> Void) {
            let cap = starCap(ov?.luma ?? target)
            if let ov, Float(ov.luma.width) * relativeRadius >= 2 {
                let b = pyramidBlur(starCapped(ov.luma, cap: cap), levels: blurLevels(width: ov.luma.width, relativeRadius: relativeRadius))
                apply(t, n, b.sampled(x: ov.rect.minX, y: ov.rect.minY, width: ov.rect.width, height: ov.rect.height, toWidth: img.width, toHeight: img.height))
                apply(ov.luma.pointer, ov.luma.count, b)
            } else {
                let levels = blurLevels(width: Int(fullWidth.rounded()), relativeRadius: relativeRadius)
                apply(t, n, pyramidBlur(starCapped(target, cap: cap), levels: levels))
            }
        }
        if d.lowFrequency != 0 {
            stage(relativeRadius: lowFrequencyRadius) { t, n, blur in localContrast(t, count: n, blur: blur, amount: d.lowFrequency * 1.2, darkFactor: 0.7) }
        }
        if d.dehaze != 0 {
            let k = d.dehaze * 0.6
            stage(relativeRadius: dehazeRadius) { t, n, haze in
                let hp = haze.pointer
                for i in 0..<n {
                    // Remove a fraction of the local haze level, renormalise so bright areas stay put.
                    let v = (t[i] - k * hp[i]) / max(1 - k, 0.05)
                    t[i] = min(max(v, 0), 1)
                }
            }
        }
        if d.clarity != 0 {
            stage(relativeRadius: clarityRadius) { t, n, blur in localContrast(t, count: n, blur: blur, amount: d.clarity * 1.2, darkFactor: 0.7) }
        }
        if d.texture != 0 {
            stage(relativeRadius: textureRadius) { t, n, blur in localContrast(t, count: n, blur: blur, amount: d.texture * 1.0, darkFactor: 0.5) }
        }
        if d.micro != 0 {
            stage(relativeRadius: microRadius) { t, n, blur in localContrast(t, count: n, blur: blur, amount: d.micro * 1.0, darkFactor: 0.5) }
        }
        if d.sharpen > 0 {
            // Below ~0.25 px the blur is the identity and the mask vanishes (a down-sampled preview
            // of a 1 px sharpening legitimately shows nothing).
            let sigma = d.sharpenRadius * scale
            if sigma >= 0.25 { sharpen(t, count: n, source: target.copy(), sigma: sigma, amount: d.sharpen, masking: d.sharpenMasking, method: d.sharpenMethod) }
        }
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer
        for i in 0..<n {
            let base = yp[i]
            if base > 1e-5 {
                let ratio = min(t[i] / base, 8)
                r[i] *= ratio; g[i] *= ratio; b[i] *= ratio
            } else {
                r[i] = t[i]; g[i] = t[i]; b[i] = t[i]
            }
        }
        if d.dehaze > 0 { saturation(img, factor: 1 + 0.15 * d.dehaze) }
        img.clamp01()
    }

    // MARK: - Luminance injection

    /// Replaces the luminance of `img` with `lum` (both in [0,1]) by `amount`.
    public static func injectLuminance(_ img: RGBImage, lum: PlanarImage, amount: Float, method: LuminanceMethod) {
        switch method {
        case .ratio: injectLuminanceRatio(img, lum: lum, amount: amount)
        case .lab: injectLuminanceLab(img, lum: lum, amount: amount)
        }
    }

    static func injectLuminanceRatio(_ img: RGBImage, lum: PlanarImage, amount: Float) {
        let y = img.luma()
        let n = y.count
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer, l = lum.pointer, yp = y.pointer
        for i in 0..<n {
            let target = yp[i] + amount * (l[i] - yp[i])
            let ratio = yp[i] > 1e-5 ? min(target / yp[i], 16) : 0
            if yp[i] <= 1e-5 { r[i] = target; g[i] = target; b[i] = target }
            else { r[i] *= ratio; g[i] *= ratio; b[i] *= ratio }
        }
        img.clamp01()
    }

    /// L* of the image replaced by `amount` of the luminance's L* (both treated as linear-light, the
    /// convention PixInsight's LRGBCombination uses on stretched data), a and b kept. Vectorised
    /// (vDSP + vForce) in chunks, chunks in parallel; `injectLuminanceLabScalar` is the reference.
    static func injectLuminanceLab(_ img: RGBImage, lum: PlanarImage, amount: Float) {
        let n = img.width * img.height
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer, l = lum.pointer
        let chunk = 16_384
        let chunks = (n + chunk - 1) / chunk
        let thr: Float = 0.008856, k16: Float = 16.0 / 116.0, slope: Float = 7.787
        DispatchQueue.concurrentPerform(iterations: chunks) { c in
            let start = c * chunk, len = min(chunk, n - start)
            let vn = vDSP_Length(len); var cnt = Int32(len)
            let fx = UnsafeMutablePointer<Float>.allocate(capacity: len * 7)
            defer { fx.deallocate() }
            let fy = fx + len, fz = fy + len, fl = fz + len, cube = fl + len, lin = cube + len, mask = lin + len
            // X / 0.95047, Y, Z / 1.08883 from R, G, B (the white-point divisions folded into the weights).
            func mix(_ out: UnsafeMutablePointer<Float>, _ wr: Float, _ wg: Float, _ wb: Float) {
                var a = wr, bb = wg, cc = wb
                vDSP_vsmul(r + start, 1, &a, out, 1, vn)
                vDSP_vsma(g + start, 1, &bb, out, 1, out, 1, vn)
                vDSP_vsma(b + start, 1, &cc, out, 1, out, 1, vn)
            }
            mix(fx, 0.4124 / 0.95047, 0.3576 / 0.95047, 0.1805 / 0.95047)
            mix(fy, 0.2126, 0.7152, 0.0722)
            mix(fz, 0.0193 / 1.08883, 0.1192 / 1.08883, 0.9505 / 1.08883)
            var zero: Float = 0
            vDSP_vthr(l + start, 1, &zero, fl, 1, vn)
            /// t → t > thr ? ∛t : 7.787 t + 16/116, in place.
            func fwd(_ t: UnsafeMutablePointer<Float>) {
                vvcbrtf(cube, t, &cnt)
                var sl = slope, k = k16
                vDSP_vsmsa(t, 1, &sl, &k, lin, 1, vn)
                var th = thr, half: Float = 0.5
                vDSP_vthrsc(t, 1, &th, &half, mask, 1, vn)
                vDSP_vsadd(mask, 1, &half, mask, 1, vn)          // 1 where t > thr, else 0
                vDSP_vsub(lin, 1, cube, 1, cube, 1, vn)            // cube − lin
                vDSP_vma(mask, 1, cube, 1, lin, 1, t, 1, vn)      // lin + mask·(cube − lin)
            }
            fwd(fx); fwd(fy); fwd(fz); fwd(fl)
            // L* moves by amount·(Lt − L*): the same shift on fx, fy, fz keeps a and b.
            var am = amount
            vDSP_vsub(fy, 1, fl, 1, fl, 1, vn)                     // fl − fy
            vDSP_vsmul(fl, 1, &am, fl, 1, vn)
            vDSP_vadd(fx, 1, fl, 1, fx, 1, vn)
            vDSP_vadd(fy, 1, fl, 1, fy, 1, vn)
            vDSP_vadd(fz, 1, fl, 1, fz, 1, vn)
            /// t → t³ > thr ? t³ : (t − 16/116) / 7.787, in place.
            func inv(_ t: UnsafeMutablePointer<Float>) {
                vDSP_vsq(t, 1, cube, 1, vn)
                vDSP_vmul(cube, 1, t, 1, cube, 1, vn)
                var isl = 1 / slope, k = -k16 / slope
                vDSP_vsmsa(t, 1, &isl, &k, lin, 1, vn)
                var th = thr, half: Float = 0.5
                vDSP_vthrsc(cube, 1, &th, &half, mask, 1, vn)
                vDSP_vsadd(mask, 1, &half, mask, 1, vn)
                vDSP_vsub(lin, 1, cube, 1, cube, 1, vn)
                vDSP_vma(mask, 1, cube, 1, lin, 1, t, 1, vn)
            }
            inv(fx); inv(fy); inv(fz)
            // Back to RGB (X = 0.95047·fx, Z = 1.08883·fz folded into the matrix).
            func out(_ dst: UnsafeMutablePointer<Float>, _ wx: Float, _ wy: Float, _ wz: Float) {
                var a = wx * 0.95047, bb = wy, cc = wz * 1.08883
                vDSP_vsmul(fx, 1, &a, dst, 1, vn)
                vDSP_vsma(fy, 1, &bb, dst, 1, dst, 1, vn)
                vDSP_vsma(fz, 1, &cc, dst, 1, dst, 1, vn)
            }
            out(r + start, 3.2406, -1.5372, -0.4986)
            out(g + start, -0.9689, 1.8758, 0.0415)
            out(b + start, 0.0557, -0.2040, 1.0570)
        }
        img.clamp01()
    }

    /// Scalar reference of `injectLuminanceLab` (tests).
    static func injectLuminanceLabScalar(_ img: RGBImage, lum: PlanarImage, amount: Float) {
        let n = img.width * img.height
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer, l = lum.pointer
        @inline(__always) func fwd(_ t: Float) -> Float { t > 0.008856 ? cbrtf(t) : (7.787 * t + 16.0 / 116.0) }
        @inline(__always) func inv(_ t: Float) -> Float { let c = t * t * t; return c > 0.008856 ? c : (t - 16.0 / 116.0) / 7.787 }
        for i in 0..<n {
            let R = r[i], G = g[i], B = b[i]
            let X = 0.4124 * R + 0.3576 * G + 0.1805 * B
            let Y = 0.2126 * R + 0.7152 * G + 0.0722 * B
            let Z = 0.0193 * R + 0.1192 * G + 0.9505 * B
            let fx = fwd(X / 0.95047), fy = fwd(Y), fz = fwd(Z / 1.08883)
            var Lstar = 116 * fy - 16
            let a = 500 * (fx - fy), bb = 200 * (fy - fz)
            let Lt = 116 * fwd(max(l[i], 0)) - 16
            Lstar = Lstar + amount * (Lt - Lstar)
            let fy2 = (Lstar + 16) / 116
            let fx2 = fy2 + a / 500, fz2 = fy2 - bb / 200
            let X2 = 0.95047 * inv(fx2), Y2 = inv(fy2), Z2 = 1.08883 * inv(fz2)
            r[i] = 3.2406 * X2 - 1.5372 * Y2 - 0.4986 * Z2
            g[i] = -0.9689 * X2 + 1.8758 * Y2 + 0.0415 * Z2
            b[i] = 0.0557 * X2 - 0.2040 * Y2 + 1.0570 * Z2
        }
        img.clamp01()
    }

    // MARK: - Star blending

    /// Blends a stretched RGB star image into `img`.
    public static func blendStars(_ img: RGBImage, stars: RGBImage, settings s: StarsBlendSettings) {
        let n = img.width * img.height
        let st = RGBImage(r: stars.r.copy(), g: stars.g.copy(), b: stars.b.copy())
        if s.saturation != 1 { saturation(st, factor: s.saturation) }
        let mask: PlanarImage
        if s.useMask {
            mask = st.luma()
            let m = mask.pointer
            let lo = s.threshold, hi = s.threshold + max(s.softness, 1e-3)
            for i in 0..<n {
                let t = min(max((m[i] - lo) / (hi - lo), 0), 1)
                m[i] = t * t * (3 - 2 * t)
            }
        } else {
            mask = PlanarImage(width: img.width, height: img.height, fill: 1)
        }
        // With "brightest stars to white" the amount is a tone curve on the layer that keeps its
        // peak: v·(amount + (1 − amount)·v) fades the faint stars by the amount while the cores
        // stay at 1, so lowering the stars does not turn the big ones grey. Otherwise it mixes.
        let keepWhite = s.normalize && s.amount < 1
        let amount = keepWhite ? 1 : s.amount
        for (p, q) in zip(img.planes, st.planes) {
            let a = p.pointer, b = q.pointer, m = mask.pointer
            for i in 0..<n {
                var v = b[i]
                if keepWhite { v = v * (s.amount + (1 - s.amount) * v) }
                let blended: Float
                switch s.mode {
                case .screen: blended = 1 - (1 - a[i]) * (1 - v)
                case .lighten: blended = max(a[i], v)
                case .add: blended = a[i] + v
                }
                a[i] += amount * m[i] * (blended - a[i])
            }
        }
        img.clamp01()
    }
}
