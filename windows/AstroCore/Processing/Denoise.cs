namespace AstroCore;

/// <summary>
/// Noise reduction on the combined image: luminance by one of four classical methods and colour
/// by chroma smoothing. All methods run on the CPU, spread over every core.
///
/// * <b>Wavelets</b> — starlet (à trous B3-spline) transform with k·σ soft thresholding per scale,
///   the standard tool of astronomical image processing (Starck &amp; Murtagh).
/// * <b>Non-local means</b> — Buades et al. 2005, patch-wise averaging; separable box sums keep it
///   O(N · search²).
/// * <b>Guided filter</b> — He et al. 2010 (self-guided), edge-preserving smoothing with box filters.
/// * <b>BM3D</b> — Dabov et al. 2007, block matching + collaborative 3-D transform filtering; the
///   strongest classical denoiser (see <see cref="Bm3dDenoise"/>).
///
/// The noise level σ is estimated from the finest wavelet scale (median absolute deviation), so
/// the strength adapts to the preview level and to the export resolution.
/// </summary>
public static class Denoise
{
    /// <summary>B3-spline kernel of the starlet transform.</summary>
    static readonly float[] B3 = [1f / 16, 4f / 16, 6f / 16, 4f / 16, 1f / 16];
    /// <summary>Noise standard deviation of each 2-D starlet scale for unit white noise (scales 1…6).</summary>
    public static readonly float[] ScaleNoise = [0.889f, 0.200f, 0.086f, 0.041f, 0.020f, 0.010f];

    /// <summary>Separable convolution with the B3 kernel dilated by <paramref name="step"/> (à trous), edges extended.</summary>
    internal static PlanarImage Smooth(PlanarImage p, int step)
    {
        int len = 4 * step + 1;
        var k = new float[len];
        for (int i = 0; i < B3.Length; i++) k[i * step] = B3[i];
        return Convolve.Separable(p, k, true);
    }

    /// <summary>Noise σ of the image, from the MAD of the finest starlet scale (robust to signal).</summary>
    public static float NoiseSigma(PlanarImage p)
    {
        var s = Smooth(p, 1);
        int n = p.Count;
        int step = Math.Max(1, n / 200_000);
        var sample = new List<float>(n / step + 1);
        for (int i = 0; i < n; i += step) sample.Add(MathF.Abs(p.Data[i] - s.Data[i]));
        var arr = sample.ToArray();
        Array.Sort(arr);
        float mad = Statistics.Percentile(arr, 0.5);
        return mad / 0.6745f / ScaleNoise[0];
    }

    // Wavelets

    /// <summary>Starlet k·σ soft thresholding on <paramref name="layers"/> scales; <paramref name="amount"/> scales the threshold (0…1 → 0…3σ).</summary>
    internal static PlanarImage Wavelets(PlanarImage y, float sigma, float amount, int layers)
    {
        int n = y.Count;
        var out_ = PlanarImage.Uninitialized(y.Width, y.Height);
        var cur = y;
        var residual = new float[n];   // sum of thresholded detail scales
        for (int j = 0; j < Math.Max(1, Math.Min(layers, ScaleNoise.Length)); j++)
        {
            var s = Smooth(cur, 1 << j);
            // Finer scales are denoised harder; the threshold shrinks with the scale index.
            float t = amount * 3 * sigma * ScaleNoise[j] * (j == 0 ? 1 : 0.8f);
            var cp = cur.Data; var sp = s.Data;
            Par.Rows(y.Height, y.Width, (y0, y1) =>
            {
                for (int i = y0 * y.Width; i < y1 * y.Width; i++)
                {
                    float w = cp[i] - sp[i];
                    residual[i] += w > t ? w - t : (w < -t ? w + t : 0);
                }
            });
            cur = s;
        }
        Vec.Add(cur.Data, residual, out_.Data);
        return out_;
    }

    // Non-local means

    /// <summary>
    /// Non-local means with patch radius 2 and search radius <paramref name="search"/>; <paramref name="h"/>
    /// is the filtering parameter (≈ amount · σ). Patch distances come from separable box sums of the
    /// squared difference between the image and its shifted copy, one offset at a time.
    /// </summary>
    internal static PlanarImage NonLocalMeans(PlanarImage y, float sigma, float h, int search)
    {
        int w = y.Width, hgt = y.Height, n = y.Count;
        const int patch = 2;
        float patchN = (2 * patch + 1) * (2 * patch + 1);
        var acc = y.Copy();                                   // the centre pixel with weight 1
        var wsum = new PlanarImage(w, hgt, 1);
        var diff = PlanarImage.Uninitialized(w, hgt);
        float h2 = MathF.Max(h * h, 1e-12f), noiseFloor = 2 * sigma * sigma;
        var yp = y.Data; var ap = acc.Data; var wp = wsum.Data; var dp = diff.Data;
        for (int dy = -search; dy <= search; dy++)
        {
            for (int dx = -search; dx <= search; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int ddx = dx, ddy = dy;
                // Squared difference between the image and the image shifted by (dx, dy) (edges extended).
                Par.Rows(hgt, w * 2, (y0, y1) =>
                {
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int sy = Math.Min(Math.Max(yy + ddy, 0), hgt - 1);
                        int row = yy * w, srow = sy * w;
                        for (int xx = 0; xx < w; xx++)
                        {
                            int sx = Math.Min(Math.Max(xx + ddx, 0), w - 1);
                            float d = yp[row + xx] - yp[srow + sx];
                            dp[row + xx] = d * d;
                        }
                    }
                });
                var dist = Convolve.BoxSum(diff, patch);
                var distp = dist.Data;
                Par.Rows(hgt, w * 4, (y0, y1) =>
                {
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int sy = yy + ddy;
                        if (sy < 0 || sy >= hgt) continue;
                        int row = yy * w, srow = sy * w;
                        int xa = Math.Max(0, -ddx), xb = Math.Min(w, w - ddx);
                        for (int xx = xa; xx < xb; xx++)
                        {
                            float d2 = MathF.Max(distp[row + xx] / patchN - noiseFloor, 0);
                            float wgt = MathF.Exp(-d2 / h2);
                            ap[row + xx] += wgt * yp[srow + xx + ddx]; wp[row + xx] += wgt;
                        }
                    }
                });
            }
        }
        var out_ = PlanarImage.Uninitialized(w, hgt);
        Vec.Div(ap, wp, out_.Data);
        return out_;
    }

    // Guided filter

    /// <summary>Self-guided filter (He et al.): q = a·I + b with a = var/(var + ε) averaged over the window.</summary>
    internal static PlanarImage Guided(PlanarImage y, float epsilon, int r)
    {
        int n = y.Count;
        float k = 1f / ((2 * r + 1) * (2 * r + 1));
        PlanarImage Mean(PlanarImage p)
        {
            var s = Convolve.BoxSum(p, r);
            Vec.Scale(s.Data, k, s.Data);
            return s;
        }
        var mI = Mean(y);
        var sq = PlanarImage.Uninitialized(y.Width, y.Height);
        Vec.Square(y.Data, sq.Data);
        var mII = Mean(sq);
        var a = PlanarImage.Uninitialized(y.Width, y.Height); var b = PlanarImage.Uninitialized(y.Width, y.Height);
        var mid = mI.Data; var miid = mII.Data; var ad = a.Data; var bd = b.Data;
        for (int i = 0; i < n; i++)
        {
            float v = MathF.Max(miid[i] - mid[i] * mid[i], 0);
            ad[i] = v / (v + epsilon);
            bd[i] = mid[i] - ad[i] * mid[i];
        }
        var ma = Mean(a); var mb = Mean(b);
        var out_ = PlanarImage.Uninitialized(y.Width, y.Height);
        Vec.MulAdd(ma.Data, y.Data, mb.Data, out_.Data);
        return out_;
    }

    // Entry point

    /// <summary>Applies the configured noise reduction to <paramref name="img"/> in place.</summary>
    public static void Apply(RGBImage img, DenoiseSettings d)
    {
        if (d.IsNeutral) return;
        int n = img.Width * img.Height, w = img.Width;
        var y = img.Luma();
        float sigma = NoiseSigma(y);
        var target = y;
        if (d.Amount > 0 && sigma > 0)
        {
            switch (d.Method)
            {
                case DenoiseMethod.Wavelets:
                    target = Wavelets(y, sigma, d.Amount, d.Layers);
                    break;
                case DenoiseMethod.NonLocalMeans:
                    target = GpuDenoise.NonLocalMeans(y, sigma, d.Amount * 1.5f * sigma, d.Search) ?? NonLocalMeans(y, sigma, d.Amount * 1.5f * sigma, d.Search);
                    break;
                case DenoiseMethod.Bm3d:
                    // GPU first, then the CPU port; the wavelets when both are disabled (tests).
                    target = GpuDenoise.Bm3d(y, sigma, d.Amount, d.BlockSearch) ?? Bm3dDenoise.Bm3d(y, sigma, d.Amount, d.BlockSearch) ?? Wavelets(y, sigma, d.Amount, 3);
                    break;
                case DenoiseMethod.Guided:
                    target = Guided(y, MathF.Pow(d.Amount * 3 * sigma, 2), d.Radius);
                    break;
            }
            // Bright structures (stars, cores) keep their original values, with a soft ramp.
            if (d.ProtectBright > 0)
            {
                float hi = 1 - 0.8f * d.ProtectBright, lo = hi * 0.5f;
                var tp = target.Data; var yp = y.Data;
                for (int i = 0; i < n; i++)
                {
                    float l = yp[i];
                    if (l <= lo) continue;
                    float t = MathF.Min((l - lo) / (hi - lo), 1);
                    float keep = t * t * (3 - 2 * t);
                    tp[i] = tp[i] + keep * (l - tp[i]);
                }
            }
        }
        var r = img.R.Data; var g = img.G.Data; var b = img.B.Data;
        if (d.Amount > 0 && sigma > 0)
        {
            var tp = target.Data; var yp = y.Data;
            Par.Rows(img.Height, w * 4, (y0, y1) =>
            {
                for (int i = y0 * w; i < y1 * w; i++)
                {
                    float baseV = yp[i];
                    if (baseV > 1e-5f)
                    {
                        float ratio = MathF.Min(tp[i] / baseV, 8);
                        r[i] *= ratio; g[i] *= ratio; b[i] *= ratio;
                    }
                    else { r[i] = tp[i]; g[i] = tp[i]; b[i] = tp[i]; }
                }
            });
        }
        if (d.Color > 0)
        {
            // Chroma smoothing: blur the colour ratios (r/Y, g/Y, b/Y), keep the luminance.
            var yNow = img.Luma();
            int levels = 1 + (int)MathF.Round(d.Color * 3, MidpointRounding.AwayFromZero);
            foreach (var plane in img.Planes)
            {
                var ratio = PlanarImage.Uninitialized(img.Width, img.Height);
                var pp = plane.Data; var yp = yNow.Data; var rp = ratio.Data;
                for (int i = 0; i < n; i++) rp[i] = yp[i] > 1e-5f ? pp[i] / yp[i] : 1;
                var blurred = PostProcess.PyramidBlur(ratio, levels);
                var bp = blurred.Data;
                float col = d.Color;
                for (int i = 0; i < n; i++)
                {
                    float mixed = rp[i] + col * (bp[i] - rp[i]);
                    pp[i] = yp[i] > 1e-5f ? mixed * yp[i] : pp[i];
                }
            }
        }
        img.Clamp01();
    }
}
