using System.Runtime.Intrinsics;

namespace AstroCore;

/// <summary>
/// BM3D (Dabov et al. 2007: block matching + collaborative 3-D transform filtering, hard
/// thresholding stage followed by the Wiener stage) on the CPU. This is a port of the Metal compute
/// shaders of the macOS build; the block matching uses 256-bit SIMD when available and the work is
/// spread over every core in bands of reference rows whose aggregation footprints never overlap.
///
/// Large images are processed in tiles (<see cref="TileSize"/> + margin) so buffers stay bounded;
/// the margin is wide enough that tiling does not change the result.
/// </summary>
public static class Bm3dDenoise
{
    /// <summary>Core tile size in pixels (tests shrink it to exercise the seams).</summary>
    public static int TileSize = 1536;
    /// <summary>Set to false to force the fallback (tests, benchmarks).</summary>
    public static bool Enabled = true;
    /// <summary>Runs the second (Wiener) stage of BM3D; false keeps the hard-thresholding estimate (diagnostics).</summary>
    internal static bool WienerStage = true;

    public static bool IsAvailable => Enabled;

    const int N = 8, NN = 64, Step = 3, KMax = 16;

    /// <summary>
    /// BM3D on a luminance plane. <paramref name="amount"/> scales the thresholds (1 = the canonical
    /// 2.7·σ hard threshold and σ² Wiener filter); <paramref name="search"/> is the block-matching
    /// window radius. The noise is measured per DCT coefficient (stacked / resampled data has
    /// correlated noise, which a single σ would under-threshold at low frequencies and leave as
    /// worm-like patterns); <paramref name="sigma"/> is the white-noise fallback. Null when disabled.
    /// </summary>
    public static PlanarImage? Bm3d(PlanarImage y, float sigma, float amount, int search)
    {
        if (!Enabled || sigma <= 0 || amount <= 0) return null;
        var spectrum = CoefficientNoise(y);
        if (spectrum.Length == 0 || spectrum.All(s => s <= 0)) { spectrum = new float[NN]; Array.Fill(spectrum, sigma); }
        var noise = spectrum.Select(s => s * amount).ToArray();
        // Each stage reaches 2·(search + block) pixels; the reference grid is phase-locked to the
        // image so tiles produce the same groups as a whole-image run.
        return Tiled(y, 4 * (search + N), (tile, ox, oy) => Bm3dTile(tile, ox, oy, noise, search));
    }

    /// <summary>
    /// Noise σ of each 8×8 DCT coefficient: MAD over sampled blocks of the high-pass image (the image
    /// minus its starlet approximation after three scales, a ~30 px low-pass that removes sky and
    /// nebula but keeps the noise at every block frequency). The DC term, lost in the high-pass,
    /// takes the σ of the lowest AC terms.
    /// </summary>
    public static float[] CoefficientNoise(PlanarImage y)
    {
        int w = y.Width, h = y.Height;
        if (w < N || h < N) return [];
        // A single dilated B3 pass is not a low-pass (it passes every multiple of 1/step), so
        // chain the steps as the starlet transform does.
        var hp = Denoise.Smooth(Denoise.Smooth(Denoise.Smooth(y, 1), 2), 4);
        var yp = y.Data; var sp = hp.Data;
        int stride = Math.Max(2, (int)Math.Ceiling(Math.Sqrt((double)w * h / 16384)));
        var xs = new List<int>(); for (int x = 0; x + N <= w; x += stride) xs.Add(x);
        var ys = new List<int>(); for (int yy = 0; yy + N <= h; yy += stride) ys.Add(yy);
        int nBlocks = xs.Count * ys.Count;
        if (nBlocks < 32) return [];
        var coeffs = new float[NN][];
        for (int k = 0; k < NN; k++) coeffs[k] = new float[nBlocks];
        var c = DctMatrix;
        int bi = 0;
        var blk = new float[NN]; var tmp = new float[NN];
        foreach (var by in ys)
        {
            foreach (var bx in xs)
            {
                for (int j = 0; j < N; j++) for (int i = 0; i < N; i++) { int idx = (by + j) * w + bx + i; blk[j * N + i] = yp[idx] - sp[idx]; }
                // rows then columns: X = C · x · Cᵀ
                for (int j = 0; j < N; j++) for (int u = 0; u < N; u++) { float a = 0; for (int i = 0; i < N; i++) a += c[u * N + i] * blk[j * N + i]; tmp[j * N + u] = a; }
                for (int u = 0; u < N; u++) for (int v = 0; v < N; v++) { float a = 0; for (int j = 0; j < N; j++) a += c[v * N + j] * tmp[j * N + u]; coeffs[v * N + u][bi] = MathF.Abs(a); }
                bi++;
            }
        }
        var sigma = new float[NN];
        Parallel.For(0, NN, k =>
        {
            var col = coeffs[k];
            Array.Sort(col);
            sigma[k] = Statistics.Percentile(col, 0.5) / 0.6745f;
        });
        sigma[0] = 0.5f * (sigma[1] + sigma[8]);
        return sigma;
    }

    sealed class Params
    {
        public int Width, Height, NRefX, NRefY, PhaseX, PhaseY, Search;
        public float Tau, Threshold, Sigma2;
    }

    static PlanarImage Bm3dTile(PlanarImage tile, int ox, int oy, float[] noise, int search)
    {
        int w = tile.Width, h = tile.Height;
        if (w < N || h < N) return tile.Copy();
        int phaseX = (Step - ox % Step) % Step, phaseY = (Step - oy % Step) % Step;
        int nRefX = (w - N - phaseX + Step - 1) / Step + 1, nRefY = (h - N - phaseY + Step - 1) / Step + 1;
        int nRef = nRefX * nRefY;
        var noisy = tile.Data;
        var basic = new float[tile.Count]; var out_ = new float[tile.Count];
        var num = new float[tile.Count]; var den = new float[tile.Count];
        var matches = new int[nRef * KMax]; var counts = new int[nRef];
        // Mean per-pixel noise variance (orthonormal transform: the mean of the coefficient variances).
        float s2 = noise.Sum(v => v * v) / noise.Length;
        var p = new Params
        {
            Width = w, Height = h, NRefX = nRefX, NRefY = nRefY, Search = search, PhaseX = phaseX, PhaseY = phaseY,
            // Per-pixel mean squared distance thresholds for a block to join a group (noise alone gives 2σ²).
            Tau = 10 * s2 * NN, Threshold = 2.7f, Sigma2 = s2,
        };
        Stage(p, matchOn: noisy, noisy, basic, result: basic, noise, wiener: false, num, den, matches, counts);
        if (WienerStage)
        {
            p.Tau = 3 * s2 * NN;
            Stage(p, matchOn: basic, noisy, basic, result: out_, noise, wiener: true, num, den, matches, counts);
        }
        return PlanarImage.Wrap(w, h, WienerStage ? out_ : basic);
    }

    static void Stage(Params p, float[] matchOn, float[] noisy, float[] basic, float[] result, float[] sig, bool wiener, float[] num, float[] den, int[] matches, int[] counts)
    {
        Array.Clear(num); Array.Clear(den);
        // Block matching: one task per reference block.
        Parallel.For(0, p.NRefY, ry => { for (int rx = 0; rx < p.NRefX; rx++) Match(p, matchOn, rx, ry, matches, counts); });
        // Filtering + aggregation in bands of reference rows: a band's footprint (its matches reach
        // ±search rows, plus the block) must not overlap the band two slots away, so even and odd
        // bands run in two rounds without any write conflicts.
        int bandRows = Math.Max(1, (2 * p.Search + N + Step - 1) / Step + 1);
        int bands = (p.NRefY + bandRows - 1) / bandRows;
        var fallback = wiener ? basic : noisy;
        for (int parity = 0; parity < 2; parity++)
        {
            var list = Enumerable.Range(0, bands).Where(b => b % 2 == parity).ToArray();
            Parallel.ForEach(list, band =>
            {
                var g = new float[KMax * NN]; var gB = wiener ? new float[KMax * NN] : null;
                var v = new float[KMax]; var vb = new float[KMax]; var tmp = new float[KMax];
                var wsum = new float[NN];
                int ry0 = band * bandRows, ry1 = Math.Min(p.NRefY, ry0 + bandRows);
                for (int ry = ry0; ry < ry1; ry++)
                    for (int rx = 0; rx < p.NRefX; rx++)
                    {
                        if (wiener) FilterWiener(p, noisy, basic, rx, ry, matches, counts, num, den, sig, g, gB!, v, vb, tmp, wsum);
                        else FilterHard(p, noisy, rx, ry, matches, counts, num, den, sig, g, v, tmp);
                    }
            });
        }
        // Normalisation.
        Parallel.For(0, p.Height, y =>
        {
            int row = y * p.Width;
            for (int x = 0; x < p.Width; x++) { int i = row + x; result[i] = den[i] > 0 ? num[i] / den[i] : fallback[i]; }
        });
    }

    /// <summary>Keeps the KMax closest blocks of the window for one reference block (grid step <see cref="Step"/>).</summary>
    static void Match(Params p, float[] img, int gx, int gy, int[] matches, int[] counts)
    {
        int W = p.Width, H = p.Height;
        int rx = Math.Min(p.PhaseX + gx * Step, W - N), ry = Math.Min(p.PhaseY + gy * Step, H - N);
        Span<float> bestD = stackalloc float[KMax]; Span<int> bestP = stackalloc int[KMax];
        for (int k = 0; k < KMax; k++) { bestD[k] = float.PositiveInfinity; bestP[k] = 0; }
        int x0 = Math.Max(rx - p.Search, 0), x1 = Math.Min(rx + p.Search, W - N);
        int y0 = Math.Max(ry - p.Search, 0), y1 = Math.Min(ry + p.Search, H - N);
        bool simd = Vector256.IsHardwareAccelerated;
        Span<Vector256<float>> refRows = stackalloc Vector256<float>[N];
        if (simd) for (int j = 0; j < N; j++) refRows[j] = Vector256.Create<float>(img.AsSpan((ry + j) * W + rx, N));
        for (int cy = y0; cy <= y1; cy++)
        {
            for (int cx = x0; cx <= x1; cx++)
            {
                float limit = MathF.Min(bestD[KMax - 1], p.Tau);
                float d = 0;
                if (simd)
                {
                    var acc = Vector256<float>.Zero;
                    int j = 0;
                    for (; j < N; j += 2)
                    {
                        var r0 = Vector256.Create<float>(img.AsSpan((cy + j) * W + cx, N)) - refRows[j];
                        var r1 = Vector256.Create<float>(img.AsSpan((cy + j + 1) * W + cx, N)) - refRows[j + 1];
                        acc += r0 * r0 + r1 * r1;
                        if (j == 2 && Vector256.Sum(acc) >= limit) { d = float.PositiveInfinity; break; }
                    }
                    if (d == 0) d = Vector256.Sum(acc);
                }
                else
                {
                    for (int j = 0; j < N && d < limit; j++)
                    {
                        int rr = (ry + j) * W + rx, cr = (cy + j) * W + cx;
                        for (int i = 0; i < N; i++) { float t = img[rr + i] - img[cr + i]; d += t * t; }
                    }
                }
                if (d >= limit) continue;
                int k = KMax - 1;
                while (k > 0 && bestD[k - 1] > d) { bestD[k] = bestD[k - 1]; bestP[k] = bestP[k - 1]; k--; }
                bestD[k] = d; bestP[k] = cx | (cy << 16);
            }
        }
        int n = 0;
        for (int k = 0; k < KMax; k++) if (bestD[k] < float.PositiveInfinity) n++;
        int ri = gy * p.NRefX + gx;
        for (int k = 0; k < KMax; k++) matches[ri * KMax + k] = bestP[k];
        counts[ri] = n;
    }

    static int LoadGroup(float[] g, float[] img, int[] matches, int mOff, int count, int W)
    {
        int K = count == 0 ? 0 : 1 << (31 - System.Numerics.BitOperations.LeadingZeroCount((uint)count));
        for (int k = 0; k < K; k++)
        {
            int pos = matches[mOff + k];
            int x = pos & 0xffff, y = pos >> 16;
            for (int j = 0; j < N; j++) Array.Copy(img, (y + j) * W + x, g, k * NN + j * N, N);
        }
        return K;
    }

    static void Aggregate(float[] g, float[] num, float[] den, int[] matches, int mOff, int K, float weight, int W)
    {
        var kaiser = KaiserWindow;
        for (int k = 0; k < K; k++)
        {
            int pos = matches[mOff + k];
            int x = pos & 0xffff, y = pos >> 16;
            for (int j = 0; j < N; j++)
            {
                int idx = (y + j) * W + x, gi = k * NN + j * N;
                for (int i = 0; i < N; i++)
                {
                    float wk = weight * kaiser[j * N + i];
                    num[idx + i] += wk * g[gi + i];
                    den[idx + i] += wk;
                }
            }
        }
    }

    static void FilterHard(Params p, float[] noisy, int gx, int gy, int[] matches, int[] counts, float[] num, float[] den, float[] sig, float[] g, float[] v, float[] tmp)
    {
        int ri = gy * p.NRefX + gx, mOff = ri * KMax;
        int K = LoadGroup(g, noisy, matches, mOff, counts[ri], p.Width);
        if (K == 0) return;
        for (int k = 0; k < K; k++) Dct8x8(g.AsSpan(k * NN, NN), false);
        int nzTotal = 0;
        for (int t = 0; t < NN; t++)
        {
            for (int k = 0; k < K; k++) v[k] = g[k * NN + t];
            Haar(v, K, false, tmp);
            float thr = p.Threshold * sig[t];
            for (int k = 0; k < K; k++)
            {
                if (t == 0 && k == 0) { nzTotal++; continue; }          // keep the DC of the group
                if (MathF.Abs(v[k]) < thr) v[k] = 0; else nzTotal++;
            }
            Haar(v, K, true, tmp);
            for (int k = 0; k < K; k++) g[k * NN + t] = v[k];
        }
        float weight = nzTotal > 0 ? 1f / (p.Sigma2 * nzTotal) : 1f;
        for (int k = 0; k < K; k++) Dct8x8(g.AsSpan(k * NN, NN), true);
        Aggregate(g, num, den, matches, mOff, K, weight, p.Width);
    }

    static void FilterWiener(Params p, float[] noisy, float[] basic, int gx, int gy, int[] matches, int[] counts, float[] num, float[] den, float[] sig,
                             float[] gN, float[] gB, float[] vn, float[] vb, float[] tmp, float[] wsum)
    {
        int ri = gy * p.NRefX + gx, mOff = ri * KMax;
        int K = LoadGroup(gN, noisy, matches, mOff, counts[ri], p.Width);
        LoadGroup(gB, basic, matches, mOff, counts[ri], p.Width);
        if (K == 0) return;
        for (int k = 0; k < K; k++) { Dct8x8(gN.AsSpan(k * NN, NN), false); Dct8x8(gB.AsSpan(k * NN, NN), false); }
        float total = 0;
        for (int t = 0; t < NN; t++)
        {
            for (int k = 0; k < K; k++) { vn[k] = gN[k * NN + t]; vb[k] = gB[k * NN + t]; }
            Haar(vn, K, false, tmp); Haar(vb, K, false, tmp);
            float w2 = 0, s2 = sig[t] * sig[t];
            for (int k = 0; k < K; k++)
            {
                float b2 = vb[k] * vb[k];
                float w = b2 / (b2 + s2);
                vn[k] *= w; w2 += w * w;
            }
            total += w2;
            Haar(vn, K, true, tmp);
            for (int k = 0; k < K; k++) gN[k * NN + t] = vn[k];
        }
        float weight = total > 0 ? 1f / (p.Sigma2 * total) : 1f;
        for (int k = 0; k < K; k++) Dct8x8(gN.AsSpan(k * NN, NN), true);
        Aggregate(gN, num, den, matches, mOff, K, weight, p.Width);
    }

    /// <summary>Orthonormal Haar transform along the stack (K is a power of two).</summary>
    static void Haar(float[] v, int K, bool inverse, float[] tmp)
    {
        const float r = 0.70710678f;
        if (!inverse)
        {
            for (int len = K; len > 1; len >>= 1)
            {
                int hf = len / 2;
                for (int i = 0; i < hf; i++) { tmp[i] = (v[2 * i] + v[2 * i + 1]) * r; tmp[hf + i] = (v[2 * i] - v[2 * i + 1]) * r; }
                for (int i = 0; i < len; i++) v[i] = tmp[i];
            }
        }
        else
        {
            for (int len = 2; len <= K; len <<= 1)
            {
                int hf = len / 2;
                for (int i = 0; i < hf; i++) { tmp[2 * i] = (v[i] + v[hf + i]) * r; tmp[2 * i + 1] = (v[i] - v[hf + i]) * r; }
                for (int i = 0; i < len; i++) v[i] = tmp[i];
            }
        }
    }

    /// <summary>2-D orthonormal DCT-II (or its inverse) of an 8×8 block in place.</summary>
    internal static void Dct8x8(Span<float> block, bool inverse)
    {
        Span<float> tmp = stackalloc float[NN];
        var c = DctMatrix;
        if (Vector256.IsHardwareAccelerated)
        {
            // Rows: tmp[j][·] = Σ_i x[j][i] · C[·][i]  (forward) or Σ_i x[j][i] · C[i][·] (inverse).
            var cols = inverse ? DctRowsV : DctColsV;
            for (int j = 0; j < N; j++)
            {
                var acc = Vector256<float>.Zero;
                for (int i = 0; i < N; i++) acc += Vector256.Create<float>(block[j * N + i]) * cols[i];
                acc.CopyTo(tmp.Slice(j * N, N));
            }
            // Columns: out[v][·] = Σ_j C[v][j] · tmp[j][·] (forward) or Σ_j C[j][v] · tmp[j][·] (inverse).
            for (int v = 0; v < N; v++)
            {
                var acc = Vector256<float>.Zero;
                for (int j = 0; j < N; j++) acc += Vector256.Create<float>(inverse ? c[j * N + v] : c[v * N + j]) * Vector256.Create<float>(tmp.Slice(j * N, N));
                acc.CopyTo(block.Slice(v * N, N));
            }
            return;
        }
        for (int j = 0; j < N; j++)
            for (int u = 0; u < N; u++)
            {
                float a = 0;
                for (int i = 0; i < N; i++) a += (inverse ? c[i * N + u] : c[u * N + i]) * block[j * N + i];
                tmp[j * N + u] = a;
            }
        for (int u = 0; u < N; u++)
            for (int v = 0; v < N; v++)
            {
                float a = 0;
                for (int j = 0; j < N; j++) a += (inverse ? c[j * N + v] : c[v * N + j]) * tmp[j * N + u];
                block[v * N + u] = a;
            }
    }

    /// <summary>Orthonormal 8-point DCT-II matrix, C[i][j].</summary>
    internal static readonly float[] DctMatrix = BuildDct();
    /// <summary>Column i of C as a vector over the row index (DctColsV[i][u] = C[u][i]).</summary>
    static readonly Vector256<float>[] DctColsV = Enumerable.Range(0, N).Select(i => Vector256.Create<float>(Enumerable.Range(0, N).Select(u => DctMatrix[u * N + i]).ToArray())).ToArray();
    /// <summary>Row i of C as a vector (DctRowsV[i][u] = C[i][u]).</summary>
    static readonly Vector256<float>[] DctRowsV = Enumerable.Range(0, N).Select(i => Vector256.Create<float>(DctMatrix.AsSpan(i * N, N))).ToArray();

    static float[] BuildDct()
    {
        var c = new float[NN];
        for (int i = 0; i < N; i++) for (int j = 0; j < N; j++)
            c[i * N + j] = (float)((i == 0 ? Math.Sqrt(1.0 / 8) : 0.5) * Math.Cos(Math.PI * (2 * j + 1) * i / 16));
        return c;
    }

    /// <summary>8×8 Kaiser window (β = 2) used to weight the aggregation of the filtered blocks.</summary>
    internal static readonly float[] KaiserWindow = BuildKaiser();

    static float[] BuildKaiser()
    {
        static double I0(double x)
        {
            double sum = 1, term = 1;
            for (int k = 1; k <= 25; k++) { term *= (x / 2 / k) * (x / 2 / k); sum += term; }
            return sum;
        }
        const double beta = 2.0;
        var w1 = new double[N];
        for (int i = 0; i < N; i++) { double r = (2.0 * i - 7) / 7; w1[i] = I0(beta * Math.Sqrt(1 - r * r)) / I0(beta); }
        var k = new float[NN];
        for (int y = 0; y < N; y++) for (int x = 0; x < N; x++) k[y * N + x] = (float)(w1[y] * w1[x]);
        return k;
    }

    // Tiling

    /// <summary>Runs <paramref name="body"/> on overlapping tiles and stitches the cores back together.</summary>
    internal static PlanarImage Tiled(PlanarImage y, int margin, Func<PlanarImage, int, int, PlanarImage> body)
    {
        int w = y.Width, h = y.Height, t = Math.Max(TileSize, 64);
        if (w <= t + 2 * margin && h <= t + 2 * margin) return body(y, 0, 0);
        var out_ = PlanarImage.Uninitialized(w, h);
        int y0 = 0;
        while (y0 < h)
        {
            int y1 = Math.Min(y0 + t, h);
            int x0 = 0;
            while (x0 < w)
            {
                int x1 = Math.Min(x0 + t, w);
                int px0 = Math.Max(x0 - margin, 0), py0 = Math.Max(y0 - margin, 0);
                int px1 = Math.Min(x1 + margin, w), py1 = Math.Min(y1 + margin, h);
                var tile = y.Cropped(px0, py0, px1 - px0, py1 - py0);
                var done = body(tile, px0, py0);
                for (int yy = y0; yy < y1; yy++)
                    Array.Copy(done.Data, (yy - py0) * done.Width + (x0 - px0), out_.Data, yy * w + x0, x1 - x0);
                x0 = x1;
            }
            y0 = y1;
        }
        return out_;
    }
}
