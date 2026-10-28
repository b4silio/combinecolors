import Foundation
import Accelerate

/// A single-plane 32-bit float image. Memory is owned by the instance and
/// 64-byte aligned so that Accelerate can use its fastest code paths.
public final class PlanarImage {
    public let width: Int
    public let height: Int
    public let data: UnsafeMutableBufferPointer<Float>

    public var count: Int { width * height }

    public init(width: Int, height: Int) {
        precondition(width > 0 && height > 0, "PlanarImage requires positive dimensions")
        self.width = width
        self.height = height
        let n = width * height
        var raw: UnsafeMutableRawPointer? = nil
        let rc = posix_memalign(&raw, 64, max(64, n * MemoryLayout<Float>.stride))
        precondition(rc == 0 && raw != nil, "allocation failed")
        let p = raw!.bindMemory(to: Float.self, capacity: n)
        p.initialize(repeating: 0, count: n)
        data = UnsafeMutableBufferPointer(start: p, count: n)
    }

    public convenience init(width: Int, height: Int, fill: Float) {
        self.init(width: width, height: height)
        if fill != 0 { var f = fill; vDSP_vfill(&f, data.baseAddress!, 1, vDSP_Length(count)) }
    }

    public convenience init(width: Int, height: Int, pixels: [Float]) {
        precondition(pixels.count == width * height)
        self.init(width: width, height: height)
        pixels.withUnsafeBufferPointer { src in
            data.baseAddress!.update(from: src.baseAddress!, count: pixels.count)
        }
    }

    deinit { free(data.baseAddress) }

    @inline(__always) public subscript(x: Int, y: Int) -> Float {
        get { data[y * width + x] }
        set { data[y * width + x] = newValue }
    }

    public var pointer: UnsafeMutablePointer<Float> { data.baseAddress! }

    public func copy() -> PlanarImage {
        let out = PlanarImage(width: width, height: height)
        out.pointer.update(from: pointer, count: count)
        return out
    }

    public func toArray() -> [Float] { Array(data) }

    /// Returns a sub-image. The rectangle is clamped to the image bounds.
    public func cropped(x: Int, y: Int, width w: Int, height h: Int) -> PlanarImage {
        let x0 = max(0, min(x, width - 1)), y0 = max(0, min(y, height - 1))
        let x1 = max(x0 + 1, min(x + w, width)), y1 = max(y0 + 1, min(y + h, height))
        let cw = x1 - x0, ch = y1 - y0
        let out = PlanarImage(width: cw, height: ch)
        for row in 0..<ch {
            (out.pointer + row * cw).update(from: pointer + (y0 + row) * width + x0, count: cw)
        }
        return out
    }

    /// Rotated by 90° clockwise: pixel (x, y) of the source lands at (height − 1 − y, x).
    public func rotated90CW() -> PlanarImage {
        let out = PlanarImage(width: height, height: width)
        var src = vImage_Buffer(data: pointer, height: vImagePixelCount(height), width: vImagePixelCount(width), rowBytes: width * MemoryLayout<Float>.stride)
        var dst = vImage_Buffer(data: out.pointer, height: vImagePixelCount(out.height), width: vImagePixelCount(out.width), rowBytes: out.width * MemoryLayout<Float>.stride)
        // vImage's rotation constants are counter-clockwise quarter turns: 3 = 270° CCW = 90° CW.
        let err = vImageRotate90_PlanarF(&src, &dst, 3, 0, vImage_Flags(kvImageNoFlags))
        precondition(err == kvImageNoError, "vImageRotate90_PlanarF failed: \(err)")
        return out
    }

    /// Rotated by `times` quarter turns clockwise (0 returns self).
    public func rotated90CW(times: Int) -> PlanarImage {
        var img = self
        for _ in 0..<(((times % 4) + 4) % 4) { img = img.rotated90CW() }
        return img
    }

    /// 2×2 box-filter down-sample. Odd trailing rows/columns are averaged with
    /// their only neighbour (edge replication), so every level keeps full coverage.
    public func downsampled2x() -> PlanarImage {
        let ow = (width + 1) / 2, oh = (height + 1) / 2
        let out = PlanarImage(width: ow, height: oh)
        let rowSum = UnsafeMutablePointer<Float>.allocate(capacity: width + 1)
        defer { rowSum.deallocate() }
        rowSum[width] = 0
        for oy in 0..<oh {
            let y0 = 2 * oy, y1 = min(2 * oy + 1, height - 1)
            vDSP_vadd(pointer + y0 * width, 1, pointer + y1 * width, 1, rowSum, 1, vDSP_Length(width))
            if width % 2 == 1 { rowSum[width] = rowSum[width - 1] }
            // out[ox] = (rowSum[2ox] + rowSum[2ox+1]) * 0.25
            vDSP_vadd(rowSum, 2, rowSum + 1, 2, out.pointer + oy * ow, 1, vDSP_Length(ow))
        }
        var q: Float = 0.25
        vDSP_vsmul(out.pointer, 1, &q, out.pointer, 1, vDSP_Length(out.count))
        return out
    }

    /// Vertical flip in place (used for FITS BOTTOM-UP row order).
    public func flipVertically() {
        let tmp = UnsafeMutablePointer<Float>.allocate(capacity: width)
        defer { tmp.deallocate() }
        for y in 0..<(height / 2) {
            let a = pointer + y * width, b = pointer + (height - 1 - y) * width
            tmp.update(from: a, count: width)
            a.update(from: b, count: width)
            b.update(from: tmp, count: width)
        }
    }

    /// Lanczos / linear resample to an arbitrary size using vImage.
    public func resized(width nw: Int, height nh: Int) -> PlanarImage {
        let out = PlanarImage(width: nw, height: nh)
        var src = vImage_Buffer(data: pointer, height: vImagePixelCount(height), width: vImagePixelCount(width), rowBytes: width * 4)
        var dst = vImage_Buffer(data: out.pointer, height: vImagePixelCount(nh), width: vImagePixelCount(nw), rowBytes: nw * 4)
        vImageScale_PlanarF(&src, &dst, nil, vImage_Flags(kvImageHighQualityResampling))
        return out
    }

    // MARK: - Simple whole-image arithmetic helpers (in place)

    public func clamp(min lo: Float, max hi: Float) {
        var l = lo, h = hi
        vDSP_vclip(pointer, 1, &l, &h, pointer, 1, vDSP_Length(count))
    }

    public func affine(scale a: Float, offset b: Float) {
        var aa = a, bb = b
        vDSP_vsmsa(pointer, 1, &aa, &bb, pointer, 1, vDSP_Length(count))
    }

    public func minMax() -> (min: Float, max: Float) {
        var lo: Float = 0, hi: Float = 0
        vDSP_minv(pointer, 1, &lo, vDSP_Length(count))
        vDSP_maxv(pointer, 1, &hi, vDSP_Length(count))
        return (lo, hi)
    }

    public func mean() -> Float {
        var m: Float = 0
        vDSP_meanv(pointer, 1, &m, vDSP_Length(count))
        return m
    }
}

/// Three planes of identical size.
public final class RGBImage {
    public let r: PlanarImage, g: PlanarImage, b: PlanarImage
    public var width: Int { r.width }
    public var height: Int { r.height }
    public var planes: [PlanarImage] { [r, g, b] }

    public init(r: PlanarImage, g: PlanarImage, b: PlanarImage) {
        precondition(r.width == g.width && g.width == b.width && r.height == g.height && g.height == b.height)
        self.r = r; self.g = g; self.b = b
    }

    public init(width: Int, height: Int) {
        r = PlanarImage(width: width, height: height)
        g = PlanarImage(width: width, height: height)
        b = PlanarImage(width: width, height: height)
    }

    public func clamp01() { for p in planes { p.clamp(min: 0, max: 1) } }

    public func downsampled2x() -> RGBImage {
        RGBImage(r: r.downsampled2x(), g: g.downsampled2x(), b: b.downsampled2x())
    }

    /// Rec. 709 luma.
    public func luma() -> PlanarImage {
        let out = PlanarImage(width: width, height: height)
        var wr: Float = 0.2126, wg: Float = 0.7152, wb: Float = 0.0722
        let n = vDSP_Length(out.count)
        vDSP_vsmul(r.pointer, 1, &wr, out.pointer, 1, n)
        vDSP_vsma(g.pointer, 1, &wg, out.pointer, 1, out.pointer, 1, n)
        vDSP_vsma(b.pointer, 1, &wb, out.pointer, 1, out.pointer, 1, n)
        return out
    }
}

/// A multi-level 2× pyramid of a plane. Level 0 is the original.
public final class Pyramid {
    public let levels: [PlanarImage]

    public init(base: PlanarImage, minSize: Int = 64) {
        var ls = [base]
        var cur = base
        while cur.width > minSize && cur.height > minSize {
            cur = cur.downsampled2x()
            ls.append(cur)
        }
        levels = ls
    }

    public var base: PlanarImage { levels[0] }
    public var count: Int { levels.count }

    public func level(_ i: Int) -> PlanarImage { levels[max(0, min(i, levels.count - 1))] }

    /// Level whose pixel count does not exceed `maxPixels` (coarsest fallback).
    public func levelIndex(maxPixels: Int) -> Int {
        for (i, l) in levels.enumerated() where l.count <= maxPixels { return i }
        return levels.count - 1
    }

    /// Finest level whose scale (2^-i) is still >= the requested screen scale,
    /// i.e. the level that has at least one source pixel per displayed pixel.
    public func levelIndex(forScale scale: Double) -> Int {
        var i = 0
        while i + 1 < levels.count && pow(2.0, -Double(i + 1)) >= scale { i += 1 }
        return i
    }
}
