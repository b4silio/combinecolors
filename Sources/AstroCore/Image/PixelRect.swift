import Foundation
import CoreGraphics

/// An integer rectangle in full-resolution pixel coordinates (origin top-left).
public struct PixelRect: Codable, Equatable, Hashable {
    public var x: Int
    public var y: Int
    public var width: Int
    public var height: Int

    public init(x: Int, y: Int, width: Int, height: Int) {
        self.x = x; self.y = y; self.width = width; self.height = height
    }

    public init(_ r: CGRect) {
        let x0 = Int(r.minX.rounded()), y0 = Int(r.minY.rounded())
        let x1 = Int(r.maxX.rounded()), y1 = Int(r.maxY.rounded())
        self.init(x: x0, y: y0, width: max(1, x1 - x0), height: max(1, y1 - y0))
    }

    public var maxX: Int { x + width }
    public var maxY: Int { y + height }
    public var count: Int { width * height }
    public var cgRect: CGRect { CGRect(x: x, y: y, width: width, height: height) }
    public var isEmpty: Bool { width <= 0 || height <= 0 }

    /// Clamped to an image of the given size; returns nil when nothing remains.
    public func clamped(width w: Int, height h: Int) -> PixelRect? {
        let x0 = max(0, min(x, w)), y0 = max(0, min(y, h))
        let x1 = max(x0, min(maxX, w)), y1 = max(y0, min(maxY, h))
        guard x1 > x0, y1 > y0 else { return nil }
        return PixelRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0)
    }

    public func contains(x px: Int, y py: Int) -> Bool { px >= x && px < maxX && py >= y && py < maxY }

    public static func full(width: Int, height: Int) -> PixelRect { PixelRect(x: 0, y: 0, width: width, height: height) }

    /// Scaled to a pyramid level (divides by 2^level, rounding outwards).
    public func scaled(level: Int) -> PixelRect {
        guard level > 0 else { return self }
        let s = 1 << level
        let x0 = x / s, y0 = y / s
        let x1 = (maxX + s - 1) / s, y1 = (maxY + s - 1) / s
        return PixelRect(x: x0, y: y0, width: max(1, x1 - x0), height: max(1, y1 - y0))
    }
}

extension PlanarImage {
    public func cropped(_ r: PixelRect) -> PlanarImage {
        cropped(x: r.x, y: r.y, width: r.width, height: r.height)
    }
}

/// Finds the rectangle that excludes the empty (zero) borders left by registration.
public enum AutoCrop {
    /// Shrinks each side until the row/column is at least `minValidFraction` valid in every image.
    public static func detect(images: [PlanarImage], minValidFraction: Float = 0.98) -> PixelRect? {
        guard let first = images.first else { return nil }
        let w = first.width, h = first.height
        func rowValid(_ y: Int, _ x0: Int, _ x1: Int) -> Bool {
            let n = x1 - x0
            guard n > 0 else { return false }
            for img in images {
                var valid = 0
                let p = img.pointer + y * w
                for x in x0..<x1 where p[x] != 0 { valid += 1 }
                if Float(valid) < minValidFraction * Float(n) { return false }
            }
            return true
        }
        func colValid(_ x: Int, _ y0: Int, _ y1: Int) -> Bool {
            let n = y1 - y0
            guard n > 0 else { return false }
            for img in images {
                var valid = 0
                let p = img.pointer
                for y in y0..<y1 where p[y * w + x] != 0 { valid += 1 }
                if Float(valid) < minValidFraction * Float(n) { return false }
            }
            return true
        }
        var x0 = 0, y0 = 0, x1 = w, y1 = h
        // Alternate sides so irregular (rotated) borders converge to a good inner rectangle.
        var changed = true
        while changed && x1 - x0 > 16 && y1 - y0 > 16 {
            changed = false
            if !rowValid(y0, x0, x1) { y0 += 1; changed = true }
            if !rowValid(y1 - 1, x0, x1) { y1 -= 1; changed = true }
            if !colValid(x0, y0, y1) { x0 += 1; changed = true }
            if !colValid(x1 - 1, y0, y1) { x1 -= 1; changed = true }
        }
        // Grow back sides that were trimmed only because of another side's border.
        changed = true
        while changed {
            changed = false
            if y0 > 0 && rowValid(y0 - 1, x0, x1) { y0 -= 1; changed = true }
            if y1 < h && rowValid(y1, x0, x1) { y1 += 1; changed = true }
            if x0 > 0 && colValid(x0 - 1, y0, y1) { x0 -= 1; changed = true }
            if x1 < w && colValid(x1, y0, y1) { x1 += 1; changed = true }
        }
        guard x1 > x0, y1 > y0 else { return nil }
        return PixelRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0)
    }
}
