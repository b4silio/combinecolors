import Foundation
import Accelerate

/// Robust statistics of one channel, computed on a sub-sample of the full-resolution image.
public struct ChannelStats: Codable, Equatable {
    public var min: Float = 0
    public var max: Float = 0
    public var mean: Float = 0
    public var median: Float = 0
    /// Median absolute deviation from the median.
    public var mad: Float = 0
    /// MAD scaled to be comparable with a Gaussian sigma (× 1.4826).
    public var madn: Float = 0
    public var stdDev: Float = 0
    /// Sigma-clipped median: the sky background level.
    public var background: Float = 0
    /// Sigma-clipped dispersion (MADN) of the background pixels.
    public var backgroundSigma: Float = 0
    /// Pixel-to-pixel noise estimate (from horizontal differences).
    public var noise: Float = 0
    /// Fraction of pixels that are valid (non-zero, finite, unmasked).
    public var validFraction: Float = 1
    /// Sorted sample of valid pixel values (used for percentiles / histogram matching).
    public var sortedSample: [Float] = []

    public init() {}

    enum CodingKeys: String, CodingKey { case min, max, mean, median, mad, madn, stdDev, background, backgroundSigma, noise, validFraction }

    /// Percentile in [0, 1] from the sorted sample (linear interpolation).
    public func percentile(_ q: Double) -> Float {
        Statistics.percentile(sorted: sortedSample, q: q)
    }

    /// Signal-to-noise proxy: (p99.5 - background) / noise.
    public var signalStrength: Float {
        noise > 0 ? (percentile(0.995) - background) / noise : 0
    }

    /// Stats after a monotonic transform `f` (the sorted order is preserved).
    public func transformed(_ f: (Float) -> Float, scale: Float) -> ChannelStats {
        var s = self
        s.sortedSample = sortedSample.map(f)
        if let last = s.sortedSample.last, let first = s.sortedSample.first, last < first { s.sortedSample.reverse() }
        s.min = f(min); s.max = f(max); s.mean = f(mean); s.median = f(median)
        s.background = f(background)
        s.mad = mad * abs(scale); s.madn = madn * abs(scale); s.stdDev = stdDev * abs(scale)
        s.backgroundSigma = backgroundSigma * abs(scale); s.noise = noise * abs(scale)
        return s
    }

    /// Stats after an affine transform a·x + b.
    public func affine(a: Float, b: Float) -> ChannelStats {
        transformed({ a * $0 + b }, scale: a)
    }
}

public struct StatsOptions {
    public var maxSamples: Int = 600_000
    public var excludeZeros: Bool = true
    public var clipSigma: Float = 2.5
    public var clipIterations: Int = 4
    /// Optional exclusion mask (1 = excluded) at a coarser pyramid level.
    public var mask: PlanarImage? = nil
    public var maskLevel: Int = 0
    /// Restrict every computation to this region (full-resolution coordinates).
    public var region: PixelRect? = nil

    public init() {}

    func bounds(width: Int, height: Int) -> PixelRect {
        region?.clamped(width: width, height: height) ?? PixelRect.full(width: width, height: height)
    }
}

public enum Statistics {

    public static func compute(image: PlanarImage, options: StatsOptions = StatsOptions()) -> ChannelStats {
        let (sample, validFraction) = sample(image: image, options: options)
        var stats = fromSample(sample)
        stats.validFraction = validFraction
        stats.noise = noiseEstimate(image: image, options: options)
        return stats
    }

    /// Statistics derived from an (unsorted or sorted) sample of values.
    public static func fromSample(_ values: [Float], clipSigma: Float = 2.5, clipIterations: Int = 4) -> ChannelStats {
        var s = ChannelStats()
        guard !values.isEmpty else { return s }
        var sorted = values
        sorted.withUnsafeMutableBufferPointer { vDSP_vsort($0.baseAddress!, vDSP_Length($0.count), 1) }
        s.sortedSample = sorted
        s.min = sorted.first!
        s.max = sorted.last!
        var mean: Float = 0, sd: Float = 0
        vDSP_normalize(sorted, 1, nil, 1, &mean, &sd, vDSP_Length(sorted.count))
        s.mean = mean
        s.stdDev = sd
        s.median = percentile(sorted: sorted, q: 0.5)
        s.mad = medianAbsoluteDeviation(sorted: sorted, center: s.median)
        s.madn = 1.4826 * s.mad
        // Sigma-clipped background.
        var lo = s.median - clipSigma * max(s.madn, 1e-12), hi = s.median + clipSigma * max(s.madn, 1e-12)
        var bg = s.median, bgSigma = s.madn
        if s.madn > 0 {
            for _ in 0..<clipIterations {
                let l = lowerBound(sorted, lo), h = upperBound(sorted, hi)
                guard h - l > 16 else { break }
                let sub = Array(sorted[l..<h])
                bg = percentile(sorted: sub, q: 0.5)
                let m = medianAbsoluteDeviation(sorted: sub, center: bg) * 1.4826
                guard m > 0 else { break }
                bgSigma = m
                lo = bg - clipSigma * m; hi = bg + clipSigma * m
            }
        }
        s.background = bg
        s.backgroundSigma = bgSigma
        return s
    }

    /// Deterministic grid sample of valid pixels. Returns the sample and the valid fraction.
    public static func sample(image: PlanarImage, options: StatsOptions) -> ([Float], Float) {
        let w = image.width, h = image.height
        let b = options.bounds(width: w, height: h)
        let total = b.count
        let step = max(1, Int((Double(total) / Double(options.maxSamples)).squareRoot().rounded(.down)))
        var out: [Float] = []
        out.reserveCapacity(total / (step * step) + 1)
        var visited = 0
        let mask = options.mask
        let shift = options.maskLevel
        var y = b.y
        var row = 0
        while y < b.maxY {
            let jitter = (row * 7) % step
            var x = b.x + jitter
            let rowPtr = image.pointer + y * w
            while x < b.maxX {
                let v = rowPtr[x]
                visited += 1
                var ok = v.isFinite && !(options.excludeZeros && v == 0)
                if ok, let m = mask {
                    let mx = Swift.min(m.width - 1, x >> shift), my = Swift.min(m.height - 1, y >> shift)
                    if m.pointer[my * m.width + mx] != 0 { ok = false }
                }
                if ok { out.append(v) }
                x += step
            }
            y += step
            row += 1
        }
        return (out, visited > 0 ? Float(out.count) / Float(visited) : 0)
    }

    /// Sample pairs of values at identical positions from two same-sized images.
    public static func pairedSample(_ a: PlanarImage, _ b: PlanarImage, options: StatsOptions, maskB: PlanarImage? = nil) -> ([Float], [Float]) {
        precondition(a.width == b.width && a.height == b.height)
        let w = a.width, h = a.height
        let bounds = options.bounds(width: w, height: h)
        let total = bounds.count
        let step = max(1, Int((Double(total) / Double(options.maxSamples)).squareRoot().rounded(.down)))
        var xs: [Float] = [], ys: [Float] = []
        let shift = options.maskLevel
        var y = bounds.y, row = 0
        while y < bounds.maxY {
            var x = bounds.x + (row * 7) % step
            while x < bounds.maxX {
                let va = a.pointer[y * w + x], vb = b.pointer[y * w + x]
                var ok = va.isFinite && vb.isFinite && !(options.excludeZeros && (va == 0 || vb == 0))
                if ok {
                    for m in [options.mask, maskB].compactMap({ $0 }) {
                        let mx = Swift.min(m.width - 1, x >> shift), my = Swift.min(m.height - 1, y >> shift)
                        if m.pointer[my * m.width + mx] != 0 { ok = false; break }
                    }
                }
                if ok { xs.append(va); ys.append(vb) }
                x += step
            }
            y += step; row += 1
        }
        return (xs, ys)
    }

    /// Samples several same-sized images at identical grid positions, keeping only positions
    /// where every image is valid (finite and non-zero). Returns one array per image.
    public static func multiSample(images: [PlanarImage], maxSamples: Int = 400_000, region: PixelRect? = nil) -> [[Float]] {
        guard let first = images.first else { return [] }
        let w = first.width, h = first.height
        precondition(images.allSatisfy { $0.width == w && $0.height == h })
        let b = region?.clamped(width: w, height: h) ?? PixelRect.full(width: w, height: h)
        let total = b.count
        let step = max(1, Int((Double(total) / Double(maxSamples)).squareRoot().rounded(.down)))
        var out = [[Float]](repeating: [], count: images.count)
        for i in out.indices { out[i].reserveCapacity(total / (step * step) + 1) }
        var y = b.y, row = 0
        while y < b.maxY {
            var x = b.x + (row * 7) % step
            while x < b.maxX {
                let idx = y * w + x
                var ok = true
                for img in images { let v = img.pointer[idx]; if !v.isFinite || v == 0 { ok = false; break } }
                if ok { for (i, img) in images.enumerated() { out[i].append(img.pointer[idx]) } }
                x += step
            }
            y += step; row += 1
        }
        return out
    }

    /// Robust pixel noise estimate: MAD of horizontal neighbour differences / sqrt(2).
    public static func noiseEstimate(image: PlanarImage, options: StatsOptions = StatsOptions()) -> Float {
        let w = image.width, h = image.height
        guard w > 2 else { return 0 }
        let b = options.bounds(width: w, height: h)
        guard b.width > 2 else { return 0 }
        let rowStep = max(1, b.count / max(1, options.maxSamples))
        var diffs: [Float] = []
        diffs.reserveCapacity(options.maxSamples + w)
        let mask = options.mask, shift = options.maskLevel
        var y = b.y + (rowStep / 2) % b.height
        while y < b.maxY && diffs.count < options.maxSamples {
            let p = image.pointer + y * w
            var x = b.x + 1
            while x < b.maxX {
                let a = p[x - 1], b = p[x]
                var ok = a != 0 && b != 0 && a.isFinite && b.isFinite
                if ok, let m = mask {
                    let mx = Swift.min(m.width - 1, x >> shift), my = Swift.min(m.height - 1, y >> shift)
                    if m.pointer[my * m.width + mx] != 0 { ok = false }
                }
                if ok { diffs.append(abs(b - a)) }
                x += 1
            }
            y += rowStep
        }
        guard diffs.count > 16 else { return 0 }
        diffs.withUnsafeMutableBufferPointer { vDSP_vsort($0.baseAddress!, vDSP_Length($0.count), 1) }
        let mad = percentile(sorted: diffs, q: 0.5)
        return 1.4826 * mad / Float(2.0.squareRoot())
    }

    // MARK: - Helpers on sorted arrays

    public static func percentile(sorted: [Float], q: Double) -> Float {
        guard !sorted.isEmpty else { return 0 }
        let pos = Swift.max(0, Swift.min(1, q)) * Double(sorted.count - 1)
        let i = Int(pos.rounded(.down))
        let f = Float(pos - Double(i))
        if i + 1 < sorted.count { return sorted[i] * (1 - f) + sorted[i + 1] * f }
        return sorted[i]
    }

    /// MAD of a sorted array about `center`, computed in O(n) with a two-pointer merge
    /// (deviations decrease towards the centre from the left and increase to the right).
    public static func medianAbsoluteDeviation(sorted a: [Float], center c: Float) -> Float {
        let n = a.count
        guard n > 0 else { return 0 }
        var r = lowerBound(a, c)
        var l = r - 1
        let k1 = (n - 1) / 2, k2 = n / 2
        var v1: Float = 0, v2: Float = 0
        for i in 0...k2 {
            let dl = l >= 0 ? c - a[l] : Float.infinity
            let dr = r < n ? a[r] - c : Float.infinity
            let v: Float
            if dl <= dr { v = dl; l -= 1 } else { v = dr; r += 1 }
            if i == k1 { v1 = v }
            if i == k2 { v2 = v }
        }
        return 0.5 * (v1 + v2)
    }

    /// Index of the first element >= v.
    public static func lowerBound(_ a: [Float], _ v: Float) -> Int {
        var lo = 0, hi = a.count
        while lo < hi { let m = (lo + hi) / 2; if a[m] < v { lo = m + 1 } else { hi = m } }
        return lo
    }

    /// Index of the first element > v.
    public static func upperBound(_ a: [Float], _ v: Float) -> Int {
        var lo = 0, hi = a.count
        while lo < hi { let m = (lo + hi) / 2; if a[m] <= v { lo = m + 1 } else { hi = m } }
        return lo
    }

    /// Fraction of samples <= v.
    public static func cdf(sorted: [Float], value: Float) -> Float {
        guard !sorted.isEmpty else { return 0 }
        return Float(upperBound(sorted, value)) / Float(sorted.count)
    }

    /// Histogram of an image (sub-sampled when large) over [lo, hi].
    public static func histogram(image: PlanarImage, bins: Int, lo: Float = 0, hi: Float = 1, maxSamples: Int = 2_000_000) -> [Int] {
        var counts = [Int](repeating: 0, count: bins)
        let n = image.count
        let step = max(1, n / maxSamples)
        let scale = Float(bins) / max(hi - lo, 1e-12)
        var i = 0
        let p = image.pointer
        while i < n {
            let v = p[i]
            if v.isFinite {
                var b = Int((v - lo) * scale)
                if b < 0 { b = 0 } else if b >= bins { b = bins - 1 }
                counts[b] += 1
            }
            i += step
        }
        return counts
    }
}
