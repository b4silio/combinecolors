namespace AstroCore;

/// <summary>How the per-channel signal levels are equalised before combination.</summary>
public enum NormalizationMethod { None, BackgroundEqualize, LinearFit, BackgroundDispersion, BackgroundSignal, NoiseWeighted, HistogramMatch }

public static class NormalizationMethods
{
    public static readonly NormalizationMethod[] All =
        [NormalizationMethod.None, NormalizationMethod.BackgroundEqualize, NormalizationMethod.LinearFit, NormalizationMethod.BackgroundDispersion,
         NormalizationMethod.BackgroundSignal, NormalizationMethod.NoiseWeighted, NormalizationMethod.HistogramMatch];

    public static string DisplayName(this NormalizationMethod m) => m switch
    {
        NormalizationMethod.None => "None",
        NormalizationMethod.BackgroundEqualize => "Background equalise",
        NormalizationMethod.LinearFit => "Linear fit",
        NormalizationMethod.BackgroundDispersion => "Background + dispersion",
        NormalizationMethod.BackgroundSignal => "Background + signal",
        NormalizationMethod.NoiseWeighted => "Noise weighted",
        _ => "Histogram match",
    };

    public static string Summary(this NormalizationMethod m) => m switch
    {
        NormalizationMethod.None => "Channels are used as loaded.",
        NormalizationMethod.BackgroundEqualize => "Shifts each channel so that its sky background equals the reference background (offset only).",
        NormalizationMethod.LinearFit => "Robust iterative linear regression of the reference on each channel (PixInsight LinearFit style). Matches background and overall signal.",
        NormalizationMethod.BackgroundDispersion => "Matches the sky background and the robust dispersion (MADN) of each channel to the reference.",
        NormalizationMethod.BackgroundSignal => "Models each channel as background + emission: subtracts the sigma-clipped sky, then scales so the chosen signal percentile of the star-masked image equals the reference.",
        NormalizationMethod.NoiseWeighted => "Subtracts the background and scales so every channel has the same noise floor. Faint channels keep their lower SNR instead of being amplified.",
        _ => "Maps the cumulative histogram of each channel onto the reference (quantile mapping).",
    };
}

public sealed partial class NormalizationSettings : IEquatable<NormalizationSettings>
{
    public NormalizationMethod Method = NormalizationMethod.BackgroundSignal;
    /// <summary>Reference channel; null = automatic (Hα when present, else the strongest signal).</summary>
    public ChannelKind? Reference = null;
    /// <summary>Compute statistics on star-masked pixels.</summary>
    public bool UseStarMask = true;
    /// <summary>Percentile (0..1) defining the "signal" level for background+signal matching.</summary>
    public float SignalPercentile = 0.995f;
    /// <summary>Linear-fit rejection threshold (sigma) and iterations.</summary>
    public float FitRejectSigma = 2.5f;
    public int FitIterations = 5;
    /// <summary>Exclude the extreme tails from histogram matching (fraction, each side).</summary>
    public float HistogramClip = 0.0005f;

    public NormalizationSettings Clone() => (NormalizationSettings)MemberwiseClone();
    public bool Equals(NormalizationSettings? o) => o != null && SettingsJson.SameJson(this, o);
    public override bool Equals(object? obj) => Equals(obj as NormalizationSettings);
    public override int GetHashCode() => SettingsJson.JsonHash(this);
}

/// <summary>A uniformly sampled look-up table over [lo, hi] with linear extrapolation outside.</summary>
public sealed class UniformLUT : IEquatable<UniformLUT>
{
    public float Lo, Hi;
    public float[] Table;
    public float SlopeBelow, SlopeAbove;

    public UniformLUT(float lo, float hi, float[] table, float slopeBelow, float slopeAbove)
    {
        Lo = lo; Hi = hi; Table = table; SlopeBelow = slopeBelow; SlopeAbove = slopeAbove;
    }

    public float Evaluate(float x)
    {
        if (x <= Lo) return Table[0] + SlopeBelow * (x - Lo);
        if (x >= Hi) return Table[^1] + SlopeAbove * (x - Hi);
        float pos = (x - Lo) / (Hi - Lo) * (Table.Length - 1);
        int i = (int)pos;
        float f = pos - i;
        if (i + 1 < Table.Length) return Table[i] * (1 - f) + Table[i + 1] * f;
        return Table[i];
    }

    public void Apply(PlanarImage plane)
    {
        float scale = (Table.Length - 1) / (Hi - Lo), offset = -Lo * scale;
        var table = Table; float lo = Lo, hi = Hi, sb = SlopeBelow, sa = SlopeAbove;
        Par.Rows(plane.Height, plane.Width * 2, (y0, y1) =>
        {
            var seg = plane.Data.AsSpan(y0 * plane.Width, (y1 - y0) * plane.Width);
            int last = table.Length - 1;
            for (int i = 0; i < seg.Length; i++)
            {
                float x = seg[i];
                float pos = x * scale + offset;
                float v;
                if (!(pos > 0)) v = table[0];
                else if (pos >= last) v = table[last];
                else { int k = (int)pos; float f = pos - k; v = table[k] * (1 - f) + table[k + 1] * f; }
                // Extrapolation: below lo and above hi.
                v += sa * MathF.Max(x - hi, 0) + sb * MathF.Min(x - lo, 0);
                seg[i] = v;
            }
        });
    }

    public bool Equals(UniformLUT? o) => o != null && Lo == o.Lo && Hi == o.Hi && SlopeBelow == o.SlopeBelow && SlopeAbove == o.SlopeAbove && Table.AsSpan().SequenceEqual(o.Table);
    public override bool Equals(object? obj) => Equals(obj as UniformLUT);
    public override int GetHashCode() => HashCode.Combine(Lo, Hi, Table.Length);
}

/// <summary>The transform applied to one channel by the normaliser.</summary>
public abstract record ChannelTransform
{
    public sealed record IdentityT : ChannelTransform { public override string ToString() => "identity"; }
    public sealed record AffineT(float A, float B) : ChannelTransform { public override string ToString() => $"×{A:F4} {B:+0.00000;-0.00000}"; }
    public sealed record LutT(UniformLUT Lut) : ChannelTransform { public override string ToString() => "histogram LUT"; }

    public static readonly ChannelTransform Identity = new IdentityT();
    public static ChannelTransform Affine(float a, float b) => new AffineT(a, b);
    public static ChannelTransform OfLut(UniformLUT l) => new LutT(l);

    public bool IsIdentity => this is IdentityT;

    public float Evaluate(float x) => this switch
    {
        AffineT a => a.A * x + a.B,
        LutT l => l.Lut.Evaluate(x),
        _ => x,
    };

    public void Apply(PlanarImage plane)
    {
        switch (this)
        {
            case AffineT a: plane.Affine(a.A, a.B); break;
            case LutT l: l.Lut.Apply(plane); break;
        }
    }

    /// <summary>Statistics after the transform.</summary>
    public ChannelStats Transform(ChannelStats stats)
    {
        switch (this)
        {
            case AffineT a: return stats.Affine(a.A, a.B);
            case LutT l:
            {
                // Approximate the local scale from the central slope.
                float s = (l.Lut.Evaluate(stats.Median + stats.Madn) - l.Lut.Evaluate(stats.Median - stats.Madn)) / MathF.Max(2 * stats.Madn, 1e-12f);
                return stats.Transformed(l.Lut.Evaluate, s);
            }
            default: return stats;
        }
    }

    public string Description => ToString() ?? "";
}

/// <summary>Everything the normaliser needs to know about one channel.</summary>
public sealed class NormalizationInput
{
    public ChannelKind Kind { get; }
    public PlanarImage Image { get; }
    /// <summary>Statistics computed on the whole image (zeros excluded).</summary>
    public ChannelStats Stats { get; }
    /// <summary>Statistics computed with the star mask (falls back to Stats).</summary>
    public ChannelStats MaskedStats { get; }
    public PlanarImage? Mask { get; }
    public int MaskLevel { get; }
    public PixelRect? Region { get; }

    public NormalizationInput(ChannelKind kind, PlanarImage image, ChannelStats stats, ChannelStats? maskedStats = null, PlanarImage? mask = null, int maskLevel = 0, PixelRect? region = null)
    {
        Kind = kind; Image = image; Stats = stats; MaskedStats = maskedStats ?? stats; Mask = mask; MaskLevel = maskLevel; Region = region;
    }
}

public static class Normalizer
{
    /// <summary>Chooses the reference channel.</summary>
    public static ChannelKind? ReferenceChannel(NormalizationSettings settings, IReadOnlyList<NormalizationInput> available)
    {
        if (settings.Reference is ChannelKind r && available.Any(i => i.Kind == r)) return r;
        if (available.Any(i => i.Kind == ChannelKind.Ha)) return ChannelKind.Ha;
        if (available.Any(i => i.Kind == ChannelKind.Luminance)) return ChannelKind.Luminance;
        NormalizationInput? best = null;
        foreach (var i in available) if (best == null || i.MaskedStats.SignalStrength > best.MaskedStats.SignalStrength) best = i;
        return best?.Kind;
    }

    /// <summary>Computes the transform for every channel.</summary>
    public static Dictionary<ChannelKind, ChannelTransform> Transforms(NormalizationSettings settings, IReadOnlyList<NormalizationInput> inputs)
    {
        var out_ = new Dictionary<ChannelKind, ChannelTransform>();
        foreach (var i in inputs) out_[i.Kind] = ChannelTransform.Identity;
        if (settings.Method == NormalizationMethod.None) return out_;
        if (ReferenceChannel(settings, inputs) is not ChannelKind refKind) return out_;
        var reference = inputs.FirstOrDefault(i => i.Kind == refKind);
        if (reference == null) return out_;
        var refStats = settings.UseStarMask ? reference.MaskedStats : reference.Stats;
        foreach (var input in inputs)
        {
            if (input.Kind == refKind) continue;
            var st = settings.UseStarMask ? input.MaskedStats : input.Stats;
            switch (settings.Method)
            {
                case NormalizationMethod.BackgroundEqualize:
                    out_[input.Kind] = ChannelTransform.Affine(1, refStats.Background - st.Background);
                    break;
                case NormalizationMethod.BackgroundDispersion:
                {
                    float a = st.BackgroundSigma > 0 ? refStats.BackgroundSigma / st.BackgroundSigma : 1;
                    out_[input.Kind] = ChannelTransform.Affine(a, refStats.Background - a * st.Background);
                    break;
                }
                case NormalizationMethod.BackgroundSignal:
                {
                    double q = settings.SignalPercentile;
                    float sRef = refStats.Percentile(q) - refStats.Background;
                    float sCh = st.Percentile(q) - st.Background;
                    float a = sCh > 0 && sRef > 0 ? sRef / sCh : 1;
                    out_[input.Kind] = ChannelTransform.Affine(a, refStats.Background - a * st.Background);
                    break;
                }
                case NormalizationMethod.NoiseWeighted:
                {
                    float a = st.Noise > 0 && refStats.Noise > 0 ? refStats.Noise / st.Noise : 1;
                    out_[input.Kind] = ChannelTransform.Affine(a, refStats.Background - a * st.Background);
                    break;
                }
                case NormalizationMethod.LinearFit:
                {
                    var fit = LinearFit(reference, input, settings);
                    out_[input.Kind] = ChannelTransform.Affine(fit.a, fit.b);
                    break;
                }
                case NormalizationMethod.HistogramMatch:
                    out_[input.Kind] = ChannelTransform.OfLut(HistogramMatchLUT(st, refStats, settings.HistogramClip));
                    break;
            }
        }
        return out_;
    }

    /// <summary>Robust iterative least squares fit ref ≈ a·channel + b.</summary>
    public static (float a, float b) LinearFit(NormalizationInput reference, NormalizationInput channel, NormalizationSettings settings)
    {
        if (reference.Image.Width != channel.Image.Width || reference.Image.Height != channel.Image.Height) return (1, 0);
        var opts = new StatsOptions { MaxSamples = 400_000, Region = reference.Region };
        if (settings.UseStarMask) { opts.Mask = reference.Mask; opts.MaskLevel = reference.MaskLevel; }
        var (xs, ys) = Statistics.PairedSample(channel.Image, reference.Image, opts, settings.UseStarMask ? channel.Mask : null);
        return RobustLinearFit(xs, ys, settings.FitRejectSigma, settings.FitIterations);
    }

    /// <summary>Iteratively re-weighted least squares with sigma rejection on the residuals.</summary>
    public static (float a, float b) RobustLinearFit(float[] x, float[] y, float rejectSigma = 2.5f, int iterations = 5)
    {
        if (x.Length != y.Length || x.Length <= 8) return (1, 0);
        var keep = new bool[x.Length];
        Array.Fill(keep, true);
        double a = 1, b = 0;
        for (int it = 0; it < Math.Max(1, iterations); it++)
        {
            double sx = 0, sy = 0, sxx = 0, sxy = 0, n = 0;
            for (int i = 0; i < x.Length; i++)
            {
                if (!keep[i]) continue;
                double xi = x[i], yi = y[i];
                sx += xi; sy += yi; sxx += xi * xi; sxy += xi * yi; n += 1;
            }
            if (n <= 8) break;
            double den = n * sxx - sx * sx;
            if (Math.Abs(den) <= 1e-30) break;
            a = (n * sxy - sx * sy) / den;
            b = (sy - a * sx) / n;
            // Residual sigma (robust).
            var res = new List<float>(x.Length);
            for (int i = 0; i < x.Length; i++) if (keep[i]) res.Add((float)(y[i] - (a * x[i] + b)));
            var arr = res.ToArray();
            Array.Sort(arr);
            float med = Statistics.Percentile(arr, 0.5);
            float sigma = 1.4826f * Statistics.MedianAbsoluteDeviation(arr, med);
            if (sigma <= 0) break;
            bool changed = false;
            for (int i = 0; i < x.Length; i++)
            {
                float r = (float)(y[i] - (a * x[i] + b));
                bool k = MathF.Abs(r - med) <= rejectSigma * sigma;
                if (k != keep[i]) { changed = true; keep[i] = k; }
            }
            if (!changed) break;
        }
        return ((float)a, (float)b);
    }

    /// <summary>Quantile mapping from the source distribution onto the reference distribution.</summary>
    public static UniformLUT HistogramMatchLUT(ChannelStats source, ChannelStats reference, float clip = 0.0005f, int knots = 2048, int tableSize = 65536)
    {
        double qLo = clip, qHi = 1 - (double)clip;
        float lo = source.Percentile(qLo), hi = source.Percentile(qHi);
        if (!(hi > lo) || source.SortedSample.Length == 0 || reference.SortedSample.Length == 0)
            return new UniformLUT(0, 1, [0, 1], 1, 1);
        // Knots in quantile space.
        var sx = new float[knots + 1]; var sy = new float[knots + 1];
        for (int i = 0; i <= knots; i++)
        {
            double q = qLo + (qHi - qLo) * i / knots;
            sx[i] = source.Percentile(q); sy[i] = reference.Percentile(q);
        }
        // Enforce monotonicity.
        for (int i = 1; i < sx.Length; i++) { sx[i] = MathF.Max(sx[i], sx[i - 1]); sy[i] = MathF.Max(sy[i], sy[i - 1]); }
        var table = new float[tableSize];
        int k = 0;
        for (int i = 0; i < tableSize; i++)
        {
            float x = lo + (hi - lo) * i / (tableSize - 1);
            while (k + 1 < sx.Length - 1 && sx[k + 1] <= x) k++;
            float x0 = sx[k], x1 = sx[k + 1];
            float t = x1 > x0 ? (x - x0) / (x1 - x0) : 0;
            table[i] = sy[k] + (sy[k + 1] - sy[k]) * MathF.Min(MathF.Max(t, 0), 1);
        }
        // Slopes for extrapolation: use the outer 1% of the mapping.
        float Slope(int i0, int i1) { float dx = sx[i1] - sx[i0]; return dx > 0 ? (sy[i1] - sy[i0]) / dx : 1; }
        int m = Math.Max(1, knots / 100);
        float sb = Slope(0, m), sa = Slope(knots - m, knots);
        return new UniformLUT(lo, hi, table, MathF.Max(sb, 1e-6f), MathF.Max(sa, 1e-6f));
    }
}
