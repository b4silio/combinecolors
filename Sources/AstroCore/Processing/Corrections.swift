import Foundation
import Accelerate
import CryptoKit

/// Hand-painted brightness corrections of a channel: a smooth map at a reduced resolution with
/// values in −1…1 (negative darkens, positive brightens), applied to the linear data as an
/// exposure change, `out = in × 2^(strength × m)`. Corrections are feathered and smooth by nature,
/// so the map's long side is at most `maximumSize` pixels and it is sampled bilinearly.
public final class CorrectionMap {
    public static let maximumSize = 1024
    public let width: Int
    public let height: Int
    /// Size of the image the map belongs to (oriented).
    public let imageWidth: Int
    public let imageHeight: Int
    public let plane: PlanarImage
    /// Bumped by every edit (change detection without comparing pixels).
    public private(set) var version = 0

    public init(imageWidth: Int, imageHeight: Int, plane: PlanarImage? = nil) {
        self.imageWidth = max(imageWidth, 1); self.imageHeight = max(imageHeight, 1)
        let long = max(self.imageWidth, self.imageHeight)
        let scale = Double(min(CorrectionMap.maximumSize, long)) / Double(long)
        if let p = plane {
            width = p.width; height = p.height; self.plane = p
        } else {
            width = max(1, Int((Double(self.imageWidth) * scale).rounded()))
            height = max(1, Int((Double(self.imageHeight) * scale).rounded()))
            self.plane = PlanarImage(width: width, height: height)
        }
    }

    /// Map pixels per image pixel.
    public var scale: Double { Double(width) / Double(imageWidth) }

    public var isEmpty: Bool {
        var lo: Float = 0, hi: Float = 0
        vDSP_minv(plane.pointer, 1, &lo, vDSP_Length(plane.count)); vDSP_maxv(plane.pointer, 1, &hi, vDSP_Length(plane.count))
        return lo == 0 && hi == 0
    }

    public func copy() -> CorrectionMap {
        let m = CorrectionMap(imageWidth: imageWidth, imageHeight: imageHeight, plane: plane.copy())
        m.version = version
        return m
    }

    /// Value at an image point (bilinear).
    public func value(x: Double, y: Double) -> Float {
        let fx = min(max(x * scale - 0.5, 0), Double(width - 1)), fy = min(max(y * scale - 0.5, 0), Double(height - 1))
        let x0 = Int(fx), y0 = Int(fy), x1 = min(x0 + 1, width - 1), y1 = min(y0 + 1, height - 1)
        let tx = Float(fx - Double(x0)), ty = Float(fy - Double(y0))
        let p = plane.pointer
        let a = p[y0 * width + x0] + (p[y0 * width + x1] - p[y0 * width + x0]) * tx
        let b = p[y1 * width + x0] + (p[y1 * width + x1] - p[y1 * width + x0]) * tx
        return a + (b - a) * ty
    }

    // MARK: Painting

    /// One brush dab at an image point: `amount` (± flow) added inside `radius` (image pixels) with a
    /// smooth fall-off beyond `hardness × radius`; the map stays in −1…1.
    public func dab(x: Double, y: Double, radius: Double, amount: Float, hardness: Float) {
        let r = max(radius * scale, 0.75)
        let cx = x * scale - 0.5, cy = y * scale - 0.5
        let x0 = max(0, Int(floor(cx - r))), x1 = min(width - 1, Int(ceil(cx + r)))
        let y0 = max(0, Int(floor(cy - r))), y1 = min(height - 1, Int(ceil(cy + r)))
        guard x0 <= x1, y0 <= y1 else { return }
        let h = Double(min(max(hardness, 0), 0.98))
        let p = plane.pointer
        for yy in y0...y1 {
            let dy = Double(yy) - cy
            for xx in x0...x1 {
                let dx = Double(xx) - cx
                let t = (dx * dx + dy * dy).squareRoot() / r
                guard t < 1 else { continue }
                var w: Float = 1
                if t > h { let u = Float((t - h) / (1 - h)); w = 1 - u * u * (3 - 2 * u) }
                let i = yy * width + xx
                p[i] = min(max(p[i] + amount * w, -1), 1)
            }
        }
        version += 1
    }

    /// A stroke segment with even spacing across segments: a dab every quarter radius of *path*,
    /// however the mouse events cut the path. `carry` is the distance travelled since the last dab
    /// (start a stroke with `strokeStart(radius:)` so its first point gets a dab). Without it, a
    /// slow drag (one tiny segment per mouse event, each ending in a dab) piled up hundreds of dabs
    /// on the same pixels and the flow looked far stronger than set.
    public func stroke(from a: CGPoint, to b: CGPoint, radius: Double, amount: Float, hardness: Float, carry: inout Double) {
        let step = max(radius / 4, 0.5)
        let d = Double(hypot(b.x - a.x, b.y - a.y))
        var pos = step - carry            // distance along this segment to the next dab
        if pos < 0 { pos = 0 }
        var last: Double? = nil
        while pos <= d {
            let t = d > 0 ? pos / d : 0
            dab(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, radius: radius, amount: amount, hardness: hardness)
            last = pos
            pos += step
        }
        carry = last.map { d - $0 } ?? carry + d
    }

    /// The `carry` that starts a stroke: its first point gets a dab.
    public static func strokeStart(radius: Double) -> Double { max(radius / 4, 0.5) }

    /// A stroke segment: dabs every quarter radius from `from` to `to` (both image points).
    public func stroke(from a: CGPoint, to b: CGPoint, radius: Double, amount: Float, hardness: Float) {
        let d = hypot(b.x - a.x, b.y - a.y)
        let step = max(radius / 4, 0.5)
        let n = max(1, Int((d / step).rounded(.up)))
        for i in 1...n {
            let t = Double(i) / Double(n)
            dab(x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t, radius: radius, amount: amount, hardness: hardness)
        }
    }

    public func clear() {
        vDSP_vclr(plane.pointer, 1, vDSP_Length(plane.count))
        version += 1
    }

    /// The map after the image has undergone `op`.
    public func applying(_ op: Orientation.Op) -> CorrectionMap {
        let swaps = op == .rotateClockwise
        let m = CorrectionMap(imageWidth: swaps ? imageHeight : imageWidth, imageHeight: swaps ? imageWidth : imageHeight, plane: plane.applying(op))
        m.version = version
        return m
    }

    /// The map after a fine rotation of the image by `degrees` (resampled; what turns out of the
    /// frame is lost, what comes in is unpainted).
    public func rotated(byDegrees degrees: Double) -> CorrectionMap {
        guard degrees != 0 else { return self }
        let m = CorrectionMap(imageWidth: imageWidth, imageHeight: imageHeight, plane: plane.rotated(byDegrees: degrees))
        m.version = version + 1
        return m
    }

    /// The map of an unoriented image (a sidecar) oriented like the channels.
    public func oriented(_ o: Orientation) -> CorrectionMap {
        var m = o.flipped ? applying(.flipHorizontal) : self
        for _ in 0..<o.rotation { m = m.applying(.rotateClockwise) }
        return m.rotated(byDegrees: o.angle)
    }

    /// The map of the unoriented image (what the sidecar stores): the orientation undone.
    public func unoriented(_ o: Orientation) -> CorrectionMap {
        var m = rotated(byDegrees: -o.angle)
        for _ in 0..<((4 - o.rotation) % 4) { m = m.applying(.rotateClockwise) }
        if o.flipped { m = m.applying(.flipHorizontal) }
        return m
    }

    // MARK: Application

    /// The sky level a sky-relative correction pivots on: the image's sigma-clipped background.
    public static func pedestal(of image: PlanarImage) -> Float { Statistics.compute(image: image).background }

    /// In place, for the region `rect` of the image (full-resolution pixels) that `image` covers (nil:
    /// the whole image); zero pixels stay zero. With a `pedestal` (sky-relative, the default mode)
    /// only the signal above the sky is scaled: `p + (image − p) × 2^(strength × map)`; without,
    /// the whole value: `image × 2^(strength × map)`. On linear data the whole value is mostly the
    /// sky pedestal, which the stretch then magnifies: 0.5 EV on a sky at 0.0062 with a black point
    /// at 0.0060 multiplied the stretched sky level by ≈ 11, so the strength felt erratic.
    public func apply(to image: PlanarImage, strength: Float, rect: CGRect? = nil, pedestal: Float? = nil) {
        guard strength != 0 else { return }
        let r = rect ?? CGRect(x: 0, y: 0, width: imageWidth, height: imageHeight)
        let s = scale
        let n = image.count
        // The map sampled over the region, then 2^(strength·m), then the product (zero pixels zero).
        let sampled = plane.sampled(x: r.minX * s, y: r.minY * s, width: r.width * s, height: r.height * s, toWidth: image.width, toHeight: image.height)
        var k = strength
        vDSP_vsmul(sampled.pointer, 1, &k, sampled.pointer, 1, vDSP_Length(n))
        var count = Int32(n)
        vvexp2f(sampled.pointer, sampled.pointer, &count)
        guard let p = pedestal else {
            vDSP_vmul(image.pointer, 1, sampled.pointer, 1, image.pointer, 1, vDSP_Length(n))
            return
        }
        let v = image.pointer, g = sampled.pointer
        let chunks = max(1, min(16, n / 65536))
        DispatchQueue.concurrentPerform(iterations: chunks) { c in
            let start = c * n / chunks, end = (c + 1) * n / chunks
            for i in start..<end where v[i] != 0 { v[i] = p + (v[i] - p) * g[i] }
        }
    }

    /// A corrected copy of the whole image.
    public func applied(to image: PlanarImage, strength: Float, pedestal: Float? = nil) -> PlanarImage {
        let out = image.copy()
        apply(to: out, strength: strength, pedestal: pedestal)
        return out
    }

    // MARK: Storage

    /// Sidecar next to the image: `<image>.<channel>.ccmask.png` (16-bit grey, 32768 = no change).
    /// The channel is part of the name so a colour file split into R, G and B keeps one map per
    /// channel; maps saved before that (`<image>.ccmask.png`, no channel) are still read.
    public static func sidecarURL(for image: URL, kind: ChannelKind? = nil) -> URL {
        image.deletingLastPathComponent().appendingPathComponent(image.lastPathComponent + suffix(kind))
    }

    static func suffix(_ kind: ChannelKind?) -> String { (kind.map { "." + $0.rawValue } ?? "") + ".ccmask.png" }

    /// Fallback when the image's folder is not writable: Application Support, keyed by the image path.
    public static func fallbackURL(for image: URL, kind: ChannelKind? = nil) -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first ?? URL(fileURLWithPath: NSTemporaryDirectory())
        let dir = base.appendingPathComponent("CombineColors/Corrections", isDirectory: true)
        let key = SHA256.hash(data: Data(image.standardizedFileURL.path.utf8)).map { String(format: "%02x", $0) }.joined().prefix(32)
        return dir.appendingPathComponent(String(key) + suffix(kind))
    }

    /// Writes the channel's map (16-bit PNG). Tries the sidecar, then the fallback; returns where
    /// it went. A map saved without the channel in its name is removed (it is superseded).
    @discardableResult
    public func write(for image: URL, kind: ChannelKind) throws -> URL {
        let data = PlanarImage(width: width, height: height)
        var a: Float = 0.5, b: Float = 0.5
        vDSP_vsmsa(plane.pointer, 1, &a, &b, data.pointer, 1, vDSP_Length(plane.count))
        defer { CorrectionMap.removeLegacy(for: image) }
        let sidecar = CorrectionMap.sidecarURL(for: image, kind: kind)
        do {
            try RasterWriter.write(planes: [data], to: sidecar, format: .png16)
            return sidecar
        } catch {
            let fb = CorrectionMap.fallbackURL(for: image, kind: kind)
            try FileManager.default.createDirectory(at: fb.deletingLastPathComponent(), withIntermediateDirectories: true)
            try RasterWriter.write(planes: [data], to: fb, format: .png16)
            return fb
        }
    }

    /// Removes the channel's stored map (sidecar and fallback), and the channel-less one.
    public static func remove(for image: URL, kind: ChannelKind) {
        for u in [sidecarURL(for: image, kind: kind), fallbackURL(for: image, kind: kind)] { try? FileManager.default.removeItem(at: u) }
        removeLegacy(for: image)
    }

    static func removeLegacy(for image: URL) {
        for u in [sidecarURL(for: image), fallbackURL(for: image)] { try? FileManager.default.removeItem(at: u) }
    }

    /// Reads the channel's stored map of an image of the given (unoriented) size, if any; a map
    /// without the channel in its name is the fallback.
    public static func read(for image: URL, kind: ChannelKind, imageWidth: Int, imageHeight: Int) -> CorrectionMap? {
        let candidates = [sidecarURL(for: image, kind: kind), fallbackURL(for: image, kind: kind), sidecarURL(for: image), fallbackURL(for: image)]
        for u in candidates where FileManager.default.fileExists(atPath: u.path) {
            guard let r = try? RasterReader.read(url: u), let p = r.planes.first else { continue }
            let expected = CorrectionMap(imageWidth: imageWidth, imageHeight: imageHeight)
            guard p.width == expected.width, p.height == expected.height else { continue }
            var a: Float = 2, b: Float = -1
            vDSP_vsmsa(p.pointer, 1, &a, &b, p.pointer, 1, vDSP_Length(p.count))
            return CorrectionMap(imageWidth: imageWidth, imageHeight: imageHeight, plane: p)
        }
        return nil
    }
}

/// A correction map applied live to one channel of a render: `rect` is the region (full-resolution
/// pixels) the rendered planes cover (nil: the whole image).
public struct CorrectionInput {
    public var map: CorrectionMap
    public var strength: Float
    public var kind: ChannelKind
    public var rect: CGRect?
    /// Sky level of a sky-relative correction (nil: the whole value is scaled).
    public var pedestal: Float?
    public init(map: CorrectionMap, strength: Float, kind: ChannelKind, rect: CGRect?, pedestal: Float? = nil) {
        self.map = map; self.strength = strength; self.kind = kind; self.rect = rect; self.pedestal = pedestal
    }
}

/// What a painted correction scales.
public enum CorrectionMode: String, Codable, CaseIterable, Identifiable {
    /// The signal above the channel's sky level: even on linear data, sky glow and nebulosity are
    /// dimmed or lifted without the sky pedestal being amplified by the stretch.
    case aboveSky
    /// The whole value (an exposure change): strong on linear data, where the sky pedestal is most
    /// of every value.
    case exposure
    public var id: String { rawValue }
    public var displayName: String { self == .aboveSky ? "Above the sky" : "Exposure" }
}

/// Per-channel strength of the painted corrections and the brush defaults (stored in the project;
/// the maps themselves live next to the images).
public struct CorrectionSettings: Codable, Equatable {
    /// Exposure range of the map in stops: the map's ±1 means ±strength EV.
    public var strength: [ChannelKind: Double] = [:]
    /// Brush radius as a fraction of the image's long side (0.005…0.2).
    public var brushRadius: Double = 0.04
    /// Amount added per dab (0.005…1; shown as a percentage).
    public var brushFlow: Double = 0.02
    /// Fraction of the radius over which the dab fades (0 hard … 1 soft).
    public var brushFeather: Double = 0.7
    public var darken: Bool = true
    /// What the map scales: the signal above the sky (default) or the whole value.
    public var mode: CorrectionMode = .aboveSky

    public init() {}

    public static let defaultStrength = 0.5
    public func strength(for kind: ChannelKind) -> Double { strength[kind] ?? CorrectionSettings.defaultStrength }
}
