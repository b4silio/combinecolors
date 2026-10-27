import Foundation
import XCTest
@testable import AstroCore

enum Synth {
    /// Deterministic pseudo-random generator.
    struct LCG {
        var state: UInt64
        mutating func next() -> Float {
            state = state &* 6364136223846793005 &+ 1442695040888963407
            return Float((state >> 33) & 0xFFFFFF) / Float(0xFFFFFF)
        }
        /// Approximately Gaussian via sum of 12 uniforms.
        mutating func gaussian() -> Float {
            var s: Float = 0
            for _ in 0..<12 { s += next() }
            return s - 6
        }
    }

    /// Background + gradient + gaussian blobs (nebula) + stars + noise.
    static func channel(width: Int = 256, height: Int = 192, background: Float = 0.01, nebula: Float = 0.02, stars: Float = 0.5, noise: Float = 0.001, seed: UInt64 = 1, gradient: Float = 0) -> PlanarImage {
        let img = PlanarImage(width: width, height: height)
        var rng = LCG(state: seed)
        let blobs: [(Float, Float, Float, Float)] = [(0.5, 0.5, 40, 1), (0.3, 0.6, 25, 0.7), (0.7, 0.3, 30, 0.5)]
        var starList: [(Float, Float, Float)] = []
        for _ in 0..<40 { starList.append((rng.next() * Float(width), rng.next() * Float(height), 0.2 + rng.next())) }
        for y in 0..<height {
            for x in 0..<width {
                var v = background + gradient * Float(x) / Float(width)
                for (cx, cy, s, a) in blobs {
                    let dx = Float(x) - cx * Float(width), dy = Float(y) - cy * Float(height)
                    v += nebula * a * exp(-(dx * dx + dy * dy) / (2 * s * s))
                }
                for (sx, sy, a) in starList {
                    let dx = Float(x) - sx, dy = Float(y) - sy
                    let d2 = dx * dx + dy * dy
                    if d2 < 36 { v += stars * a * exp(-d2 / 2.5) }
                }
                v += noise * rng.gaussian()
                img[x, y] = max(v, 1e-6)
            }
        }
        return img
    }

    static func source(_ kind: ChannelKind, scale: Float = 1, seed: UInt64 = 1, width: Int = 256, height: Int = 192, gradient: Float = 0) -> ChannelSource {
        let img = channel(width: width, height: height, background: 0.01 * scale, nebula: 0.02 * scale, stars: 0.5 * scale, noise: 0.001 * scale, seed: seed, gradient: gradient)
        return ChannelSource(kind: kind, image: img, fileName: "\(kind.rawValue).fits")
    }

    static func tempURL(_ name: String) -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("CombineColorsTests", isDirectory: true)
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        return dir.appendingPathComponent(name)
    }
}

extension PlanarImage {
    func maxAbsDifference(_ other: PlanarImage) -> Float {
        var m: Float = 0
        for i in 0..<count { m = max(m, abs(pointer[i] - other.pointer[i])) }
        return m
    }
}
