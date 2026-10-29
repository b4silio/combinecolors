import XCTest
@testable import AstroCore

/// Opt-in timing of the denoise methods on real data: `CC_BENCH=<fits file> swift test -c release --filter DenoiseBenchmark`.
/// With `CC_BENCH_OUT=<dir>` it also writes 8-bit PGM crops of every variant for a visual check.
final class DenoiseBenchmark: XCTestCase {
    func testTimings() throws {
        guard let path = ProcessInfo.processInfo.environment["CC_BENCH"] else { throw XCTSkip("set CC_BENCH=<fits>") }
        let f = try ImageLoader.load(url: URL(fileURLWithPath: path))
        let full = f.planes[0]
        // Crude stretch so the noise level resembles the app's output.
        let (lo, _) = full.minMax()
        for i in 0..<full.count { full.pointer[i] = asinh((full.pointer[i] - lo) * 300) / asinh(300) }
        if let dir = ProcessInfo.processInfo.environment["CC_BENCH_OUT"] {
            let crop = full.cropped(x: 2900, y: 2300, width: 450, height: 350)
            let sigma = Denoise.noiseSigma(crop)
            print("sigma \(sigma) spectrum", MetalDenoise.coefficientNoise(crop).map { String(format: "%.4f", $0) }.joined(separator: " "))
            func save(_ name: String, _ p: PlanarImage) throws {
                var d = Data("P5\n\(p.width) \(p.height)\n255\n".utf8)
                let sorted = crop.toArray().sorted()
                let a = Statistics.percentile(sorted: sorted, q: 0.02), b = Statistics.percentile(sorted: sorted, q: 0.9)
                d.append(contentsOf: (0..<p.count).map { UInt8(min(max((p.pointer[$0] - a) / (b - a) * 255, 0), 255)) })
                try d.write(to: URL(fileURLWithPath: dir).appendingPathComponent(name + ".pgm"))
            }
            try save("noisy", crop)
            try save("wavelets", Denoise.wavelets(crop, sigma: sigma, amount: 0.8, layers: 3))
            try save("nlm", MetalDenoise.nonLocalMeans(crop, sigma: sigma, h: 1.2 * sigma, search: 5)!)
            for a: Float in [0.8, 1, 1.3] { for srch in [8, 16] {
                try save("bm3d_a\(a)_s\(srch)", MetalDenoise.bm3d(crop, sigma: sigma, amount: a, search: srch)!)
            } }
        }
        if ProcessInfo.processInfo.environment["CC_BENCH_TIMING"] == nil { return }
        let sizes = [("preview 1900×1300", full.cropped(x: 2000, y: 1500, width: 1900, height: 1300)), ("full \(full.width)×\(full.height)", full)]
        for (name, img) in sizes {
            let sigma = Denoise.noiseSigma(img)
            func time(_ label: String, _ body: () -> PlanarImage?) {
                let t = Date(); _ = body(); print(String(format: "%@ %@: %.3f s", name, label, Date().timeIntervalSince(t)))
            }
            time("wavelets") { Denoise.wavelets(img, sigma: sigma, amount: 0.8, layers: 3) }
            time("guided") { Denoise.guided(img, epsilon: pow(2.4 * sigma, 2), radius: 3) }
            time("nlm gpu") { MetalDenoise.nonLocalMeans(img, sigma: sigma, h: 1.2 * sigma, search: 5) }
            if name.hasPrefix("preview") { time("nlm cpu") { Denoise.nonLocalMeans(img, sigma: sigma, h: 1.2 * sigma, search: 5) } }
            time("bm3d s8") { MetalDenoise.bm3d(img, sigma: sigma, amount: 0.8, search: 8) }
            time("bm3d s16") { MetalDenoise.bm3d(img, sigma: sigma, amount: 0.8, search: 16) }
            time("noise spectrum") { _ = MetalDenoise.coefficientNoise(img); return nil }
        }
    }
}
