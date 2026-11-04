import Foundation

/// Rotation by an arbitrary angle about the centre of the image, on the same canvas (the corners
/// that leave it are lost, the ones that come in are zero, like registration borders). Positive
/// angles turn the picture clockwise on screen (image y points down), like `rotateClockwise`.
///
/// Coordinates use the pixel-edge convention (pixel (i, j) covers [i, i+1) × [j, j+1), centre at
/// (i + ½, j + ½)), so a point and the pixel under it move together.
public enum FineRotation {
    /// Where the point (`x`, `y`) of a `width` × `height` image lands after a rotation by `degrees`.
    public static func rotate(x: Double, y: Double, degrees: Double, width: Int, height: Int) -> (x: Double, y: Double) {
        guard degrees != 0 else { return (x, y) }
        let t = degrees * .pi / 180, c = cos(t), s = sin(t)
        let cx = Double(width) / 2, cy = Double(height) / 2
        let dx = x - cx, dy = y - cy
        return (cx + c * dx - s * dy, cy + s * dx + c * dy)
    }

    /// Lanczos-3 kernel sampled finely (the weights are looked up, not computed: 36 taps per pixel).
    static let lutSteps = 1024
    static let lanczos: [Float] = {
        (0...(3 * lutSteps)).map { i in
            let x = Double(i) / Double(lutSteps)
            if x == 0 { return 1 }
            if x >= 3 { return 0 }
            let px = Double.pi * x
            return Float(3 * sin(px) * sin(px / 3) / (px * px))
        }
    }()
}

extension PlanarImage {
    /// The image turned by `degrees` (clockwise on screen) about its centre on the same canvas,
    /// resampled with Lanczos-3. Samples are clamped to the range of the 2 × 2 nearest pixels, so
    /// stars do not get dark rings and zero borders do not ring into the picture; pixels whose source
    /// lies outside the image are zero.
    public func rotated(byDegrees degrees: Double) -> PlanarImage {
        guard degrees.truncatingRemainder(dividingBy: 360) != 0 else { return copy() }
        let w = width, h = height
        let out = PlanarImage(width: w, height: h)
        let t = degrees * .pi / 180, c = cos(t), s = sin(t)
        let cx = Double(w) / 2, cy = Double(h) / 2
        let src = pointer, dst = out.pointer
        let lut = FineRotation.lanczos, steps = Float(FineRotation.lutSteps)
        let rowsPerChunk = 16
        let chunks = (h + rowsPerChunk - 1) / rowsPerChunk
        lut.withUnsafeBufferPointer { k in
            DispatchQueue.concurrentPerform(iterations: chunks) { chunk in
                var wx = [Float](repeating: 0, count: 6), wy = [Float](repeating: 0, count: 6)
                for j in (chunk * rowsPerChunk)..<min(h, (chunk + 1) * rowsPerChunk) {
                    let qy = Double(j) + 0.5 - cy
                    for i in 0..<w {
                        let qx = Double(i) + 0.5 - cx
                        // Inverse rotation: where this output pixel comes from (pixel-edge coordinates).
                        let sx = cx + c * qx + s * qy, sy = cy - s * qx + c * qy
                        guard sx >= 0, sy >= 0, sx < Double(w), sy < Double(h) else { dst[j * w + i] = 0; continue }
                        // Pixel-centre coordinates of the sample.
                        let ux = sx - 0.5, uy = sy - 0.5
                        let x0 = Int(ux.rounded(.down)), y0 = Int(uy.rounded(.down))
                        let fx = Float(ux - Double(x0)), fy = Float(uy - Double(y0))
                        var sumX: Float = 0, sumY: Float = 0
                        for n in 0..<6 {
                            let dxn = abs(Float(n - 2) - fx), dyn = abs(Float(n - 2) - fy)
                            wx[n] = k[min(Int(dxn * steps), 3 * FineRotation.lutSteps)]
                            wy[n] = k[min(Int(dyn * steps), 3 * FineRotation.lutSteps)]
                            sumX += wx[n]; sumY += wy[n]
                        }
                        var acc: Float = 0
                        var lo = Float.greatestFiniteMagnitude, hi = -Float.greatestFiniteMagnitude
                        for m in 0..<6 {
                            let yy = Swift.min(Swift.max(y0 - 2 + m, 0), h - 1)
                            let row = src + yy * w
                            var r: Float = 0
                            for n in 0..<6 {
                                let xx = Swift.min(Swift.max(x0 - 2 + n, 0), w - 1)
                                let v = row[xx]
                                r += wx[n] * v
                                if (m == 2 || m == 3) && (n == 2 || n == 3) { lo = Swift.min(lo, v); hi = Swift.max(hi, v) }
                            }
                            acc += wy[m] * r
                        }
                        let v = acc / (sumX * sumY)
                        dst[j * w + i] = Swift.min(Swift.max(v, lo), hi)
                    }
                }
            }
        }
        return out
    }
}
