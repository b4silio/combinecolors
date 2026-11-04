using AstroCore;

namespace AstroCore.Tests;

public class HotPixelAndBandTests
{
    /// <summary>Background with noise, one isolated coloured spike and one small star (Gaussian, σ 1.2 px).</summary>
    static RGBImage Scene()
    {
        int w = 64, h = 64;
        var rng = new Synth.LCG(7);
        var img = new RGBImage(w, h);
        foreach (var p in img.Planes) for (int i = 0; i < w * h; i++) p.Data[i] = 0.1f + 0.005f * rng.Gaussian();
        img.R[20, 20] = 0.9f;                       // red hot pixel
        for (int y = 40; y < 50; y++)
            for (int x = 40; x < 50; x++)
            {
                float d2 = (x - 45) * (x - 45) + (y - 45) * (y - 45);
                float v = 0.8f * MathF.Exp(-d2 / (2 * 1.2f * 1.2f));
                foreach (var p in img.Planes) p[x, y] += v;
            }
        return img;
    }

    [Fact]
    public void HotPixelsGoStarsStay()
    {
        var img = Scene();
        float star = img.G[45, 45], starR = img.R[45, 45];
        Denoise.RemoveHotPixels(img, 0.5f);
        A.Close(0.1f, img.R[20, 20], 0.02f);
        A.Close(star, img.G[45, 45], 1e-6f);
        A.Close(starR, img.R[45, 45], 1e-6f);
    }

    [Fact]
    public void HotPixelsOnlyAtFullResolution()
    {
        var d = new DenoiseSettings { HotPixels = 0.5f };
        Assert.False(d.IsNeutral);
        var reduced = Scene();
        Denoise.Apply(reduced, d, 0.5f);
        A.Close(0.9f, reduced.R[20, 20], 1e-6f);
        var full = Scene();
        Denoise.Apply(full, d, 1);
        A.Close(0.1f, full.R[20, 20], 0.02f);
        // Old projects decode with the filter off.
        Assert.Equal(0, DenoiseSettings.FromJson(SettingsJson.ParseObject("{\"amount\":0.3}")).HotPixels);
    }

    [Fact]
    public void BandSoftnessWidensAndSmoothsTheBands()
    {
        var centers = ColorBands.All.Select(b => b.Hue()).ToArray();
        int teal = Array.IndexOf(ColorBands.All, ColorBand.Teals);
        // Softness 0 is the original partition of unity.
        for (float h = 0; h < 360; h += 7)
        {
            float sum = 0;
            for (int i = 0; i < centers.Length; i++) sum += PostProcess.BandWeight(h, i, centers);
            A.Close(1, sum, 1e-4f);
        }
        A.Close(0, PostProcess.BandWeight(120, teal, centers), 1e-6f);
        Assert.True(PostProcess.BandWeight(120, teal, centers, 1) > 0.2f, "soft teal reaches into the greens");
        A.Close(1, PostProcess.BandWeight(180, teal, centers, 1), 1e-6f);
        float last = 2;
        for (float h = 180; h <= 300; h += 5)
        {
            float w = PostProcess.BandWeight(h, teal, centers, 0.6f);
            Assert.True(w <= last + 1e-6f);
            last = w;
        }
        // An edit on the teals now moves a green pixel too.
        var s = new ColorBandsSettings();
        s.Set(ColorBand.Teals, new ColorBandAdjust { Saturation = -1 });
        var hard = new RGBImage(1, 1); var soft = new RGBImage(1, 1);
        foreach (var img in new[] { hard, soft }) { img.R[0, 0] = 0.2f; img.G[0, 0] = 0.7f; img.B[0, 0] = 0.35f; }   // hue ≈ 138°
        PostProcess.ColorBands(hard, s);
        s.Softness = 1;
        PostProcess.ColorBands(soft, s);
        Assert.True(Math.Abs(soft.G[0, 0] - soft.R[0, 0]) < Math.Abs(hard.G[0, 0] - hard.R[0, 0]), "the softer band desaturates the green more");
    }
}

public class ToneTests
{
    static readonly PostSettings Levels = new();

    [Fact]
    public void NeutralCurveIsIdentityAndLevelsWorkAsBefore()
    {
        for (float x = 0; x <= 1.0001f; x += 0.05f) A.Close(x, PostProcess.ToneCurve(x, new ToneSettings(), Levels), 1e-6f);
        var l = new PostSettings { BlackPoint = 0.1f, WhitePoint = 0.9f, Gamma = 2 };
        A.Close(0, PostProcess.ToneCurve(0.1f, new ToneSettings(), l), 1e-6f);
        A.Close(0.25f, PostProcess.ToneCurve(0.5f, new ToneSettings(), l), 1e-5f);
        A.Close(1, PostProcess.ToneCurve(0.95f, new ToneSettings(), l), 1e-6f);
    }

    [Fact]
    public void ControlsMoveTheRightTonesAndStayMonotonic()
    {
        Func<float, float> Curve(ToneSettings t) => x => PostProcess.ToneCurve(x, t, Levels);
        var cases = new (Action<ToneSettings> set, string name)[]
        {
            (t => t.Brightness = 1, "brightness +"), (t => t.Brightness = -1, "brightness −"), (t => t.Contrast = 1, "contrast +"), (t => t.Contrast = -1, "contrast −"),
            (t => t.Highlights = 1, "highlights +"), (t => t.Highlights = -1, "highlights −"), (t => t.Shadows = 1, "shadows +"), (t => t.Shadows = -1, "shadows −"),
        };
        foreach (var (set, name) in cases)
        {
            var t = new ToneSettings(); set(t);
            var f = Curve(t);
            float last = -1;
            for (float x = 0; x <= 1.0001f; x += 0.01f) { Assert.True(f(x) >= last - 1e-6f, name); last = f(x); }
            A.Close(0, f(0), 1e-5f); A.Close(1, f(1), 1e-5f);
        }
        Assert.True(Curve(new ToneSettings { Brightness = 0.5f })(0.5f) > 0.55f);
        var c = Curve(new ToneSettings { Contrast = 0.8f });
        Assert.True(c(0.25f) < 0.22f); Assert.True(c(0.75f) > 0.78f);
        var sh = Curve(new ToneSettings { Shadows = 1 });
        Assert.True(sh(0.25f) > 0.35f); A.Close(0.75f, sh(0.75f), 1e-6f);
        var hi = Curve(new ToneSettings { Highlights = -1 });
        Assert.True(hi(0.75f) < 0.65f); A.Close(0.25f, hi(0.25f), 1e-6f);
    }

    [Fact]
    public void ExposureAndDecoding()
    {
        var img = new RGBImage(2, 1);
        img.R[0, 0] = 0.2f; img.G[0, 0] = 0.3f; img.B[0, 0] = 0.6f;
        PostProcess.ApplyTone(new ToneSettings { Exposure = 1 }, new PostSettings(), img);
        A.Close(0.4f, img.R[0, 0], 1e-6f);
        A.Close(1, img.B[0, 0], 1e-6f);
        var s = CombineSettings.FromJson(SettingsJson.ParseObject("{\"post\":{\"gamma\":1.2}}"));
        Assert.True(s.Tone.IsNeutral, "projects from before the Contrast block decode neutral");
        A.Close(1.2f, s.Post.Gamma, 1e-6f);
        // Round trip of the new fields.
        var t = new CombineSettings();
        t.Tone.Contrast = 0.4f; t.ColorBands.Softness = 0.7f; t.Denoise.HotPixels = 0.3f;
        var back = CombineSettings.FromJson(SettingsJson.ParseObject(SettingsJson.Serialize(t.ToJson(), false)));
        Assert.Equal(t, back);
    }
}

public class BrushSpacingTests
{
    /// <summary>A slow drag (one-pixel mouse events) paints as much as one fast drag along the same path.</summary>
    [Fact]
    public void SlowDragPaintsLikeAFastOne()
    {
        var slow = new CorrectionMap(400, 200); var fast = new CorrectionMap(400, 200);
        double c1 = CorrectionMap.StrokeStart(40);
        double px = 100;
        slow.Stroke(px, 100, px, 100, 40, 0.02f, 0.3f, ref c1);
        for (int i = 0; i < 200; i++) { slow.Stroke(px, 100, px + 1, 100, 40, 0.02f, 0.3f, ref c1); px += 1; }
        double c2 = CorrectionMap.StrokeStart(40);
        fast.Stroke(100, 100, 100, 100, 40, 0.02f, 0.3f, ref c2);
        fast.Stroke(100, 100, 300, 100, 40, 0.02f, 0.3f, ref c2);
        A.Close(fast.Value(200, 100), slow.Value(200, 100), 1e-4f);
        // One pass at 2 % flow stays gentle.
        Assert.True(slow.Value(200, 100) < 0.2f);
        Assert.True(slow.Value(200, 100) > 0.05f);
    }
}

public class LayerDocumentTests
{
    [Fact]
    public void StarsTabRoundTripsAndOldProjectsHaveNone()
    {
        var doc = new ProjectDocument();
        doc.Channels.Add(new ProjectDocument.ChannelRef(ChannelKind.Ha, @"C:\data\Ha_bg.fits"));
        var l = new LayerDocument();
        l.Channels.Add(new ProjectDocument.ChannelRef(ChannelKind.Ha, @"C:\data\Ha_stars.fits"));
        l.Settings.Stretch.Method = StretchMethod.None;
        l.Presets.Add(new SettingsPreset("stars", l.Settings.Clone()));
        doc.StarsLayer = l;
        var back = ProjectDocument.Parse(doc.Json());
        Assert.NotNull(back.StarsLayer);
        Assert.Single(back.StarsLayer!.Channels);
        Assert.Equal(@"C:\data\Ha_stars.fits", back.StarsLayer.Channels[0].Path);
        Assert.Equal(StretchMethod.None, back.StarsLayer.Settings.Stretch.Method);
        Assert.Single(back.StarsLayer.Presets);
        Assert.Null(ProjectDocument.Parse("{\"channels\":[]}").StarsLayer);
    }
}
