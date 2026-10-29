import Foundation
import Metal

/// GPU (Metal) implementations of the patch-based noise reduction methods: non-local means and
/// BM3D (Dabov et al. 2007, block matching + collaborative 3-D transform filtering, hard
/// thresholding stage followed by the Wiener stage). The shaders are compiled from source at
/// first use; when no Metal device is available the callers fall back to the CPU paths.
///
/// Large images are processed in tiles (`tileSize` + margin) so GPU buffers stay bounded; the
/// margin is wide enough that tiling does not change the result.
public enum MetalDenoise {
    /// Core tile size in pixels (tests shrink it to exercise the seams).
    public static var tileSize = 1536
    /// Set to false to force the CPU paths (tests, benchmarks).
    public static var enabled = true
    /// Runs the second (Wiener) stage of BM3D; false keeps the hard-thresholding estimate (diagnostics).
    static var wienerStage = true

    public static var isAvailable: Bool { enabled && MetalContext.shared != nil }

    // MARK: - Non-local means

    /// Same definition as `Denoise.nonLocalMeans` (patch radius 2, edges extended).
    static func nonLocalMeans(_ y: PlanarImage, sigma: Float, h: Float, search: Int) -> PlanarImage? {
        guard enabled, let ctx = MetalContext.shared else { return nil }
        return try? tiled(y, margin: search + 3) { tile, _ in
            let out = PlanarImage(width: tile.width, height: tile.height)
            var p = NLMParams(width: UInt32(tile.width), height: UInt32(tile.height), search: Int32(search), patch: 2,
                              h2: max(h * h, 1e-12), noiseFloor: 2 * sigma * sigma)
            let src = try ctx.buffer(tile), dst = try ctx.buffer(count: tile.count)
            try ctx.run { enc in
                enc.setComputePipelineState(ctx.pipeline("nlm"))
                enc.setBuffer(src, offset: 0, index: 0); enc.setBuffer(dst, offset: 0, index: 1)
                enc.setBytes(&p, length: MemoryLayout<NLMParams>.stride, index: 2)
                enc.dispatchThreads(MTLSize(width: tile.width, height: tile.height, depth: 1), threadsPerThreadgroup: MTLSize(width: 16, height: 16, depth: 1))
            }
            out.pointer.update(from: dst.contents().assumingMemoryBound(to: Float.self), count: tile.count)
            return out
        }
    }

    // MARK: - BM3D

    static let block = 8, step = 3, maxMatches = 16

    /// BM3D on a luminance plane. `amount` scales the thresholds (1 = the canonical 2.7·σ hard
    /// threshold and σ² Wiener filter); `search` is the block-matching window radius. The noise is
    /// measured per DCT coefficient (stacked / resampled data has correlated noise, which a single
    /// σ would under-threshold at low frequencies and leave as worm-like patterns); `sigma` is the
    /// white-noise fallback.
    static func bm3d(_ y: PlanarImage, sigma: Float, amount: Float, search: Int) -> PlanarImage? {
        guard enabled, let ctx = MetalContext.shared, sigma > 0, amount > 0 else { return nil }
        var spectrum = coefficientNoise(y)
        if spectrum.allSatisfy({ $0 <= 0 }) { spectrum = [Float](repeating: sigma, count: block * block) }
        let noise = spectrum.map { $0 * amount }
        // Each stage reaches 2·(search + block) pixels; the reference grid is phase-locked to the
        // image so tiles produce the same groups as a whole-image run.
        return try? tiled(y, margin: 4 * (search + block)) { tile, origin in
            try bm3dTile(tile, origin: origin, ctx: ctx, noise: noise, search: search)
        }
    }

    /// Noise σ of each 8×8 DCT coefficient: MAD over sampled blocks of the high-pass image
    /// (the image minus its starlet approximation after three scales, a ~30 px low-pass that
    /// removes sky and nebula but keeps the noise at every block frequency). The DC term, lost
    /// in the high-pass, takes the σ of the lowest AC terms.
    static func coefficientNoise(_ y: PlanarImage) -> [Float] {
        let w = y.width, h = y.height
        guard w >= block && h >= block else { return [] }
        // A single dilated B3 pass is not a low-pass (it passes every multiple of 1/step), so
        // chain the steps as the starlet transform does.
        let hp = Denoise.smooth(Denoise.smooth(Denoise.smooth(y, step: 1), step: 2), step: 4)
        let yp = y.pointer, sp = hp.pointer
        let stride = max(2, Int((Double(w * h) / 16384).squareRoot().rounded(.up)))
        let xs = Array(Swift.stride(from: 0, to: w - block + 1, by: stride)), ys = Array(Swift.stride(from: 0, to: h - block + 1, by: stride))
        let nBlocks = xs.count * ys.count
        guard nBlocks >= 32 else { return [] }
        var coeffs = [[Float]](repeating: [], count: block * block)
        for k in 0..<block * block { coeffs[k].reserveCapacity(nBlocks) }
        var blk = [Float](repeating: 0, count: 64), tmp = [Float](repeating: 0, count: 64)
        let c = dctMatrix
        for by in ys { for bx in xs {
            for j in 0..<8 { for i in 0..<8 { let idx = (by + j) * w + bx + i; blk[j * 8 + i] = yp[idx] - sp[idx] } }
            // rows then columns: X = C · x · Cᵀ
            for j in 0..<8 { for u in 0..<8 { var a: Float = 0; for i in 0..<8 { a += c[u * 8 + i] * blk[j * 8 + i] }; tmp[j * 8 + u] = a } }
            for u in 0..<8 { for v in 0..<8 { var a: Float = 0; for j in 0..<8 { a += c[v * 8 + j] * tmp[j * 8 + u] }; coeffs[v * 8 + u].append(abs(a)) } }
        } }
        var sigma = coeffs.map { col -> Float in
            var s = col; s.sort()
            return Statistics.percentile(sorted: s, q: 0.5) / 0.6745
        }
        sigma[0] = 0.5 * (sigma[1] + sigma[8])
        return sigma
    }

    private static func bm3dTile(_ tile: PlanarImage, origin: (Int, Int), ctx: MetalContext, noise: [Float], search: Int) throws -> PlanarImage {
        let w = tile.width, h = tile.height
        guard w >= block && h >= block else { return tile.copy() }
        let phaseX = (step - origin.0 % step) % step, phaseY = (step - origin.1 % step) % step
        let nRefX = (w - block - phaseX + step - 1) / step + 1, nRefY = (h - block - phaseY + step - 1) / step + 1
        let nRef = nRefX * nRefY
        let noisy = try ctx.buffer(tile)
        let basic = try ctx.buffer(count: tile.count), out = try ctx.buffer(count: tile.count)
        let num = try ctx.buffer(count: tile.count), den = try ctx.buffer(count: tile.count)
        let matches = try ctx.buffer(count: nRef * maxMatches), counts = try ctx.buffer(count: nRef)
        let dct = try ctx.buffer(floats: dctMatrix), kaiser = try ctx.buffer(floats: kaiserWindow)
        let spectrum = try ctx.buffer(floats: noise)
        // Mean per-pixel noise variance (orthonormal transform: the mean of the coefficient variances).
        let s2 = noise.reduce(0) { $0 + $1 * $1 } / Float(noise.count)
        // Per-pixel mean squared distance thresholds for a block to join a group (noise alone gives 2σ²).
        var p = BMParams(width: UInt32(w), height: UInt32(h), nRefX: UInt32(nRefX), nRefY: UInt32(nRefY),
                         step: Int32(step), search: Int32(search), phaseX: Int32(phaseX), phaseY: Int32(phaseY),
                         tau: 10 * s2 * Float(block * block), threshold: 2.7, sigma2: s2)
        let refGrid = MTLSize(width: nRefX, height: nRefY, depth: 1)
        let refTG = MTLSize(width: 8, height: 8, depth: 1), blockTG = MTLSize(width: block * block, height: 1, depth: 1)
        func stage(_ filter: String, matchOn: MTLBuffer, fallback: MTLBuffer, result: MTLBuffer) throws {
            try ctx.run { enc in
                enc.fill(num, count: tile.count); enc.fill(den, count: tile.count)
                enc.setComputePipelineState(ctx.pipeline("bm3d_match"))
                enc.setBuffer(matchOn, offset: 0, index: 0); enc.setBuffer(matches, offset: 0, index: 1); enc.setBuffer(counts, offset: 0, index: 2)
                enc.setBytes(&p, length: MemoryLayout<BMParams>.stride, index: 3)
                enc.dispatchThreads(refGrid, threadsPerThreadgroup: refTG)
                enc.setComputePipelineState(ctx.pipeline(filter))
                enc.setBuffer(noisy, offset: 0, index: 0); enc.setBuffer(basic, offset: 0, index: 1)
                enc.setBuffer(matches, offset: 0, index: 2); enc.setBuffer(counts, offset: 0, index: 3)
                enc.setBuffer(num, offset: 0, index: 4); enc.setBuffer(den, offset: 0, index: 5)
                enc.setBytes(&p, length: MemoryLayout<BMParams>.stride, index: 6)
                enc.setBuffer(dct, offset: 0, index: 7); enc.setBuffer(kaiser, offset: 0, index: 8); enc.setBuffer(spectrum, offset: 0, index: 9)
                enc.dispatchThreadgroups(refGrid, threadsPerThreadgroup: blockTG)
                enc.setComputePipelineState(ctx.pipeline("bm3d_divide"))
                enc.setBuffer(num, offset: 0, index: 0); enc.setBuffer(den, offset: 0, index: 1)
                enc.setBuffer(fallback, offset: 0, index: 2); enc.setBuffer(result, offset: 0, index: 3)
                enc.setBytes(&p, length: MemoryLayout<BMParams>.stride, index: 4)
                enc.dispatchThreads(MTLSize(width: w, height: h, depth: 1), threadsPerThreadgroup: MTLSize(width: 16, height: 16, depth: 1))
            }
        }
        try stage("bm3d_hard", matchOn: noisy, fallback: noisy, result: basic)
        if wienerStage {
            p.tau = 3 * s2 * Float(block * block)
            try stage("bm3d_wiener", matchOn: basic, fallback: basic, result: out)
        }
        let res = PlanarImage(width: w, height: h)
        res.pointer.update(from: (wienerStage ? out : basic).contents().assumingMemoryBound(to: Float.self), count: tile.count)
        return res
    }

    /// Orthonormal 8-point DCT-II matrix, C[i][j].
    static let dctMatrix: [Float] = {
        var c = [Float](repeating: 0, count: 64)
        for i in 0..<8 { for j in 0..<8 {
            c[i * 8 + j] = Float((i == 0 ? (1.0 / 8).squareRoot() : 0.5) * cos(Double.pi * Double(2 * j + 1) * Double(i) / 16))
        } }
        return c
    }()

    /// 8×8 Kaiser window (β = 2) used to weight the aggregation of the filtered blocks.
    static let kaiserWindow: [Float] = {
        func i0(_ x: Double) -> Double {
            var sum = 1.0, term = 1.0
            for k in 1...25 { term *= (x / 2 / Double(k)) * (x / 2 / Double(k)); sum += term }
            return sum
        }
        let beta = 2.0
        let w1 = (0..<8).map { i -> Double in
            let r = (2 * Double(i) - 7) / 7
            return i0(beta * (1 - r * r).squareRoot()) / i0(beta)
        }
        var k = [Float](repeating: 0, count: 64)
        for y in 0..<8 { for x in 0..<8 { k[y * 8 + x] = Float(w1[y] * w1[x]) } }
        return k
    }()

    // MARK: - Tiling

    /// Runs `body` on overlapping tiles and stitches the cores back together.
    static func tiled(_ y: PlanarImage, margin: Int, _ body: (PlanarImage, (Int, Int)) throws -> PlanarImage) rethrows -> PlanarImage {
        let w = y.width, h = y.height, t = max(tileSize, 64)
        if w <= t + 2 * margin && h <= t + 2 * margin { return try body(y, (0, 0)) }
        let out = PlanarImage(width: w, height: h)
        var y0 = 0
        while y0 < h {
            let y1 = min(y0 + t, h)
            var x0 = 0
            while x0 < w {
                let x1 = min(x0 + t, w)
                let px0 = max(x0 - margin, 0), py0 = max(y0 - margin, 0)
                let px1 = min(x1 + margin, w), py1 = min(y1 + margin, h)
                let tile = y.cropped(x: px0, y: py0, width: px1 - px0, height: py1 - py0)
                let done = try body(tile, (px0, py0))
                for yy in y0..<y1 {
                    let src = done.pointer + (yy - py0) * done.width + (x0 - px0)
                    (out.pointer + yy * w + x0).update(from: src, count: x1 - x0)
                }
                x0 = x1
            }
            y0 = y1
        }
        return out
    }
}

struct NLMParams { var width, height: UInt32; var search, patch: Int32; var h2, noiseFloor: Float }
struct BMParams { var width, height, nRefX, nRefY: UInt32; var step, search, phaseX, phaseY: Int32; var tau, threshold, sigma2: Float }

/// Shared Metal device, queue and the compiled pipelines.
final class MetalContext {
    static let shared: MetalContext? = MetalContext()
    let device: MTLDevice
    let queue: MTLCommandQueue
    private var pipelines: [String: MTLComputePipelineState] = [:]

    struct Failure: Error, CustomStringConvertible { let description: String }

    private init?() {
        guard let d = MTLCreateSystemDefaultDevice(), let q = d.makeCommandQueue() else { return nil }
        device = d; queue = q
        do {
            let lib = try d.makeLibrary(source: metalShaderSource, options: nil)
            for name in ["nlm", "bm3d_match", "bm3d_hard", "bm3d_wiener", "bm3d_divide"] {
                guard let f = lib.makeFunction(name: name) else { return nil }
                pipelines[name] = try d.makeComputePipelineState(function: f)
            }
        } catch {
            NSLog("Metal denoise unavailable: \(error)")
            return nil
        }
    }

    func pipeline(_ name: String) -> MTLComputePipelineState { pipelines[name]! }

    func buffer(_ p: PlanarImage) throws -> MTLBuffer {
        guard let b = device.makeBuffer(bytes: p.pointer, length: p.count * 4, options: .storageModeShared) else { throw Failure(description: "buffer") }
        return b
    }
    func buffer(floats: [Float]) throws -> MTLBuffer {
        guard let b = device.makeBuffer(bytes: floats, length: floats.count * 4, options: .storageModeShared) else { throw Failure(description: "buffer") }
        return b
    }
    func buffer(count: Int) throws -> MTLBuffer {
        guard let b = device.makeBuffer(length: max(count, 1) * 4, options: .storageModeShared) else { throw Failure(description: "buffer") }
        return b
    }

    /// Encodes `body` into one command buffer and waits for it.
    func run(_ body: (MTLComputeCommandEncoder) throws -> Void) throws {
        guard let cb = queue.makeCommandBuffer(), let enc = cb.makeComputeCommandEncoder() else { throw Failure(description: "command buffer") }
        try body(enc)
        enc.endEncoding()
        cb.commit()
        cb.waitUntilCompleted()
        if let e = cb.error { throw Failure(description: "GPU: \(e.localizedDescription)") }
    }
}

extension MTLComputeCommandEncoder {
    /// Zeroes `count` floats of a shared buffer. Called while encoding, before the command buffer
    /// is committed, so the GPU sees the cleared memory.
    func fill(_ buffer: MTLBuffer, count: Int) { memset(buffer.contents(), 0, count * 4) }
}

let metalShaderSource = """
#include <metal_stdlib>
using namespace metal;

struct NLMParams { uint width, height; int search, patch; float h2, noiseFloor; };
struct BMParams { uint width, height, nRefX, nRefY; int step, search, phaseX, phaseY; float tau, threshold, sigma2; };

#define N 8
#define NN 64
#define KMAX 16

// ---------------------------------------------------------------- non-local means
kernel void nlm(device const float* img [[buffer(0)]], device float* out [[buffer(1)]],
                constant NLMParams& p [[buffer(2)]], uint2 gid [[thread_position_in_grid]]) {
    if (gid.x >= p.width || gid.y >= p.height) return;
    const int W = p.width, H = p.height, S = p.search, P = p.patch;
    const int x = gid.x, y = gid.y;
    const float patchN = float((2 * P + 1) * (2 * P + 1));
    float acc = img[y * W + x], wsum = 1.0f;
    for (int dy = -S; dy <= S; dy++) {
        const int sy = y + dy;
        if (sy < 0 || sy >= H) continue;
        for (int dx = -S; dx <= S; dx++) {
            const int sx = x + dx;
            if (sx < 0 || sx >= W || (dx == 0 && dy == 0)) continue;
            float d = 0;
            for (int j = -P; j <= P; j++) {
                const int ay = clamp(y + j, 0, H - 1), by = clamp(ay + dy, 0, H - 1);
                for (int i = -P; i <= P; i++) {
                    const int ax = clamp(x + i, 0, W - 1), bx = clamp(ax + dx, 0, W - 1);
                    const float t = img[ay * W + ax] - img[by * W + bx];
                    d += t * t;
                }
            }
            const float d2 = max(d / patchN - p.noiseFloor, 0.0f);
            const float w = exp(-d2 / p.h2);
            acc += w * img[sy * W + sx]; wsum += w;
        }
    }
    out[y * W + x] = acc / wsum;
}

// ---------------------------------------------------------------- BM3D: block matching
// One thread per reference block (grid step `step`); keeps the KMAX closest blocks of the window.
kernel void bm3d_match(device const float* img [[buffer(0)]], device uint* matches [[buffer(1)]],
                       device uint* counts [[buffer(2)]], constant BMParams& p [[buffer(3)]],
                       uint2 gid [[thread_position_in_grid]]) {
    if (gid.x >= p.nRefX || gid.y >= p.nRefY) return;
    const int W = p.width, H = p.height;
    const int rx = min(p.phaseX + int(gid.x) * p.step, W - N), ry = min(p.phaseY + int(gid.y) * p.step, H - N);
    float ref[NN];
    for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) ref[j * N + i] = img[(ry + j) * W + rx + i];
    float bestD[KMAX]; uint bestP[KMAX];
    for (int k = 0; k < KMAX; k++) { bestD[k] = INFINITY; bestP[k] = 0; }
    const int x0 = max(rx - p.search, 0), x1 = min(rx + p.search, W - N);
    const int y0 = max(ry - p.search, 0), y1 = min(ry + p.search, H - N);
    for (int cy = y0; cy <= y1; cy++) {
        for (int cx = x0; cx <= x1; cx++) {
            const float limit = min(bestD[KMAX - 1], p.tau);
            float d = 0;
            for (int j = 0; j < N && d < limit; j++) {
                device const float* row = img + (cy + j) * W + cx;
                for (int i = 0; i < N; i++) { const float t = ref[j * N + i] - row[i]; d += t * t; }
            }
            if (d >= limit) continue;
            int k = KMAX - 1;
            while (k > 0 && bestD[k - 1] > d) { bestD[k] = bestD[k - 1]; bestP[k] = bestP[k - 1]; k--; }
            bestD[k] = d; bestP[k] = uint(cx) | (uint(cy) << 16);
        }
    }
    uint n = 0;
    for (int k = 0; k < KMAX; k++) if (bestD[k] < INFINITY) n++;
    const uint ri = gid.y * p.nRefX + gid.x;
    for (int k = 0; k < KMAX; k++) matches[ri * KMAX + k] = bestP[k];
    counts[ri] = n;
}

// ---------------------------------------------------------------- BM3D: transforms
// 1-D DCT of every row (stride 1) or column (stride N) of the K blocks held in threadgroup memory.
static void dct_pass(threadgroup float* g, uint K, uint t, bool columns, bool inverse, constant float* C) {
    for (uint id = t; id < K * N; id += NN) {
        const uint k = id / N, r = id % N;
        threadgroup float* v = g + k * NN + (columns ? r : r * N);
        const uint s = columns ? N : 1;
        float x[N];
        for (int i = 0; i < N; i++) x[i] = v[i * s];
        for (int i = 0; i < N; i++) {
            float sum = 0;
            for (int j = 0; j < N; j++) sum += (inverse ? C[j * N + i] : C[i * N + j]) * x[j];
            v[i * s] = sum;
        }
    }
}

static void dct2d(threadgroup float* g, uint K, uint t, bool inverse, constant float* C) {
    if (inverse) {
        dct_pass(g, K, t, true, true, C); threadgroup_barrier(mem_flags::mem_threadgroup);
        dct_pass(g, K, t, false, true, C); threadgroup_barrier(mem_flags::mem_threadgroup);
    } else {
        dct_pass(g, K, t, false, false, C); threadgroup_barrier(mem_flags::mem_threadgroup);
        dct_pass(g, K, t, true, false, C); threadgroup_barrier(mem_flags::mem_threadgroup);
    }
}

// Orthonormal Haar transform along the stack (K is a power of two).
static void haar(thread float* v, uint K, bool inverse) {
    const float r = 0.70710678f;
    float tmp[KMAX];
    if (!inverse) {
        for (uint len = K; len > 1; len >>= 1) {
            const uint hf = len / 2;
            for (uint i = 0; i < hf; i++) { tmp[i] = (v[2 * i] + v[2 * i + 1]) * r; tmp[hf + i] = (v[2 * i] - v[2 * i + 1]) * r; }
            for (uint i = 0; i < len; i++) v[i] = tmp[i];
        }
    } else {
        for (uint len = 2; len <= K; len <<= 1) {
            const uint hf = len / 2;
            for (uint i = 0; i < hf; i++) { tmp[2 * i] = (v[i] + v[hf + i]) * r; tmp[2 * i + 1] = (v[i] - v[hf + i]) * r; }
            for (uint i = 0; i < len; i++) v[i] = tmp[i];
        }
    }
}

static uint load_group(threadgroup float* g, device const float* img, device const uint* matches, uint count, uint W, uint t) {
    const uint K = count == 0 ? 0 : (1u << (31 - clz(count)));
    for (uint k = 0; k < K; k++) {
        const uint pos = matches[k];
        const uint x = pos & 0xffffu, y = pos >> 16;
        g[k * NN + t] = img[(y + t / N) * W + x + t % N];
    }
    return K;
}

static void aggregate(threadgroup const float* g, device atomic_float* num, device atomic_float* den,
                      device const uint* matches, uint K, float weight, uint W, uint t, constant float* kaiser) {
    const float wk = weight * kaiser[t];
    for (uint k = 0; k < K; k++) {
        const uint pos = matches[k];
        const uint idx = ((pos >> 16) + t / N) * W + (pos & 0xffffu) + t % N;
        atomic_fetch_add_explicit(num + idx, wk * g[k * NN + t], memory_order_relaxed);
        atomic_fetch_add_explicit(den + idx, wk, memory_order_relaxed);
    }
}

// ---------------------------------------------------------------- BM3D: hard thresholding stage
kernel void bm3d_hard(device const float* noisy [[buffer(0)]], device const float* basic [[buffer(1)]],
                      device const uint* matches [[buffer(2)]], device const uint* counts [[buffer(3)]],
                      device atomic_float* num [[buffer(4)]], device atomic_float* den [[buffer(5)]],
                      constant BMParams& p [[buffer(6)]], constant float* C [[buffer(7)]], constant float* kaiser [[buffer(8)]],
                      constant float* sig [[buffer(9)]],
                      uint2 tg [[threadgroup_position_in_grid]], uint t [[thread_index_in_threadgroup]]) {
    threadgroup float g[KMAX * NN];
    threadgroup atomic_uint nzTotal;
    const uint ri = tg.y * p.nRefX + tg.x;
    device const uint* m = matches + ri * KMAX;
    const uint K = load_group(g, noisy, m, counts[ri], p.width, t);
    if (t == 0) atomic_store_explicit(&nzTotal, 0u, memory_order_relaxed);
    threadgroup_barrier(mem_flags::mem_threadgroup);
    if (K == 0) return;
    dct2d(g, K, t, false, C);
    float v[KMAX];
    for (uint k = 0; k < K; k++) v[k] = g[k * NN + t];
    haar(v, K, false);
    uint nz = 0;
    for (uint k = 0; k < K; k++) {
        if (t == 0 && k == 0) { nz++; continue; }          // keep the DC of the group
        if (fabs(v[k]) < p.threshold * sig[t]) v[k] = 0; else nz++;
    }
    atomic_fetch_add_explicit(&nzTotal, nz, memory_order_relaxed);
    haar(v, K, true);
    for (uint k = 0; k < K; k++) g[k * NN + t] = v[k];
    threadgroup_barrier(mem_flags::mem_threadgroup);
    const uint total = atomic_load_explicit(&nzTotal, memory_order_relaxed);
    const float weight = total > 0 ? 1.0f / (p.sigma2 * float(total)) : 1.0f;
    dct2d(g, K, t, true, C);
    aggregate(g, num, den, m, K, weight, p.width, t, kaiser);
}

// ---------------------------------------------------------------- BM3D: Wiener stage
kernel void bm3d_wiener(device const float* noisy [[buffer(0)]], device const float* basic [[buffer(1)]],
                        device const uint* matches [[buffer(2)]], device const uint* counts [[buffer(3)]],
                        device atomic_float* num [[buffer(4)]], device atomic_float* den [[buffer(5)]],
                        constant BMParams& p [[buffer(6)]], constant float* C [[buffer(7)]], constant float* kaiser [[buffer(8)]],
                        constant float* sig [[buffer(9)]],
                        uint2 tg [[threadgroup_position_in_grid]], uint t [[thread_index_in_threadgroup]]) {
    threadgroup float gN[KMAX * NN];
    threadgroup float gB[KMAX * NN];
    threadgroup float wsum[NN];
    const uint ri = tg.y * p.nRefX + tg.x;
    device const uint* m = matches + ri * KMAX;
    const uint K = load_group(gN, noisy, m, counts[ri], p.width, t);
    load_group(gB, basic, m, counts[ri], p.width, t);
    threadgroup_barrier(mem_flags::mem_threadgroup);
    if (K == 0) return;
    dct2d(gN, K, t, false, C);
    dct2d(gB, K, t, false, C);
    float vn[KMAX], vb[KMAX];
    for (uint k = 0; k < K; k++) { vn[k] = gN[k * NN + t]; vb[k] = gB[k * NN + t]; }
    haar(vn, K, false); haar(vb, K, false);
    float w2 = 0;
    const float s2 = sig[t] * sig[t];
    for (uint k = 0; k < K; k++) {
        const float b2 = vb[k] * vb[k];
        const float w = b2 / (b2 + s2);
        vn[k] *= w; w2 += w * w;
    }
    wsum[t] = w2;
    haar(vn, K, true);
    for (uint k = 0; k < K; k++) gN[k * NN + t] = vn[k];
    threadgroup_barrier(mem_flags::mem_threadgroup);
    float total = 0;
    for (uint i = 0; i < NN; i++) total += wsum[i];
    const float weight = total > 0 ? 1.0f / (p.sigma2 * total) : 1.0f;
    dct2d(gN, K, t, true, C);
    aggregate(gN, num, den, m, K, weight, p.width, t, kaiser);
}

// ---------------------------------------------------------------- BM3D: normalisation
kernel void bm3d_divide(device const float* num [[buffer(0)]], device const float* den [[buffer(1)]],
                        device const float* fallback [[buffer(2)]], device float* out [[buffer(3)]],
                        constant BMParams& p [[buffer(4)]], uint2 gid [[thread_position_in_grid]]) {
    if (gid.x >= p.width || gid.y >= p.height) return;
    const uint i = gid.y * p.width + gid.x;
    out[i] = den[i] > 0 ? num[i] / den[i] : fallback[i];
}
"""
