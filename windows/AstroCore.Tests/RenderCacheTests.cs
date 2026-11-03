using AstroCore;

namespace AstroCore.Tests;

/// <summary>The render cache must give the very same pixels as a render from scratch, whatever stage changed.</summary>
public class RenderCacheTests
{
    static Dictionary<ChannelKind, ChannelSource> Sources() => new()
    {
        [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha, 1, 7, 160, 120),
        [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 1, 8, 160, 120),
        [ChannelKind.SII] = Synth.Source(ChannelKind.SII, 1, 9, 160, 120),
    };

    static RenderInputKey Key(Dictionary<ChannelKind, ChannelSource> s, RenderContext ctx, int level = 0)
        => new(s.ToDictionary(kv => kv.Key, kv => kv.Value.Id), null, ctx.Id, level, 0, 0, 160 >> level, 120 >> level);

    static void AssertSame(RGBImage a, RGBImage b, string what)
    {
        Assert.Equal(a.Width, b.Width);
        foreach (var (p, q) in a.Planes.Zip(b.Planes))
        {
            float maxDiff = 0;
            for (int i = 0; i < p.Count; i++) maxDiff = Math.Max(maxDiff, Math.Abs(p.Data[i] - q.Data[i]));
            Assert.True(maxDiff == 0, $"{what}: max difference {maxDiff}");
        }
    }

    [Fact]
    public void CachedRendersMatchFreshOnesAndReuseTheRightStages()
    {
        var srcs = Sources();
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!);
        s.Detail.Clarity = 0.4f; s.Denoise.Amount = 0.3f; s.Post.Saturation = 0.2f;
        var ctx = RenderContext.Make(s.Normalization, srcs);
        var analysis = Renderer.AnalysisSamples(srcs);
        var cache = new RenderCache();
        var planes = srcs.ToDictionary(kv => kv.Key, kv => kv.Value.Pyramid.Level(0));
        RenderRequest Request(CombineSettings st) => new(planes, st, ctx, analysis);
        void Check(CombineSettings st, RenderCache.Stage expect, string what)
        {
            var cached = cache.Render(Request(st.Clone()), Key(srcs, ctx));
            var fresh = Renderer.Render(Request(st.Clone()));
            AssertSame(cached.Image, fresh.Image, what);
            Assert.Equal(expect, cached.RanFrom);
        }
        Check(s, RenderCache.Stage.Combine, "first render runs everything");
        Check(s, RenderCache.Stage.Detail, "same settings: only the detail copy runs");
        s.Detail.Sharpen = 0.8f;
        Check(s, RenderCache.Stage.Detail, "detail change");
        s.Denoise.Amount = 0.6f;
        Check(s, RenderCache.Stage.Denoise, "denoise change");
        s.Grading.SetBalance(ToneRange.Midtones, new ToneBalance { CyanRed = 0.3f });
        Check(s, RenderCache.Stage.Colour, "grading change");
        s.Stretch.TargetBackground = 0.3f;
        Check(s, RenderCache.Stage.Combine, "stretch change");
        s.Detail = new DetailSettings(); s.Denoise = new DenoiseSettings();
        Check(s, RenderCache.Stage.Nothing, "neutral late stages: the developed image is returned as is");
        s.StretchStage = StretchStage.PostCombine;
        Check(s, RenderCache.Stage.Combine, "post-combine stretch");
        // A fixed post-stretch equal to the derived one is a hit; a different one is not.
        var derived = cache.Render(Request(s.Clone()), Key(srcs, ctx));
        var fixedReq = Request(s.Clone()); fixedReq.FixedPostStretch = derived.PostStretch;
        Assert.Equal(RenderCache.Stage.Nothing, cache.Render(fixedReq, Key(srcs, ctx)).RanFrom);
        fixedReq.FixedPostStretch = derived.PostStretch!.Select(_ => ResolvedStretch.Identity).ToList();
        var other = cache.Render(fixedReq, Key(srcs, ctx));
        Assert.Equal(RenderCache.Stage.Combine, other.RanFrom);
        AssertSame(other.Image, Renderer.Render(fixedReq).Image, "fixed stretch differs");
        // Other inputs (another level, another context) start over.
        var ctx2 = RenderContext.Make(s.Normalization, srcs);
        Assert.Equal(RenderCache.Stage.Combine, cache.Render(Request(s.Clone()), Key(srcs, ctx2)).RanFrom);
        var lvl1 = srcs.ToDictionary(kv => kv.Key, kv => kv.Value.Pyramid.Level(1));
        var r1 = cache.Render(new RenderRequest(lvl1, s.Clone(), ctx2, analysis), Key(srcs, ctx2, 1));
        Assert.Equal(RenderCache.Stage.Combine, r1.RanFrom);
        Assert.Equal(80, r1.Image.Width);
    }

    [Fact]
    public void OneChannelChangePreparesThatChannelOnly()
    {
        var srcs = Sources();
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!);
        var ctx = RenderContext.Make(s.Normalization, srcs);
        var analysis = Renderer.AnalysisSamples(srcs);
        var cache = new RenderCache();
        var planes = srcs.ToDictionary(kv => kv.Key, kv => kv.Value.Pyramid.Level(0));
        void Check(CombineSettings st, int reused, string what)
        {
            var cached = cache.Render(new RenderRequest(planes, st.Clone(), ctx, analysis), Key(srcs, ctx));
            var fresh = Renderer.Render(new RenderRequest(planes, st.Clone(), ctx, analysis));
            AssertSame(cached.Image, fresh.Image, what);
            Assert.Equal(RenderCache.Stage.Combine, cached.RanFrom);
            Assert.True(reused == cached.ReusedChannels, $"{what}: reused {cached.ReusedChannels}, expected {reused}");
        }
        Check(s, 0, "first render prepares every channel");
        var a = s.Adjust(ChannelKind.OIII).Clone(); a.Gain = 1.4f; s.SetAdjust(ChannelKind.OIII, a);
        Check(s, 0, "gain of one channel with a linked stretch: the stretch of every channel moves");
        s.Stretch.Linked = false;
        Check(s, 0, "unlinked: every channel stretched on its own");
        a = a.Clone(); a.Gain = 1.8f; s.SetAdjust(ChannelKind.OIII, a);
        Check(s, 2, "gain of one channel, unlinked: that channel only");
        s.Stretch.Linked = true;
        a = a.Clone(); a.Weight = 0.7f; s.SetAdjust(ChannelKind.OIII, a);
        Check(s, 0, "back to linked");
        a = a.Clone(); a.Weight = 0.6f; s.SetAdjust(ChannelKind.OIII, a);
        Check(s, 2, "weight of one channel (after the stretch): that channel only");
        s.Luminance.Amount = 0.5f;
        Check(s, 3, "luminance amount: every plane reused");
        s.Stretch.TargetBackground = 0.3f;
        Check(s, 0, "stretch: every plane prepared again");
        s.StretchStage = StretchStage.PostCombine;
        Check(s, 0, "post-combine: linear planes");
        a = a.Clone(); a.Weight = 1.3f; s.SetAdjust(ChannelKind.OIII, a);
        Check(s, 2, "post-combine weight of one channel");
        s.Stretch.TargetBackground = 0.25f;
        Check(s, 3, "post-combine stretch: the linear planes stay");
        s.StretchStage = StretchStage.PreCombine;
        s.Mode = CombineMode.PixelMath; s.PixelMath = new PixelMathProgram("SII", "Ha", "OIII", "Ha");
        var ha = s.Adjust(ChannelKind.Ha).Clone(); ha.Weight = 1.5f; s.SetAdjust(ChannelKind.Ha, ha);
        Check(s, 0, "pixel math with a Ha luminance");
        a = a.Clone(); a.Weight = 0.9f; s.SetAdjust(ChannelKind.OIII, a);
        Check(s, 2, "the stored Ha plane (reused) was not clamped by the first render's luminance");
    }

    [Fact]
    public void StarsLastReblendOnly()
    {
        var srcs = Sources();
        srcs[ChannelKind.Red] = Synth.Source(ChannelKind.Red, 0.6f, 20, 160, 120);
        srcs[ChannelKind.Green] = Synth.Source(ChannelKind.Green, 0.6f, 21, 160, 120);
        srcs[ChannelKind.Blue] = Synth.Source(ChannelKind.Blue, 0.6f, 22, 160, 120);
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!);
        s.Denoise.Amount = 0.4f; s.Detail.Clarity = 0.5f;
        s.Stars.Enabled = true; s.Stars.Stage = StarsBlendStage.Last;
        var ctx = RenderContext.Make(s.Normalization, srcs);
        var analysis = Renderer.AnalysisSamples(srcs);
        var cache = new RenderCache();
        var planes = srcs.ToDictionary(kv => kv.Key, kv => kv.Value.Pyramid.Level(0));
        void Check(CombineSettings st, RenderCache.Stage expect, string what)
        {
            var cached = cache.Render(new RenderRequest(planes, st.Clone(), ctx, analysis), Key(srcs, ctx));
            var fresh = Renderer.Render(new RenderRequest(planes, st.Clone(), ctx, analysis));
            AssertSame(cached.Image, fresh.Image, what);
            Assert.True(expect == cached.RanFrom, $"{what}: ran from {cached.RanFrom}, expected {expect}");
        }
        Check(s, RenderCache.Stage.Combine, "first render");
        s.Stars.Amount = 0.6f;
        Check(s, RenderCache.Stage.Stars, "star amount: re-blend on the kept detail output");
        s.Stars.Brightness = 0.5f;
        Check(s, RenderCache.Stage.Stars, "star brightness");
        s.Detail.Sharpen = 0.7f;
        Check(s, RenderCache.Stage.Detail, "detail: detail + stars");
        s.Denoise.Amount = 0.2f;
        Check(s, RenderCache.Stage.Denoise, "denoise: denoise + detail + stars");
        s.Stars.Stage = StarsBlendStage.BeforeDetail;
        Check(s, RenderCache.Stage.Colour, "stars before detail: part of the developed image");
        s.Stars.Amount = 0.3f;
        Check(s, RenderCache.Stage.Colour, "a star slider then re-runs from the colour stage");
    }

    [Fact]
    public void LiveCorrectionAndStripRendersBypassTheCache()
    {
        var srcs = Sources();
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!);
        var ctx = RenderContext.Make(s.Normalization, srcs);
        var cache = new RenderCache();
        var planes = srcs.ToDictionary(kv => kv.Key, kv => kv.Value.Pyramid.Level(0));
        var req = new RenderRequest(planes, s, ctx);
        Assert.Equal(RenderCache.Stage.Combine, cache.Render(req, Key(srcs, ctx)).RanFrom);
        req.Correction = new CorrectionInput(new CorrectionMap(160, 120), 1, ChannelKind.Ha, null);
        Assert.Null(cache.Render(req, Key(srcs, ctx)).RanFrom);
        req.Correction = null; req.ApplyDetail = false;
        Assert.Null(cache.Render(req, Key(srcs, ctx)).RanFrom);
        req.ApplyDetail = true;
        Assert.Equal(RenderCache.Stage.Nothing, cache.Render(req, Key(srcs, ctx)).RanFrom);
    }
}
