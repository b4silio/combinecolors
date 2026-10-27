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
