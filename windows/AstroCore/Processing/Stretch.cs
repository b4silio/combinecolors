namespace AstroCore;

public enum StretchMethod { None, AutoSTF, Mtf, Arcsinh, Ghs, Log }

public static class StretchMethods
{
    public static readonly StretchMethod[] All = [StretchMethod.None, StretchMethod.AutoSTF, StretchMethod.Mtf, StretchMethod.Arcsinh, StretchMethod.Ghs, StretchMethod.Log];

    public static string DisplayName(this StretchMethod m) => m switch
    {
        StretchMethod.None => "None (linear)",
        StretchMethod.AutoSTF => "Auto STF",
        StretchMethod.Mtf => "Histogram (MTF)",
        StretchMethod.Arcsinh => "Arcsinh",
        StretchMethod.Ghs => "Hyperbolic (GHS)",
        _ => "Logarithmic",
    };
}

/// <summary>User-facing stretch parameters.</summary>
public sealed partial class StretchSettings : IEquatable<StretchSettings>
{
    public StretchMethod Method = StretchMethod.AutoSTF;
    /// <summary>Auto STF: where the median lands (PixInsight default 0.25).</summary>
    public float TargetBackground = 0.2f;
    /// <summary>Auto black point in units of MADN below the median (PixInsight default −2.8).</summary>
    public float ShadowsClipping = -2.8f;
    /// <summary>Highlight clipping as a percentile in [0,1]; 1 = none.</summary>
    public float HighlightsPercentile = 1;
    /// <summary>Use the same transform for all channels (computed from the averaged statistics).</summary>
    public bool Linked = true;
    // Manual MTF (applied in the normalised [0,1] domain after the automatic black point).
    public float MtfShadows = 0;
    public float MtfMidtones = 0.5f;
    public float MtfHighlights = 1;
    // Arcsinh
    public float ArcsinhStretch = 200;
    // GHS
    public float GhsLogD = 5;      // D = exp(logD) - 1
    public float GhsB = 2;
    public float GhsSP = 0;
    public float GhsLP = 0;
    public float GhsHP = 1;
    // Log
    public float LogK = 2000;
    /// <summary>Output gamma after the stretch (1 = none).</summary>
    public float Gamma = 1;

    public StretchSettings Clone() => (StretchSettings)MemberwiseClone();
    public bool Equals(StretchSettings? o) => o != null && SettingsJson.SameJson(this, o);
    public override bool Equals(object? obj) => Equals(obj as StretchSettings);
    public override int GetHashCode() => SettingsJson.JsonHash(this);
}

/// <summary>A stretch resolved for one channel: fixed numbers, ready to be applied.</summary>
public sealed class ResolvedStretch : IEquatable<ResolvedStretch>
{
    public StretchMethod Method = StretchMethod.None;
    /// <summary>Input black / white points (linear domain).</summary>
    public float C0 = 0;
    public float C1 = 1;
    /// <summary>MTF midtones balance (for AutoSTF / Mtf).</summary>
    public float Midtones = 0.5f;
    public float MtfShadows = 0;
    public float MtfHighlights = 1;
    public float ArcsinhStretch = 200;
    public float GhsD = 100;
    public float GhsB = 2;
    public float GhsSP = 0;
    public float GhsLP = 0;
    public float GhsHP = 1;
    public float LogK = 2000;
    public float Gamma = 1;

    public static ResolvedStretch Identity => new();

    public ResolvedStretch Clone() => (ResolvedStretch)MemberwiseClone();

    public bool Equals(ResolvedStretch? o) => o != null && Method == o.Method && C0 == o.C0 && C1 == o.C1 && Midtones == o.Midtones && MtfShadows == o.MtfShadows
        && MtfHighlights == o.MtfHighlights && ArcsinhStretch == o.ArcsinhStretch && GhsD == o.GhsD && GhsB == o.GhsB && GhsSP == o.GhsSP && GhsLP == o.GhsLP && GhsHP == o.GhsHP && LogK == o.LogK && Gamma == o.Gamma;
    public override bool Equals(object? obj) => Equals(obj as ResolvedStretch);
    public override int GetHashCode() => HashCode.Combine(Method, C0, C1, Midtones, Gamma);

    /// <summary>Scalar reference implementation (used by tests and pixel read-outs).</summary>
    public float Evaluate(float x)
    {
        if (Method == StretchMethod.None) return (Gamma != 1 && Gamma > 0) ? MathF.Pow(MathF.Max(x, 0), Gamma) : x;
        float u = (x - C0) / MathF.Max(C1 - C0, 1e-12f);
        u = MathF.Min(MathF.Max(u, 0), 1);
        float y;
        switch (Method)
        {
            case StretchMethod.AutoSTF: y = Stretch.Mtf(Midtones, u); break;
            case StretchMethod.Mtf:
            {
                float v = MathF.Min(MathF.Max((u - MtfShadows) / MathF.Max(MtfHighlights - MtfShadows, 1e-12f), 0), 1);
                y = Stretch.Mtf(Midtones, v);
                break;
            }
            case StretchMethod.Arcsinh: y = ArcsinhStretch > 0 ? MathF.Asinh(ArcsinhStretch * u) / MathF.Asinh(ArcsinhStretch) : u; break;
            case StretchMethod.Log: y = LogK > 0 ? Vec.Log1pScalar(LogK * u) / Vec.Log1pScalar(LogK) : u; break;
            case StretchMethod.Ghs: y = Stretch.Ghs(u, GhsD, GhsB, GhsSP, GhsLP, GhsHP); break;
            default: y = u; break;
        }
        if (Gamma != 1 && Gamma > 0) y = MathF.Pow(MathF.Max(y, 0), Gamma);
        return y;
    }

    /// <summary>Applies the stretch in place to a whole plane.</summary>
    public void Apply(PlanarImage plane)
    {
        if (Method == StretchMethod.None && Gamma == 1) return;
        var p = plane.Data.AsSpan();
        if (Method != StretchMethod.None)
        {
            // u = clamp((x - c0) / (c1 - c0), 0, 1)
            float a = 1 / MathF.Max(C1 - C0, 1e-12f);
            float b = -C0 * a;
            Vec.ScaleAdd(p, a, b, p);
            Vec.Clip(p, 0, 1, p);
        }
        switch (Method)
        {
            case StretchMethod.None: break;
            case StretchMethod.AutoSTF: Stretch.ApplyMtf(Midtones, plane); break;
            case StretchMethod.Mtf:
            {
                float a = 1 / MathF.Max(MtfHighlights - MtfShadows, 1e-12f);
                float b = -MtfShadows * a;
                Vec.ScaleAdd(p, a, b, p);
                Vec.Clip(p, 0, 1, p);
                Stretch.ApplyMtf(Midtones, plane);
                break;
            }
            case StretchMethod.Arcsinh:
            {
                if (ArcsinhStretch <= 0) break;
                float s = ArcsinhStretch, inv = 1 / MathF.Asinh(ArcsinhStretch);
                Par.Rows(plane.Height, plane.Width, (y0, y1) =>
                {
                    var seg = plane.Data.AsSpan(y0 * plane.Width, (y1 - y0) * plane.Width);
                    for (int i = 0; i < seg.Length; i++) seg[i] = MathF.Asinh(s * seg[i]) * inv;
                });
                break;
            }
            case StretchMethod.Log:
            {
                if (LogK <= 0) break;
                float s = LogK, inv = 1 / Vec.Log1pScalar(LogK);
                Par.Rows(plane.Height, plane.Width, (y0, y1) =>
                {
                    var seg = plane.Data.AsSpan(y0 * plane.Width, (y1 - y0) * plane.Width);
                    for (int i = 0; i < seg.Length; i++) seg[i] = Vec.Log1pScalar(s * seg[i]) * inv;
                });
                break;
            }
            case StretchMethod.Ghs: Stretch.ApplyGhs(plane, GhsD, GhsB, GhsSP, GhsLP, GhsHP); break;
        }
        if (Gamma != 1 && Gamma > 0)
        {
            Vec.Clip(p, 0, 1, p);
            float g = Gamma;
            Par.Rows(plane.Height, plane.Width, (y0, y1) =>
            {
                var seg = plane.Data.AsSpan(y0 * plane.Width, (y1 - y0) * plane.Width);
                Vec.Pow(seg, g, seg);
            });
        }
    }
}

public static class Stretch
{
    /// <summary>PixInsight midtones transfer function.</summary>
    public static float Mtf(float m, float x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (m == 0.5f) return x;
        return (m - 1) * x / ((2 * m - 1) * x - m);
    }

    public static void ApplyMtf(float m, PlanarImage plane)
    {
        if (m == 0.5f) return;
        float num = m - 1, den = 2 * m - 1, negm = -m;
        Par.Rows(plane.Height, plane.Width, (y0, y1) =>
        {
            var seg = plane.Data.AsSpan(y0 * plane.Width, (y1 - y0) * plane.Width);
            for (int i = 0; i < seg.Length; i++)
            {
                float x = seg[i];
                seg[i] = num * x / (den * x + negm);
            }
        });
    }

    /// <summary>
    /// Generalised hyperbolic stretch on the normalised domain [0,1].
    /// T(v) = 1 − (1 + D·b·v)^(−1/b) (b &gt; 0) or 1 − exp(−D·v) (b = 0), mirrored about SP,
    /// with linear continuation below LP and above HP, then normalised to [0,1].
    /// </summary>
    public static float Ghs(float x, float D, float b, float SP, float LP, float HP)
    {
        if (D <= 0) return x;
        float lp = MathF.Min(LP, SP), hp = MathF.Max(HP, SP);
        float g0 = GhsRaw(0, D, b, SP, lp, hp), g1 = GhsRaw(1, D, b, SP, lp, hp);
        float d = g1 - g0;
        if (d <= 0) return x;
        return (GhsRaw(x, D, b, SP, lp, hp) - g0) / d;
    }

    public static void ApplyGhs(PlanarImage plane, float D, float b, float SP, float LP, float HP)
    {
        if (D <= 0) return;
        float lp = MathF.Min(LP, SP), hp = MathF.Max(HP, SP);
        float g0 = GhsRaw(0, D, b, SP, lp, hp), g1 = GhsRaw(1, D, b, SP, lp, hp);
        float d = g1 - g0;
        if (d <= 0) return;
        float fLP = GhsF(lp, D, b, SP), dLP = GhsDF(lp, D, b, SP);
        float fHP = GhsF(hp, D, b, SP), dHP = GhsDF(hp, D, b, SP);
        float invd = 1 / d;
        Par.Rows(plane.Height, plane.Width * 4, (y0, y1) =>
        {
            var seg = plane.Data.AsSpan(y0 * plane.Width, (y1 - y0) * plane.Width);
            for (int i = 0; i < seg.Length; i++)
            {
                float x = seg[i];
                float v;
                if (x < lp) v = fLP + dLP * (x - lp);
                else if (x > hp) v = fHP + dHP * (x - hp);
                else v = GhsF(x, D, b, SP);
                seg[i] = (v - g0) * invd;
            }
        });
    }

    static float GhsT(float v, float D, float b) => b > 0 ? 1 - MathF.Pow(1 + D * b * v, -1 / b) : 1 - MathF.Exp(-D * v);
    static float GhsDT(float v, float D, float b) => b > 0 ? D * MathF.Pow(1 + D * b * v, -1 / b - 1) : D * MathF.Exp(-D * v);
    static float GhsF(float t, float D, float b, float SP) => t >= SP ? GhsT(t - SP, D, b) : -GhsT(SP - t, D, b);
    static float GhsDF(float t, float D, float b, float SP) => GhsDT(MathF.Abs(t - SP), D, b);
    static float GhsRaw(float t, float D, float b, float SP, float lp, float hp)
    {
        if (t < lp) return GhsF(lp, D, b, SP) + GhsDF(lp, D, b, SP) * (t - lp);
        if (t > hp) return GhsF(hp, D, b, SP) + GhsDF(hp, D, b, SP) * (t - hp);
        return GhsF(t, D, b, SP);
    }

    // Resolution from statistics

    /// <summary>Auto-STF parameters (PixInsight algorithm) from a median and a normalised MAD.</summary>
    public static (float c0, float c1, float midtones) AutoSTF(float median, float madn, float targetBackground, float shadowsClipping)
    {
        if (madn <= 0)
        {
            // Flat image: just map the median to the target background.
            float m = median > 0 && median < 1 ? Mtf(targetBackground, median) : 0.5f;
            return (0, 1, m);
        }
        if (median <= 0.5f)
        {
            float c0 = MathF.Min(MathF.Max(median + shadowsClipping * madn, 0), 1);
            float c1 = 1;
            float m = Mtf(targetBackground, MathF.Max(median - c0, 1e-7f) / MathF.Max(c1 - c0, 1e-7f));
            return (c0, c1, m);
        }
        else
        {
            // Inverted image.
            float c1 = MathF.Min(MathF.Max(median - shadowsClipping * madn, 0), 1);
            float c0 = 0;
            float m = Mtf(MathF.Max(c1 - median, 1e-7f) / MathF.Max(c1 - c0, 1e-7f), targetBackground);
            return (c0, c1, m);
        }
    }

    /// <summary>Resolve the settings for a channel with the given (already normalised) statistics.</summary>
    public static ResolvedStretch Resolve(StretchSettings s, ChannelStats stats)
        => Resolve(s, stats.Median, stats.Madn, stats.Percentile(s.HighlightsPercentile), stats.Max);

    /// <summary>Linked resolution: statistics are averaged across channels.</summary>
    public static ResolvedStretch ResolveLinked(StretchSettings s, IReadOnlyList<ChannelStats> stats)
    {
        if (stats.Count == 0) return ResolvedStretch.Identity;
        float med = 0, madn = 0, hl = float.NegativeInfinity, mx = float.NegativeInfinity;
        foreach (var st in stats)
        {
            med += st.Median; madn += st.Madn;
            hl = MathF.Max(hl, st.Percentile(s.HighlightsPercentile));
            mx = MathF.Max(mx, st.Max);
        }
        med /= stats.Count; madn /= stats.Count;
        return Resolve(s, med, madn, hl, mx);
    }

    public static ResolvedStretch Resolve(StretchSettings s, float median, float madn, float highlight, float max)
    {
        var r = new ResolvedStretch { Method = s.Method, Gamma = s.Gamma };
        if (s.Method == StretchMethod.None) return r;
        var auto = AutoSTF(median, madn, s.TargetBackground, s.ShadowsClipping);
        r.C0 = auto.c0;
        r.C1 = s.HighlightsPercentile < 1 ? MathF.Max(highlight, auto.c0 + 1e-6f) : auto.c1;
        if (s.HighlightsPercentile < 1 && s.Method == StretchMethod.AutoSTF)
        {
            // Re-derive the midtones for the narrower range so the median still lands on target.
            r.Midtones = Mtf(s.TargetBackground, MathF.Max(median - r.C0, 1e-7f) / MathF.Max(r.C1 - r.C0, 1e-7f));
        }
        else r.Midtones = auto.midtones;
        switch (s.Method)
        {
            case StretchMethod.Mtf:
                r.Midtones = s.MtfMidtones;
                r.MtfShadows = s.MtfShadows;
                r.MtfHighlights = s.MtfHighlights;
                break;
            case StretchMethod.Arcsinh: r.ArcsinhStretch = s.ArcsinhStretch; break;
            case StretchMethod.Ghs:
                r.GhsD = MathF.Exp(s.GhsLogD) - 1;
                r.GhsB = s.GhsB; r.GhsSP = s.GhsSP; r.GhsLP = s.GhsLP; r.GhsHP = s.GhsHP;
                break;
            case StretchMethod.Log: r.LogK = s.LogK; break;
        }
        return r;
    }
}
