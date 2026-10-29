import Foundation
import Accelerate

/// A circular region around a dust shadow (mote or donut) to be flattened. Coordinates are in
/// oriented image pixels with the pixel-edge convention: pixel (i, j) covers [i, i+1) × [j, j+1).
public struct DustSpot: Codable, Equatable, Hashable, Identifiable {
    public var id: UUID
    public var x: Double
    public var y: Double
    public var radius: Double

    public init(x: Double, y: Double, radius: Double, id: UUID = UUID()) {
        self.id = id; self.x = x; self.y = y; self.radius = radius
    }

    public var center: CGPoint { CGPoint(x: x, y: y) }

    /// The spot after the image (`imageWidth` × `imageHeight`) has undergone `op`.
    public func applying(_ op: Orientation.Op, imageWidth: Int, imageHeight: Int) -> DustSpot {
        var s = self
        switch op {
        case .rotateClockwise: s.x = Double(imageHeight) - y; s.y = x
        case .flipHorizontal: s.x = Double(imageWidth) - x
        case .flipVertical: s.y = Double(imageHeight) - y
        }
        return s
    }

    /// The spot after an unoriented image (`imageWidth` × `imageHeight`) is oriented (flip, then quarter turns).
    public func applying(_ o: Orientation, imageWidth: Int, imageHeight: Int) -> DustSpot {
        var s = o.flipped ? applying(.flipHorizontal, imageWidth: imageWidth, imageHeight: imageHeight) : self
        var w = imageWidth, h = imageHeight
        for _ in 0..<o.rotation { s = s.applying(.rotateClockwise, imageWidth: w, imageHeight: h); swap(&w, &h) }
        return s
    }
}

/// How the shadow of a spot is modelled from the pixels under it.
public enum DustModel: String, Codable, CaseIterable, Identifiable {
    /// Free-form 2-D transmission (star-masked smoothing of the local ratio): follows elongated or
    /// irregular motes and the fringe pattern of a mote, but also flattens nebular detail under the
    /// spot at the smoothing scale. The default.
    case surface
    /// Transmission as a function of the distance to the centre (median per annulus): robust to
    /// nebulosity and noise, right for round motes and donuts.
    case radial
    public var id: String { rawValue }
    public var displayName: String {
        switch self { case .radial: return "Radial profile"; case .surface: return "Local flat" }
    }
}

/// Dust spots per channel plus the shared estimation parameters. Stored in the project, applied to
/// the loaded channel once (before statistics, normalisation and everything else).
public struct DustSettings: Codable, Equatable {
    public var spots: [ChannelKind: [DustSpot]] = [:]
    public var model: DustModel = .surface
    /// Smoothing of the shadow estimate in pixels (Gaussian σ for `surface`, profile window for `radial`).
    public var smoothness: Double = 3
    /// Fraction of the radius over which the correction fades out towards the edge of the spot.
    public var feather: Double = 0.2

    public init() {}

    public var isEmpty: Bool { spots.values.allSatisfy(\.isEmpty) }
    public func spots(for kind: ChannelKind) -> [DustSpot] { spots[kind] ?? [] }
    public var options: DustRemoval.Options { DustRemoval.Options(model: model, smoothness: smoothness, feather: feather) }
}

/// Flattens the shadows of dust motes (sensor window, filters) and dust donuts (reflectors, out of
/// focus motes) in a stacked channel that was not, or not correctly, flat-field calibrated.
///
/// A mote attenuates the light reaching the sensor, so the stacked image is the true scene times a
/// transmission map T ≤ 1 — the same multiplicative error a flat frame would have divided out. For
/// each spot the expected unshadowed level B(x, y) is a quadratic surface fitted (sigma-clipped,
/// star-masked) to the annulus around the spot; the ratio image / B under the spot is then
/// summarised as the transmission — either a radial profile (median per annulus, right for round
/// motes and donuts and robust to nebulosity) or a star-masked smoothed surface (irregular motes)
/// — and the pixels are divided by it (stars included: they were dimmed by the same shadow). The
/// correction fades out over the outer `feather` fraction of the radius so the edge is seamless;
/// pixels beyond the radius are untouched, zero (padding) pixels stay zero.
public enum DustRemoval {
    public struct Options: Equatable {
        public var model: DustModel = .surface
        public var smoothness: Double = 3
        public var feather: Double = 0.2
        public init(model: DustModel = .surface, smoothness: Double = 3, feather: Double = 0.2) {
            self.model = model; self.smoothness = smoothness; self.feather = feather
        }
    }

    /// Transmission range accepted for a shadow (dust never brightens; a little over 1 is allowed
    /// for the diffraction rim of a donut).
    static let transmissionRange: ClosedRange<Float> = 0.05...1.5

    /// Healed copy of `image` (the same instance when there is nothing to do).
    public static func heal(_ image: PlanarImage, spots: [DustSpot], options: Options) -> PlanarImage {
        guard !spots.isEmpty else { return image }
        let out = image.copy()
        for s in spots { heal(inPlace: out, spot: s, options: options, others: spots.filter { $0.id != s.id }) }
        return out
    }

    /// Divides the pixels under `spot` by the estimated transmission. Pixels under `others` (the
    /// remaining spots, typically neighbours in a cluster) are left out of the background fit. False
    /// when the spot cannot be analysed (outside the image or too few usable pixels around it).
    @discardableResult
    public static func heal(inPlace image: PlanarImage, spot: DustSpot, options: Options, others: [DustSpot] = []) -> Bool {
        guard let sh = shadow(image: image, spot: spot, options: options, others: others) else { return false }
        let win = sh.window, t = sh.transmission
        for y in 0..<win.height {
            let row = image.pointer + (win.y + y) * image.width + win.x
            let trow = t.pointer + y * t.width
            for x in 0..<win.width {
                let g = trow[x]
                let v = row[x]
                if v != 0 && g != 1 { row[x] = v / g }
            }
        }
        return true
    }

    /// Window around a spot and the effective transmission inside it (1 where nothing changes).
    public struct Shadow {
        public var window: PixelRect
        public var transmission: PlanarImage
    }

    /// Estimates the transmission map of a spot.
    ///
    /// Radial model: the profile is taken inside the circle and feathered out at its edge. Surface
    /// model: the circle only bounds the search — the shadow's own outline is found as the connected
    /// region (seeded at the centre, holes filled) where the smoothed ratio sits below the sky by more
    /// than its noise, so elongated and irregular motes are corrected to their real edge; the
    /// correction fades out over a band of `feather` × radius outside that outline, and the sky plane
    /// is refitted away from it.
    public static func shadow(image: PlanarImage, spot: DustSpot, options: Options, others: [DustSpot] = []) -> Shadow? {
        let r = max(spot.radius, 3)
        let surfaceModel = options.model == .surface
        guard let a = Analysis(image: image, cx: spot.x, cy: spot.y, radius: r, options: options, others: others, outerFactor: surfaceModel ? 1.8 : 1.7) else { return nil }
        let w = a.window.width, h = a.window.height, n = w * h
        let t = PlanarImage(width: w, height: h, fill: 1)
        var mask = a.mask
        var raw: PlanarImage? = nil
        var weight: PlanarImage? = nil
        var exclude: PlanarImage? = nil
        let sigma = max(0.5, options.smoothness)
        let featherPx = Float(max(2, options.feather * r))
        // Two passes: the first star mask may flag the bright side of a deep shadow edge; masking the
        // flattened window again gives a clean mask (and, for the surface model, the plane is refitted
        // away from the shadow found in the first pass).
        for pass in 0..<2 {
            if pass == 1, let t0 = raw {
                let flat = a.crop.copy()
                vDSP_vdiv(t0.pointer, 1, flat.pointer, 1, flat.pointer, 1, vDSP_Length(n))
                mask = a.starMask(flat, thresholdSigma: 4)
            }
            guard let q = a.ratio(mask: mask, exclude: exclude) else { return nil }
            switch options.model {
            case .radial:
                guard let p = radialProfile(ratio: q, mask: mask, rad: a.rad, radius: r, window: max(2, options.smoothness)) else { return nil }
                raw = expand(profile: p, rad: a.rad, radius: r)
            case .surface:
                let sm = surface(ratio: q, mask: mask, rad: a.rad, radius: a.outer, sigma: sigma)
                // Noise of the smoothed ratio: pixel noise (measured on the sky around the spot, not
                // under it) through the Gaussian (Σk² = 1 / 4πσ²).
                var dev: [Float] = []; dev.reserveCapacity(n / 5 + 1)
                let skyFrom = Float(r) * 1.1
                for i in stride(from: 0, to: n, by: 5) where mask.pointer[i] == 0 && q.pointer[i] > 0 && a.rad.pointer[i] >= skyFrom && (exclude?.pointer[i] ?? 0) == 0 { dev.append(abs(q.pointer[i] - 1)) }
                guard dev.count >= 16 else { return nil }
                dev.sort()
                let pixelSigma = 1.4826 * Statistics.percentile(sorted: dev, q: 0.5)
                let smoothSigma = pixelSigma / (2 * Float.pi.squareRoot() * Float(sigma))
                let threshold = 1 - max(2.5 * smoothSigma, 0.0005)
                let region = shadowRegion(sm, rad: a.rad, threshold: threshold, seedRadius: Float(r) * 0.5, limit: Float(r) * 1.3)
                let dist = distanceOutside(region)
                let wgt = PlanarImage(width: w, height: h)
                let ex = PlanarImage(width: w, height: h)
                let band = featherPx + 2 * Float(sigma)
                for i in 0..<n {
                    let d = dist.pointer[i]
                    wgt.pointer[i] = d <= 0 ? 1 : max(0, 1 - d / featherPx)
                    ex.pointer[i] = d < band ? 1 : 0
                }
                raw = sm; weight = wgt; exclude = ex
            }
        }
        guard let tr = raw else { return nil }
        if let wgt = weight {
            for i in 0..<n {
                let f = wgt.pointer[i]
                guard f > 0 else { continue }
                let v = min(max(tr.pointer[i], transmissionRange.lowerBound), transmissionRange.upperBound)
                t.pointer[i] = 1 + (v - 1) * f
            }
        } else {
            // Feather towards the edge of the circle and clamp.
            let fr = Float(r), inner = fr * Float(1 - min(max(options.feather, 0), 0.9))
            for i in 0..<n {
                let d = a.rad.pointer[i]
                guard d < fr else { continue }
                let f: Float = d <= inner ? 1 : 0.5 * (1 + cos(Float.pi * (d - inner) / max(fr - inner, 1e-3)))
                let v = min(max(tr.pointer[i], transmissionRange.lowerBound), transmissionRange.upperBound)
                t.pointer[i] = 1 + (v - 1) * f
            }
        }
        return Shadow(window: a.window, transmission: t)
    }

    /// Connected region of pixels below `threshold` that touches the disc of `seedRadius` around the
    /// centre, limited to `limit`, with its holes filled (1 inside, 0 outside).
    static func shadowRegion(_ s: PlanarImage, rad: PlanarImage, threshold: Float, seedRadius: Float, limit: Float) -> PlanarImage {
        let w = s.width, h = s.height, n = w * h
        let region = PlanarImage(width: w, height: h)
        var stack: [Int] = []
        for i in 0..<n where rad.pointer[i] <= seedRadius && s.pointer[i] < threshold { region.pointer[i] = 1; stack.append(i) }
        func grow(_ stack: inout [Int], _ out: PlanarImage, accept: (Int) -> Bool) {
            while let i = stack.popLast() {
                let x = i % w, y = i / w
                for (dx, dy) in [(1, 0), (-1, 0), (0, 1), (0, -1)] {
                    let xx = x + dx, yy = y + dy
                    guard xx >= 0, yy >= 0, xx < w, yy < h else { continue }
                    let j = yy * w + xx
                    if out.pointer[j] == 0 && accept(j) { out.pointer[j] = 1; stack.append(j) }
                }
            }
        }
        grow(&stack, region) { s.pointer[$0] < threshold && rad.pointer[$0] <= limit }
        // Holes: whatever the outside (grown from the window border) does not reach is inside.
        let outside = PlanarImage(width: w, height: h)
        var border: [Int] = []
        for x in 0..<w { for y in [0, h - 1] { let i = y * w + x; if region.pointer[i] == 0 { outside.pointer[i] = 1; border.append(i) } } }
        for y in 0..<h { for x in [0, w - 1] { let i = y * w + x; if region.pointer[i] == 0 && outside.pointer[i] == 0 { outside.pointer[i] = 1; border.append(i) } } }
        grow(&border, outside) { region.pointer[$0] == 0 }
        for i in 0..<n where outside.pointer[i] == 0 { region.pointer[i] = 1 }
        return region
    }

    /// Distance of every pixel to the nearest region pixel (0 inside), chamfer 3-4 approximation.
    static func distanceOutside(_ region: PlanarImage) -> PlanarImage {
        let w = region.width, h = region.height
        let big = Float(w + h) * 2
        let d = PlanarImage(width: w, height: h)
        for i in 0..<(w * h) { d.pointer[i] = region.pointer[i] > 0 ? 0 : big }
        for y in 0..<h { for x in 0..<w {
            var v = d[x, y]
            if x > 0 { v = min(v, d[x - 1, y] + 1) }
            if y > 0 { v = min(v, d[x, y - 1] + 1); if x > 0 { v = min(v, d[x - 1, y - 1] + 1.4142) }; if x + 1 < w { v = min(v, d[x + 1, y - 1] + 1.4142) } }
            d[x, y] = v
        } }
        for y in stride(from: h - 1, through: 0, by: -1) { for x in stride(from: w - 1, through: 0, by: -1) {
            var v = d[x, y]
            if x + 1 < w { v = min(v, d[x + 1, y] + 1) }
            if y + 1 < h { v = min(v, d[x, y + 1] + 1); if x + 1 < w { v = min(v, d[x + 1, y + 1] + 1.4142) }; if x > 0 { v = min(v, d[x - 1, y + 1] + 1.4142) } }
            d[x, y] = v
        } }
        return d
    }

    /// Refines a rough click: finds the centre and the extent of the shadow around (`x`, `y`),
    /// searching out to several times `radius` (the search widens while nothing is found, so the
    /// starting radius may be well below the real one). Nil when no shadow is found there.
    ///
    /// Shadows of stacked data can be very shallow (a few ‰ of the sky level: the Soul Core OIII
    /// motes are 0.5 % deep) yet obvious after stretching, so the detection threshold is set by the
    /// bin-to-bin noise of the profile rather than by a fixed depth.
    public static func fit(image: PlanarImage, x: Double, y: Double, radius r0: Double, options: Options) -> DustSpot? {
        var cx = x, cy = y, r = max(r0, 12)
        var found = false
        let maxSearch = Double(max(image.width, image.height)) / 2
        for iteration in 0..<4 {
            // Extent: the radial profile out to a few times the current radius; the shadow ends where
            // the profile comes back to its outer level.
            var hit: (p: [Float], outer: Float, depth: Float)? = nil
            for factor in (iteration == 0 ? [2.5, 5, 10, 20] : [1.6]) {
                let search = min(r * factor, maxSearch)
                guard let a = Analysis(image: image, cx: cx, cy: cy, radius: search, options: options),
                      let q = a.ratio(mask: a.mask),
                      let (p, counts) = radialProfileWithCounts(ratio: q, mask: a.mask, rad: a.rad, radius: search, window: max(2, options.smoothness)), p.count >= 10 else { break }
                var tail = Array(p[(p.count * 4 / 5)...]); tail.sort()
                let outer = Statistics.percentile(sorted: tail, q: 0.5)
                // Per-pixel noise of the ratio, then per-bin noise of a median over `counts` pixels:
                // the dip must be significant where it is deepest (the innermost bins hold few pixels).
                var dev: [Float] = []; dev.reserveCapacity(q.count / 7 + 1)
                for i in stride(from: 0, to: q.count, by: 7) where a.mask.pointer[i] == 0 && q.pointer[i] > 0 { dev.append(abs(q.pointer[i] - 1)) }
                guard dev.count >= 16 else { break }
                dev.sort()
                let pixelSigma = max(1.4826 * Statistics.percentile(sorted: dev, q: 0.5), 1e-6)
                var best: (depth: Float, significance: Float) = (0, 0)
                for i in 0..<p.count where counts[i] > 0 {
                    let d = outer - p[i]
                    let sig = d / (1.2533 * pixelSigma / Float(counts[i]).squareRoot())
                    if sig > best.significance { best = (d, sig) }
                }
                let depth = best.depth
                if depth > 0.0015 && best.significance > 6 { hit = (p, outer, depth); break }
                if search >= maxSearch { break }
            }
            guard let (p, outer, depth) = hit, let edge = p.lastIndex(where: { $0 < outer - 0.15 * depth }), edge >= 3 else { break }
            let newR = Double(edge + 1) * 1.25 + 2
            // Centre: centroid of the pixels darker than half the depth, measured with the background
            // fitted close around the new radius (a far annulus extrapolates nebulosity badly). The
            // binary weight is far less sensitive to gradients than the darkening itself.
            guard let b = Analysis(image: image, cx: cx, cy: cy, radius: newR, options: options), let qb = b.ratio(mask: b.mask) else { break }
            var sx = 0.0, sy = 0.0, sw = 0.0
            let w = b.window.width, h = b.window.height
            let half = outer - 0.5 * depth
            for yy in 0..<h { for xx in 0..<w {
                let i = yy * w + xx
                guard b.mask.pointer[i] == 0, Double(b.rad.pointer[i]) <= newR, qb.pointer[i] < half else { continue }
                sx += Double(b.window.x + xx) + 0.5; sy += Double(b.window.y + yy) + 0.5; sw += 1
            } }
            let ncx = sw > 0 ? sx / sw : cx, ncy = sw > 0 ? sy / sw : cy
            let moved = hypot(ncx - cx, ncy - cy)
            cx = ncx; cy = ncy
            found = true
            if abs(newR - r) < 1 && moved < 0.5 { r = newR; break }
            r = newR
        }
        return found ? DustSpot(x: cx, y: cy, radius: r) : nil
    }

    // MARK: - Pieces

    /// The window around a spot: crop, distance map, star mask and the fitted background surface.
    struct Analysis {
        let window: PixelRect
        let crop: PlanarImage
        /// Distance of each pixel centre to the spot centre.
        let rad: PlanarImage
        let mask: PlanarImage
        let radius: Double
        let outer: Double
        let cx: Double, cy: Double

        init?(image: PlanarImage, cx: Double, cy: Double, radius: Double, options: Options, others: [DustSpot] = [], outerFactor: Double = 1.7) {
            let outer = radius * outerFactor
            let x0 = Int(floor(cx - outer)), y0 = Int(floor(cy - outer))
            let x1 = Int(ceil(cx + outer)), y1 = Int(ceil(cy + outer))
            guard let win = PixelRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0).clamped(width: image.width, height: image.height),
                  win.width >= 8, win.height >= 8 else { return nil }
            window = win
            crop = image.cropped(win)
            self.radius = radius; self.outer = outer; self.cx = cx; self.cy = cy
            let w = win.width, h = win.height
            let rad = PlanarImage(width: w, height: h)
            let lcx = Float(cx - Double(win.x)), lcy = Float(cy - Double(win.y))
            for y in 0..<h {
                let dy = Float(y) + 0.5 - lcy
                for x in 0..<w { let dx = Float(x) + 0.5 - lcx; rad[x, y] = (dx * dx + dy * dy).squareRoot() }
            }
            self.rad = rad
            let m = Analysis.starMask(crop, radius: radius, thresholdSigma: 6)
            // Other spots overlapping the window are masked so that their shadows do not bias the
            // background fit (the mask is also reused for the second pass through `excluded`).
            let ex = PlanarImage(width: w, height: h)
            var anyExcluded = false
            for o in others {
                let ox = Float(o.x - Double(win.x)), oy = Float(o.y - Double(win.y)), orad = Float(o.radius)
                guard ox + orad > 0, oy + orad > 0, ox - orad < Float(w), oy - orad < Float(h) else { continue }
                anyExcluded = true
                for y in 0..<h {
                    let dy = Float(y) + 0.5 - oy
                    for x in 0..<w where ex[x, y] == 0 {
                        let dx = Float(x) + 0.5 - ox
                        if dx * dx + dy * dy < orad * orad { ex[x, y] = 1 }
                    }
                }
            }
            excluded = anyExcluded ? ex : nil
            if let ex = excluded { vDSP_vmax(m.pointer, 1, ex.pointer, 1, m.pointer, 1, vDSP_Length(w * h)) }
            mask = m
        }

        /// 1 under the other spots (nil when none overlaps the window).
        let excluded: PlanarImage?

        /// Star mask of `img` combined with the exclusion of the other spots.
        func starMask(_ img: PlanarImage, thresholdSigma: Float) -> PlanarImage {
            let m = Analysis.starMask(img, radius: radius, thresholdSigma: thresholdSigma)
            if let ex = excluded { vDSP_vmax(m.pointer, 1, ex.pointer, 1, m.pointer, 1, vDSP_Length(m.count)) }
            return m
        }

        static func starMask(_ img: PlanarImage, radius: Double, thresholdSigma: Float) -> PlanarImage {
            var o = StarMask.Options()
            o.thresholdSigma = thresholdSigma
            o.dilation = 2
            o.backgroundLevels = min(4, max(1, Int(log2(max(radius / 4, 2)))))
            o.maxSamples = 40_000
            return StarMask.build(image: img, options: o)
        }

        /// crop / B where B is the background surface fitted to the annulus outside the spot — or, with
        /// `exclude`, to every unmasked pixel outside the excluded area (1 where undefined).
        func ratio(mask: PlanarImage, exclude: PlanarImage? = nil) -> PlanarImage? {
            guard let b = backgroundSurface(mask: mask, exclude: exclude) else { return nil }
            let n = window.width * window.height
            let q = PlanarImage(width: window.width, height: window.height, fill: 1)
            for i in 0..<n {
                let v = crop.pointer[i], bb = b.pointer[i]
                if v > 0 && bb > 0 { q.pointer[i] = v / bb }
            }
            return q
        }

        /// Least-squares plane (a constant when few samples) through the unmasked annulus pixels,
        /// with three rounds of 3σ clipping. A plane extrapolates nebulosity more safely than a
        /// quadratic, which over-shoots under the spot.
        func backgroundSurface(mask: PlanarImage, exclude: PlanarImage? = nil) -> PlanarImage? {
            let w = window.width, h = window.height
            let inner = exclude == nil ? Float(radius * 1.1) : 0, out = Float(outer)
            let lcx = Float(cx - Double(window.x)), lcy = Float(cy - Double(window.y)), scale = 1 / Float(outer)
            var su: [Float] = [], sv: [Float] = [], sz: [Float] = []
            for y in 0..<h {
                let v = (Float(y) + 0.5 - lcy) * scale
                for x in 0..<w {
                    let i = y * w + x
                    let d = rad.pointer[i]
                    guard d >= inner, d <= out, mask.pointer[i] == 0, crop.pointer[i] != 0, exclude?.pointer[i] ?? 0 == 0 else { continue }
                    su.append((Float(x) + 0.5 - lcx) * scale); sv.append(v); sz.append(crop.pointer[i])
                }
            }
            let n = su.count
            guard n >= 12 else { return nil }
            let terms = n >= 20 ? 3 : 1
            var use = [Bool](repeating: true, count: n)
            var coef = [Float](repeating: 0, count: 3)
            for round in 0..<3 {
                var ata = [Float](repeating: 0, count: 9), atb = [Float](repeating: 0, count: 3)
                for k in 0..<n where use[k] {
                    let u = su[k], v = sv[k], z = sz[k]
                    ata[0] += 1; atb[0] += z
                    if terms == 3 {
                        ata[1] += u; ata[2] += v; ata[4] += u * u; ata[5] += u * v; ata[8] += v * v
                        atb[1] += u * z; atb[2] += v * z
                    }
                }
                ata[3] = ata[1]; ata[6] = ata[2]; ata[7] = ata[5]
                let c: [Float]
                if terms == 3 {
                    guard let sol = solve(ata, atb, n: 3) else { return nil }
                    c = sol
                } else {
                    c = [atb[0] / ata[0], 0, 0]
                }
                coef = c
                if round == 2 { break }
                // Noise of the residuals from a sample (sorting every pixel of a large window is slow).
                var res: [Float] = []; res.reserveCapacity(n / 4 + 1)
                for k in stride(from: 0, to: n, by: 4) where use[k] { res.append(abs(sz[k] - (coef[0] + coef[1] * su[k] + coef[2] * sv[k]))) }
                guard res.count >= 8 else { break }
                res.sort()
                let sigma = 1.4826 * Statistics.percentile(sorted: res, q: 0.5)
                guard sigma > 0 else { break }
                let limit = 3 * sigma
                for k in 0..<n where use[k] { if abs(sz[k] - (coef[0] + coef[1] * su[k] + coef[2] * sv[k])) > limit { use[k] = false } }
            }
            let b = PlanarImage(width: w, height: h)
            for y in 0..<h {
                let v = (Float(y) + 0.5 - lcy) * scale
                let row = b.pointer + y * w
                for x in 0..<w { row[x] = coef[0] + coef[1] * (Float(x) + 0.5 - lcx) * scale + coef[2] * v }
            }
            return b
        }

        /// Gaussian elimination with partial pivoting for a small dense system.
        private func solve(_ a: [Float], _ b: [Float], n: Int) -> [Float]? {
            var m = a, v = b
            for col in 0..<n {
                var piv = col
                for r in (col + 1)..<max(n, col + 1) where abs(m[r * n + col]) > abs(m[piv * n + col]) { piv = r }
                guard abs(m[piv * n + col]) > 1e-12 else { return nil }
                if piv != col {
                    for j in 0..<n { m.swapAt(col * n + j, piv * n + j) }
                    v.swapAt(col, piv)
                }
                for r in (col + 1)..<max(n, col + 1) {
                    let f = m[r * n + col] / m[col * n + col]
                    guard f != 0 else { continue }
                    for j in col..<n { m[r * n + j] -= f * m[col * n + j] }
                    v[r] -= f * v[col]
                }
            }
            var x = [Float](repeating: 0, count: n)
            for r in stride(from: n - 1, through: 0, by: -1) {
                var s = v[r]
                for j in (r + 1)..<max(n, r + 1) { s -= m[r * n + j] * x[j] }
                x[r] = s / m[r * n + r]
            }
            return x
        }
    }

    /// Median of the ratio per 1-px annulus, each taken over a sliding window of `window` px;
    /// annuli without usable pixels are filled from their neighbours.
    static func radialProfile(ratio q: PlanarImage, mask: PlanarImage, rad: PlanarImage, radius: Double, window: Double) -> [Float]? {
        radialProfileWithCounts(ratio: q, mask: mask, rad: rad, radius: radius, window: window)?.profile
    }

    /// The profile and the number of pixels behind each bin.
    static func radialProfileWithCounts(ratio q: PlanarImage, mask: PlanarImage, rad: PlanarImage, radius: Double, window: Double) -> (profile: [Float], counts: [Int])? {
        let nb = Int(ceil(radius)) + 1
        var bins = [[Float]](repeating: [], count: nb)
        for i in 0..<q.count {
            let d = rad.pointer[i]
            guard Double(d) < Double(nb), mask.pointer[i] == 0, q.pointer[i] > 0 else { continue }
            bins[Int(d)].append(q.pointer[i])
        }
        let half = max(1, Int((window / 2).rounded()))
        var p = [Float](repeating: .nan, count: nb)
        var counts = [Int](repeating: 0, count: nb)
        var any = false
        for i in 0..<nb {
            var v: [Float] = []
            for j in max(0, i - half)...min(nb - 1, i + half) { v.append(contentsOf: bins[j]) }
            guard v.count >= 4 else { continue }
            v.sort()
            p[i] = Statistics.percentile(sorted: v, q: 0.5)
            counts[i] = v.count
            any = true
        }
        guard any else { return nil }
        // Fill gaps from the nearest defined bins.
        var last: Float = .nan
        for i in 0..<nb { if p[i].isNaN { p[i] = last } else { last = p[i] } }
        last = .nan
        for i in stride(from: nb - 1, through: 0, by: -1) { if p[i].isNaN { p[i] = last } else { last = p[i] } }
        return (p, counts)
    }

    /// Per-pixel transmission from a radial profile (linear interpolation between bin centres, 1 beyond the radius).
    static func expand(profile p: [Float], rad: PlanarImage, radius: Double) -> PlanarImage {
        let t = PlanarImage(width: rad.width, height: rad.height, fill: 1)
        let fr = Float(radius)
        for i in 0..<rad.count {
            let d = rad.pointer[i]
            guard d < fr else { continue }
            let u = max(0, d - 0.5)
            let k = min(p.count - 2, Int(u))
            let f = min(1, u - Float(k))
            t.pointer[i] = p[k] * (1 - f) + p[k + 1] * f
        }
        return t
    }

    /// Star-masked Gaussian smoothing of the ratio (normalised convolution), 1 beyond the radius.
    static func surface(ratio q: PlanarImage, mask: PlanarImage, rad: PlanarImage, radius: Double, sigma: Double) -> PlanarImage {
        let w = q.width, h = q.height, n = w * h
        let num = PlanarImage(width: w, height: h), den = PlanarImage(width: w, height: h)
        for i in 0..<n where mask.pointer[i] == 0 && q.pointer[i] > 0 { num.pointer[i] = q.pointer[i]; den.pointer[i] = 1 }
        let rk = max(1, Int(ceil(3 * sigma)))
        var kernel = (-rk...rk).map { Float(exp(-Double($0 * $0) / (2 * sigma * sigma))) }
        let ks = kernel.reduce(0, +); kernel = kernel.map { $0 / ks }
        func blur(_ img: PlanarImage) -> PlanarImage {
            let tmp = PlanarImage(width: w, height: h), out = PlanarImage(width: w, height: h)
            for y in 0..<h { for x in 0..<w {
                var s: Float = 0
                for (j, k) in kernel.enumerated() { let xx = x + j - rk; if xx >= 0 && xx < w { s += k * img[xx, y] } }
                tmp[x, y] = s
            } }
            for y in 0..<h { for x in 0..<w {
                var s: Float = 0
                for (j, k) in kernel.enumerated() { let yy = y + j - rk; if yy >= 0 && yy < h { s += k * tmp[x, yy] } }
                out[x, y] = s
            } }
            return out
        }
        let bn = blur(num), bd = blur(den)
        let t = PlanarImage(width: w, height: h, fill: 1)
        let fr = Float(radius)
        for i in 0..<n where rad.pointer[i] < fr && bd.pointer[i] > 1e-3 { t.pointer[i] = bn.pointer[i] / bd.pointer[i] }
        return t
    }
}

extension DustSpot {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        id = c.value("id", UUID()); x = c.value("x", 0.0); y = c.value("y", 0.0); radius = c.value("radius", 30.0)
    }
}

extension DustSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = DustSettings()
        spots = c.value("spots", d.spots); model = c.value("model", d.model)
        smoothness = c.value("smoothness", d.smoothness); feather = c.value("feather", d.feather)
    }
}
