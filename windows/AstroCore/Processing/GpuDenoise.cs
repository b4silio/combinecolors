using System.Runtime.InteropServices;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AstroCore;

/// <summary>
/// GPU (Direct3D 11 compute shader) implementations of the patch-based noise reduction methods:
/// non-local means and BM3D. The kernels are the Metal shaders of the macOS build translated to
/// HLSL (shader model 5) and compiled from source at first use; float atomics, which SM5 lacks, are
/// emulated with a compare-exchange loop. Without a usable GPU the callers fall back to the CPU paths.
///
/// Large images are processed in tiles (see <see cref="Bm3dDenoise.TileSize"/>) so GPU buffers stay
/// bounded; the margins are wide enough that tiling does not change the result.
/// </summary>
public static class GpuDenoise
{
    /// <summary>Set to false to force the CPU paths (tests, benchmarks).</summary>
    public static bool Enabled = true;

    public static bool IsAvailable => Enabled && GpuContext.Shared != null;
    public static string? DeviceName => GpuContext.Shared?.DeviceName;
    /// <summary>Why the GPU could not be used (null when it can).</summary>
    public static string? UnavailableReason => GpuContext.Shared != null ? null : GpuContext.LastError ?? "no Direct3D 11 device";

    const int N = 8, NN = 64, Step = 3, KMax = 16;

    [StructLayout(LayoutKind.Sequential)]
    struct NlmParams { public uint Width, Height; public int Search, Patch; public float H2, NoiseFloor; public float Pad0, Pad1; }

    [StructLayout(LayoutKind.Sequential)]
    struct BmParams { public uint Width, Height, NRefX, NRefY; public int Step, Search, PhaseX, PhaseY; public float Tau, Threshold, Sigma2, Pad0; }

    // ---- Non-local means ---------------------------------------------------------------------------

    /// <summary>Same definition as <see cref="Denoise.NonLocalMeans"/> (patch radius 2, edges extended).</summary>
    internal static PlanarImage? NonLocalMeans(PlanarImage y, float sigma, float h, int search)
    {
        if (!Enabled || GpuContext.Shared is not GpuContext ctx) return null;
        try
        {
            return Bm3dDenoise.Tiled(y, search + 3, (tile, _, _) =>
            {
                lock (ctx.Gate)
                {
                    var p = new NlmParams { Width = (uint)tile.Width, Height = (uint)tile.Height, Search = search, Patch = 2, H2 = MathF.Max(h * h, 1e-12f), NoiseFloor = 2 * sigma * sigma };
                    using var src = ctx.Buffer(tile.Data);
                    using var dst = ctx.Buffer(tile.Count);
                    using var cb = ctx.Constants(p);
                    ctx.Run("nlm", cb, [src, dst], (tile.Width + 15) / 16, (tile.Height + 15) / 16);
                    var out_ = PlanarImage.Uninitialized(tile.Width, tile.Height);
                    ctx.Read(dst, out_.Data);
                    return out_;
                }
            });
        }
        catch (Exception e) { GpuContext.Fail(e); return null; }
    }

    // ---- BM3D ----------------------------------------------------------------------------------------

    /// <summary>BM3D on a luminance plane (see <see cref="Bm3dDenoise.Bm3d"/> for the parameters). Null without a GPU.</summary>
    internal static PlanarImage? Bm3d(PlanarImage y, float sigma, float amount, int search)
    {
        if (!Enabled || GpuContext.Shared is not GpuContext ctx || sigma <= 0 || amount <= 0) return null;
        var spectrum = Bm3dDenoise.CoefficientNoise(y);
        if (spectrum.Length == 0 || spectrum.All(s => s <= 0)) { spectrum = new float[NN]; Array.Fill(spectrum, sigma); }
        var noise = spectrum.Select(s => s * amount).ToArray();
        try
        {
            return Bm3dDenoise.Tiled(y, 4 * (search + N), (tile, ox, oy) => { lock (ctx.Gate) return Bm3dTile(ctx, tile, ox, oy, noise, search); });
        }
        catch (Exception e) { GpuContext.Fail(e); return null; }
    }

    static PlanarImage Bm3dTile(GpuContext ctx, PlanarImage tile, int ox, int oy, float[] noise, int search)
    {
        int w = tile.Width, h = tile.Height;
        if (w < N || h < N) return tile.Copy();
        int phaseX = (Step - ox % Step) % Step, phaseY = (Step - oy % Step) % Step;
        int nRefX = (w - N - phaseX + Step - 1) / Step + 1, nRefY = (h - N - phaseY + Step - 1) / Step + 1;
        int nRef = nRefX * nRefY;
        using var noisy = ctx.Buffer(tile.Data);
        using var basic = ctx.Buffer(tile.Count); using var out_ = ctx.Buffer(tile.Count);
        using var num = ctx.Buffer(tile.Count); using var den = ctx.Buffer(tile.Count);
        using var matches = ctx.Buffer(nRef * KMax); using var counts = ctx.Buffer(nRef);
        using var dct = ctx.Buffer(Bm3dDenoise.DctMatrix); using var kaiser = ctx.Buffer(Bm3dDenoise.KaiserWindow);
        using var spectrum = ctx.Buffer(noise);
        // Mean per-pixel noise variance (orthonormal transform: the mean of the coefficient variances).
        float s2 = noise.Sum(v => v * v) / noise.Length;
        var p = new BmParams
        {
            Width = (uint)w, Height = (uint)h, NRefX = (uint)nRefX, NRefY = (uint)nRefY, Step = Step, Search = search, PhaseX = phaseX, PhaseY = phaseY,
            // Per-pixel mean squared distance thresholds for a block to join a group (noise alone gives 2σ²).
            Tau = 10 * s2 * NN, Threshold = 2.7f, Sigma2 = s2,
        };
        var zeros = new float[tile.Count];
        void Stage(string filter, GpuBuffer matchOn, GpuBuffer fallback, GpuBuffer result)
        {
            using var cb = ctx.Constants(p);
            ctx.Write(num, zeros); ctx.Write(den, zeros);
            ctx.Run("bm3d_match", cb, [matchOn, matches, counts], (nRefX + 7) / 8, (nRefY + 7) / 8);
            ctx.Run(filter, cb, [noisy, basic, matches, counts, num, den, dct, kaiser, spectrum], nRefX, nRefY);
            ctx.Run("bm3d_divide", cb, [num, den, fallback, result], (w + 15) / 16, (h + 15) / 16);
        }
        Stage("bm3d_hard", noisy, noisy, basic);
        if (Bm3dDenoise.WienerStage)
        {
            p.Tau = 3 * s2 * NN;
            Stage("bm3d_wiener", basic, basic, out_);
        }
        var res = PlanarImage.Uninitialized(w, h);
        ctx.Read(Bm3dDenoise.WienerStage ? out_ : basic, res.Data);
        return res;
    }

    // ---- Shaders -----------------------------------------------------------------------------------

    internal const string ShaderSource = """
cbuffer NLMParams : register(b0) { uint nlm_width; uint nlm_height; int nlm_search; int nlm_patch; float nlm_h2; float nlm_noiseFloor; float nlm_pad0; float nlm_pad1; };
cbuffer BMParams : register(b0) { uint bm_width; uint bm_height; uint bm_nRefX; uint bm_nRefY; int bm_step; int bm_search; int bm_phaseX; int bm_phaseY; float bm_tau; float bm_threshold; float bm_sigma2; float bm_pad0; };

#define N 8
#define NN 64
#define KMAX 16

// ---------------------------------------------------------------- non-local means
RWStructuredBuffer<float> nlm_img : register(u0);
RWStructuredBuffer<float> nlm_out : register(u1);

[numthreads(16, 16, 1)]
void nlm(uint3 gid : SV_DispatchThreadID) {
    if (gid.x >= nlm_width || gid.y >= nlm_height) return;
    const int W = nlm_width, H = nlm_height, S = nlm_search, P = nlm_patch;
    const int x = gid.x, y = gid.y;
    const float patchN = float((2 * P + 1) * (2 * P + 1));
    float acc = nlm_img[y * W + x], wsum = 1.0f;
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
                    const float t = nlm_img[ay * W + ax] - nlm_img[by * W + bx];
                    d += t * t;
                }
            }
            const float d2 = max(d / patchN - nlm_noiseFloor, 0.0f);
            const float w = exp(-d2 / nlm_h2);
            acc += w * nlm_img[sy * W + sx]; wsum += w;
        }
    }
    nlm_out[y * W + x] = acc / wsum;
}

// ---------------------------------------------------------------- BM3D: block matching
// One thread per reference block (grid step `step`); keeps the KMAX closest blocks of the window.
RWStructuredBuffer<float> match_img : register(u0);
RWStructuredBuffer<uint> match_matches : register(u1);
RWStructuredBuffer<uint> match_counts : register(u2);

[numthreads(8, 8, 1)]
void bm3d_match(uint3 gid : SV_DispatchThreadID) {
    if (gid.x >= bm_nRefX || gid.y >= bm_nRefY) return;
    const int W = bm_width, H = bm_height;
    const int rx = min(bm_phaseX + int(gid.x) * bm_step, W - N), ry = min(bm_phaseY + int(gid.y) * bm_step, H - N);
    float refb[NN];
    for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) refb[j * N + i] = match_img[(ry + j) * W + rx + i];
    float bestD[KMAX]; uint bestP[KMAX];
    for (int k = 0; k < KMAX; k++) { bestD[k] = 3.402823466e+38f; bestP[k] = 0; }
    const int x0 = max(rx - bm_search, 0), x1 = min(rx + bm_search, W - N);
    const int y0 = max(ry - bm_search, 0), y1 = min(ry + bm_search, H - N);
    for (int cy = y0; cy <= y1; cy++) {
        for (int cx = x0; cx <= x1; cx++) {
            const float limit = min(bestD[KMAX - 1], bm_tau);
            float d = 0;
            for (int j = 0; j < N && d < limit; j++) {
                const int row = (cy + j) * W + cx;
                for (int i = 0; i < N; i++) { const float t = refb[j * N + i] - match_img[row + i]; d += t * t; }
            }
            if (d >= limit) continue;
            int k = KMAX - 1;
            while (k > 0 && bestD[k - 1] > d) { bestD[k] = bestD[k - 1]; bestP[k] = bestP[k - 1]; k--; }
            bestD[k] = d; bestP[k] = uint(cx) | (uint(cy) << 16);
        }
    }
    uint n = 0;
    for (int k2 = 0; k2 < KMAX; k2++) if (bestD[k2] < 3.402823466e+38f) n++;
    const uint ri = gid.y * bm_nRefX + gid.x;
    for (int k3 = 0; k3 < KMAX; k3++) match_matches[ri * KMAX + k3] = bestP[k3];
    match_counts[ri] = n;
}

// ---------------------------------------------------------------- BM3D: transforms
RWStructuredBuffer<float> f_noisy : register(u0);
RWStructuredBuffer<float> f_basic : register(u1);
RWStructuredBuffer<uint> f_matches : register(u2);
RWStructuredBuffer<uint> f_counts : register(u3);
RWStructuredBuffer<uint> f_num : register(u4);
RWStructuredBuffer<uint> f_den : register(u5);
RWStructuredBuffer<float> f_C : register(u6);
RWStructuredBuffer<float> f_kaiser : register(u7);
RWStructuredBuffer<float> f_sig : register(u8);

groupshared float g[KMAX * NN];
groupshared float gB[KMAX * NN];
groupshared float wsum[NN];
groupshared uint nzTotal;

// 1-D DCT of every row (stride 1) or column (stride N) of the K blocks held in group memory.
void dct_pass(uint K, uint t, bool columns, bool inverse, bool second) {
    for (uint id = t; id < K * N; id += NN) {
        const uint k = id / N, r = id % N;
        const uint base = k * NN + (columns ? r : r * N);
        const uint s = columns ? N : 1;
        float x[N];
        for (int i = 0; i < N; i++) x[i] = second ? gB[base + i * s] : g[base + i * s];
        for (int i2 = 0; i2 < N; i2++) {
            float sum = 0;
            for (int j = 0; j < N; j++) sum += (inverse ? f_C[j * N + i2] : f_C[i2 * N + j]) * x[j];
            if (second) gB[base + i2 * s] = sum; else g[base + i2 * s] = sum;
        }
    }
}

void dct2d(uint K, uint t, bool inverse, bool second) {
    if (inverse) {
        dct_pass(K, t, true, true, second); GroupMemoryBarrierWithGroupSync();
        dct_pass(K, t, false, true, second); GroupMemoryBarrierWithGroupSync();
    } else {
        dct_pass(K, t, false, false, second); GroupMemoryBarrierWithGroupSync();
        dct_pass(K, t, true, false, second); GroupMemoryBarrierWithGroupSync();
    }
}

// Orthonormal Haar transform along the stack (K is a power of two).
void haar(inout float v[KMAX], uint K, bool inverse) {
    const float r = 0.70710678f;
    float tmp[KMAX];
    if (!inverse) {
        for (uint len = K; len > 1; len >>= 1) {
            const uint hf = len / 2;
            for (uint i = 0; i < hf; i++) { tmp[i] = (v[2 * i] + v[2 * i + 1]) * r; tmp[hf + i] = (v[2 * i] - v[2 * i + 1]) * r; }
            for (uint i2 = 0; i2 < len; i2++) v[i2] = tmp[i2];
        }
    } else {
        for (uint len = 2; len <= K; len <<= 1) {
            const uint hf = len / 2;
            for (uint i = 0; i < hf; i++) { tmp[2 * i] = (v[i] + v[hf + i]) * r; tmp[2 * i + 1] = (v[i] - v[hf + i]) * r; }
            for (uint i2 = 0; i2 < len; i2++) v[i2] = tmp[i2];
        }
    }
}

uint group_size(uint count) { return count == 0 ? 0 : (1u << firstbithigh(count)); }

void load_group(bool second, bool fromBasic, uint mOff, uint K, uint W, uint t) {
    for (uint k = 0; k < K; k++) {
        const uint pos = f_matches[mOff + k];
        const uint x = pos & 0xffffu, y = pos >> 16;
        const uint idx = (y + t / N) * W + x + t % N;
        const float v = fromBasic ? f_basic[idx] : f_noisy[idx];
        if (second) gB[k * NN + t] = v; else g[k * NN + t] = v;
    }
}

void atomic_add_float(RWStructuredBuffer<uint> buf, uint idx, float v) {
    uint old = buf[idx];
    uint assumed;
    [allow_uav_condition]
    do {
        assumed = old;
        const float f = asfloat(assumed) + v;
        InterlockedCompareExchange(buf[idx], assumed, asuint(f), old);
    } while (old != assumed);
}

void aggregate(uint mOff, uint K, float weight, uint W, uint t) {
    const float wk = weight * f_kaiser[t];
    for (uint k = 0; k < K; k++) {
        const uint pos = f_matches[mOff + k];
        const uint idx = ((pos >> 16) + t / N) * W + (pos & 0xffffu) + t % N;
        atomic_add_float(f_num, idx, wk * g[k * NN + t]);
        atomic_add_float(f_den, idx, wk);
    }
}

// ---------------------------------------------------------------- BM3D: hard thresholding stage
[numthreads(64, 1, 1)]
void bm3d_hard(uint3 tg : SV_GroupID, uint t : SV_GroupIndex) {
    const uint ri = tg.y * bm_nRefX + tg.x;
    const uint mOff = ri * KMAX;
    const uint K = group_size(f_counts[ri]);
    load_group(false, false, mOff, K, bm_width, t);
    if (t == 0) nzTotal = 0;
    GroupMemoryBarrierWithGroupSync();
    // (an empty group just runs every loop zero times; fxc forbids a barrier after a data-dependent return)
    dct2d(K, t, false, false);
    float v[KMAX];
    for (uint k = 0; k < K; k++) v[k] = g[k * NN + t];
    haar(v, K, false);
    uint nz = 0;
    for (uint k2 = 0; k2 < K; k2++) {
        if (t == 0 && k2 == 0) { nz++; continue; }          // keep the DC of the group
        if (abs(v[k2]) < bm_threshold * f_sig[t]) v[k2] = 0; else nz++;
    }
    InterlockedAdd(nzTotal, nz);
    haar(v, K, true);
    for (uint k3 = 0; k3 < K; k3++) g[k3 * NN + t] = v[k3];
    GroupMemoryBarrierWithGroupSync();
    const uint total = nzTotal;
    const float weight = total > 0 ? 1.0f / (bm_sigma2 * float(total)) : 1.0f;
    dct2d(K, t, true, false);
    aggregate(mOff, K, weight, bm_width, t);
}

// ---------------------------------------------------------------- BM3D: Wiener stage
[numthreads(64, 1, 1)]
void bm3d_wiener(uint3 tg : SV_GroupID, uint t : SV_GroupIndex) {
    const uint ri = tg.y * bm_nRefX + tg.x;
    const uint mOff = ri * KMAX;
    const uint K = group_size(f_counts[ri]);
    load_group(false, false, mOff, K, bm_width, t);
    load_group(true, true, mOff, K, bm_width, t);
    GroupMemoryBarrierWithGroupSync();
    // (an empty group just runs every loop zero times; fxc forbids a barrier after a data-dependent return)
    dct2d(K, t, false, false);
    dct2d(K, t, false, true);
    float vn[KMAX], vb[KMAX];
    for (uint k = 0; k < K; k++) { vn[k] = g[k * NN + t]; vb[k] = gB[k * NN + t]; }
    haar(vn, K, false); haar(vb, K, false);
    float w2 = 0;
    const float s2 = f_sig[t] * f_sig[t];
    for (uint k2 = 0; k2 < K; k2++) {
        const float b2 = vb[k2] * vb[k2];
        const float w = b2 / (b2 + s2);
        vn[k2] *= w; w2 += w * w;
    }
    wsum[t] = w2;
    haar(vn, K, true);
    for (uint k3 = 0; k3 < K; k3++) g[k3 * NN + t] = vn[k3];
    GroupMemoryBarrierWithGroupSync();
    float total = 0;
    for (uint i = 0; i < NN; i++) total += wsum[i];
    const float weight = total > 0 ? 1.0f / (bm_sigma2 * total) : 1.0f;
    dct2d(K, t, true, false);
    aggregate(mOff, K, weight, bm_width, t);
}

// ---------------------------------------------------------------- BM3D: normalisation
RWStructuredBuffer<uint> d_num : register(u0);
RWStructuredBuffer<uint> d_den : register(u1);
RWStructuredBuffer<float> d_fallback : register(u2);
RWStructuredBuffer<float> d_out : register(u3);

[numthreads(16, 16, 1)]
void bm3d_divide(uint3 gid : SV_DispatchThreadID) {
    if (gid.x >= bm_width || gid.y >= bm_height) return;
    const uint i = gid.y * bm_width + gid.x;
    const float den = asfloat(d_den[i]);
    d_out[i] = den > 0 ? asfloat(d_num[i]) / den : d_fallback[i];
}
""";
}

/// <summary>A structured buffer of 32-bit elements on the GPU.</summary>
sealed class GpuBuffer : IDisposable
{
    public readonly ID3D11Buffer Buffer;
    public readonly ID3D11UnorderedAccessView Uav;
    public readonly int Count;
    public GpuBuffer(ID3D11Buffer buffer, ID3D11UnorderedAccessView uav, int count) { Buffer = buffer; Uav = uav; Count = count; }
    public void Dispose() { Uav.Dispose(); Buffer.Dispose(); }
}

/// <summary>Shared Direct3D 11 device, immediate context and the compiled compute shaders.</summary>
sealed class GpuContext : IDisposable
{
    static readonly Lazy<GpuContext?> shared = new(Create);
    public static GpuContext? Shared => failed ? null : shared.Value;
    static volatile bool failed;
    public static string? LastError;

    public readonly object Gate = new();
    public readonly string DeviceName;
    readonly ID3D11Device device;
    readonly ID3D11DeviceContext context;
    readonly Dictionary<string, ID3D11ComputeShader> shaders = new();
    ID3D11Buffer? staging; int stagingCount;

    /// <summary>Disables the GPU for the rest of the session after a runtime failure (the CPU paths take over).</summary>
    public static void Fail(Exception e)
    {
        failed = true;
        LastError = e.Message;
        System.Diagnostics.Trace.WriteLine($"GPU denoise disabled: {e.Message}");
    }

    static GpuContext? Create()
    {
        try
        {
            IDXGIAdapter1? adapter = PickAdapter();
            FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];
            var result = D3D11.D3D11CreateDevice(adapter, adapter == null ? DriverType.Hardware : DriverType.Unknown, DeviceCreationFlags.None, levels, out ID3D11Device? dev, out ID3D11DeviceContext? ctx);
            if (result.Failure || dev == null || ctx == null) return null;
            var name = adapter?.Description1.Description ?? "GPU";
            adapter?.Dispose();
            var g = new GpuContext(dev, ctx, name);
            foreach (var entry in new[] { "nlm", "bm3d_match", "bm3d_hard", "bm3d_wiener", "bm3d_divide" })
            {
                var bytecode = Compiler.Compile(GpuDenoise.ShaderSource, entry, "denoise.hlsl", "cs_5_0", ShaderFlags.OptimizationLevel3);
                g.shaders[entry] = dev.CreateComputeShader(bytecode.Span);
            }
            return g;
        }
        catch (Exception e)
        {
            LastError = e.ToString();
            System.Diagnostics.Trace.WriteLine($"GPU denoise unavailable: {e.Message}");
            return null;
        }
    }

    /// <summary>The adapter with the most dedicated memory (the discrete GPU on a laptop with two).</summary>
    static IDXGIAdapter1? PickAdapter()
    {
        try
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            IDXGIAdapter1? best = null; ulong bestMem = 0;
            for (int i = 0; factory.EnumAdapters1((uint)i, out IDXGIAdapter1 a).Success; i++)
            {
                var d = a.Description1;
                if ((d.Flags & AdapterFlags.Software) != 0) { a.Dispose(); continue; }
                ulong mem = (ulong)d.DedicatedVideoMemory;
                if (best == null || mem > bestMem) { best?.Dispose(); best = a; bestMem = mem; }
                else a.Dispose();
            }
            return best;
        }
        catch { return null; }
    }

    GpuContext(ID3D11Device d, ID3D11DeviceContext c, string name) { device = d; context = c; DeviceName = name; }

    public GpuBuffer Buffer(int count)
    {
        int n = Math.Max(count, 1);
        var desc = new BufferDescription((uint)(n * 4), BindFlags.UnorderedAccess | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 4u);
        var buf = device.CreateBuffer(desc);
        var uav = device.CreateUnorderedAccessView(buf, new UnorderedAccessViewDescription(buf, Format.Unknown, 0u, (uint)n));
        return new GpuBuffer(buf, uav, n);
    }

    public GpuBuffer Buffer(float[] data)
    {
        var b = Buffer(data.Length);
        Write(b, data);
        return b;
    }

    public void Write(GpuBuffer b, float[] data) => context.UpdateSubresource<float>(data.AsSpan(0, Math.Min(data.Length, b.Count)), b.Buffer);

    public ID3D11Buffer Constants<T>(T value) where T : unmanaged
    {
        int size = (Marshal.SizeOf<T>() + 15) / 16 * 16;
        var desc = new BufferDescription((uint)size, BindFlags.ConstantBuffer, ResourceUsage.Default);
        var cb = device.CreateBuffer(desc);
        var bytes = new byte[size];
        MemoryMarshal.Write(bytes, in value);
        context.UpdateSubresource<byte>(bytes, cb);
        return cb;
    }

    /// <summary>Dispatches one shader with the given UAVs bound to u0… and the constants at b0.</summary>
    public void Run(string name, ID3D11Buffer constants, GpuBuffer[] buffers, int groupsX, int groupsY)
    {
        context.CSSetShader(shaders[name]);
        context.CSSetConstantBuffer(0, constants);
        var uavs = buffers.Select(b => b.Uav).ToArray();
        context.CSSetUnorderedAccessViews(0, uavs);
        context.Dispatch((uint)groupsX, (uint)groupsY, 1u);
        context.CSSetUnorderedAccessViews(0, new ID3D11UnorderedAccessView?[uavs.Length]);
    }

    /// <summary>Copies a buffer back to the CPU (synchronises with the GPU).</summary>
    public void Read(GpuBuffer b, float[] dst)
    {
        if (staging == null || stagingCount < b.Count)
        {
            staging?.Dispose();
            staging = device.CreateBuffer(new BufferDescription((uint)(b.Count * 4), BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, ResourceOptionFlags.BufferStructured, 4u));
            stagingCount = b.Count;
        }
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, b.Buffer, 0, new Vortice.Mathematics.Box(0, 0, 0, b.Count * 4, 1, 1));
        var mapped = context.Map(staging, MapMode.Read);
        try
        {
            unsafe { new ReadOnlySpan<float>((void*)mapped.DataPointer, b.Count).Slice(0, Math.Min(b.Count, dst.Length)).CopyTo(dst); }
        }
        finally { context.Unmap(staging, 0); }
    }

    public void Dispose()
    {
        foreach (var s in shaders.Values) s.Dispose();
        staging?.Dispose();
        context.Dispose();
        device.Dispose();
    }
}
