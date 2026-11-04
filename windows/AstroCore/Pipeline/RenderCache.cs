namespace AstroCore;

/// <summary>
/// What a preview render reads: the channel sources, the stars layer, the normalisation context and
/// the region of the pyramid level rendered. Two requests with equal keys read the same pixels.
/// </summary>
public sealed record RenderInputKey(IReadOnlyDictionary<ChannelKind, Guid> Sources, Guid? Stars, Guid Context, int Level, int X, int Y, int Width, int Height)
{
    public bool Equals(RenderInputKey? o)
        => o != null && Stars == o.Stars && Context == o.Context && Level == o.Level && X == o.X && Y == o.Y && Width == o.Width && Height == o.Height
           && Sources.Count == o.Sources.Count && Sources.All(kv => o.Sources.TryGetValue(kv.Key, out var id) && id == kv.Value);
    public override int GetHashCode() => HashCode.Combine(Context, Level, X, Y, Width, Height, Sources.Count);
}

/// <summary>
/// Keeps the intermediate images of the last preview render so that a change late in the pipeline
/// only re-runs the stages after it. Four intermediates, each valid while its own inputs are unchanged:
/// <list type="bullet">
/// <item><b>combined</b> — steps 1–4: normalise, stretch, combine, luminance;</item>
/// <item><b>developed</b> — the above plus colour (bands, colour, balance, contrast) and stars;</item>
/// <item><b>denoised</b> — the above plus denoise;</item>
/// <item><b>detailed</b> — the above plus detail (local contrast, sharpening), the last finishing stage.</item>
/// </list>
/// The stars, when they go in last, are blended on a copy of the last one, so a star slider
/// re-blends only. Moving a detail slider therefore costs detail, a denoise slider denoise +
/// detail, a colour slider colour + stars + denoise + detail,
/// and only a change to the channels, the stretch or the combination runs the whole pipeline. Even
/// then the single channels' prepared planes (normalised, stretched, adjusted) are kept per channel
/// (<see cref="ChannelPlaneStore"/>): a gain or weight change on one channel prepares that channel only.
/// <para>
/// The cache holds up to four images of the preview size (an intermediate behind a neutral stage is
/// the same object as the one before it, not a copy). Renders with a live correction, or without the
/// detail stage, bypass it. Not thread safe: one render queue owns it. The result image of a render
/// whose detail stage is neutral is the cached intermediate itself — read it, do not write to it.
/// </para>
/// </summary>
public sealed class RenderCache
{
    public enum Stage { Combine, Colour, Denoise, Detail, Stars, Nothing }

    RenderInputKey? inputs;
    readonly ChannelPlaneStore channels = new();
    (CombineSettings settings, Renderer.Developed developed)? combined;
    (CombineSettings settings, RGBImage image, DetailOverview? overview)? developed;
    (DenoiseSettings settings, float scale, RGBImage @base, RGBImage image)? denoised;
    /// <summary>The detail stage's output, kept: a star slider re-blends only.</summary>
    (DetailSettings settings, float scale, RGBImage @base, RGBImage image)? detailed;

    public void Clear() { inputs = null; channels.Clear(); combined = null; developed = null; denoised = null; detailed = null; }

    /// <summary>Settings as far as the combined stage sees them (later stages neutral).</summary>
    internal static CombineSettings CombineSettingsOf(CombineSettings s)
    {
        var c = DevelopSettingsOf(s);
        c.ColorBands = new ColorBandsSettings(); c.Post = new PostSettings(); c.Tone = new ToneSettings(); c.Grading = new ColorGradingSettings(); c.Stars = new StarsBlendSettings();
        return c;
    }

    /// <summary>Settings as far as the developed stage sees them (denoise and detail neutral, and the stars when they go in last).</summary>
    internal static CombineSettings DevelopSettingsOf(CombineSettings s)
    {
        var c = s.Clone();
        c.Denoise = new DenoiseSettings(); c.Detail = new DetailSettings();
        if (s.Stars.Stage == StarsBlendStage.Last) c.Stars = new StarsBlendSettings();
        return c;
    }

    public RenderResult Render(RenderRequest req, RenderInputKey key)
    {
        if (!req.ApplyDetail || req.Correction != null) return Renderer.Render(req);
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        var plan = new Renderer.Plan(req);
        var s = plan.Settings;
        var ranFrom = Stage.Nothing;

        // 1–4. Combined: reused while the inputs, the settings up to the luminance and the fixed
        //      stretch agree. A request without a fixed stretch derives it from the same inputs, so
        //      it matches whatever the cached render used.
        var combineSettings = CombineSettingsOf(s);
        bool reuse = key.Equals(inputs) && combined is { } c0 && c0.settings.Equals(combineSettings);
        if (reuse && req.FixedPostStretch is { } f && !SameStretch(f, combined!.Value.developed.PostStretch)) reuse = false;
        int reusedChannels = 0;
        if (!reuse)
        {
            if (!key.Equals(inputs)) channels.Clear();
            var d = Renderer.CombineStage(plan, req.Planes, req.Context, req.AnalysisPlanes, req.FixedPostStretch, null, channels);
            reusedChannels = d.ReusedChannels;
            inputs = key;
            combined = (combineSettings, d);
            developed = null; denoised = null;
            ranFrom = Stage.Combine;
        }
        var @base = combined!.Value.developed;

        // 5–6. Developed: colour and stars.
        var developSettings = DevelopSettingsOf(s);
        if (developed is not { } d0 || !d0.settings.Equals(developSettings))
        {
            var img = @base.Image.Copy();
            Renderer.ColourStage(img, plan, req.Planes, req.Context, req.Stars);
            developed = (developSettings, img, null);
            denoised = null; detailed = null;
            if (ranFrom == Stage.Nothing) ranFrom = Stage.Colour;
        }
        if (!s.Detail.IsNeutral && developed!.Value.overview == null)
        {
            var dv = developed.Value;
            developed = (dv.settings, dv.image, Renderer.DetailOverview(plan, req, @base.PostStretch));
        }

        // 7a. Denoise on a copy of the developed image, kept: a detail slider then re-runs the
        //     detail only.
        bool starsLast = s.Stars.Enabled && s.Stars.Stage == StarsBlendStage.Last;
        RGBImage afterDenoise;
        if (s.Denoise.IsNeutral) afterDenoise = developed!.Value.image;
        else if (denoised is { } dn && dn.settings.Equals(s.Denoise) && dn.scale == req.Scale && ReferenceEquals(dn.@base, developed!.Value.image)) afterDenoise = dn.image;
        else
        {
            var img = developed!.Value.image.Copy();
            Denoise.Apply(img, s.Denoise, req.Scale);
            denoised = (s.Denoise.Clone(), req.Scale, developed!.Value.image, img); detailed = null;
            afterDenoise = img;
            if (ranFrom == Stage.Nothing) ranFrom = Stage.Denoise;
        }

        // 7b. Detail (local contrast, sharpening) on the denoised image, on a copy, kept: a star
        //     slider then re-blends only.
        RGBImage afterDetail;
        if (s.Detail.IsNeutral) afterDetail = afterDenoise;
        else if (detailed is { } dt && dt.settings.Equals(s.Detail) && dt.scale == req.Scale && ReferenceEquals(dt.@base, afterDenoise)) afterDetail = dt.image;
        else
        {
            var img = afterDenoise.Copy();
            PostProcess.Detail(img, s.Detail, req.Scale, developed!.Value.overview);
            detailed = (s.Detail.Clone(), req.Scale, afterDenoise, img);
            afterDetail = img;
            if (ranFrom == Stage.Nothing) ranFrom = Stage.Detail;
        }

        // 7c. The stars last, on a copy.
        RGBImage output;
        if (starsLast)
        {
            output = afterDetail.Copy();
            Renderer.StarsStage(output, plan, req.Planes, req.Context, req.Stars);
            if (ranFrom == Stage.Nothing) ranFrom = Stage.Stars;
        }
        else output = afterDetail;
        return new RenderResult { Image = output, PostStretch = @base.PostStretch, ChannelStretch = @base.ChannelStretch, Millis = t0.Elapsed.TotalMilliseconds, RanFrom = ranFrom, ReusedChannels = reusedChannels };
    }

    static bool SameStretch(List<ResolvedStretch> a, List<ResolvedStretch>? b)
        => b != null && a.Count == b.Count && a.Zip(b).All(p => p.First.Equals(p.Second));
}
