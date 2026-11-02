import Foundation
import Accelerate

/// Shape of the guide the user draws over a gradient.
public enum GradientShape: String, Codable, CaseIterable, Identifiable {
    /// The background varies along the line and is constant across it (light pollution from one side).
    case line
    /// The background is a function of the elliptical radius around the centre (vignetting, a glow
    /// from a bright source that may lie outside the frame).
    case ellipse
    /// No guide: a plain 2-D polynomial of the chosen degree (the classic parametric model).
    case polynomial
    public var id: String { rawValue }
    public var displayName: String {
        switch self { case .line: return "Line"; case .ellipse: return "Ellipse"; case .polynomial: return "Polynomial" }
    }
}

/// How the estimated background is taken out.
public enum GradientMode: String, Codable, CaseIterable, Identifiable {
    /// Background subtracted, overall level kept (sky glow, light pollution, stretched data).
    case subtract
    /// Pixels divided by the background normalised to its brightest level (vignetting / missing
    /// flats on linear data: stars at the edge get their flux back).
    case divide
    public var id: String { rawValue }
    public var displayName: String { self == .subtract ? "Subtract" : "Divide" }
}

/// Low-order 2-D term fitted after the guided profile, for whatever the profile cannot explain
/// (a sky gradient on top of the vignetting).
public enum GradientResidual: String, Codable, CaseIterable, Identifiable {
    case none, plane, quadratic
    public var id: String { rawValue }
    public var displayName: String {
        switch self { case .none: return "None"; case .plane: return "Plane"; case .quadratic: return "Quadratic" }
    }
    var terms: Int { switch self { case .none: return 0; case .plane: return 3; case .quadratic: return 6 } }
    var degree: Int { switch self { case .none: return 0; case .plane: return 1; case .quadratic: return 2 } }
}

/// A guide drawn on the image, in oriented image pixels. Line: from (x, y) to (x2, y2). Ellipse:
/// centre (x, y) — it may lie outside the frame —, semi-axes `a` (along `angle`) and `b`, `angle` in
/// radians measured from the +x axis towards +y (clockwise on screen). Polynomial: geometry unused.
public struct GradientGesture: Codable, Equatable, Hashable {
    public var shape: GradientShape
    public var x: Double
    public var y: Double
    public var x2: Double
    public var y2: Double
    public var a: Double
    public var b: Double
    public var angle: Double

    public init(shape: GradientShape, x: Double = 0, y: Double = 0, x2: Double = 0, y2: Double = 0, a: Double = 1, b: Double = 1, angle: Double = 0) {
        self.shape = shape; self.x = x; self.y = y; self.x2 = x2; self.y2 = y2; self.a = a; self.b = b; self.angle = angle
    }

    public static func line(from p0: CGPoint, to p1: CGPoint) -> GradientGesture {
        GradientGesture(shape: .line, x: p0.x, y: p0.y, x2: p1.x, y2: p1.y)
    }
    public static func ellipse(center c: CGPoint, a: Double, b: Double, angle: Double = 0) -> GradientGesture {
        GradientGesture(shape: .ellipse, x: c.x, y: c.y, a: a, b: b, angle: angle)
    }
    public static func polynomial() -> GradientGesture { GradientGesture(shape: .polynomial) }

    public var center: CGPoint { CGPoint(x: x, y: y) }
    public var end: CGPoint { CGPoint(x: x2, y: y2) }
    public var lineLength: Double { hypot(x2 - x, y2 - y) }

    /// The same guide converted to another shape (so the drawing survives a shape switch).
    public func converted(to s: GradientShape) -> GradientGesture {
        guard s != shape else { return self }
        var g = self
        g.shape = s
        switch (shape, s) {
        case (.line, .ellipse):
            let l = max(lineLength, 1)
            g.a = l; g.b = l; g.angle = atan2(y2 - y, x2 - x)
        case (.ellipse, .line):
            g.x2 = x + a * cos(angle); g.y2 = y + a * sin(angle)
        default: break
        }
        return g
    }

    /// The guide after the image (`imageWidth` × `imageHeight`) has undergone `op`.
    public func applying(_ op: Orientation.Op, imageWidth: Int, imageHeight: Int) -> GradientGesture {
        var g = self
        let w = Double(imageWidth), h = Double(imageHeight)
        switch op {
        case .rotateClockwise:
            g.x = h - y; g.y = x; g.x2 = h - y2; g.y2 = x2; g.angle = angle + .pi / 2
        case .flipHorizontal:
            g.x = w - x; g.x2 = w - x2; g.angle = .pi - angle
        case .flipVertical:
            g.y = h - y; g.y2 = h - y2; g.angle = -angle
        }
        return g
    }

    /// The guide after an unoriented image is oriented (flip, then quarter turns).
    public func applying(_ o: Orientation, imageWidth: Int, imageHeight: Int) -> GradientGesture {
        var g = o.flipped ? applying(.flipHorizontal, imageWidth: imageWidth, imageHeight: imageHeight) : self
        var w = imageWidth, h = imageHeight
        for _ in 0..<o.rotation { g = g.applying(.rotateClockwise, imageWidth: w, imageHeight: h); swap(&w, &h) }
        return g
    }
}

/// An elliptical region (axis-aligned) whose pixels are left out of the background estimate: the
/// nebula, a galaxy, anything that is not sky.
public struct GradientRegion: Codable, Equatable, Hashable, Identifiable {
    public var id: UUID
    public var x: Double
    public var y: Double
    public var a: Double
    public var b: Double

    public init(x: Double, y: Double, a: Double, b: Double, id: UUID = UUID()) {
        self.id = id; self.x = x; self.y = y; self.a = max(a, 1); self.b = max(b, 1)
    }

    public var center: CGPoint { CGPoint(x: x, y: y) }
    public func contains(x px: Double, y py: Double) -> Bool {
        let dx = (px - x) / a, dy = (py - y) / b
        return dx * dx + dy * dy <= 1
    }

    public func applying(_ op: Orientation.Op, imageWidth: Int, imageHeight: Int) -> GradientRegion {
        var r = self
        switch op {
        case .rotateClockwise: r.x = Double(imageHeight) - y; r.y = x; r.a = b; r.b = a
        case .flipHorizontal: r.x = Double(imageWidth) - x
        case .flipVertical: r.y = Double(imageHeight) - y
        }
        return r
    }

    public func applying(_ o: Orientation, imageWidth: Int, imageHeight: Int) -> GradientRegion {
        var r = o.flipped ? applying(.flipHorizontal, imageWidth: imageWidth, imageHeight: imageHeight) : self
        var w = imageWidth, h = imageHeight
        for _ in 0..<o.rotation { r = r.applying(.rotateClockwise, imageWidth: w, imageHeight: h); swap(&w, &h) }
        return r
    }
}

/// The gradient model of one channel: the guide plus the estimation parameters.
public struct GradientSpec: Codable, Equatable, Hashable {
    public var gesture: GradientGesture
    public var mode: GradientMode
    public var residual: GradientResidual
    /// Angular sectors of the elliptical profile (1 = one profile for every direction; 4–12 let the
    /// fall-off differ with the direction, for a tilted sensor or asymmetric vignetting).
    public var sectors: Int
    /// Smoothing of the profile along the guide, in bins (of 120 over the frame).
    public var smoothness: Double
    /// Degree of the polynomial shape (2…6).
    public var degree: Int
    /// Optimise the guide's geometry (line direction; ellipse centre, axis ratio and angle) from the
    /// data before estimating the profile — the drawing is a starting point, not a measurement.
    public var refine: Bool

    public init(gesture: GradientGesture, mode: GradientMode = .subtract, residual: GradientResidual = .plane, sectors: Int = 1,
                smoothness: Double = 1.5, degree: Int = 4, refine: Bool = true) {
        self.gesture = gesture; self.mode = mode; self.residual = residual; self.sectors = sectors
        self.smoothness = smoothness; self.degree = degree; self.refine = refine
    }

    public func applying(_ op: Orientation.Op, imageWidth: Int, imageHeight: Int) -> GradientSpec {
        var s = self; s.gesture = gesture.applying(op, imageWidth: imageWidth, imageHeight: imageHeight); return s
    }
}

/// Gradient models per channel plus the shared protected regions and the parameters given to new
/// models. Stored in the project; applied to the loaded channel after dust healing and before the
/// statistics, normalisation and everything else.
public struct GradientSettings: Codable, Equatable {
    public var specs: [ChannelKind: GradientSpec] = [:]
    /// Regions left out of every channel's background estimate (the object is at the same place in
    /// every channel).
    public var protect: [GradientRegion] = []
    /// Parameters given to new models.
    public var shape: GradientShape = .ellipse
    public var mode: GradientMode = .subtract
    public var residual: GradientResidual = .plane
    public var sectors: Int = 1
    public var smoothness: Double = 1.5
    public var degree: Int = 4
    public var refine: Bool = true

    public init() {}

    public var isEmpty: Bool { specs.isEmpty }
    public func spec(for kind: ChannelKind) -> GradientSpec? { specs[kind] }
    /// A model with the parameters for new models around a drawn guide.
    public func newSpec(gesture: GradientGesture) -> GradientSpec {
        GradientSpec(gesture: gesture, mode: mode, residual: residual, sectors: sectors, smoothness: smoothness, degree: degree, refine: refine)
    }

    /// Everything mapped through an orientation operation of the `imageWidth` × `imageHeight` image.
    public func applying(_ op: Orientation.Op, imageWidth: Int, imageHeight: Int) -> GradientSettings {
        var g = self
        g.specs = specs.mapValues { $0.applying(op, imageWidth: imageWidth, imageHeight: imageHeight) }
        g.protect = protect.map { $0.applying(op, imageWidth: imageWidth, imageHeight: imageHeight) }
        return g
    }
}

/// Removes a smooth background (sky gradient, glow, vignetting) from a channel, guided by a shape
/// the user draws over it.
///
/// A parametric surface (polynomial, spline) has to be flexible enough to follow the gradient and
/// is then flexible enough to eat the nebula; a guide fixes the *topology* of the background
/// instead: along a line, or around an ellipse, and the algorithm only has to estimate a robust
/// 1-D profile f(u) along that coordinate (median of the star-masked cell medians per bin,
/// smoothed) plus, optionally, a plane or quadratic for whatever the profile cannot explain. The
/// drawn geometry is refined from the data (Nelder–Mead on the robust residual, several starts
/// for the ellipse centre) since the eye places a vignetting centre to within a few hundred
/// pixels at best — and a wrong centre leaves rings. Everything is estimated on star-masked
/// medians of small cells of a pyramid level (≈ 100–250 k samples), the model is evaluated per
/// pixel at full resolution, and the correction is subtracted (level kept) or divided out
/// (normalised to the brightest level: vignetting). Zero (padding) pixels stay zero.
///
/// On the two reference frames (Pleiades Hα corner glow, M78 OIII without flats) the guided
/// ellipse leaves a large-scale residual of 1.9σ / 5.6σ against 4.0σ / 8.0σ for a 4th-degree
/// polynomial and 3.8σ / 9.8σ for a large-kernel high-pass; on synthetic truth the RMSE is 3–15×
/// lower than the polynomial when the guide is right and still 2× lower after refinement from a
/// badly placed one.
public enum GradientRemoval {
    /// Background samples: star-masked medians of small cells, in full-resolution coordinates.
    public struct Samples {
        public var x: [Float]
        public var y: [Float]
        public var v: [Float]
        /// Robust noise of one sample (from the differences of neighbouring cells).
        public var noise: Float
        public var width: Int
        public var height: Int
        public var count: Int { v.count }

        /// Every `step`-th sample (for the geometry refinement).
        func subsampled(maxCount: Int) -> Samples {
            let step = max(1, count / maxCount)
            guard step > 1 else { return self }
            var s = self
            s.x = stride(from: 0, to: count, by: step).map { x[$0] }
            s.y = stride(from: 0, to: count, by: step).map { y[$0] }
            s.v = stride(from: 0, to: count, by: step).map { v[$0] }
            return s
        }
    }

    /// Number of profile bins across the range of the guide coordinate.
    static let bins = 120
    /// Cells per side at the analysis level.
    static let cell = 4
    /// Pixel budget of the analysis level.
    static let levelPixels = 4_000_000

    /// Star-masked cell medians of `image`, leaving out zero pixels and the protected regions.
    public static func samples(image: PlanarImage, protect: [GradientRegion] = []) -> Samples {
        var level = image
        var scale = 1
        while level.width * level.height > levelPixels && level.width > 64 && level.height > 64 {
            level = level.downsampled2x(); scale *= 2
        }
        var o = StarMask.Options()
        o.thresholdSigma = 3; o.dilation = 2; o.backgroundLevels = 3; o.maxSamples = 200_000
        let mask = StarMask.build(image: level, options: o)
        let w = level.width, h = level.height, c = cell
        let cw = w / c, ch = h / c
        var xs: [Float] = [], ys: [Float] = [], vs: [Float] = []
        xs.reserveCapacity(cw * ch); ys.reserveCapacity(cw * ch); vs.reserveCapacity(cw * ch)
        var buf = [Float](repeating: 0, count: c * c)
        let minCount = max(3, (c * c * 2) / 5)
        let fs = Float(scale)
        for cy in 0..<ch {
            for cx in 0..<cw {
                let px = (Float(cx) + 0.5) * Float(c) * fs, py = (Float(cy) + 0.5) * Float(c) * fs
                if protect.contains(where: { $0.contains(x: Double(px), y: Double(py)) }) { continue }
                var n = 0
                for y in (cy * c)..<(cy * c + c) {
                    let row = y * w
                    for x in (cx * c)..<(cx * c + c) {
                        let i = row + x
                        let v = level.pointer[i]
                        if v != 0 && mask.pointer[i] == 0 { buf[n] = v; n += 1 }
                    }
                }
                guard n >= minCount else { continue }
                // Median of n values (insertion sort: n ≤ 16).
                for i in 1..<n { let t = buf[i]; var j = i - 1; while j >= 0 && buf[j] > t { buf[j + 1] = buf[j]; j -= 1 }; buf[j + 1] = t }
                let med = n % 2 == 1 ? buf[n / 2] : 0.5 * (buf[n / 2 - 1] + buf[n / 2])
                xs.append(px); ys.append(py); vs.append(med)
            }
        }
        // Noise of a sample: MAD of differences between consecutive samples of the same row.
        var diffs: [Float] = []
        diffs.reserveCapacity(vs.count)
        for i in 1..<max(vs.count, 1) where ys[i] == ys[i - 1] && xs[i] - xs[i - 1] <= Float(c) * fs * 1.5 { diffs.append(abs(vs[i] - vs[i - 1])) }
        var noise: Float = 0
        if diffs.count >= 8 {
            diffs.sort()
            noise = 1.4826 * Statistics.percentile(sorted: diffs, q: 0.5) / Float(2).squareRoot()
        }
        return Samples(x: xs, y: ys, v: vs, noise: max(noise, 1e-12), width: image.width, height: image.height)
    }

    // MARK: Model

    /// Robust profile f(u[, φ]) over equal-width bins of the guide coordinate (sector-wise for the
    /// ellipse when `sectors` > 1), smoothed along u and circularly along φ.
    struct Profile {
        var lo: Float
        var hi: Float
        var sectors: Int
        var values: [Float]   // sectors × bins

        @inline(__always) func eval(u: Float, phi: Float) -> Float {
            let nb = GradientRemoval.bins
            let t = (u - lo) / (hi - lo) * Float(nb) - 0.5
            let i0 = Int(floor(t))
            let ia = min(max(i0, 0), nb - 1), ib = min(max(i0 + 1, 0), nb - 1)
            let f = min(max(t - Float(i0), 0), 1)
            if sectors == 1 { return values[ia] + (values[ib] - values[ia]) * f }
            // Bilinear in the sector direction (sector centres at (k + 0.5) · 2π / sectors).
            var s = (phi / (2 * .pi) + 1) * Float(sectors) - 0.5
            s -= floor(s / Float(sectors)) * Float(sectors)
            let s0 = Int(floor(s)), sf = s - Float(s0)
            let ka = ((s0 % sectors) + sectors) % sectors, kb = (ka + 1) % sectors
            let va = values[ka * nb + ia] + (values[ka * nb + ib] - values[ka * nb + ia]) * f
            let vb = values[kb * nb + ia] + (values[kb * nb + ib] - values[kb * nb + ia]) * f
            return va + (vb - va) * sf
        }

        /// Fits the profile to (u, φ, v) samples.
        init?(u: [Float], phi: [Float], v: [Float], sectors ns: Int, smoothness: Double) {
            let n = v.count
            guard n >= 16 else { return nil }
            let nb = GradientRemoval.bins
            var lo = Float.greatestFiniteMagnitude, hi = -Float.greatestFiniteMagnitude
            for x in u { lo = min(lo, x); hi = max(hi, x) }
            guard hi > lo else { return nil }
            hi += (hi - lo) * 1e-4
            self.lo = lo; self.hi = hi; sectors = max(1, ns)
            // Bucket the samples.
            var key = [Int](repeating: 0, count: n)
            var counts = [Int](repeating: 0, count: sectors * nb)
            for i in 0..<n {
                let b = min(max(Int((u[i] - lo) / (hi - lo) * Float(nb)), 0), nb - 1)
                var s = 0
                if sectors > 1 {
                    var t = (phi[i] / (2 * .pi) + 1) * Float(sectors)
                    t -= floor(t / Float(sectors)) * Float(sectors)
                    s = min(max(Int(t), 0), sectors - 1)
                }
                key[i] = s * nb + b
                counts[s * nb + b] += 1
            }
            var starts = [Int](repeating: 0, count: sectors * nb + 1)
            for k in 0..<(sectors * nb) { starts[k + 1] = starts[k] + counts[k] }
            var sorted = [Float](repeating: 0, count: n)
            var fill = starts
            for i in 0..<n { sorted[fill[key[i]]] = v[i]; fill[key[i]] += 1 }
            var med = [Float](repeating: .nan, count: sectors * nb)
            for k in 0..<(sectors * nb) where counts[k] >= 3 {
                var slice = Array(sorted[starts[k]..<starts[k + 1]])
                slice.withUnsafeMutableBufferPointer { vDSP_vsort($0.baseAddress!, vDSP_Length($0.count), 1) }
                med[k] = Statistics.percentile(sorted: slice, q: 0.5)
            }
            // Global (all-sector) profile fills the gaps of each sector.
            var global = [Float](repeating: .nan, count: nb)
            for b in 0..<nb {
                var acc: [Float] = []
                for s in 0..<sectors where !med[s * nb + b].isNaN { acc.append(med[s * nb + b]) }
                if !acc.isEmpty { acc.sort(); global[b] = Statistics.percentile(sorted: acc, q: 0.5) }
            }
            Profile.interpolateGaps(&global)
            if global.allSatisfy({ $0.isNaN }) { return nil }
            var weights = [Float](repeating: 0, count: sectors * nb)
            for s in 0..<sectors {
                for b in 0..<nb {
                    let k = s * nb + b
                    if med[k].isNaN { med[k] = global[b]; weights[k] = 0.25 } else { weights[k] = Float(counts[k]) }
                }
            }
            // Count-weighted Gaussian smoothing along u (σ = smoothness bins), circular along φ (σ = 0.7 sector).
            let su = Float(max(smoothness, 0))
            var out = [Float](repeating: 0, count: sectors * nb)
            let ru = Int(ceil(3 * su))
            let sphi: Float = sectors > 1 ? 0.7 : 0
            let rphi = sectors > 1 ? min(sectors / 2, Int(ceil(3 * sphi))) : 0
            for s in 0..<sectors {
                for b in 0..<nb {
                    var num: Float = 0, den: Float = 0
                    for ds in -rphi...rphi {
                        let ss = ((s + ds) % sectors + sectors) % sectors
                        let wp: Float = sphi > 0 ? exp(-0.5 * Float(ds * ds) / (sphi * sphi)) : 1
                        for db in -ru...ru {
                            let bb = min(max(b + db, 0), nb - 1)
                            let wu: Float = su > 0 ? exp(-0.5 * Float(db * db) / (su * su)) : (db == 0 ? 1 : 0)
                            let w = wp * wu * weights[ss * nb + bb]
                            num += w * med[ss * nb + bb]; den += w
                        }
                    }
                    out[s * nb + b] = den > 0 ? num / den : med[s * nb + b]
                }
            }
            values = out
        }

        /// Linear interpolation across NaN runs, flat extension at the ends.
        static func interpolateGaps(_ a: inout [Float]) {
            let n = a.count
            guard let first = a.firstIndex(where: { !$0.isNaN }) else { return }
            for i in 0..<first { a[i] = a[first] }
            var i = first
            while i < n {
                if a[i].isNaN {
                    var j = i
                    while j < n && a[j].isNaN { j += 1 }
                    let left = a[i - 1]
                    if j < n {
                        let right = a[j]
                        for k in i..<j { a[k] = left + (right - left) * Float(k - i + 1) / Float(j - i + 1) }
                    } else {
                        for k in i..<n { a[k] = left }
                    }
                    i = j
                } else { i += 1 }
            }
        }
    }

    /// The fitted model of one channel.
    public struct Fit {
        /// The guide actually used (refined when asked).
        public var gesture: GradientGesture
        public var spec: GradientSpec
        var profile: Profile?
        /// Polynomial coefficients (residual term, or the whole model for the polynomial shape) over
        /// the monomials x^i y^j (i + j ≤ degree) of the normalised coordinates.
        var poly: [Float]
        var polyDegree: Int
        /// Level the corrected background is brought to (subtract: median of the model over the
        /// samples; divide: its brightest level).
        public var reference: Float
        /// RMS of the sample residuals in units of the sample noise (quality read-out).
        public var residualSigma: Float
        public var width: Int
        public var height: Int

        @inline(__always) func background(x: Float, y: Float) -> Float {
            var v: Float = 0
            if let p = profile {
                let (u, phi) = GradientRemoval.coordinate(gesture, x: x, y: y)
                v = p.eval(u: u, phi: phi)
            }
            if !poly.isEmpty { v += GradientRemoval.polynomial(poly, degree: polyDegree, nx: x / Float(width), ny: y / Float(height)) }
            return v
        }

        /// The model evaluated at every pixel.
        public func backgroundImage() -> PlanarImage {
            let out = PlanarImage(width: width, height: height)
            let bands = max(1, min(height, ProcessInfo.processInfo.activeProcessorCount * 4))
            DispatchQueue.concurrentPerform(iterations: bands) { band in
                let y0 = height * band / bands, y1 = height * (band + 1) / bands
                for y in y0..<y1 {
                    let row = out.pointer + y * width
                    let py = Float(y) + 0.5
                    for x in 0..<width { row[x] = background(x: Float(x) + 0.5, y: py) }
                }
            }
            return out
        }
    }

    /// Guide coordinate of a pixel: (u, φ). Line: projection along the line in units of its length
    /// (0 at the start, 1 at the end), φ = 0. Ellipse: elliptical radius (1 on the drawn ellipse) and
    /// the angle in the ellipse's frame.
    @inline(__always) static func coordinate(_ g: GradientGesture, x: Float, y: Float) -> (Float, Float) {
        switch g.shape {
        case .line:
            let dx = Float(g.x2 - g.x), dy = Float(g.y2 - g.y)
            let l2 = max(dx * dx + dy * dy, 1e-6)
            return (((x - Float(g.x)) * dx + (y - Float(g.y)) * dy) / l2, 0)
        case .ellipse:
            let ct = Float(cos(g.angle)), st = Float(sin(g.angle))
            let dx = x - Float(g.x), dy = y - Float(g.y)
            let xr = (dx * ct + dy * st) / Float(max(g.a, 1)), yr = (-dx * st + dy * ct) / Float(max(g.b, 1))
            return ((xr * xr + yr * yr).squareRoot(), atan2(yr, xr))
        case .polynomial:
            return (0, 0)
        }
    }

    static func polynomialTerms(_ degree: Int) -> Int { (degree + 1) * (degree + 2) / 2 }

    /// Σ c_k x^i y^j over i + j ≤ degree (k in the order i = 0…d, j = 0…d − i).
    @inline(__always) static func polynomial(_ c: [Float], degree: Int, nx: Float, ny: Float) -> Float {
        var v: Float = 0, k = 0
        var xi: Float = 1
        for i in 0...degree {
            var yj: Float = 1
            for _ in 0...(degree - i) { v += c[k] * xi * yj; yj *= ny; k += 1 }
            xi *= nx
        }
        return v
    }

    /// Robust (2.5σ-clipped, 3 rounds) least-squares polynomial through (x, y, v) in normalised coordinates.
    static func fitPolynomial(x: [Float], y: [Float], v: [Float], degree: Int, width: Int, height: Int, use: inout [Bool]) -> [Float]? {
        let m = polynomialTerms(degree), n = v.count
        guard n >= 2 * m else { return nil }
        var coef = [Float](repeating: 0, count: m)
        var row = [Double](repeating: 0, count: m)
        let fw = Float(width), fh = Float(height)
        for round in 0..<3 {
            // Normal equations accumulated in Double: 15 monomials over 250 k samples are ill-conditioned in Float.
            var ata = [Double](repeating: 0, count: m * m), atb = [Double](repeating: 0, count: m)
            for s in 0..<n where use[s] {
                let nx = Double(x[s] / fw), ny = Double(y[s] / fh), z = Double(v[s])
                var k = 0, xi = 1.0
                for i in 0...degree { var yj = 1.0; for _ in 0...(degree - i) { row[k] = xi * yj; yj *= ny; k += 1 }; xi *= nx }
                for i in 0..<m { atb[i] += row[i] * z; for j in i..<m { ata[i * m + j] += row[i] * row[j] } }
            }
            for i in 0..<m { for j in 0..<i { ata[i * m + j] = ata[j * m + i] } }
            guard let c = solve(ata, atb, n: m) else { return nil }
            coef = c
            if round == 2 { break }
            var res: [Float] = []; res.reserveCapacity(n / 4 + 1)
            for s in stride(from: 0, to: n, by: 4) where use[s] { res.append(abs(v[s] - polynomial(coef, degree: degree, nx: x[s] / fw, ny: y[s] / fh))) }
            guard res.count >= 8 else { break }
            res.sort()
            let sigma = 1.4826 * Statistics.percentile(sorted: res, q: 0.5)
            guard sigma > 0 else { break }
            let limit = 2.5 * sigma
            for s in 0..<n where use[s] { if abs(v[s] - polynomial(coef, degree: degree, nx: x[s] / fw, ny: y[s] / fh)) > limit { use[s] = false } }
        }
        return coef
    }

    /// Gaussian elimination with partial pivoting for a small dense system.
    static func solve(_ a: [Double], _ b: [Double], n: Int) -> [Float]? {
        var m = a, v = b
        for col in 0..<n {
            var piv = col
            for r in (col + 1)..<max(n, col + 1) where abs(m[r * n + col]) > abs(m[piv * n + col]) { piv = r }
            guard abs(m[piv * n + col]) > 1e-18 else { return nil }
            if piv != col { for j in 0..<n { m.swapAt(col * n + j, piv * n + j) }; v.swapAt(col, piv) }
            for r in (col + 1)..<max(n, col + 1) {
                let f = m[r * n + col] / m[col * n + col]
                guard f != 0 else { continue }
                for j in col..<n { m[r * n + j] -= f * m[col * n + j] }
                v[r] -= f * v[col]
            }
        }
        var x = [Double](repeating: 0, count: n)
        for r in stride(from: n - 1, through: 0, by: -1) {
            var s = v[r]
            for j in (r + 1)..<max(n, r + 1) { s -= m[r * n + j] * x[j] }
            x[r] = s / m[r * n + r]
        }
        return x.map(Float.init)
    }

    /// Profile (+ residual polynomial) for a given guide: the two are alternated so each explains
    /// what the other leaves. `use` marks the samples kept by the clipping (all when nil).
    static func fitModel(gesture: GradientGesture, samples s: Samples, spec: GradientSpec, iterations: Int) -> (Profile?, [Float], Int)? {
        let n = s.count
        if gesture.shape == .polynomial {
            var use = [Bool](repeating: true, count: n)
            let d = min(max(spec.degree, 1), 6)
            guard let c = fitPolynomial(x: s.x, y: s.y, v: s.v, degree: d, width: s.width, height: s.height, use: &use) else { return nil }
            return (nil, c, d)
        }
        var u = [Float](repeating: 0, count: n), phi = [Float](repeating: 0, count: n)
        for i in 0..<n { (u[i], phi[i]) = coordinate(gesture, x: s.x[i], y: s.y[i]) }
        let sectors = gesture.shape == .ellipse ? max(1, spec.sectors) : 1
        let rd = spec.residual.degree
        var poly: [Float] = []
        var prof: Profile? = nil
        var work = [Float](repeating: 0, count: n)
        let fw = Float(s.width), fh = Float(s.height)
        for it in 0..<(rd > 0 ? iterations : 1) {
            if poly.isEmpty { work = s.v } else { for i in 0..<n { work[i] = s.v[i] - polynomial(poly, degree: rd, nx: s.x[i] / fw, ny: s.y[i] / fh) } }
            guard let p = Profile(u: u, phi: phi, v: work, sectors: sectors, smoothness: spec.smoothness) else { return nil }
            prof = p
            guard rd > 0, it < iterations else { break }
            for i in 0..<n { work[i] = s.v[i] - p.eval(u: u[i], phi: phi[i]) }
            var use = [Bool](repeating: true, count: n)
            guard let c = fitPolynomial(x: s.x, y: s.y, v: work, degree: rd, width: s.width, height: s.height, use: &use) else { break }
            poly = c
        }
        return (prof, poly, rd)
    }

    /// Mean Huber loss of the model residuals (k = 2 sample noises).
    static func cost(gesture: GradientGesture, samples s: Samples, spec: GradientSpec) -> Double {
        guard let (prof, poly, rd) = fitModel(gesture: gesture, samples: s, spec: spec, iterations: 2) else { return .greatestFiniteMagnitude }
        let k = 2 * s.noise
        var total: Double = 0
        let fw = Float(s.width), fh = Float(s.height)
        for i in 0..<s.count {
            var m: Float = 0
            if let p = prof { let (u, phi) = coordinate(gesture, x: s.x[i], y: s.y[i]); m = p.eval(u: u, phi: phi) }
            if !poly.isEmpty { m += polynomial(poly, degree: rd, nx: s.x[i] / fw, ny: s.y[i] / fh) }
            let r = abs(s.v[i] - m)
            total += Double(r < k ? 0.5 * r * r : k * (r - 0.5 * k))
        }
        return total / Double(max(s.count, 1))
    }

    /// Nelder–Mead minimisation of `f` from `start` with an initial simplex of the given steps.
    static func nelderMead(start: [Double], steps: [Double], maxIterations: Int, tolerance: Double, f: ([Double]) -> Double) -> ([Double], Double) {
        let n = start.count
        var simplex: [[Double]] = [start]
        for i in 0..<n { var p = start; p[i] += steps[i]; simplex.append(p) }
        var values = simplex.map(f)
        for _ in 0..<maxIterations {
            let order = (0...n).sorted { values[$0] < values[$1] }
            simplex = order.map { simplex[$0] }; values = order.map { values[$0] }
            // Convergence: simplex small relative to the steps.
            var spread = 0.0
            for i in 1...n { for j in 0..<n { spread = max(spread, abs(simplex[i][j] - simplex[0][j]) / max(abs(steps[j]), 1e-12)) } }
            if spread < tolerance { break }
            var centroid = [Double](repeating: 0, count: n)
            for i in 0..<n { for j in 0..<n { centroid[j] += simplex[i][j] / Double(n) } }
            func point(_ t: Double) -> [Double] { (0..<n).map { centroid[$0] + t * (simplex[n][$0] - centroid[$0]) } }
            let xr = point(-1), fr = f(xr)
            if fr < values[0] {
                let xe = point(-2), fe = f(xe)
                if fe < fr { simplex[n] = xe; values[n] = fe } else { simplex[n] = xr; values[n] = fr }
            } else if fr < values[n - 1] {
                simplex[n] = xr; values[n] = fr
            } else {
                let xc = fr < values[n] ? point(-0.5) : point(0.5)
                let fc = f(xc)
                if fc < min(fr, values[n]) { simplex[n] = xc; values[n] = fc }
                else {
                    for i in 1...n { simplex[i] = (0..<n).map { simplex[0][$0] + 0.5 * (simplex[i][$0] - simplex[0][$0]) }; values[i] = f(simplex[i]) }
                }
            }
        }
        let best = (0...n).min { values[$0] < values[$1] }!
        return (simplex[best], values[best])
    }

    /// The guide's geometry optimised from the samples: the line's direction; the ellipse's centre,
    /// axis ratio and angle (several starting centres around the drawn one, the best kept). The
    /// polynomial has nothing to refine.
    public static func refine(_ gesture: GradientGesture, samples full: Samples, spec: GradientSpec) -> GradientGesture {
        let s = full.subsampled(maxCount: 3000)
        guard s.count >= 50 else { return gesture }
        let scale = Double(max(full.width, full.height))
        switch gesture.shape {
        case .polynomial:
            return gesture
        case .line:
            let l = max(gesture.lineLength, 1), th0 = atan2(gesture.y2 - gesture.y, gesture.x2 - gesture.x)
            func make(_ p: [Double]) -> GradientGesture {
                var g = gesture; g.x2 = g.x + l * cos(th0 + p[0]); g.y2 = g.y + l * sin(th0 + p[0]); return g
            }
            let (p, _) = nelderMead(start: [0], steps: [0.3], maxIterations: 60, tolerance: 1e-3) { cost(gesture: make($0), samples: s, spec: spec) }
            return make(p)
        case .ellipse:
            let a0 = max(gesture.a, 1), b0 = max(gesture.b, 1)
            func make(_ c: (Double, Double), _ p: [Double]) -> GradientGesture {
                var g = gesture
                g.x = c.0 + p[0] * scale; g.y = c.1 + p[1] * scale
                g.a = a0; g.b = a0 * (b0 / a0) * exp(p[2]); g.angle = gesture.angle + p[3]
                return g
            }
            let ct = cos(gesture.angle), st = sin(gesture.angle)
            let starts: [(Double, Double)] = [(gesture.x, gesture.y),
                                              (gesture.x + 0.6 * a0 * ct, gesture.y + 0.6 * a0 * st), (gesture.x - 0.6 * a0 * ct, gesture.y - 0.6 * a0 * st),
                                              (gesture.x - 0.6 * b0 * st, gesture.y + 0.6 * b0 * ct), (gesture.x + 0.6 * b0 * st, gesture.y - 0.6 * b0 * ct)]
            var best: (GradientGesture, Double)? = nil
            let lock = NSLock()
            DispatchQueue.concurrentPerform(iterations: starts.count) { i in
                let c = starts[i]
                let (p, v) = nelderMead(start: [0, 0, 0, 0], steps: [0.08, 0.08, 0.3, 0.3], maxIterations: 150, tolerance: 2e-2) { cost(gesture: make(c, $0), samples: s, spec: spec) }
                lock.lock(); if best == nil || v < best!.1 { best = (make(c, p), v) }; lock.unlock()
            }
            guard var g = best?.0 else { return gesture }
            // Keep a ≥ b with the angle along the long axis (a canonical form for the overlay).
            if g.b > g.a { swap(&g.a, &g.b); g.angle += .pi / 2 }
            g.angle = atan2(sin(g.angle), cos(g.angle))
            return g
        }
    }

    /// Fits the model of `spec` to the samples (refining the guide when asked). Nil when the samples
    /// cannot support it (too few, degenerate guide).
    public static func fit(samples s: Samples, spec: GradientSpec) -> Fit? {
        guard s.count >= 50 else { return nil }
        var g = spec.gesture
        if g.shape == .line, g.lineLength < 1 { return nil }
        if g.shape == .ellipse { g.a = max(g.a, 1); g.b = max(g.b, 1) }
        if spec.refine { g = refine(g, samples: s, spec: spec) }
        guard let (prof, poly, rd) = fitModel(gesture: g, samples: s, spec: spec, iterations: 3) else { return nil }
        var fit = Fit(gesture: g, spec: spec, profile: prof, poly: poly, polyDegree: rd, reference: 0, residualSigma: 0, width: s.width, height: s.height)
        var model = [Float](repeating: 0, count: s.count)
        var sq: Double = 0
        for i in 0..<s.count {
            model[i] = fit.background(x: s.x[i], y: s.y[i])
            let r = Double(s.v[i] - model[i]); sq += r * r
        }
        fit.residualSigma = Float((sq / Double(s.count)).squareRoot()) / s.noise
        model.withUnsafeMutableBufferPointer { vDSP_vsort($0.baseAddress!, vDSP_Length($0.count), 1) }
        fit.reference = spec.mode == .divide ? Statistics.percentile(sorted: model, q: 0.995) : Statistics.percentile(sorted: model, q: 0.5)
        return fit
    }

    /// Smallest transmission accepted in divide mode.
    static let minimumTransmission: Float = 0.05

    /// Corrected copy of `image` (zero pixels stay zero).
    public static func apply(_ image: PlanarImage, fit: Fit) -> PlanarImage {
        let out = PlanarImage(width: image.width, height: image.height)
        let w = image.width, h = image.height
        let divide = fit.spec.mode == .divide
        let ref = fit.reference
        let bands = max(1, min(h, ProcessInfo.processInfo.activeProcessorCount * 4))
        DispatchQueue.concurrentPerform(iterations: bands) { band in
            let y0 = h * band / bands, y1 = h * (band + 1) / bands
            for y in y0..<y1 {
                let src = image.pointer + y * w, dst = out.pointer + y * w
                let py = Float(y) + 0.5
                for x in 0..<w {
                    let v = src[x]
                    if v == 0 { dst[x] = 0; continue }
                    let b = fit.background(x: Float(x) + 0.5, y: py)
                    if divide {
                        let t = ref > 0 ? max(b / ref, minimumTransmission) : 1
                        dst[x] = v / t
                    } else {
                        dst[x] = v - b + ref
                    }
                }
            }
        }
        return out
    }

    /// Samples, fit and correction in one go (the same instance when nothing can be done).
    public static func correct(_ image: PlanarImage, spec: GradientSpec, protect: [GradientRegion] = []) -> (image: PlanarImage, fit: Fit)? {
        let s = samples(image: image, protect: protect)
        guard let f = fit(samples: s, spec: spec) else { return nil }
        return (apply(image, fit: f), f)
    }

    /// Every channel's correction (channels without a model are returned as they are).
    public static func correct(_ image: PlanarImage, kind: ChannelKind, settings: GradientSettings) -> PlanarImage {
        guard let spec = settings.spec(for: kind) else { return image }
        return correct(image, spec: spec, protect: settings.protect)?.image ?? image
    }
}
