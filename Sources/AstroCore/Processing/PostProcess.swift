import Foundation
import Accelerate

/// Colour post-processing on stretched [0,1] RGB data. Everything is point-wise.
public enum PostProcess {

    public static func apply(_ s: PostSettings, to img: RGBImage) {
        if s.hueShift != 0 { hueShift(img, degrees: s.hueShift) }
        if s.vibrance != 0 { vibrance(img, amount: s.vibrance) }
        if s.saturation != 1 { saturation(img, factor: s.saturation) }
        if s.scnrGreen > 0 { scnr(img, amount: s.scnrGreen) }
        if s.blackPoint != 0 || s.whitePoint != 1 {
            let a = 1 / max(s.whitePoint - s.blackPoint, 1e-4)
            for p in img.planes { p.affine(scale: a, offset: -s.blackPoint * a) }
        }
        img.clamp01()
        if s.gamma != 1 && s.gamma > 0 {
            var g = s.gamma
            for p in img.planes { var n = Int32(p.count); vvpowsf(p.pointer, &g, p.pointer, &n) }
        }
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
    /// centres around the hue circle and fade out for nearly neutral pixels.
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
                let w = bandWeight(hue: h, index: k, centers: centers)
                if w <= 0 { continue }
                dh += w * a.hue; ds += w * a.saturation; dl += w * a.lightness
            }
            if dh == 0 && ds == 0 && dl == 0 { continue }
            let t = min(sat / 0.25, 1)
            let f = t * t * (3 - 2 * t)          // fade for nearly grey pixels
            h = fmodf(h + f * dh + 360, 360)
            sat = min(max(sat * (1 + f * ds), 0), 1)
            let dlf = f * dl
            l = dlf >= 0 ? l + dlf * 0.5 * (1 - l) : l + dlf * 0.5 * l
            (r[i], g[i], b[i]) = hslToRGB(h, sat, min(max(l, 0), 1))
        }
    }

    /// Piecewise-linear interpolation weight of band `index` at `hue` (degrees).
    static func bandWeight(hue: Float, index: Int, centers: [Float]) -> Float {
        let c = centers[index]
        let prev = centers[(index + centers.count - 1) % centers.count]
        let next = centers[(index + 1) % centers.count]
        let toPrev = fmodf(c - prev + 360, 360), toNext = fmodf(next - c + 360, 360)
        let d = fmodf(hue - c + 540, 360) - 180      // signed distance in (−180, 180]
        if d >= 0 { return toNext > 0 ? max(0, 1 - d / toNext) : 0 }
        return toPrev > 0 ? max(0, 1 + d / toPrev) : 0
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

    public static let textureRadius: Float = 0.006
    public static let clarityRadius: Float = 0.06
    public static let dehazeRadius: Float = 0.25

    /// Luminance with the stars clipped off (99th percentile, at least 0.15): the blurs are taken
    /// from this copy so bright stars do not dig dark halos around themselves.
    static func starCapped(_ y: PlanarImage) -> PlanarImage {
        var opts = StatsOptions(); opts.maxSamples = 200_000
        var sample = Statistics.sample(image: y, options: opts).0
        sample.sort()
        var lo: Float = 0, hi = max(Statistics.percentile(sorted: sample, q: 0.99), 0.15)
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

    /// Unsharp mask: `t += amount × (t − blur)` with the blur taken from the star-capped image and
    /// darkening halved, like the other scales. `sigma` is in pixels of `t`. `masking` (0…1)
    /// fades the mask out where the high-pass is below `masking × 4σ` of the high-pass noise
    /// (median absolute deviation), so flat noisy sky is left alone and edges are sharpened.
    static func sharpen(_ t: UnsafeMutablePointer<Float>, count n: Int, source: PlanarImage, sigma: Float, amount: Float, masking: Float = 0) {
        let blur = gaussianBlur(starCapped(source), sigma: sigma)
        let bp = blur.pointer
        var threshold: Float = 0
        if masking > 0 {
            let step = max(1, n / 200_000)
            var sample = stride(from: 0, to: n, by: step).map { abs(t[$0] - bp[$0]) }
            sample.sort()
            let noise = Statistics.percentile(sorted: sample, q: 0.5) / 0.6745
            threshold = masking * 4 * noise
        }
        for i in 0..<n {
            let l = t[i]
            let d = l - bp[i]
            var delta = amount * d
            if threshold > 0 {
                // Smooth ramp from 0 at half the threshold to 1 at the threshold.
                let w = min(max((abs(d) - 0.5 * threshold) / (0.5 * threshold), 0), 1)
                delta *= w * w * (3 - 2 * w)
            }
            if delta < 0 { delta *= 0.5 }
            t[i] = min(max(l + delta, 0), 1)
        }
    }

    /// Texture (fine), clarity (mid) and dehaze (large-scale) local contrast plus sharpening,
    /// applied to the luminance and propagated to RGB by ratio. The local-contrast radii are
    /// relative to the image width so every pyramid level and the export agree; the sharpening
    /// radius is in full-resolution pixels and scaled by `scale` (rendered px per full-res px).
    public static func detail(_ img: RGBImage, settings d: DetailSettings, scale: Float = 1) {
        guard !d.isNeutral else { return }
        let n = img.width * img.height
        let y = img.luma()
        let target = y.copy()
        let t = target.pointer, yp = y.pointer
        if d.dehaze != 0 {
            let haze = pyramidBlur(starCapped(target), levels: blurLevels(width: img.width, relativeRadius: dehazeRadius))
            let hp = haze.pointer
            let k = d.dehaze * 0.6
            for i in 0..<n {
                // Remove a fraction of the local haze level, renormalise so bright areas stay put.
                let v = (t[i] - k * hp[i]) / max(1 - k, 0.05)
                t[i] = min(max(v, 0), 1)
            }
        }
        if d.clarity != 0 {
            let blur = pyramidBlur(starCapped(target), levels: blurLevels(width: img.width, relativeRadius: clarityRadius))
            localContrast(t, count: n, blur: blur, amount: d.clarity * 1.2, darkFactor: 0.7)
        }
        if d.texture != 0 {
            let blur = pyramidBlur(starCapped(target), levels: blurLevels(width: img.width, relativeRadius: textureRadius))
            localContrast(t, count: n, blur: blur, amount: d.texture * 1.0, darkFactor: 0.5)
        }
        if d.sharpen > 0 {
            // Below ~0.25 px the blur is the identity and the mask vanishes (a down-sampled preview
            // of a 1 px sharpening legitimately shows nothing).
            let sigma = d.sharpenRadius * scale
            if sigma >= 0.25 { sharpen(t, count: n, source: target, sigma: sigma, amount: d.sharpen, masking: d.sharpenMasking) }
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

    static func injectLuminanceLab(_ img: RGBImage, lum: PlanarImage, amount: Float) {
        let n = img.width * img.height
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer, l = lum.pointer
        @inline(__always) func fwd(_ t: Float) -> Float { t > 0.008856 ? cbrtf(t) : (7.787 * t + 16.0 / 116.0) }
        @inline(__always) func inv(_ t: Float) -> Float { let c = t * t * t; return c > 0.008856 ? c : (t - 16.0 / 116.0) / 7.787 }
        for i in 0..<n {
            // Treat the stretched values as linear-light for the purpose of the L replacement
            // (the same convention PixInsight uses for LRGBCombination on stretched data).
            let R = r[i], G = g[i], B = b[i]
            let X = 0.4124 * R + 0.3576 * G + 0.1805 * B
            let Y = 0.2126 * R + 0.7152 * G + 0.0722 * B
            let Z = 0.0193 * R + 0.1192 * G + 0.9505 * B
            let fx = fwd(X / 0.95047), fy = fwd(Y), fz = fwd(Z / 1.08883)
            var Lstar = 116 * fy - 16
            let a = 500 * (fx - fy), bb = 200 * (fy - fz)
            // Target L* from the luminance plane (treated the same way).
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
        for (p, q) in zip(img.planes, st.planes) {
            let a = p.pointer, b = q.pointer, m = mask.pointer
            for i in 0..<n {
                let blended: Float
                switch s.mode {
                case .screen: blended = 1 - (1 - a[i]) * (1 - b[i])
                case .lighten: blended = max(a[i], b[i])
                case .add: blended = a[i] + b[i]
                }
                a[i] += s.amount * m[i] * (blended - a[i])
            }
        }
        img.clamp01()
    }
}
