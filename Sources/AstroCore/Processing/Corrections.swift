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

    /// The map of the unoriented image (what the sidecar stores): the orientation undone.
    public func unoriented(_ o: Orientation) -> CorrectionMap {
        var m = self
        for _ in 0..<((4 - o.rotation) % 4) { m = m.applying(.rotateClockwise) }
        if o.flipped { m = m.applying(.flipHorizontal) }
        return m
    }

    // MARK: Application

    /// `image × 2^(strength × map)` for the region `rect` of the image (full-resolution pixels) that
    /// `image` covers (nil: the whole image); zero pixels stay zero. In place.
    public func apply(to image: PlanarImage, strength: Float, rect: CGRect? = nil) {
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
        vDSP_vmul(image.pointer, 1, sampled.pointer, 1, image.pointer, 1, vDSP_Length(n))
    }

    /// A corrected copy of the whole image.
    public func applied(to image: PlanarImage, strength: Float) -> PlanarImage {
        let out = image.copy()
        apply(to: out, strength: strength)
        return out
    }

    // MARK: Storage

    /// Sidecar next to the image: `<image>.ccmask.png` (16-bit grey, 32768 = no change).
    public static func sidecarURL(for image: URL) -> URL {
        image.deletingLastPathComponent().appendingPathComponent(image.lastPathComponent + ".ccmask.png")
    }

    /// Fallback when the image's folder is not writable: Application Support, keyed by the image path.
    public static func fallbackURL(for image: URL) -> URL {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first ?? URL(fileURLWithPath: NSTemporaryDirectory())
        let dir = base.appendingPathComponent("CombineColors/Corrections", isDirectory: true)
        let key = SHA256.hash(data: Data(image.standardizedFileURL.path.utf8)).map { String(format: "%02x", $0) }.joined().prefix(32)
        return dir.appendingPathComponent(String(key) + ".ccmask.png")
    }

    /// Writes the map (16-bit PNG). Tries the sidecar, then the fallback; returns where it went.
    @discardableResult
    public func write(for image: URL) throws -> URL {
        let data = PlanarImage(width: width, height: height)
        var a: Float = 0.5, b: Float = 0.5
        vDSP_vsmsa(plane.pointer, 1, &a, &b, data.pointer, 1, vDSP_Length(plane.count))
        let sidecar = CorrectionMap.sidecarURL(for: image)
        do {
            try RasterWriter.write(planes: [data], to: sidecar, format: .png16)
            return sidecar
        } catch {
            let fb = CorrectionMap.fallbackURL(for: image)
            try FileManager.default.createDirectory(at: fb.deletingLastPathComponent(), withIntermediateDirectories: true)
            try RasterWriter.write(planes: [data], to: fb, format: .png16)
            return fb
        }
    }

    /// Removes the stored map (sidecar and fallback).
    public static func remove(for image: URL) {
        for u in [sidecarURL(for: image), fallbackURL(for: image)] { try? FileManager.default.removeItem(at: u) }
    }

    /// Reads the stored map of an image of the given (unoriented) size, if any.
    public static func read(for image: URL, imageWidth: Int, imageHeight: Int) -> CorrectionMap? {
        for u in [sidecarURL(for: image), fallbackURL(for: image)] where FileManager.default.fileExists(atPath: u.path) {
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
    public init(map: CorrectionMap, strength: Float, kind: ChannelKind, rect: CGRect?) { self.map = map; self.strength = strength; self.kind = kind; self.rect = rect }
}

/// Per-channel strength of the painted corrections and the brush defaults (stored in the project;
/// the maps themselves live next to the images).
public struct CorrectionSettings: Codable, Equatable {
    /// Exposure range of the map in stops: the map's ±1 means ±strength EV.
    public var strength: [ChannelKind: Double] = [:]
    /// Brush radius as a fraction of the image's long side (0.005…0.2).
    public var brushRadius: Double = 0.04
    /// Amount added per dab (0.02…1).
    public var brushFlow: Double = 0.15
    /// Fraction of the radius over which the dab fades (0 hard … 1 soft).
    public var brushFeather: Double = 0.7
    public var darken: Bool = true

    public init() {}

    public static let defaultStrength = 1.0
    public func strength(for kind: ChannelKind) -> Double { strength[kind] ?? CorrectionSettings.defaultStrength }
}
