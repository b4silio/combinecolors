import Foundation
import Accelerate

/// A background sample: a disc placed on sky (by the user or the grid generator) whose robust level
/// anchors the background surface. Full-resolution, oriented coordinates.
public struct BackgroundSample: Codable, Equatable, Hashable, Identifiable {
    public var id: UUID
    public var x: Double
    public var y: Double

    public init(x: Double, y: Double, id: UUID = UUID()) { self.x = x; self.y = y; self.id = id }

    public func applying(_ op: Orientation.Op, imageWidth: Int, imageHeight: Int) -> BackgroundSample {
        var s = self
        switch op {
        case .rotateClockwise: s.x = Double(imageHeight) - y; s.y = x
        case .flipHorizontal: s.x = Double(imageWidth) - x
        case .flipVertical: s.y = Double(imageHeight) - y
        }
        return s
    }
}

/// Surface fitted through the background samples.
public enum SampleSurface: String, Codable, CaseIterable, Identifiable {
    /// Thin-plate spline with smoothing (DBE-like): the minimum-bending surface through the samples,
    /// relaxed by the smoothing so noise in the samples is not followed.
    case spline
    /// Ordinary kriging with a Gaussian covariance (GraXpert-like): a Gaussian-process mean around a
    /// constant; the smoothing sets the correlation length and the nugget.
    case kriging
    public var id: String { rawValue }
    public var displayName: String { self == .spline ? "Spline" : "Kriging" }
}

/// What became of one sample in a fit (shown in the editor).
public struct SampleResult: Equatable {
    public var id: UUID
    /// Robust level measured in the disc (nil: not enough sky pixels — on padding, stars or a protected region).
    public var value: Float?
    /// Level of the fitted surface at the sample.
    public var model: Float
    /// Weight the fit gave the sample (0…1; below `rejected` it was treated as not sky).
    public var weight: Float
    public var isRejected: Bool { value == nil || weight < BackgroundSamples.rejectedWeight }
}

/// Background estimation from samples: robust disc statistics on the star-masked cell medians,
/// an automatic grid with nebula rejection, and a smoothing thin-plate spline or Gaussian kriging
/// surface with asymmetric robust reweighting (sky is a lower envelope: samples that sit above the
/// surface — nebulosity, halos — lose their weight faster than samples below it).
public enum BackgroundSamples {
    /// Weight below which a sample counts as rejected.
    public static let rejectedWeight: Float = 0.2
    /// Fewest cells a disc needs to give a level.
    static let minimumCells = 6
    /// Pixels on the long side of the evaluated surface (bilinear between them).
    static let surfaceSize = 512
    /// Most samples a fit takes (the dense solve is cubic).
    static let maximumSamples = 1500

    /// Radius given to samples when the model asks for the automatic one: a disc of a few cells.
    public static func defaultRadius(width: Int, height: Int) -> Double { max(12, Double(max(width, height)) / 80) }

    // MARK: Disc statistics

    /// Cell medians grouped by rows of cells, for fast disc look-ups.
    struct Index {
        let samples: GradientRemoval.Samples
        /// Sample indices sorted by y then x, with the start of every distinct y.
        let order: [Int]
        let rowStart: [Int]
        let rowY: [Float]

        init(_ s: GradientRemoval.Samples) {
            samples = s
            order = (0..<s.count).sorted { s.y[$0] != s.y[$1] ? s.y[$0] < s.y[$1] : s.x[$0] < s.x[$1] }
            var starts: [Int] = [], ys: [Float] = []
            var last: Float = .nan
            for (i, o) in order.enumerated() where s.y[o] != last { starts.append(i); ys.append(s.y[o]); last = s.y[o] }
            rowStart = starts; rowY = ys
        }

        /// Values of the cells within `radius` of (x, y).
        func values(x: Float, y: Float, radius: Float) -> [Float] {
            var out: [Float] = []
            let r2 = radius * radius
            // Rows within the disc (binary search on y).
            var lo = 0, hi = rowY.count
            while lo < hi { let m = (lo + hi) / 2; if rowY[m] < y - radius { lo = m + 1 } else { hi = m } }
            var r = lo
            while r < rowY.count && rowY[r] <= y + radius {
                let end = r + 1 < rowStart.count ? rowStart[r + 1] : order.count
                let dy = rowY[r] - y
                for i in rowStart[r]..<end {
                    let o = order[i]
                    let dx = samples.x[o] - x
                    if dx * dx + dy * dy <= r2 { out.append(samples.v[o]) }
                }
                r += 1
            }
            return out
        }
    }

    public struct Measurement: Equatable {
        public var value: Float
        /// Scatter of the cells in the disc (MAD × 1.4826).
        public var scatter: Float
        /// (mean − median) / scatter of the cells: signal skews the mean before it moves the median
        /// (the NoiseChisel test), so a disc on nebulosity or a halo shows a positive skew.
        public var skew: Float
        public var cells: Int
    }

    /// Robust level of a disc: median of the star-masked cell medians inside it.
    static func measure(_ index: Index, x: Double, y: Double, radius: Double) -> Measurement? {
        var v = index.values(x: Float(x), y: Float(y), radius: Float(radius))
        guard v.count >= minimumCells else { return nil }
        v.sort()
        let med = Statistics.percentile(sorted: v, q: 0.5)
        let mean = v.reduce(0, +) / Float(v.count)
        var dev = v.map { abs($0 - med) }
        dev.sort()
        let scatter = 1.4826 * Statistics.percentile(sorted: dev, q: 0.5)
        return Measurement(value: med, scatter: scatter, skew: scatter > 0 ? (mean - med) / scatter : 0, cells: v.count)
    }

    /// Median of `d` over the `k` nearest other points of each point: the local reference a sample is
    /// compared with (a sample far above its neighbours is not sky, whatever a surface through it says).
    static func neighbourMedians(x: [Double], y: [Double], d: [Double], k: Int = 8) -> [Double] {
        let n = x.count
        guard n > 1 else { return d }
        var out = [Double](repeating: 0, count: n)
        for i in 0..<n {
            var dist: [(Double, Int)] = []
            dist.reserveCapacity(n - 1)
            for j in 0..<n where j != i { let dx = x[i] - x[j], dy = y[i] - y[j]; dist.append((dx * dx + dy * dy, j)) }
            dist.sort { $0.0 < $1.0 }
            var vals = dist.prefix(min(k, dist.count)).map { d[$0.1] }
            vals.sort()
            out[i] = vals.count % 2 == 1 ? vals[vals.count / 2] : 0.5 * (vals[vals.count / 2 - 1] + vals[vals.count / 2])
        }
        return out
    }

    public static func measure(samples s: GradientRemoval.Samples, x: Double, y: Double, radius: Double) -> Measurement? {
        measure(Index(s), x: x, y: y, radius: radius)
    }

    // MARK: Grid

    /// A regular grid of samples over the frame, keeping those that look like sky: enough
    /// star-free cells, little structure inside the disc, outside the protected regions, and not
    /// standing above a robust low-order sky model by more than `tolerance` sample noises (a bright
    /// nebula region is left out; a smooth gradient is kept, since the model follows it).
    public static func grid(samples s: GradientRemoval.Samples, perRow: Int, radius: Double, tolerance: Double, protect: [GradientRegion] = []) -> [BackgroundSample] {
        let w = Double(s.width), h = Double(s.height)
        let cols = max(2, min(perRow, 64))
        let spacing = w / Double(cols)
        let rows = max(2, Int((h / spacing).rounded()))
        let index = Index(s)
        var candidates: [(x: Double, y: Double, m: Measurement)] = []
        let shift = min(radius, spacing / 4)
        for r in 0..<rows {
            for c in 0..<cols {
                let x0 = (Double(c) + 0.5) * spacing, y0 = (Double(r) + 0.5) * (h / Double(rows))
                // The darkest of five windows (the point and four diagonal shifts) is the one most
                // likely to be sky between the stars and the faint structure.
                var best: (x: Double, y: Double, m: Measurement)? = nil
                for (ox, oy) in [(0.0, 0.0), (shift, shift), (-shift, shift), (shift, -shift), (-shift, -shift)] {
                    let x = x0 + ox, y = y0 + oy
                    guard x >= radius, y >= radius, x <= w - radius, y <= h - radius else { continue }
                    if protect.contains(where: { $0.contains(x: x, y: y) }) { continue }
                    guard let m = measure(index, x: x, y: y, radius: radius) else { continue }
                    if best == nil || m.value < best!.m.value { best = (x, y, m) }
                }
                if let b = best { candidates.append(b) }
            }
        }
        guard candidates.count >= 6 else { return candidates.map { BackgroundSample(x: $0.x, y: $0.y) } }
        // Structure inside the disc: more scatter than the sample noise allows, or a mean pulled
        // well above the median.
        let noise = max(s.noise, 1e-12)
        let scatterLimit = max(3 * noise, Statistics.percentile(sorted: candidates.map(\.m.scatter).sorted(), q: 0.5) * 2.5)
        let skewLimit: Float = 0.35
        // Sky model: a plane fitted by asymmetric least squares (Eilers & Boelens): residuals above
        // the plane weigh 0.02, residuals below weigh 1, so the plane settles on the lower envelope of
        // the candidates — nebulosity only ever lies above the sky. A plane, not a quadratic: a
        // quadratic bends into a nebula in the middle of the frame and rejects the sky around it.
        // Light pollution is close to planar; what the plane cannot follow (a glow in one corner)
        // is left to the tolerance and the user's own samples.
        var use = [Bool](repeating: true, count: candidates.count)
        let xs = candidates.map { Float($0.x) }, ys = candidates.map { Float($0.y) }, vs = candidates.map { $0.m.value }
        let fw = Float(s.width), fh = Float(s.height)
        func plane(_ c: [Float], _ i: Int) -> Float { c[0] + c[1] * (ys[i] / fh) + c[2] * (xs[i] / fw) }
        var coef: [Float]? = nil
        var weights = [Double](repeating: 1, count: candidates.count)
        for _ in 0..<12 {
            guard let c = weightedPlane(x: xs, y: ys, v: vs, w: weights, width: fw, height: fh) else { break }
            coef = c
            var changed = false
            for i in 0..<candidates.count {
                let r = vs[i] - plane(c, i)
                let nw: Double = r > 0 ? 0.02 : 1
                if nw != weights[i] { changed = true; weights[i] = nw }
            }
            if !changed { break }
        }
        if var c = coef {
            // The envelope sits at a low quantile of the sky noise: re-centre it on the sky samples'
            // median residual, then keep what lies within the tolerance above it.
            var res = [Float](repeating: 0, count: candidates.count)
            for i in 0..<candidates.count { res[i] = vs[i] - plane(c, i) }
            let near = res.filter { abs($0) <= 3 * noise }.sorted()
            if near.count >= 4 {
                let shift = Statistics.percentile(sorted: near, q: 0.5)
                c[0] += shift
                for i in 0..<res.count { res[i] -= shift }
            }
            coef = c
            let keep = Float(tolerance) * noise
            for i in 0..<candidates.count { use[i] = res[i] <= keep && res[i] >= -4 * keep }
        }
        let degree = 1
        // Spatial consistency: a sample above the median of its eight nearest neighbours by more
        // than the tolerance (after the trend) is not sky either — the hole a nebula leaves in the
        // grid cannot be bridged by samples on its skirts.
        if let c = coef {
            let res = (0..<candidates.count).map { i in Double(vs[i] - GradientRemoval.polynomial(c, degree: degree, nx: xs[i] / Float(s.width), ny: ys[i] / Float(s.height))) }
            let nb = neighbourMedians(x: candidates.map(\.x), y: candidates.map(\.y), d: res)
            let diffs = (0..<candidates.count).map { res[$0] - nb[$0] }
            let dev = diffs.map { abs($0) }.sorted()
            let sigma = max(Double(noise), 1.4826 * dev[dev.count / 2])
            for i in 0..<candidates.count where diffs[i] > tolerance * sigma { use[i] = false }
        }
        var out: [BackgroundSample] = []
        for (i, c) in candidates.enumerated() where use[i] && c.m.scatter <= scatterLimit && c.m.skew <= skewLimit {
            out.append(BackgroundSample(x: c.x, y: c.y))
        }
        return out
    }

    // MARK: Surface

    public struct Surface {
        /// The surface over the frame at a coarse resolution (`scale` surface pixels per image pixel).
        public var plane: PlanarImage
        public var scale: Float
        public var results: [SampleResult]
        /// Robust noise of the kept samples around the surface, in units of the sample noise.
        public var residualSigma: Float
        /// Smoothing used (the one chosen by cross-validation when automatic).
        public var smoothing: Double

        /// Bilinear value at a full-resolution point.
        @inline(__always) public func value(x: Float, y: Float) -> Float {
            let w = plane.width, h = plane.height
            let fx = min(max(x * scale - 0.5, 0), Float(w - 1)), fy = min(max(y * scale - 0.5, 0), Float(h - 1))
            let x0 = Int(fx), y0 = Int(fy)
            let x1 = min(x0 + 1, w - 1), y1 = min(y0 + 1, h - 1)
            let tx = fx - Float(x0), ty = fy - Float(y0)
            let p = plane.pointer
            let a = p[y0 * w + x0] + (p[y0 * w + x1] - p[y0 * w + x0]) * tx
            let b = p[y1 * w + x0] + (p[y1 * w + x1] - p[y1 * w + x0]) * tx
            return a + (b - a) * ty
        }
    }

    /// Fits the surface through the samples (measured on `samples`, discs of `radius`).
    /// `smoothing` 0…1: 0 follows the samples closely, 1 is a very stiff surface; nil picks the
    /// smoothing by generalised cross-validation (the surface that best predicts each sample from
    /// the others).
    public static func fit(_ points: [BackgroundSample], radius: Double, samples s: GradientRemoval.Samples, surface kind: SampleSurface, smoothing: Double?) -> Surface? {
        let index = Index(s)
        var results: [SampleResult] = []
        var px: [Double] = [], py: [Double] = [], pv: [Double] = [], pw: [Double] = []
        var ids: [Int] = []
        let noise = Double(max(s.noise, 1e-12))
        for (i, p) in points.enumerated() {
            if let m = measure(index, x: p.x, y: p.y, radius: radius), px.count < maximumSamples {
                results.append(SampleResult(id: p.id, value: m.value, model: m.value, weight: 1))
                px.append(p.x); py.append(p.y); pv.append(Double(m.value))
                // Structured discs (scatter well above the noise) start with less weight.
                let q = Double(m.scatter) / noise / 3
                pw.append(1 / (1 + q * q))
                ids.append(i)
            } else {
                results.append(SampleResult(id: p.id, value: nil, model: 0, weight: 0))
            }
        }
        let n = px.count
        guard n >= 4 else { return nil }
        // Normalised coordinates (long side = 1) keep the kernel well scaled.
        let L = Double(max(s.width, s.height))
        let u = px.map { $0 / L }, v = py.map { $0 / L }
        let mean = pv.reduce(0, +) / Double(n)
        let z = pv.map { $0 - mean }
        let spacing = typicalSpacing(u, v)
        let np = kind == .spline ? 3 : 1   // polynomial part: plane for the spline, constant for kriging
        var model = Model(kind: kind, u: u, v: v, np: np, spacing: spacing)
        // Samples far above their nearest neighbours lose their weight before anything is fitted: a
        // surface bent through a sample on nebulosity (or two of them next to each other) cannot
        // tell, its neighbours can. Four neighbours: a handful of samples the user put on a compact
        // glow agree with each other and stay.
        if n >= 6 {
            let nb = neighbourMedians(x: u, y: v, d: z, k: 4)
            let diffs = (0..<n).map { z[$0] - nb[$0] }
            let dev = diffs.map { abs($0) }.sorted()
            let sigma = max(noise, 1.4826 * dev[dev.count / 2])
            for i in 0..<n where diffs[i] > 4 * sigma { pw[i] *= 0.05 }
        }
        var weights = pw
        var solution: Model.Solution? = nil
        var chosen = smoothing ?? 0.5
        var sigma = noise
        // Rounds of asymmetric robust reweighting on the leave-one-out residuals: a sample on
        // nebulosity is compared with what its neighbours predict, not with a surface bent through it.
        for round in 0..<4 {
            if smoothing == nil {
                // GCV over a coarse grid of smoothings (with the current weights), then the best.
                var best = (score: Double.infinity, value: 0.5)
                for t in stride(from: 0.0, through: 1.0, by: 0.125) {
                    guard let sol = model.solve(z: z, weights: weights, smoothing: t) else { continue }
                    var score = 0.0, cnt = 0.0
                    for i in 0..<n where weights[i] >= Double(rejectedWeight) { score += sol.loo[i] * sol.loo[i]; cnt += 1 }
                    score /= max(cnt, 1)
                    if score < best.score { best = (score, t) }
                }
                chosen = best.value
            }
            guard let sol = model.solve(z: z, weights: weights, smoothing: chosen) else { return nil }
            solution = sol
            if round == 3 { break }
            let kept = (0..<n).filter { weights[$0] >= Double(rejectedWeight) }
            let dev = kept.map { abs(sol.loo[$0]) }.sorted()
            sigma = max(noise, dev.count >= 4 ? 1.4826 * dev[dev.count / 2] : noise)
            for i in 0..<n {
                let r = sol.loo[i]
                let k = r > 0 ? 1.5 * sigma : 3.5 * sigma
                let huber = abs(r) <= k ? 1.0 : k / abs(r)
                weights[i] = pw[i] * huber * huber
            }
        }
        guard let sol = solution else { return nil }
        // Read-outs.
        // The residual read-out is the leave-one-out one (an interpolating surface has none of its own).
        var sq = 0.0, cnt = 0
        for (k, i) in ids.enumerated() {
            let m = sol.fitted[k] + mean
            results[i].model = Float(m)
            results[i].weight = Float(weights[k])
            if weights[k] >= Double(rejectedWeight) { let r = sol.loo[k]; sq += r * r; cnt += 1 }
        }
        let residualSigma = Float((sq / Double(max(cnt, 1))).squareRoot() / noise)
        // Evaluate on the coarse grid.
        let scale = Float(surfaceSize) / Float(max(s.width, s.height))
        let cw = max(2, Int((Float(s.width) * scale).rounded())), ch = max(2, Int((Float(s.height) * scale).rounded()))
        let plane = PlanarImage(width: cw, height: ch)
        let bands = max(1, min(ch, ProcessInfo.processInfo.activeProcessorCount * 2))
        DispatchQueue.concurrentPerform(iterations: bands) { band in
            let y0 = ch * band / bands, y1 = ch * (band + 1) / bands
            for yy in y0..<y1 {
                let py = (Double(yy) + 0.5) / Double(scale) / L
                for xx in 0..<cw {
                    let pxl = (Double(xx) + 0.5) / Double(scale) / L
                    plane.pointer[yy * cw + xx] = Float(model.evaluate(sol, u: pxl, v: py) + mean)
                }
            }
        }
        return Surface(plane: plane, scale: scale, results: results, residualSigma: residualSigma, smoothing: chosen)
    }

    /// The radial-basis model: kernel matrix, polynomial part, and a solver that also returns the
    /// leave-one-out residuals through the hat matrix (r_loo = r / (1 − H_ii)).
    struct Model {
        let kind: SampleSurface
        let u: [Double], v: [Double]
        let np: Int
        let spacing: Double
        /// Kernel matrix and its mean magnitude for the last smoothing asked.
        var K: [Double] = []
        var kMean: Double = 1
        var ell: Double = 0
        var built: Double = .nan

        init(kind: SampleSurface, u: [Double], v: [Double], np: Int, spacing: Double) {
            self.kind = kind; self.u = u; self.v = v; self.np = np; self.spacing = spacing
        }

        /// Correlation length of the kriging kernel: 1.5–4 sample spacings with the smoothing.
        func length(_ smoothing: Double) -> Double { spacing * (1.5 + 2.5 * min(max(smoothing, 0), 1)) }

        @inline(__always) func kernel(_ r2: Double) -> Double {
            if kind == .kriging { return exp(-r2 / (2 * ell * ell)) }
            return r2 > 1e-24 ? 0.5 * r2 * log(r2) : 0
        }

        mutating func build(smoothing: Double) {
            let l = kind == .kriging ? length(smoothing) : 0
            if built == smoothing || (kind == .spline && !K.isEmpty) { return }
            ell = l
            let n = u.count
            var K = [Double](repeating: 0, count: n * n)
            var sum = 0.0
            for i in 0..<n {
                for j in 0..<n {
                    let dx = u[i] - u[j], dy = v[i] - v[j]
                    let k = kernel(dx * dx + dy * dy)
                    K[i * n + j] = k; sum += abs(k)
                }
            }
            self.K = K
            kMean = max(sum / Double(n * n), 1e-12)
            built = smoothing
        }

        /// Ridge for a smoothing in 0…1: 10⁻⁴…1 × the kernel's mean magnitude (the nugget for kriging).
        func lambda(_ smoothing: Double) -> Double { kMean * pow(10, -4 + 4 * min(max(smoothing, 0), 1)) }

        struct Solution {
            var coefficients: [Double]
            var fitted: [Double]
            var loo: [Double]
            var ell: Double
        }

        mutating func solve(z: [Double], weights: [Double], smoothing: Double) -> Solution? {
            build(smoothing: smoothing)
            let n = u.count, dim = n + np
            let lambda = self.lambda(smoothing)
            var A = [Double](repeating: 0, count: dim * dim)
            for i in 0..<n {
                for j in 0..<n { A[i * dim + j] = K[i * n + j] }
                // Rejected samples leave the system altogether (a ridge far above the kernel);
                // the others are ridged by the smoothing over their weight.
                A[i * dim + i] += weights[i] < Double(BackgroundSamples.rejectedWeight) ? kMean * 1e6 : lambda / weights[i]
                A[i * dim + n] = 1; A[n * dim + i] = 1
                if np == 3 { A[i * dim + n + 1] = u[i]; A[(n + 1) * dim + i] = u[i]; A[i * dim + n + 2] = v[i]; A[(n + 2) * dim + i] = v[i] }
            }
            guard let lu = LU(A, n: dim) else { return nil }
            var rhs = [Double](repeating: 0, count: dim)
            for i in 0..<n { rhs[i] = z[i] }
            let c = lu.solve(rhs)
            // Fitted values and the diagonal of the hat matrix: f = B M⁻¹ z with B = [K P] (rows of
            // the system without the ridge), so H_ii = B_i · (M⁻¹ e_i).
            var fitted = [Double](repeating: 0, count: n), loo = [Double](repeating: 0, count: n)
            var e = [Double](repeating: 0, count: dim)
            for i in 0..<n {
                var f = c[n]
                if np == 3 { f += c[n + 1] * u[i] + c[n + 2] * v[i] }
                for j in 0..<n { f += K[i * n + j] * c[j] }
                fitted[i] = f
                e[i] = 1
                let col = lu.solve(e)
                e[i] = 0
                var h = col[n]
                if np == 3 { h += col[n + 1] * u[i] + col[n + 2] * v[i] }
                for j in 0..<n { h += K[i * n + j] * col[j] }
                let r = z[i] - f
                loo[i] = r / max(1 - min(h, 0.999), 1e-3)
            }
            return Solution(coefficients: c, fitted: fitted, loo: loo, ell: ell)
        }

        /// The surface of a solution at a point (normalised coordinates).
        func evaluate(_ sol: Solution, u x: Double, v y: Double) -> Double {
            let n = u.count, c = sol.coefficients
            var s = c[n]
            if np == 3 { s += c[n + 1] * x + c[n + 2] * y }
            let l2 = 2 * sol.ell * sol.ell
            for i in 0..<n {
                let dx = x - u[i], dy = y - v[i]
                let r2 = dx * dx + dy * dy
                s += c[i] * (kind == .kriging ? exp(-r2 / l2) : (r2 > 1e-24 ? 0.5 * r2 * log(r2) : 0))
            }
            return s
        }
    }

    /// LU decomposition with partial pivoting (Doolittle), for several right-hand sides.
    struct LU {
        var m: [Double]
        var perm: [Int]
        let n: Int

        init?(_ a: [Double], n: Int) {
            self.n = n
            m = a
            perm = Array(0..<n)
            var ok = true
            m.withUnsafeMutableBufferPointer { mp in
                let M = mp.baseAddress!
                for col in 0..<n {
                    var piv = col, best = abs(M[col * n + col])
                    for r in (col + 1)..<n { let a = abs(M[r * n + col]); if a > best { best = a; piv = r } }
                    guard best > 1e-14 else { ok = false; return }
                    if piv != col {
                        for j in 0..<n { let t = M[col * n + j]; M[col * n + j] = M[piv * n + j]; M[piv * n + j] = t }
                        perm.swapAt(col, piv)
                    }
                    let d = M[col * n + col]
                    for r in (col + 1)..<n {
                        let f = M[r * n + col] / d
                        M[r * n + col] = f
                        guard f != 0 else { continue }
                        var fneg = -f
                        if n - col - 1 > 0 { vDSP_vsmaD(M + col * n + col + 1, 1, &fneg, M + r * n + col + 1, 1, M + r * n + col + 1, 1, vDSP_Length(n - col - 1)) }
                    }
                }
            }
            if !ok { return nil }
        }

        func solve(_ b: [Double]) -> [Double] {
            var x = [Double](repeating: 0, count: n)
            for i in 0..<n { x[i] = b[perm[i]] }
            m.withUnsafeBufferPointer { mp in
                let M = mp.baseAddress!
                for i in 0..<n { var s = x[i]; for j in 0..<i { s -= M[i * n + j] * x[j] }; x[i] = s }
                for i in stride(from: n - 1, through: 0, by: -1) { var s = x[i]; for j in (i + 1)..<n { s -= M[i * n + j] * x[j] }; x[i] = s / M[i * n + i] }
            }
            return x
        }
    }

    /// Weighted least-squares plane c0 + c1·(y/h) + c2·(x/w) (the polynomial's coefficient order).
    static func weightedPlane(x: [Float], y: [Float], v: [Float], w: [Double], width: Float, height: Float) -> [Float]? {
        var a = [Double](repeating: 0, count: 9), b = [Double](repeating: 0, count: 3)
        for i in 0..<v.count {
            let row = [1.0, Double(y[i] / height), Double(x[i] / width)], z = Double(v[i]), wi = w[i]
            for p in 0..<3 { b[p] += wi * row[p] * z; for q in 0..<3 { a[p * 3 + q] += wi * row[p] * row[q] } }
        }
        return GradientRemoval.solve(a, b, n: 3)
    }

    /// Median distance to the nearest other sample (normalised coordinates).
    static func typicalSpacing(_ u: [Double], _ v: [Double]) -> Double {
        let n = u.count
        guard n >= 2 else { return 0.1 }
        var d = [Double](repeating: 0, count: n)
        for i in 0..<n {
            var best = Double.infinity
            for j in 0..<n where j != i { let dx = u[i] - u[j], dy = v[i] - v[j]; best = min(best, dx * dx + dy * dy) }
            d[i] = best.squareRoot()
        }
        d.sort()
        return max(d[n / 2], 1e-3)
    }

}
