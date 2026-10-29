namespace AstroCore;

/// <summary>Robust statistics of one channel, computed on a sub-sample of the full-resolution image.</summary>
public sealed class ChannelStats : IEquatable<ChannelStats>
{
    public float Min;
    public float Max;
    public float Mean;
    public float Median;
    /// <summary>Median absolute deviation from the median.</summary>
    public float Mad;
    /// <summary>MAD scaled to be comparable with a Gaussian sigma (× 1.4826).</summary>
    public float Madn;
    public float StdDev;
    /// <summary>Sigma-clipped median: the sky background level.</summary>
    public float Background;
    /// <summary>Sigma-clipped dispersion (MADN) of the background pixels.</summary>
    public float BackgroundSigma;
    /// <summary>Pixel-to-pixel noise estimate (from horizontal differences).</summary>
    public float Noise;
    /// <summary>Fraction of pixels that are valid (non-zero, finite, unmasked).</summary>
    public float ValidFraction = 1;
    /// <summary>Sorted sample of valid pixel values (used for percentiles / histogram matching).</summary>
    public float[] SortedSample = [];

    public ChannelStats Clone()
    {
        var c = (ChannelStats)MemberwiseClone();
        c.SortedSample = (float[])SortedSample.Clone();
        return c;
    }

    /// <summary>Percentile in [0, 1] from the sorted sample (linear interpolation).</summary>
    public float Percentile(double q) => Statistics.Percentile(SortedSample, q);

    /// <summary>Signal-to-noise proxy: (p99.5 − background) / noise.</summary>
    public float SignalStrength => Noise > 0 ? (Percentile(0.995) - Background) / Noise : 0;

    /// <summary>Stats after a monotonic transform <paramref name="f"/> (the sorted order is preserved).</summary>
    public ChannelStats Transformed(Func<float, float> f, float scale)
    {
        var s = Clone();
        for (int i = 0; i < s.SortedSample.Length; i++) s.SortedSample[i] = f(SortedSample[i]);
        if (s.SortedSample.Length > 0 && s.SortedSample[^1] < s.SortedSample[0]) Array.Reverse(s.SortedSample);
        s.Min = f(Min); s.Max = f(Max); s.Mean = f(Mean); s.Median = f(Median);
        s.Background = f(Background);
        float a = MathF.Abs(scale);
        s.Mad = Mad * a; s.Madn = Madn * a; s.StdDev = StdDev * a;
        s.BackgroundSigma = BackgroundSigma * a; s.Noise = Noise * a;
        return s;
    }

    /// <summary>Stats after an affine transform a·x + b.</summary>
    public ChannelStats Affine(float a, float b) => Transformed(x => a * x + b, a);

    public bool Equals(ChannelStats? o) => o != null && Min == o.Min && Max == o.Max && Mean == o.Mean && Median == o.Median && Mad == o.Mad
        && Madn == o.Madn && StdDev == o.StdDev && Background == o.Background && BackgroundSigma == o.BackgroundSigma && Noise == o.Noise
        && ValidFraction == o.ValidFraction && SortedSample.AsSpan().SequenceEqual(o.SortedSample);
    public override bool Equals(object? obj) => Equals(obj as ChannelStats);
    public override int GetHashCode() => HashCode.Combine(Median, Madn, Background, SortedSample.Length);
}

public sealed class StatsOptions
{
    public int MaxSamples = 600_000;
    public bool ExcludeZeros = true;
    public float ClipSigma = 2.5f;
    public int ClipIterations = 4;
    /// <summary>Optional exclusion mask (1 = excluded) at a coarser pyramid level.</summary>
    public PlanarImage? Mask;
    public int MaskLevel;
    /// <summary>Restrict every computation to this region (full-resolution coordinates).</summary>
    public PixelRect? Region;

    internal PixelRect Bounds(int width, int height) => Region?.Clamped(width, height) ?? PixelRect.Full(width, height);
}

public static class Statistics
{
    public static ChannelStats Compute(PlanarImage image, StatsOptions? options = null)
    {
        options ??= new StatsOptions();
        var (sample, validFraction) = Sample(image, options);
        var stats = FromSample(sample, options.ClipSigma, options.ClipIterations);
        stats.ValidFraction = validFraction;
        stats.Noise = NoiseEstimate(image, options);
        return stats;
    }

    /// <summary>Statistics derived from an (unsorted or sorted) sample of values.</summary>
    public static ChannelStats FromSample(float[] values, float clipSigma = 2.5f, int clipIterations = 4)
    {
        var s = new ChannelStats();
        if (values.Length == 0) return s;
        var sorted = (float[])values.Clone();
        Array.Sort(sorted);
        s.SortedSample = sorted;
        s.Min = sorted[0];
        s.Max = sorted[^1];
        double sum = 0;
        foreach (var v in sorted) sum += v;
        double mean = sum / sorted.Length, sq = 0;
        foreach (var v in sorted) { double d = v - mean; sq += d * d; }
        s.Mean = (float)mean;
        // vDSP_normalize uses the sample standard deviation (n − 1).
        s.StdDev = (float)Math.Sqrt(sq / Math.Max(1, sorted.Length - 1));
        s.Median = Percentile(sorted, 0.5);
        s.Mad = MedianAbsoluteDeviation(sorted, s.Median);
        s.Madn = 1.4826f * s.Mad;
        // Sigma-clipped background.
        float lo = s.Median - clipSigma * MathF.Max(s.Madn, 1e-12f), hi = s.Median + clipSigma * MathF.Max(s.Madn, 1e-12f);
        float bg = s.Median, bgSigma = s.Madn;
        if (s.Madn > 0)
        {
            for (int it = 0; it < clipIterations; it++)
            {
                int l = LowerBound(sorted, lo), h = UpperBound(sorted, hi);
                if (h - l <= 16) break;
                var sub = new ArraySegment<float>(sorted, l, h - l);
                bg = Percentile(sub, 0.5);
                float m = MedianAbsoluteDeviation(sub, bg) * 1.4826f;
                if (m <= 0) break;
                bgSigma = m;
                lo = bg - clipSigma * m; hi = bg + clipSigma * m;
            }
        }
        s.Background = bg;
        s.BackgroundSigma = bgSigma;
        return s;
    }

    static int GridStep(int total, int maxSamples) => Math.Max(1, (int)Math.Floor(Math.Sqrt(total / (double)maxSamples)));

    /// <summary>Deterministic grid sample of valid pixels. Returns the sample and the valid fraction.</summary>
    public static (float[] sample, float validFraction) Sample(PlanarImage image, StatsOptions options)
    {
        int w = image.Width, h = image.Height;
        var b = options.Bounds(w, h);
        int total = b.Count;
        int step = GridStep(total, options.MaxSamples);
        var out_ = new List<float>(total / (step * step) + 1);
        long visited = 0;
        var mask = options.Mask;
        int shift = options.MaskLevel;
        var p = image.Data;
        int y = b.Y, row = 0;
        while (y < b.MaxY)
        {
            int jitter = (row * 7) % step;
            int x = b.X + jitter;
            int rowOff = y * w;
            while (x < b.MaxX)
            {
                float v = p[rowOff + x];
                visited++;
                bool ok = float.IsFinite(v) && !(options.ExcludeZeros && v == 0);
                if (ok && mask != null)
                {
                    int mx = Math.Min(mask.Width - 1, x >> shift), my = Math.Min(mask.Height - 1, y >> shift);
                    if (mask.Data[my * mask.Width + mx] != 0) ok = false;
                }
                if (ok) out_.Add(v);
                x += step;
            }
            y += step;
            row++;
        }
        return (out_.ToArray(), visited > 0 ? (float)out_.Count / visited : 0);
    }

    /// <summary>Sample pairs of values at identical positions from two same-sized images.</summary>
    public static (float[] xs, float[] ys) PairedSample(PlanarImage a, PlanarImage b, StatsOptions options, PlanarImage? maskB = null)
    {
        if (a.Width != b.Width || a.Height != b.Height) throw new ArgumentException("images differ in size");
        int w = a.Width, h = a.Height;
        var bounds = options.Bounds(w, h);
        int total = bounds.Count;
        int step = GridStep(total, options.MaxSamples);
        var xs = new List<float>(); var ys = new List<float>();
        int shift = options.MaskLevel;
        var masks = new List<PlanarImage>();
        if (options.Mask != null) masks.Add(options.Mask);
        if (maskB != null) masks.Add(maskB);
        int y = bounds.Y, row = 0;
        while (y < bounds.MaxY)
        {
            int x = bounds.X + (row * 7) % step;
            while (x < bounds.MaxX)
            {
                float va = a.Data[y * w + x], vb = b.Data[y * w + x];
                bool ok = float.IsFinite(va) && float.IsFinite(vb) && !(options.ExcludeZeros && (va == 0 || vb == 0));
                if (ok)
                {
                    foreach (var m in masks)
                    {
                        int mx = Math.Min(m.Width - 1, x >> shift), my = Math.Min(m.Height - 1, y >> shift);
                        if (m.Data[my * m.Width + mx] != 0) { ok = false; break; }
                    }
                }
                if (ok) { xs.Add(va); ys.Add(vb); }
                x += step;
            }
            y += step; row++;
        }
        return (xs.ToArray(), ys.ToArray());
    }

    /// <summary>
    /// Samples several same-sized images at identical grid positions, keeping only positions
    /// where every image is valid (finite and non-zero). Returns one array per image.
    /// </summary>
    public static float[][] MultiSample(IReadOnlyList<PlanarImage> images, int maxSamples = 400_000, PixelRect? region = null)
    {
        if (images.Count == 0) return [];
        var first = images[0];
        int w = first.Width, h = first.Height;
        foreach (var img in images) if (img.Width != w || img.Height != h) throw new ArgumentException("images differ in size");
        var b = region?.Clamped(w, h) ?? PixelRect.Full(w, h);
        int total = b.Count;
        int step = GridStep(total, maxSamples);
        var out_ = new List<float>[images.Count];
        for (int i = 0; i < out_.Length; i++) out_[i] = new List<float>(total / (step * step) + 1);
        int y = b.Y, row = 0;
        while (y < b.MaxY)
        {
            int x = b.X + (row * 7) % step;
            while (x < b.MaxX)
            {
                int idx = y * w + x;
                bool ok = true;
                foreach (var img in images) { float v = img.Data[idx]; if (!float.IsFinite(v) || v == 0) { ok = false; break; } }
                if (ok) for (int i = 0; i < images.Count; i++) out_[i].Add(images[i].Data[idx]);
                x += step;
            }
            y += step; row++;
        }
        return out_.Select(l => l.ToArray()).ToArray();
    }

    /// <summary>Robust pixel noise estimate: MAD of horizontal neighbour differences / √2.</summary>
    public static float NoiseEstimate(PlanarImage image, StatsOptions? options = null)
    {
        options ??= new StatsOptions();
        int w = image.Width, h = image.Height;
        if (w <= 2) return 0;
        var b = options.Bounds(w, h);
        if (b.Width <= 2) return 0;
        int rowStep = Math.Max(1, b.Count / Math.Max(1, options.MaxSamples));
        var diffs = new List<float>(options.MaxSamples + w);
        var mask = options.Mask; int shift = options.MaskLevel;
        var p = image.Data;
        int y = b.Y + (rowStep / 2) % b.Height;
        while (y < b.MaxY && diffs.Count < options.MaxSamples)
        {
            int row = y * w;
            int x = b.X + 1;
            while (x < b.MaxX)
            {
                float a = p[row + x - 1], c = p[row + x];
                bool ok = a != 0 && c != 0 && float.IsFinite(a) && float.IsFinite(c);
                if (ok && mask != null)
                {
                    int mx = Math.Min(mask.Width - 1, x >> shift), my = Math.Min(mask.Height - 1, y >> shift);
                    if (mask.Data[my * mask.Width + mx] != 0) ok = false;
                }
                if (ok) diffs.Add(MathF.Abs(c - a));
                x++;
            }
            y += rowStep;
        }
        if (diffs.Count <= 16) return 0;
        var arr = diffs.ToArray();
        Array.Sort(arr);
        float mad = Percentile(arr, 0.5);
        return 1.4826f * mad / MathF.Sqrt(2);
    }

    // Helpers on sorted arrays

    public static float Percentile(ReadOnlySpan<float> sorted, double q)
    {
        if (sorted.Length == 0) return 0;
        double pos = Math.Max(0, Math.Min(1, q)) * (sorted.Length - 1);
        int i = (int)Math.Floor(pos);
        float f = (float)(pos - i);
        if (i + 1 < sorted.Length) return sorted[i] * (1 - f) + sorted[i + 1] * f;
        return sorted[i];
    }

    public static float Percentile(ArraySegment<float> sorted, double q) => Percentile((ReadOnlySpan<float>)sorted, q);

    /// <summary>
    /// MAD of a sorted array about <paramref name="c"/>, computed in O(n) with a two-pointer merge
    /// (deviations decrease towards the centre from the left and increase to the right).
    /// </summary>
    public static float MedianAbsoluteDeviation(ReadOnlySpan<float> a, float c)
    {
        int n = a.Length;
        if (n == 0) return 0;
        int r = LowerBound(a, c);
        int l = r - 1;
        int k1 = (n - 1) / 2, k2 = n / 2;
        float v1 = 0, v2 = 0;
        for (int i = 0; i <= k2; i++)
        {
            float dl = l >= 0 ? c - a[l] : float.PositiveInfinity;
            float dr = r < n ? a[r] - c : float.PositiveInfinity;
            float v;
            if (dl <= dr) { v = dl; l--; } else { v = dr; r++; }
            if (i == k1) v1 = v;
            if (i == k2) v2 = v;
        }
        return 0.5f * (v1 + v2);
    }

    public static float MedianAbsoluteDeviation(ArraySegment<float> a, float c) => MedianAbsoluteDeviation((ReadOnlySpan<float>)a, c);

    /// <summary>Index of the first element ≥ v.</summary>
    public static int LowerBound(ReadOnlySpan<float> a, float v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) / 2; if (a[m] < v) lo = m + 1; else hi = m; }
        return lo;
    }

    /// <summary>Index of the first element > v.</summary>
    public static int UpperBound(ReadOnlySpan<float> a, float v)
    {
        int lo = 0, hi = a.Length;
        while (lo < hi) { int m = (lo + hi) / 2; if (a[m] <= v) lo = m + 1; else hi = m; }
        return lo;
    }

    /// <summary>Fraction of samples ≤ v.</summary>
    public static float Cdf(ReadOnlySpan<float> sorted, float value)
    {
        if (sorted.Length == 0) return 0;
        return (float)UpperBound(sorted, value) / sorted.Length;
    }

    /// <summary>Histogram of an image (sub-sampled when large) over [lo, hi].</summary>
    public static int[] Histogram(PlanarImage image, int bins, float lo = 0, float hi = 1, int maxSamples = 2_000_000)
    {
        var counts = new int[bins];
        int n = image.Count;
        int step = Math.Max(1, n / maxSamples);
        float scale = bins / MathF.Max(hi - lo, 1e-12f);
        var p = image.Data;
        for (int i = 0; i < n; i += step)
        {
            float v = p[i];
            if (!float.IsFinite(v)) continue;
            int b = (int)((v - lo) * scale);
            if (b < 0) b = 0; else if (b >= bins) b = bins - 1;
            counts[b]++;
        }
        return counts;
    }
}
