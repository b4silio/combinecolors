using System.Diagnostics;

namespace AstroCore;

public enum RenderErrorKind { MissingChannels, PixelMath, NoOutput }

public sealed class RenderException : Exception
{
    public RenderErrorKind Kind { get; }
    public IReadOnlyList<ChannelKind> Missing { get; } = [];
    public PixelMathException? PixelMathError { get; }

    RenderException(RenderErrorKind kind, string message, IReadOnlyList<ChannelKind>? missing = null, PixelMathException? pm = null) : base(message)
    {
        Kind = kind; Missing = missing ?? []; PixelMathError = pm;
    }

    public static RenderException MissingChannels(IReadOnlyList<ChannelKind> k)
        => new(RenderErrorKind.MissingChannels, "Missing channels: " + string.Join(", ", k.Select(c => c.DisplayName())), k);
    public static RenderException PixelMath(PixelMathException e) => new(RenderErrorKind.PixelMath, e.Message, null, e);
    public static RenderException NoOutput() => new(RenderErrorKind.NoOutput, "Nothing to render.");
}

/// <summary>Normalisation state derived from the loaded channels and the normalisation settings.</summary>
public sealed class RenderContext
{
    public Dictionary<ChannelKind, ChannelTransform> Transforms = new();
    /// <summary>Whole-image statistics after normalisation.</summary>
    public Dictionary<ChannelKind, ChannelStats> NormalizedStats = new();
    /// <summary>Star-masked statistics after normalisation.</summary>
    public Dictionary<ChannelKind, ChannelStats> NormalizedMaskedStats = new();
    public ChannelKind? Reference;
    public float ReferenceBackgroundSigma;

    public static RenderContext Make(NormalizationSettings settings, IReadOnlyDictionary<ChannelKind, ChannelSource> sources)
    {
        var ctx = new RenderContext();
        var inputs = sources.Values.OrderBy(s => s.Kind.RawValue(), StringComparer.Ordinal).Select(s => s.NormalizationInput).ToList();
        ctx.Transforms = Normalizer.Transforms(settings, inputs);
        ctx.Reference = Normalizer.ReferenceChannel(settings, inputs);
        foreach (var (k, s) in sources)
        {
            var t = ctx.Transforms.GetValueOrDefault(k) ?? ChannelTransform.Identity;
            ctx.NormalizedStats[k] = t.Transform(s.Stats);
            ctx.NormalizedMaskedStats[k] = t.Transform(s.MaskedStats);
        }
        if (ctx.Reference is ChannelKind r && ctx.NormalizedMaskedStats.TryGetValue(r, out var rs)) ctx.ReferenceBackgroundSigma = rs.BackgroundSigma;
        else ctx.ReferenceBackgroundSigma = ctx.NormalizedMaskedStats.Values.FirstOrDefault()?.BackgroundSigma ?? 0;
        return ctx;
    }
}

public sealed class RenderRequest
{
    public Dictionary<ChannelKind, PlanarImage> Planes;
    /// <summary>
    /// Full-resolution samples (1×N planes, identical positions in every channel) used to derive
    /// post-combine stretch parameters independently of the preview resolution.
    /// </summary>
    public Dictionary<ChannelKind, PlanarImage>? AnalysisPlanes;
    public CombineSettings Settings;
    public RenderContext Context;
    /// <summary>When set, these resolved stretches are used for the post-combine stage instead of being derived.</summary>
    public List<ResolvedStretch>? FixedPostStretch;
    /// <summary>Apply denoise and detail inside this call (false while rendering strips).</summary>
    public bool ApplyDetail = true;
    /// <summary>
    /// Rendered pixels per full-resolution pixel (1 at full size, 0.5 at pyramid level 1, …);
    /// pixel-based radii such as the sharpening radius are scaled by it.
    /// </summary>
    public float Scale = 1;

    public RenderRequest(Dictionary<ChannelKind, PlanarImage> planes, CombineSettings settings, RenderContext context,
                         Dictionary<ChannelKind, PlanarImage>? analysisPlanes = null, List<ResolvedStretch>? fixedPostStretch = null, bool applyDetail = true, float scale = 1)
    {
        Planes = planes; AnalysisPlanes = analysisPlanes; Settings = settings; Context = context; FixedPostStretch = fixedPostStretch; ApplyDetail = applyDetail; Scale = scale;
    }
}

public sealed class RenderResult
{
    public required RGBImage Image { get; init; }
    /// <summary>The per-output-channel stretch used in post-combine mode (null for pre-combine).</summary>
    public List<ResolvedStretch>? PostStretch { get; init; }
    /// <summary>Per-channel stretch used in pre-combine mode.</summary>
    public required Dictionary<ChannelKind, ResolvedStretch> ChannelStretch { get; init; }
    public double Millis { get; init; }
}

public static class Renderer
{
    /// <summary>Channels the settings need that are not loaded.</summary>
    public static List<ChannelKind> MissingChannels(CombineSettings settings, IReadOnlySet<ChannelKind> available)
        => settings.RequiredChannels.Where(k => !available.Contains(k)).OrderBy(k => k.RawValue(), StringComparer.Ordinal).ToList();

    public static RenderResult Render(RenderRequest req)
    {
        var t0 = Stopwatch.StartNew();
        var s = req.Settings;
        var available = req.Planes.Keys.ToHashSet();
        var missing = MissingChannels(s, available);
        if (missing.Count > 0) throw RenderException.MissingChannels(missing);
        if (s.Mode == CombineMode.Monochrome && s.MonochromeChannels(available).Count == 0) throw RenderException.NoOutput();
        var anyPlane = req.Planes.Values.FirstOrDefault() ?? throw RenderException.NoOutput();
        int w = anyPlane.Width, h = anyPlane.Height;

        // 1. Normalised linear channels (+ gain, pedestal).
        var linear = new Dictionary<ChannelKind, PlanarImage>();
        var linStats = new Dictionary<ChannelKind, ChannelStats>();
        var needed = s.AllChannels(available); needed.IntersectWith(available);
        Expr? lumExpr = null;
        if (s.LuminanceApplies && s.Luminance.ResolvedExpression(available) is string src)
        {
            try { lumExpr = PixelMath.Parse(src); }
            catch (PixelMathException e) { throw RenderException.PixelMath(new PixelMathException("Luminance: " + e.Detail, e.Position)); }
        }
        foreach (var k in needed)
        {
            var (p, st) = PrepareLinear(k, req.Planes[k], s, req.Context);
            linear[k] = p; linStats[k] = st;
        }

        var channelStretch = new Dictionary<ChannelKind, ResolvedStretch>();
        RGBImage combined;
        PlanarImage? lumPlane = null;
        List<ResolvedStretch>? postStretch = null;

        if (s.StretchStage == StretchStage.PreCombine)
        {
            // 2. Stretch every channel on its own.
            var stretched = new Dictionary<ChannelKind, PlanarImage>();
            var stStats = new Dictionary<ChannelKind, ChannelStats>();
            var stretchKinds = s.Mode == CombineMode.Monochrome ? s.MonochromeChannels(available) : s.RequiredChannels;
            ResolvedStretch? linkedStretch = s.Stretch.Linked
                ? Stretch.ResolveLinked(s.Stretch, stretchKinds.Where(linStats.ContainsKey).Select(k => linStats[k]).ToList())
                : null;
            foreach (var k in needed)
            {
                var p = linear[k];
                ResolvedStretch r;
                var adj = s.Adjust(k);
                if (linkedStretch != null && stretchKinds.Contains(k) && adj.StretchBoost == 0) r = linkedStretch;
                else
                {
                    var ss = s.Stretch.Clone();
                    ss.TargetBackground = MathF.Min(MathF.Max(ss.TargetBackground + adj.StretchBoost, 0.01f), 0.9f);
                    r = Stretch.Resolve(ss, linStats[k]);
                }
                channelStretch[k] = r;
                r.Apply(p);
                ApplyPostStretchAdjust(adj, p);
                stretched[k] = p;
                if (UsesChannelStats(s))
                {
                    var st = linStats[k].Transformed(r.Evaluate, 1);
                    st = PostStretchAdjustedStats(adj, st);
                    stStats[k] = st;
                }
            }
            combined = Combine(stretched, stStats, s, w, h, out lumPlane);
            if (lumPlane == null && lumExpr != null) lumPlane = EvaluateLuminance(lumExpr, stretched, w, h);
        }
        else
        {
            // Weight acts as a linear multiplier here.
            foreach (var k in needed)
            {
                float wgt = s.Adjust(k).Weight;
                if (wgt != 1) { linear[k].Affine(wgt, 0); linStats[k] = linStats[k].Affine(wgt, 0); }
            }
            combined = Combine(linear, UsesChannelStats(s) ? linStats : new Dictionary<ChannelKind, ChannelStats>(), s, w, h, out var lumLinear);
            if (lumLinear == null && lumExpr != null) lumLinear = EvaluateLuminance(lumExpr, linear, w, h);
            // 3. Stretch parameters: fixed (cached), derived from the resolution-independent analysis
            //    samples, or — as a last resort — from this very render.
            //    Entries 0…2 are R, G, B; entry 3 (when present) is the luminance.
            List<ResolvedStretch> resolved;
            if (req.FixedPostStretch is { Count: >= 3 } fixed_ && (lumLinear == null || fixed_.Count >= 4)) resolved = fixed_;
            else
            {
                var source = combined;
                var lumSource = lumLinear;
                if (req.AnalysisPlanes is { } analysis && needed.All(analysis.ContainsKey))
                {
                    var aLinear = new Dictionary<ChannelKind, PlanarImage>();
                    foreach (var k in needed)
                    {
                        var (p, _) = PrepareLinear(k, analysis[k], s, req.Context);
                        float wgt = s.Adjust(k).Weight;
                        if (wgt != 1) p.Affine(wgt, 0);
                        aLinear[k] = p;
                    }
                    var first = analysis.Values.First();
                    int aw = first.Width, ah = first.Height;
                    source = Combine(aLinear, new Dictionary<ChannelKind, ChannelStats>(), s, aw, ah, out var aLum);
                    if (aLum == null && lumExpr != null) aLum = EvaluateLuminance(lumExpr, aLinear, aw, ah);
                    lumSource = aLum;
                }
                resolved = ResolvePostStretch(source, s.Stretch);
                if (lumSource != null) resolved.Add(Stretch.Resolve(s.Stretch, Statistics.FromSample(Subsample(lumSource))));
            }
            postStretch = resolved;
            var planes = combined.Planes;
            for (int i = 0; i < 3; i++) resolved[i].Apply(planes[i]);
            // Luminance: mixed in the linear domain, then its own auto stretch.
            if (lumLinear != null)
            {
                var r = resolved.Count >= 4 ? resolved[3] : Stretch.Resolve(s.Stretch, Statistics.FromSample(Subsample(lumLinear)));
                r.Apply(lumLinear);
                lumPlane = lumLinear;
            }
        }

        // 4. Luminance injection.
        if (lumPlane != null && s.Luminance.Amount > 0)
        {
            lumPlane.Clamp(0, 1);
            PostProcess.InjectLuminance(combined, lumPlane, s.Luminance.Amount, s.Luminance.Method);
        }

        // 5. Colour post-processing: selective colour bands, then the global controls.
        combined.Clamp01();
        PostProcess.ColorBands(combined, s.ColorBands);
        PostProcess.Apply(s.Post, combined);

        // 6. RGB stars.
        if (s.Stars.Enabled && req.Planes.TryGetValue(ChannelKind.Red, out var rr) && req.Planes.TryGetValue(ChannelKind.Green, out var gg) && req.Planes.TryGetValue(ChannelKind.Blue, out var bb))
        {
            var stars = StarsImage(rr, gg, bb, s, req.Context);
            PostProcess.BlendStars(combined, stars, s.Stars);
        }
        combined.Clamp01();
        // 7. Local contrast last (non point-wise; the strip renderer applies it on the whole image).
        if (req.ApplyDetail)
        {
            Denoise.Apply(combined, s.Denoise);
            PostProcess.Detail(combined, s.Detail, req.Scale);
        }
        return new RenderResult { Image = combined, PostStretch = postStretch, ChannelStretch = channelStretch, Millis = t0.Elapsed.TotalMilliseconds };
    }

    // Pieces

    /// <summary>Evaluates a luminance expression over the given planes; a plane returned unchanged is copied.</summary>
    static PlanarImage EvaluateLuminance(Expr e, Dictionary<ChannelKind, PlanarImage> planes, int w, int h)
    {
        var env = new PixelMathEnvironment(w, h);
        foreach (var (k, p) in planes) env.Set(k, p);
        try
        {
            var out_ = PixelMath.EvaluateToPlane(e, env);
            return planes.Values.Any(p => ReferenceEquals(p, out_)) ? out_.Copy() : out_;
        }
        catch (PixelMathException err) { throw RenderException.PixelMath(new PixelMathException("Luminance: " + err.Detail, err.Position)); }
    }

    static readonly string[] StatFunctions = ["cmed", "cmean", "cmin", "cmax", "cmad", "cbg", "cnoise", "cpct"];

    static bool UsesChannelStats(CombineSettings s)
    {
        if (s.Mode != CombineMode.PixelMath) return false;
        var src = string.Join(" ", s.PixelMath.Red, s.PixelMath.Green, s.PixelMath.Blue, s.PixelMath.Luminance).ToLowerInvariant();
        return StatFunctions.Any(src.Contains);
    }

    /// <summary>Copy + normalise + gain/pedestal.</summary>
    static (PlanarImage, ChannelStats) PrepareLinear(ChannelKind k, PlanarImage plane, CombineSettings s, RenderContext context)
    {
        var p = plane.Copy();
        var t = context.Transforms.GetValueOrDefault(k) ?? ChannelTransform.Identity;
        t.Apply(p);
        var st = context.NormalizedStats.GetValueOrDefault(k) ?? Statistics.FromSample(Subsample(p));
        var adj = s.Adjust(k);
        float offset = adj.OffsetSigma * context.ReferenceBackgroundSigma;
        if (adj.Gain != 1 || offset != 0)
        {
            p.Affine(adj.Gain, offset);
            st = st.Affine(adj.Gain, offset);
        }
        if (!adj.Enabled) p.Fill(0);
        return (p, st);
    }

    static void ApplyPostStretchAdjust(ChannelAdjust adj, PlanarImage p)
    {
        if (adj.BlackPoint != 0)
        {
            float a = 1 / MathF.Max(1 - adj.BlackPoint, 1e-3f);
            p.Affine(a, -adj.BlackPoint * a);
            p.Clamp(0, 1);
        }
        if (adj.Gamma != 1 && adj.Gamma > 0)
        {
            p.Clamp(0, 1);
            float g = adj.Gamma;
            Par.Rows(p.Height, p.Width, (y0, y1) => { var seg = p.Data.AsSpan(y0 * p.Width, (y1 - y0) * p.Width); Vec.Pow(seg, g, seg); });
        }
        if (adj.Weight != 1) p.Affine(adj.Weight, 0);
    }

    static ChannelStats PostStretchAdjustedStats(ChannelAdjust adj, ChannelStats st)
    {
        var s = st;
        if (adj.BlackPoint != 0)
        {
            float a = 1 / MathF.Max(1 - adj.BlackPoint, 1e-3f);
            s = s.Affine(a, -adj.BlackPoint * a);
        }
        if (adj.Gamma != 1 && adj.Gamma > 0) { float g = adj.Gamma; s = s.Transformed(x => MathF.Pow(MathF.Max(x, 0), g), 1); }
        if (adj.Weight != 1) s = s.Affine(adj.Weight, 0);
        return s;
    }

    static RGBImage Combine(Dictionary<ChannelKind, PlanarImage> planes, Dictionary<ChannelKind, ChannelStats> stats, CombineSettings s, int w, int h, out PlanarImage? lum)
    {
        lum = null;
        switch (s.Mode)
        {
            case CombineMode.Monochrome:
            {
                var kinds = s.MonochromeChannels(planes.Keys.ToHashSet()).Where(k => s.Adjust(k).Enabled).ToList();
                var gray = new PlanarImage(w, h);
                if (kinds.Count == 0) return new RGBImage(gray, gray.Copy(), gray.Copy());
                float k = 1f / kinds.Count;
                foreach (var kind in kinds) Vec.MulAdd(planes[kind].Data, k, gray.Data);
                return new RGBImage(gray, gray.Copy(), gray.Copy());
            }
            case CombineMode.Matrix:
            {
                var out_ = new RGBImage(w, h);
                var outPlanes = out_.Planes;
                for (int o = 0; o < 3; o++)
                {
                    foreach (var (k, p) in planes)
                    {
                        float wgt = s.Matrix.Weight(k, o);
                        if (wgt == 0) continue;
                        Vec.MulAdd(p.Data, wgt, outPlanes[o].Data);
                    }
                }
                return out_;
            }
            default:
            {
                PixelMathProgram.Compiled compiled;
                try { compiled = s.PixelMath.Compile(); } catch (PixelMathException e) { throw RenderException.PixelMath(e); }
                var env = new PixelMathEnvironment(w, h);
                foreach (var (k, p) in planes) env.Set(k, p, stats.GetValueOrDefault(k));
                try
                {
                    var r = PixelMath.EvaluateToPlane(compiled.Red, env);
                    var g = PixelMath.EvaluateToPlane(compiled.Green, env);
                    var b = PixelMath.EvaluateToPlane(compiled.Blue, env);
                    if (compiled.Luminance != null) lum = PixelMath.EvaluateToPlane(compiled.Luminance, env);
                    // Expressions may return the very same plane object (e.g. "OIII"); copy to keep planes independent.
                    PlanarImage Own(PlanarImage p) => planes.Values.Any(q => ReferenceEquals(q, p)) ? p.Copy() : p;
                    return new RGBImage(Own(r), Own(g), Own(b));
                }
                catch (PixelMathException e) { throw RenderException.PixelMath(e); }
            }
        }
    }

    static List<ResolvedStretch> ResolvePostStretch(RGBImage img, StretchSettings st)
    {
        var stats = img.Planes.Select(p => Statistics.FromSample(Subsample(p))).ToList();
        if (st.Linked)
        {
            var r = Stretch.ResolveLinked(st, stats);
            return [r, r, r];
        }
        return stats.Select(s => Stretch.Resolve(st, s)).ToList();
    }

    static float[] Subsample(PlanarImage p, int max = 300_000)
    {
        var o = new StatsOptions { MaxSamples = max };
        return Statistics.Sample(p, o).sample;
    }

    static RGBImage StarsImage(PlanarImage r, PlanarImage g, PlanarImage b, CombineSettings s, RenderContext context)
    {
        var planes = new List<PlanarImage>();
        var stats = new List<ChannelStats>();
        foreach (var (k, p) in new[] { (ChannelKind.Red, r), (ChannelKind.Green, g), (ChannelKind.Blue, b) })
        {
            var c = p.Copy();
            (context.Transforms.GetValueOrDefault(k) ?? ChannelTransform.Identity).Apply(c);
            planes.Add(c);
            stats.Add(context.NormalizedStats.GetValueOrDefault(k) ?? Statistics.FromSample(Subsample(c)));
        }
        // Already-stretched star images (bright stars reach high values) must not be stretched again.
        bool looksStretched = stats.Max(st => st.Percentile(0.999)) > 0.15f;
        bool doStretch = s.Stars.Stretch switch
        {
            StarsStretchMode.Auto => !looksStretched,
            StarsStretchMode.None => false,
            _ => true,
        };
        if (doStretch)
        {
            var st = new StretchSettings { Method = StretchMethod.AutoSTF, TargetBackground = 0.1f };
            var resolved = Stretch.ResolveLinked(st, stats);
            foreach (var p in planes) resolved.Apply(p);
        }
        return new RGBImage(planes[0], planes[1], planes[2]);
    }

    /// <summary>Builds the 1×N analysis sample planes for a set of sources.</summary>
    public static Dictionary<ChannelKind, PlanarImage> AnalysisSamples(IReadOnlyDictionary<ChannelKind, ChannelSource> sources, int maxSamples = 400_000)
    {
        var kinds = sources.Keys.OrderBy(k => k.RawValue(), StringComparer.Ordinal).ToList();
        var region = sources.Values.FirstOrDefault()?.Region;
        var arrays = Statistics.MultiSample(kinds.Select(k => sources[k].Image).ToList(), maxSamples, region);
        var out_ = new Dictionary<ChannelKind, PlanarImage>();
        for (int i = 0; i < kinds.Count; i++) if (arrays[i].Length > 0) out_[kinds[i]] = PlanarImage.Wrap(arrays[i].Length, 1, arrays[i]);
        return out_;
    }

    // Full resolution, strip-wise

    /// <summary>
    /// Renders the full-resolution image in horizontal strips to bound memory.
    /// <paramref name="postStretch"/> should come from a preview render so the result matches what was shown.
    /// </summary>
    public static RGBImage? RenderFull(IReadOnlyDictionary<ChannelKind, ChannelSource> sources, CombineSettings settings, RenderContext context,
                                       List<ResolvedStretch>? postStretch, PixelRect? region = null, int stripPixels = 4_000_000,
                                       Action<double>? progress = null, Func<bool>? isCancelled = null)
    {
        var any = sources.Values.FirstOrDefault() ?? throw RenderException.NoOutput();
        var crop = region?.Clamped(any.Width, any.Height) ?? PixelRect.Full(any.Width, any.Height);
        int w = crop.Width, h = crop.Height;
        int rows = Math.Max(1, Math.Min(h, stripPixels / w));
        var out_ = new RGBImage(w, h);
        int y = 0;
        var analysisPlanes = AnalysisSamples(sources);
        var fixed_ = postStretch;
        if (fixed_ == null && settings.StretchStage == StretchStage.PostCombine)
        {
            // Derive once from the analysis planes.
            var probe = Render(new RenderRequest(analysisPlanes, settings, context, null, applyDetail: false));
            fixed_ = probe.PostStretch;
        }
        while (y < h)
        {
            if (isCancelled?.Invoke() == true) return null;
            int hh = Math.Min(rows, h - y);
            var planes = new Dictionary<ChannelKind, PlanarImage>();
            foreach (var (k, s) in sources) planes[k] = s.Image.Cropped(crop.X, crop.Y + y, w, hh);
            var res = Render(new RenderRequest(planes, settings, context, null, fixed_, applyDetail: false));
            var srcPlanes = res.Image.Planes; var dstPlanes = out_.Planes;
            for (int c = 0; c < 3; c++) Array.Copy(srcPlanes[c].Data, 0, dstPlanes[c].Data, y * w, w * hh);
            y += hh;
            progress?.Invoke((double)y / h);
        }
        if (isCancelled?.Invoke() == true) return null;
        Denoise.Apply(out_, settings.Denoise);
        PostProcess.Detail(out_, settings.Detail, 1);
        return out_;
    }
}
