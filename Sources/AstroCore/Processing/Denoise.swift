import Foundation
import Accelerate

/// Noise reduction on the combined image: luminance by one of three classical methods and
/// colour by chroma smoothing. Everything is O(N) or O(N·window) and runs on the CPU.
///
/// * **Wavelets** — starlet (à trous B3-spline) transform with k·σ soft thresholding per scale,
///   the standard tool of astronomical image processing (Starck & Murtagh).
/// * **Non-local means** — Buades et al. 2005, patch-wise averaging; symmetric offsets and
///   separable box sums keep it O(N · search²).
/// * **Guided filter** — He et al. 2010 (self-guided), edge-preserving smoothing with box filters.
///
/// The noise level σ is estimated from the finest wavelet scale (median absolute deviation), so
/// the strength adapts to the preview level and to the export resolution.
public enum Denoise {
    /// B3-spline kernel of the starlet transform.
    static let b3: [Float] = [1 / 16, 4 / 16, 6 / 16, 4 / 16, 1 / 16]
    /// Noise standard deviation of each 2-D starlet scale for unit white noise (scales 1…6).
    public static let scaleNoise: [Float] = [0.889, 0.200, 0.086, 0.041, 0.020, 0.010]

    /// Separable convolution with the B3 kernel dilated by `step` (à trous), edges extended.
    static func smooth(_ p: PlanarImage, step: Int) -> PlanarImage {
        let len = 4 * step + 1
        var k = [Float](repeating: 0, count: len)
        for (i, v) in b3.enumerated() { k[i * step] = v }
        let tmp = PlanarImage(width: p.width, height: p.height), out = PlanarImage(width: p.width, height: p.height)
        var src = p.vBuffer, mid = tmp.vBuffer, dst = out.vBuffer
        let flags = vImage_Flags(kvImageEdgeExtend)
        k.withUnsafeBufferPointer { kp in
            vImageConvolve_PlanarF(&src, &mid, nil, 0, 0, kp.baseAddress!, 1, UInt32(len), 0, flags)
            vImageConvolve_PlanarF(&mid, &dst, nil, 0, 0, kp.baseAddress!, UInt32(len), 1, 0, flags)
        }
        return out
    }

    /// Noise σ of the image, from the MAD of the finest starlet scale (robust to signal).
    public static func noiseSigma(_ p: PlanarImage) -> Float {
        let s = smooth(p, step: 1)
        let n = p.count
        let step = max(1, n / 200_000)
        var sample = stride(from: 0, to: n, by: step).map { abs(p.pointer[$0] - s.pointer[$0]) }
        sample.sort()
        let mad = Statistics.percentile(sorted: sample, q: 0.5)
        return mad / 0.6745 / scaleNoise[0]
    }

    // MARK: - Wavelets

    /// Starlet k·σ soft thresholding on `layers` scales; `amount` scales the threshold (0…1 → 0…3σ).
    static func wavelets(_ y: PlanarImage, sigma: Float, amount: Float, layers: Int) -> PlanarImage {
        let n = y.count
        let out = PlanarImage(width: y.width, height: y.height)
        var cur = y
        var residual = [Float](repeating: 0, count: n)   // sum of thresholded detail scales
        for j in 0..<max(1, min(layers, scaleNoise.count)) {
            let s = smooth(cur, step: 1 << j)
            // Finer scales are denoised harder; the threshold shrinks with the scale index.
            let t = amount * 3 * sigma * scaleNoise[j] * (j == 0 ? 1 : 0.8)
            let cp = cur.pointer, sp = s.pointer
            for i in 0..<n {
                let w = cp[i] - sp[i]
                residual[i] += w > t ? w - t : (w < -t ? w + t : 0)
            }
            cur = s
        }
        vDSP_vadd(cur.pointer, 1, residual, 1, out.pointer, 1, vDSP_Length(n))
        return out
    }

    // MARK: - Non-local means

    /// Box sum of radius `r` (separable, edge extended), in place of a patch-wise squared distance.
    static func boxSum(_ p: PlanarImage, radius r: Int) -> PlanarImage {
        let len = 2 * r + 1
        let k = [Float](repeating: 1, count: len)
        let tmp = PlanarImage(width: p.width, height: p.height), out = PlanarImage(width: p.width, height: p.height)
        var src = p.vBuffer, mid = tmp.vBuffer, dst = out.vBuffer
        let flags = vImage_Flags(kvImageEdgeExtend)
        k.withUnsafeBufferPointer { kp in
            vImageConvolve_PlanarF(&src, &mid, nil, 0, 0, kp.baseAddress!, 1, UInt32(len), 0, flags)
            vImageConvolve_PlanarF(&mid, &dst, nil, 0, 0, kp.baseAddress!, UInt32(len), 1, 0, flags)
        }
        return out
    }

    /// Non-local means with patch radius 2 and search radius `search`; `h` is the filtering
    /// parameter (≈ amount · σ). Weights are symmetric so each offset serves two pixels.
    static func nonLocalMeans(_ y: PlanarImage, sigma: Float, h: Float, search: Int) -> PlanarImage {
        let w = y.width, hgt = y.height, n = y.count
        let patch = 2
        let patchN = Float((2 * patch + 1) * (2 * patch + 1))
        let acc = PlanarImage(width: w, height: hgt), wsum = PlanarImage(width: w, height: hgt, fill: 1)
        acc.pointer.update(from: y.pointer, count: n)        // the centre pixel with weight 1
        let diff = PlanarImage(width: w, height: hgt)
        let h2 = max(h * h, 1e-12), noiseFloor = 2 * sigma * sigma
        let yp = y.pointer, ap = acc.pointer, wp = wsum.pointer, dp = diff.pointer
        // Half of the offsets (the other half is reached by symmetry).
        var offsets: [(Int, Int)] = []
        for dy in 0...search { for dx in -search...search where dy > 0 || dx > 0 { offsets.append((dx, dy)) } }
        for (dx, dy) in offsets {
            // Squared difference between the image and the image shifted by (dx, dy) (edges extended).
            for yy in 0..<hgt {
                let sy = min(max(yy + dy, 0), hgt - 1)
                let row = yy * w, srow = sy * w
                for xx in 0..<w {
                    let sx = min(max(xx + dx, 0), w - 1)
                    let d = yp[row + xx] - yp[srow + sx]
                    dp[row + xx] = d * d
                }
            }
            let dist = boxSum(diff, radius: patch)
            let distp = dist.pointer
            for yy in 0..<hgt {
                let sy = yy + dy
                guard sy >= 0 && sy < hgt else { continue }
                let row = yy * w, srow = sy * w
                for xx in 0..<w {
                    let sx = xx + dx
                    guard sx >= 0 && sx < w else { continue }
                    let d2 = max(distp[row + xx] / patchN - noiseFloor, 0)
                    let wgt = exp(-d2 / h2)
                    ap[row + xx] += wgt * yp[srow + sx]; wp[row + xx] += wgt
                    ap[srow + sx] += wgt * yp[row + xx]; wp[srow + sx] += wgt
                }
            }
        }
        let out = PlanarImage(width: w, height: hgt)
        vDSP_vdiv(wp, 1, ap, 1, out.pointer, 1, vDSP_Length(n))
        return out
    }

    // MARK: - Guided filter

    /// Self-guided filter (He et al.): q = a·I + b with a = var/(var + ε) averaged over the window.
    static func guided(_ y: PlanarImage, epsilon: Float, radius r: Int) -> PlanarImage {
        let n = y.count
        func mean(_ p: PlanarImage) -> PlanarImage {
            let s = boxSum(p, radius: r)
            var k = 1 / Float((2 * r + 1) * (2 * r + 1))
            vDSP_vsmul(s.pointer, 1, &k, s.pointer, 1, vDSP_Length(n))
            return s
        }
        let mI = mean(y)
        let sq = PlanarImage(width: y.width, height: y.height)
        vDSP_vsq(y.pointer, 1, sq.pointer, 1, vDSP_Length(n))
        let mII = mean(sq)
        let a = PlanarImage(width: y.width, height: y.height), b = PlanarImage(width: y.width, height: y.height)
        for i in 0..<n {
            let v = max(mII.pointer[i] - mI.pointer[i] * mI.pointer[i], 0)
            a.pointer[i] = v / (v + epsilon)
            b.pointer[i] = mI.pointer[i] - a.pointer[i] * mI.pointer[i]
        }
        let ma = mean(a), mb = mean(b)
        let out = PlanarImage(width: y.width, height: y.height)
        vDSP_vma(ma.pointer, 1, y.pointer, 1, mb.pointer, 1, out.pointer, 1, vDSP_Length(n))
        return out
    }

    // MARK: - Entry point

    /// Applies the configured noise reduction to `img` in place.
    public static func apply(_ img: RGBImage, settings d: DenoiseSettings) {
        guard !d.isNeutral else { return }
        let n = img.width * img.height
        let y = img.luma()
        let sigma = noiseSigma(y)
        var target = y
        if d.amount > 0 && sigma > 0 {
            switch d.method {
            case .wavelets:
                target = wavelets(y, sigma: sigma, amount: d.amount, layers: d.layers)
            case .nonLocalMeans:
                target = nonLocalMeans(y, sigma: sigma, h: d.amount * 1.5 * sigma, search: d.search)
            case .guided:
                let eps = pow(d.amount * 3 * sigma, 2)
                target = guided(y, epsilon: eps, radius: d.radius)
            }
            // Bright structures (stars, cores) keep their original values, with a soft ramp.
            if d.protectBright > 0 {
                let hi = 1 - 0.8 * d.protectBright, lo = hi * 0.5
                let tp = target.pointer, yp = y.pointer
                for i in 0..<n {
                    let l = yp[i]
                    if l <= lo { continue }
                    let t = min((l - lo) / (hi - lo), 1)
                    let keep = t * t * (3 - 2 * t)
                    tp[i] = tp[i] + keep * (l - tp[i])
                }
            }
        }
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer
        if d.amount > 0 && sigma > 0 {
            let tp = target.pointer, yp = y.pointer
            for i in 0..<n {
                let base = yp[i]
                if base > 1e-5 {
                    let ratio = min(tp[i] / base, 8)
                    r[i] *= ratio; g[i] *= ratio; b[i] *= ratio
                } else { r[i] = tp[i]; g[i] = tp[i]; b[i] = tp[i] }
            }
        }
        if d.color > 0 {
            // Chroma smoothing: blur the colour ratios (r/Y, g/Y, b/Y), keep the luminance.
            let yNow = img.luma()
            let levels = 1 + Int((d.color * 3).rounded())
            for plane in img.planes {
                let ratio = PlanarImage(width: img.width, height: img.height)
                let pp = plane.pointer, yp = yNow.pointer, rp = ratio.pointer
                for i in 0..<n { rp[i] = yp[i] > 1e-5 ? pp[i] / yp[i] : 1 }
                let blurred = PostProcess.pyramidBlur(ratio, levels: levels)
                let bp = blurred.pointer
                for i in 0..<n {
                    let mixed = rp[i] + d.color * (bp[i] - rp[i])
                    pp[i] = yp[i] > 1e-5 ? mixed * yp[i] : pp[i]
                }
            }
        }
        img.clamp01()
    }
}

extension PlanarImage {
    /// A vImage buffer view of this image (the image must outlive the buffer).
    var vBuffer: vImage_Buffer {
        vImage_Buffer(data: pointer, height: vImagePixelCount(height), width: vImagePixelCount(width), rowBytes: width * MemoryLayout<Float>.stride)
    }
}
