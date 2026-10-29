using AstroCore;

namespace AstroCore.Tests;

public class PipelineTests
{
    readonly Dictionary<ChannelKind, ChannelSource> sources = new()
    {
        [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha, 1, 1),
        [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 0.4f, 2),
        [ChannelKind.SII] = Synth.Source(ChannelKind.SII, 0.25f, 3),
        [ChannelKind.Red] = Synth.Source(ChannelKind.Red, 0.6f, 4),
        [ChannelKind.Green] = Synth.Source(ChannelKind.Green, 0.6f, 5),
        [ChannelKind.Blue] = Synth.Source(ChannelKind.Blue, 0.6f, 6),
        [ChannelKind.Hb] = Synth.Source(ChannelKind.Hb, 0.2f, 7),
    };

    Dictionary<ChannelKind, PlanarImage> Planes(int level = 0) => sources.ToDictionary(kv => kv.Key, kv => kv.Value.Pyramid.Level(level));

    RenderResult Render(CombineSettings settings, int level = 0)
    {
        var ctx = RenderContext.Make(settings.Normalization, sources);
        return Renderer.Render(new RenderRequest(Planes(level), settings, ctx, Renderer.AnalysisSamples(sources)));
    }

    [Fact]
    public void MatrixPaletteMapsChannels()
    {
        var s = new CombineSettings();
        s.Select(Palettes.Palette("sho")!);
        s.Stretch.Method = StretchMethod.None;
        s.Normalization.Method = NormalizationMethod.None;
        s.Luminance.Enabled = false;
        var r = Render(s);
        // With no stretch and no normalisation, R == SII, G == Ha, B == OIII (clamped).
        A.Close(MathF.Min(sources[ChannelKind.SII].Image[10, 10], 1), r.Image.R[10, 10], 1e-6f);
        A.Close(MathF.Min(sources[ChannelKind.Ha].Image[10, 10], 1), r.Image.G[10, 10], 1e-6f);
        A.Close(MathF.Min(sources[ChannelKind.OIII].Image[10, 10], 1), r.Image.B[10, 10], 1e-6f);
    }

    [Fact]
    public void PreAndPostStretchPathsProduceValidImages()
    {
        foreach (var stage in StretchStages.All)
            foreach (var linked in new[] { true, false })
                foreach (var pal in new[] { "sho", "hoo", "foraxx_sho", "dyn_sho_ratio", "lrgb", "sho_natural" })
                {
                    var s = new CombineSettings();
                    s.Select(Palettes.Palette(pal)!);
                    s.StretchStage = stage;
                    s.Stretch.Linked = linked;
                    s.Luminance.Enabled = true; s.Luminance.Preset = LuminancePreset.Ha;
                    s.Post.Saturation = 1.2f; s.Post.ScnrGreen = 0.5f; s.Post.HueShift = 10;
                    var r = Render(s);
                    A.Bounded(r.Image, $"{pal} {stage} linked={linked}");
                    Assert.Equal(256, r.Image.Width);
                    // Something must be visible: the mean is far from 0 and 1.
                    float m = r.Image.Luma().Mean();
                    Assert.True(m > 0.02f, $"{pal} {stage} linked={linked}"); Assert.True(m < 0.98f);
                    if (stage == StretchStage.PostCombine) Assert.Equal(4, r.PostStretch?.Count); else Assert.Null(r.PostStretch);
                }
    }

    [Fact]
    public void MedianLandsOnTargetBackground()
    {
        var s = new CombineSettings();
        s.Select(Palettes.Palette("monochrome")!);
        s.MonoChannels = [ChannelKind.Ha];
        s.Stretch.Linked = false;
        s.Stretch.TargetBackground = 0.3f;
        var r = Render(s);
        var st = Statistics.Compute(r.Image.R);
        A.Close(0.3f, st.Median, 0.02f);
    }

    [Fact]
    public void PixelMathPermutationMatchesMatrix()
    {
        var a = new CombineSettings(); a.Select(Palettes.Palette("sho")!);
        var b = a.Clone(); b.Mode = CombineMode.PixelMath; b.PixelMath = new PixelMathProgram("SII", "Ha", "OIII");
        var ra = Render(a); var rb = Render(b);
        for (int c = 0; c < 3; c++) Assert.True(ra.Image.Planes[c].MaxAbsDifference(rb.Image.Planes[c]) < 1e-5f);
    }

    [Fact]
    public void MissingChannelsReported()
    {
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!);
        var only = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = sources[ChannelKind.Ha] };
        var ctx = RenderContext.Make(s.Normalization, only);
        var e = Assert.Throws<RenderException>(() => Renderer.Render(new RenderRequest(new Dictionary<ChannelKind, PlanarImage> { [ChannelKind.Ha] = sources[ChannelKind.Ha].Image }, s, ctx)));
        Assert.Equal(RenderErrorKind.MissingChannels, e.Kind);
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.OIII, ChannelKind.SII }, e.Missing.ToHashSet());
        Assert.Equal(new[] { ChannelKind.SII }, Renderer.MissingChannels(s, new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII }));
    }

    [Fact]
    public void PixelMathErrorsSurface()
    {
        var s = new CombineSettings { Mode = CombineMode.PixelMath, PixelMath = new PixelMathProgram("Ha +", "Ha", "Ha") };
        var ctx = RenderContext.Make(s.Normalization, sources);
        var e = Assert.Throws<RenderException>(() => Renderer.Render(new RenderRequest(Planes(), s, ctx)));
        Assert.Equal(RenderErrorKind.PixelMath, e.Kind);
    }

    [Fact]
    public void ChannelAdjustments()
    {
        var s = new CombineSettings(); s.Select(Palettes.Palette("monochrome")!); s.MonoChannels = [ChannelKind.Ha];
        float baseMean = Render(s).Image.R.Mean();
        var adj = new ChannelAdjust { Weight = 0.5f };
        s.SetAdjust(ChannelKind.Ha, adj);
        A.Close(baseMean * 0.5f, Render(s).Image.R.Mean(), 1e-3f);
        adj = new ChannelAdjust { Enabled = false };
        s.SetAdjust(ChannelKind.Ha, adj);
        A.Close(0, Render(s).Image.R.Mean(), 1e-6f);
        adj = new ChannelAdjust { StretchBoost = 0.2f }; s.SetAdjust(ChannelKind.Ha, adj);
        Assert.True(Render(s).Image.R.Mean() > baseMean);
        adj = new ChannelAdjust { BlackPoint = 0.2f, Gamma = 1.5f }; s.SetAdjust(ChannelKind.Ha, adj);
        Assert.True(Render(s).Image.R.Mean() < baseMean);
        var lin = s.Clone(); lin.Stretch.Method = StretchMethod.None; lin.SetAdjust(ChannelKind.Ha, new ChannelAdjust());
        float linBase = Render(lin).Image.R.Mean();
        adj = new ChannelAdjust { Gain = 3 }; lin.SetAdjust(ChannelKind.Ha, adj);
        A.Close(linBase * 3, Render(lin).Image.R.Mean(), 0.05f);
    }

    [Fact]
    public void StripRenderMatchesWholeRender()
    {
        foreach (var stage in StretchStages.All)
        {
            var s = new CombineSettings(); s.Select(Palettes.Palette("foraxx_sho")!);
            s.StretchStage = stage;
            s.Luminance.Enabled = true; s.Luminance.Preset = LuminancePreset.HaMostly;
            s.Stars.Enabled = true;
            var ctx = RenderContext.Make(s.Normalization, sources);
            var whole = Renderer.Render(new RenderRequest(Planes(), s, ctx, Renderer.AnalysisSamples(sources)));
            var progress = new List<double>();
            var strips = Renderer.RenderFull(sources, s, ctx, whole.PostStretch, stripPixels: 256 * 50, progress: p => progress.Add(p))!;
            for (int c = 0; c < 3; c++) Assert.True(whole.Image.Planes[c].MaxAbsDifference(strips.Planes[c]) < 1e-5f, stage.ToString());
            A.Close(1, progress[^1], 1e-9);
            Assert.True(progress.Count > 1);
            // Without a fixed post stretch the strip renderer derives it from the analysis samples: same result.
            var derived = Renderer.RenderFull(sources, s, ctx, null, stripPixels: 256 * 50)!;
            for (int c = 0; c < 3; c++) Assert.True(whole.Image.Planes[c].MaxAbsDifference(derived.Planes[c]) < 1e-5f, $"{stage} derived");
        }
    }

    [Fact]
    public void Cancellation()
    {
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!);
        var ctx = RenderContext.Make(s.Normalization, sources);
        var r = Renderer.RenderFull(sources, s, ctx, null, stripPixels: 256 * 10, isCancelled: () => true);
        Assert.Null(r);
    }

    [Fact]
    public void PreviewLevelIsDownsampleOfFull()
    {
        // For linear (no stretch) matrix combinations the level-1 preview equals the box-filtered full render.
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!); s.Stretch.Method = StretchMethod.None; s.Luminance.Enabled = false;
        var full = Render(s, 0).Image.Downsampled2x();
        var half = Render(s, 1).Image;
        for (int c = 0; c < 3; c++) Assert.True(full.Planes[c].MaxAbsDifference(half.Planes[c]) < 1e-5f);
    }

    [Fact]
    public void SettingsRoundTrip()
    {
        var s = new CombineSettings();
        s.Select(Palettes.Palette("foraxx_sho")!);
        s.SetAdjust(ChannelKind.OIII, new ChannelAdjust { Gain = 1.5f });
        s.Normalization.Method = NormalizationMethod.HistogramMatch; s.Normalization.Reference = ChannelKind.OIII;
        s.Stretch.Method = StretchMethod.Ghs; s.Post.HueShift = -12;
        var back = CombineSettings.Parse(s.Json());
        Assert.Equal(s, back);
        var doc = new ProjectDocument { Settings = s, Rotation = 3, Flipped = true };
        doc.Channels.Add(new ProjectDocument.ChannelRef(ChannelKind.Ha, "/tmp/ha.fits"));
        var docBack = ProjectDocument.Parse(doc.Json());
        Assert.Equal(s, docBack.Settings); Assert.Equal(ChannelKind.Ha, docBack.Channels[0].Kind); Assert.Equal(3, docBack.Rotation); Assert.True(docBack.Flipped);
    }

    [Fact]
    public void JsonLayoutMatchesTheMacBuild()
    {
        // Key names, enum identifiers and the channel map layout must stay as the Swift Codable output.
        var s = new CombineSettings();
        s.SetAdjust(ChannelKind.OIII, new ChannelAdjust { Gain = 2 });
        s.ColorBands.Set(ColorBand.Teals, new ColorBandAdjust { Hue = 5 });
        s.MonoChannels = [ChannelKind.SII, ChannelKind.Ha];
        var json = s.Json(pretty: false);
        Assert.Contains("\"channels\":{\"OIII\":{\"blackPoint\":0,\"enabled\":true,\"gain\":2", json);
        Assert.Contains("\"stretchStage\":\"preCombine\"", json);
        Assert.Contains("\"method\":\"autoSTF\"", json);
        Assert.Contains("\"method\":\"backgroundSignal\"", json);
        Assert.Contains("\"bands\":{\"teals\":{\"hue\":5", json);
        Assert.Contains("\"monoChannels\":[\"Ha\",\"SII\"]", json);
        Assert.Contains("\"weights\":{\"Ha\":[0,1,0],\"OIII\":[0,0,1],\"SII\":[1,0,0]}", json);
        Assert.DoesNotContain("\"reference\"", json);   // nil optionals are omitted
        var preset = new SettingsPreset("p", s) { Date = new DateTime(2001, 1, 1, 0, 0, 10, DateTimeKind.Utc) };
        var pj = SettingsJson.Serialize(preset.ToJson(), false);
        Assert.Contains("\"date\":10", pj);
        Assert.Contains(preset.Id.ToString().ToUpperInvariant(), pj);
        var back = SettingsPreset.FromJson(SettingsJson.ParseObject(pj));
        Assert.Equal(preset.Id, back.Id); Assert.Equal(preset.Date, back.Date);
    }

    [Fact]
    public void PalettesAvailability()
    {
        var avail = Palettes.Available(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII });
        Assert.Contains(avail, p => p.Id == "hoo");
        Assert.DoesNotContain(avail, p => p.Id == "sho");
        Assert.DoesNotContain(avail, p => p.Id == "rgb");
    }
}

public class DisplayPlaneTests
{
    [Fact]
    public void DisplayPlaneFollowsRegion()
    {
        var img = new PlanarImage(400, 300, 0.1f);
        var full = new ChannelSource(ChannelKind.Ha, img, "x");
        var whole = full.DisplayPlane(400 * 300);
        Assert.Equal(400, whole.Width); Assert.Equal(300, whole.Height);
        // A region crops the thumbnail at full resolution when it fits the budget...
        var region = new PixelRect(40, 30, 200, 100);
        var cropped = new ChannelSource(full, region);
        var t0 = cropped.DisplayPlane(200 * 100);
        Assert.Equal(200, t0.Width); Assert.Equal(100, t0.Height);
        // ...and picks a coarser level (region-based budget) otherwise.
        var t1 = cropped.DisplayPlane(200 * 100 / 2);
        Assert.Equal(100, t1.Width); Assert.Equal(50, t1.Height);
        // A full-image region behaves like no region.
        var same = new ChannelSource(full, PixelRect.Full(400, 300)).DisplayPlane(400 * 300);
        Assert.Equal(400, same.Width);
    }
}

public class PostProcessTests
{
    static RGBImage Sample()
    {
        var img = new RGBImage(4, 1);
        new float[] { 0.8f, 0.2f, 0.5f, 0.0f }.CopyTo(img.R.Data, 0);
        new float[] { 0.2f, 0.8f, 0.5f, 0.0f }.CopyTo(img.G.Data, 0);
        new float[] { 0.2f, 0.2f, 0.5f, 1.0f }.CopyTo(img.B.Data, 0);
        return img;
    }

    [Fact]
    public void SaturationZeroGivesGray()
    {
        var img = Sample();
        PostProcess.Saturation(img, 0);
        for (int i = 0; i < 4; i++) { A.Close(img.G.Data[i], img.R.Data[i], 1e-6f); A.Close(img.B.Data[i], img.G.Data[i], 1e-6f); }
    }

    [Fact]
    public void ScnrReducesGreenOnly()
    {
        var img = Sample();
        PostProcess.Scnr(img, 1);
        A.Close(0.2f, img.G.Data[1], 1e-6f);   // min(0.8, (0.2+0.2)/2)
        A.Close(0.2f, img.G.Data[0], 1e-6f);   // unchanged (already <= mean)
        A.Close(0.2f, img.R.Data[1], 1e-6f);
    }

    [Fact]
    public void HueShiftFullCircleIsIdentity()
    {
        var img = Sample(); var reference = Sample();
        PostProcess.HueShift(img, 360);
        for (int c = 0; c < 3; c++) Assert.True(img.Planes[c].MaxAbsDifference(reference.Planes[c]) < 1e-4f);
        PostProcess.HueShift(img, 120);
        Assert.True(img.Planes[0].MaxAbsDifference(reference.Planes[0]) > 0.1f);
    }

    [Fact]
    public void LuminanceInjection()
    {
        foreach (var method in LuminanceMethods.All)
        {
            var img = Sample();
            var lum = new PlanarImage(4, 1, [0.5f, 0.5f, 0.5f, 0.5f]);
            PostProcess.InjectLuminance(img, lum, 1, method);
            var y = img.Luma();
            // Saturated colours clip when brightened, so the achieved luminance can fall short.
            for (int i = 0; i < 3; i++) A.Close(0.5f, y.Data[i], method == LuminanceMethod.Lab ? 0.12f : 0.06f, $"{method} px {i}");
            // Hue preserved: the dominant channel stays dominant.
            Assert.True(img.R.Data[0] > img.G.Data[0]);
            Assert.True(img.G.Data[1] > img.R.Data[1]);
        }
    }

    [Fact]
    public void StarsBlendScreenBrightens()
    {
        var img = new RGBImage(2, 1);
        var stars = new RGBImage(2, 1);
        stars.R.Data[0] = 0.9f; stars.G.Data[0] = 0.9f; stars.B.Data[0] = 0.9f;
        var s = new StarsBlendSettings { Enabled = true, UseMask = true, Threshold = 0.1f, Softness = 0.2f };
        PostProcess.BlendStars(img, stars, s);
        Assert.True(img.R.Data[0] > 0.8f);
        A.Close(0, img.R.Data[1], 1e-6f);
    }

    [Fact]
    public void ApplyKeepsRange()
    {
        var img = Sample();
        var s = new PostSettings { Gamma = 0.7f, BlackPoint = 0.1f, WhitePoint = 0.9f, Vibrance = 0.5f, Saturation = 1.5f, HueShift = -30 };
        PostProcess.Apply(s, img);
        A.Bounded(img);
    }
}

public class DualBandTests
{
    [Fact]
    public void SplitPresets()
    {
        var r = new PlanarImage(2, 1, [0.8f, 0.1f]); var g = new PlanarImage(2, 1, [0.2f, 0.6f]); var b = new PlanarImage(2, 1, [0.4f, 0.4f]);
        var s = new DualBandSettings();
        var (ha, o3) = DualBand.Split(r, g, b, s);
        Assert.Equal(new float[] { 0.8f, 0.1f }, ha.ToArray());
        Assert.Equal(new float[] { 0.3f, 0.5f }, o3.ToArray());
        Assert.Equal(ChannelKind.Ha, s.OutputKinds.first); Assert.Equal(ChannelKind.OIII, s.OutputKinds.second);
        s.CrosstalkSecond = 0.5f;
        var (_, o3b) = DualBand.Split(r, g, b, s);
        Assert.Equal(0, o3b.ToArray()[0]);
        A.Close(0.45f, o3b.ToArray()[1], 1e-6f);
        s.Preset = DualBandPreset.SiiHb;
        Assert.Equal(ChannelKind.SII, s.OutputKinds.first); Assert.Equal(ChannelKind.Hb, s.OutputKinds.second);
    }
}

/// <summary>Runs only when the user's sample data is present (C:\_Astrophotography).</summary>
public class RealDataIntegrationTests
{
    static string Soul(string filter) => Path.Combine(Synth.SampleDir, $"Soul_Core-{filter}-session_1-lpc.fits");

    [Fact]
    public void LoadAndRenderSoulCoreFits()
    {
        var files = new[] { Soul("Hydrogen-alpha"), Soul("Oxygen_III"), Soul("Sulphur_II") };
        if (!files.All(File.Exists)) return;   // sample data not present
        var sources = new Dictionary<ChannelKind, ChannelSource>();
        var t = System.Diagnostics.Stopwatch.StartNew();
        Parallel.ForEach(files, f =>
        {
            var l = ImageLoader.Load(f);
            Assert.NotNull(l.DetectedKind);
            var src = new ChannelSource(l.DetectedKind!.Value, l.Planes[0], l.FileName);
            lock (sources) sources[l.DetectedKind!.Value] = src;
        });
        Console.WriteLine($"loaded 3 channels in {t.ElapsedMilliseconds} ms");
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII }, sources.Keys.ToHashSet());
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!);
        var ctx = RenderContext.Make(s.Normalization, sources);
        int lvl = sources[ChannelKind.Ha].Pyramid.LevelIndex(500_000);
        var r = Renderer.Render(new RenderRequest(sources.ToDictionary(kv => kv.Key, kv => kv.Value.Pyramid.Level(lvl)), s, ctx, Renderer.AnalysisSamples(sources)));
        Console.WriteLine($"render level {lvl}: {r.Millis:F0} ms");
        Assert.True(r.Millis < 4000);
        Assert.True(r.Image.Luma().Mean() > 0.05f);
        A.Bounded(r.Image);
    }

    [Fact]
    public void SoulCoreFitsHeaderDetection()
    {
        var f = Soul("Sulphur_II");
        if (!File.Exists(f)) return;   // sample data not present
        var h = FitsReader.ReadHeader(f);
        Assert.Equal("Sulphur II", h.String("FILTER"));
        Assert.Equal(ChannelKind.SII, ChannelDetection.Detect(Path.GetFileName(f), h.String("FILTER")));
        Assert.Equal(7272, h.Int("NAXIS1"));
    }
}

public class TolerantDecodingTests
{
    [Fact]
    public void MissingAndUnknownKeysFallBackToDefaults()
    {
        var json = """
        {"version": 1, "channels": [{"kind": "Ha", "path": "/x/ha.fits"}],
         "settings": {"paletteID": "hoo", "mode": "matrix", "stars": {"enabled": true}, "stretch": {"method": "ghs", "ghsLogD": 3.5, "bogus": 1},
                      "normalization": {"method": "noSuchMethod"}, "post": {"saturation": 1.5}, "channels": {"OIII": {"gain": 2}}},
         "crop": {"x": 1, "y": 2, "width": 30, "height": 40}}
        """;
        var doc = ProjectDocument.Parse(json);
        Assert.Equal(ChannelKind.Ha, doc.Channels[0].Kind);
        Assert.Equal("hoo", doc.Settings.PaletteID);
        Assert.True(doc.Settings.Stars.Enabled);
        Assert.Equal(StarsStretchMode.Auto, doc.Settings.Stars.Stretch);            // missing key → default
        Assert.Equal(StretchMethod.Ghs, doc.Settings.Stretch.Method);
        Assert.Equal(3.5f, doc.Settings.Stretch.GhsLogD);
        Assert.Equal(new StretchSettings().TargetBackground, doc.Settings.Stretch.TargetBackground);
        Assert.Equal(new NormalizationSettings().Method, doc.Settings.Normalization.Method);   // unknown value → default
        Assert.Equal(0, doc.Rotation);                                                    // missing → no rotation
        Assert.False(doc.Flipped);
        Assert.Equal(0, doc.Settings.Detail.Sharpen);                                     // pre-sharpen file → off
        Assert.Equal(1.5f, doc.Settings.Detail.SharpenRadius);
        Assert.Equal(1.5f, doc.Settings.Post.Saturation);
        Assert.Equal(2, doc.Settings.Adjust(ChannelKind.OIII).Gain);
        Assert.Equal(new PixelRect(1, 2, 30, 40), doc.Crop);
        Assert.Null(doc.StarsFile);
        var empty = CombineSettings.Parse("{}");
        Assert.Equal(new CombineSettings(), empty);
        // Round trip of current defaults remains exact.
        var s = new CombineSettings();
        Assert.Equal(s, CombineSettings.Parse(s.Json()));
    }

    [Fact]
    public void SwiftEncodedProjectOpens()
    {
        // A fragment as written by the macOS build (Float values expanded to Double, uppercase UUID, reference date seconds).
        var json = """
        {
          "channels" : [ { "kind" : "OIII", "path" : "/Users/x/Soul_Core-Oxygen_III.fits" } ],
          "crop" : { "height" : 4000, "width" : 6000, "x" : 100, "y" : 200 },
          "dust" : { "feather" : 0.2, "model" : "surface", "smoothness" : 3, "spots" : { "OIII" : [ { "feather" : 0.2, "id" : "0B9F2B5E-6C1C-4A3E-9A1B-7E7B6F3E2D11", "model" : "radial", "radius" : 210.5, "smoothness" : 3, "x" : 1234.5, "y" : 2345.5 } ] } },
          "flipped" : false,
          "presets" : [ { "date" : 783432000.5, "id" : "1B9F2B5E-6C1C-4A3E-9A1B-7E7B6F3E2D11", "name" : "Warm", "settings" : { "post" : { "saturation" : 1.2999999523162842 } } } ],
          "rotation" : 1,
          "settings" : { "luminance" : { "source" : "L", "amount" : 0.5 }, "stretch" : { "targetBackground" : 0.20000000298023224 } },
          "version" : 1
        }
        """;
        var doc = ProjectDocument.Parse(json);
        Assert.Equal(1, doc.Rotation);
        Assert.Equal(new PixelRect(100, 200, 6000, 4000), doc.Crop);
        Assert.Equal(0.2f, doc.Settings.Stretch.TargetBackground);
        Assert.Equal(LuminancePreset.Lum, doc.Settings.Luminance.Preset); Assert.True(doc.Settings.Luminance.Enabled); Assert.Equal(0.5f, doc.Settings.Luminance.Amount);
        Assert.Single(doc.Presets);
        Assert.Equal(1.3f, doc.Presets[0].Settings.Post.Saturation);
        Assert.Equal(Guid.Parse("1B9F2B5E-6C1C-4A3E-9A1B-7E7B6F3E2D11"), doc.Presets[0].Id);
        Assert.Equal(new DateTime(2025, 10, 29, 12, 0, 0, 500, DateTimeKind.Utc), doc.Presets[0].Date);
        var spot = doc.Dust.SpotsFor(ChannelKind.OIII).Single();
        Assert.Equal(DustModel.Radial, spot.Model); Assert.Equal(210.5, spot.Radius);
        // What we write back can be read again and keeps every value.
        var again = ProjectDocument.Parse(doc.Json());
        Assert.Equal(doc.Settings, again.Settings); Assert.Equal(doc.Dust, again.Dust); Assert.Equal(doc.Presets[0].Id, again.Presets[0].Id);
    }
}

public class LuminancePresetTests
{
    [Fact]
    public void PresetExpressionsAdaptToAvailableChannels()
    {
        var all = new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII, ChannelKind.Luminance };
        Assert.Equal("Ha", LuminancePreset.Ha.Expression(all, ""));
        Assert.Equal("0.8*Ha + 0.1*OIII + 0.1*SII", LuminancePreset.HaMostly.Expression(all, ""));
        // SII missing → weights renormalised over Hα and OIII.
        Assert.Equal("0.889*Ha + 0.111*OIII", LuminancePreset.HaMostly.Expression(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII }, ""));
        Assert.Equal("Ha", LuminancePreset.HaMostly.Expression(new HashSet<ChannelKind> { ChannelKind.Ha }, ""));
        Assert.Null(LuminancePreset.HaMostly.Expression(new HashSet<ChannelKind> { ChannelKind.Red, ChannelKind.Green }, ""));
        Assert.Equal("max(Ha, OIII)", LuminancePreset.MaxLines.Expression(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII }, ""));
        Assert.Equal("SII", LuminancePreset.MeanLines.Expression(new HashSet<ChannelKind> { ChannelKind.SII }, ""));
        Assert.Equal("Ha^0.5", LuminancePreset.Custom.Expression(all, " Ha^0.5 "));
        Assert.Null(LuminancePreset.Custom.Expression(all, "  "));
        foreach (var p in LuminancePresets.All)
        {
            if (p == LuminancePreset.Custom) continue;
            if (p.Expression(all, "") is string e) PixelMath.Parse(e);
        }
        Assert.Equal("0.5*L + 0.5*Ha", LuminancePreset.LumHa.Expression(all, ""));
    }

    [Fact]
    public void FallbackChainAndRequiredChannels()
    {
        var l = new LuminanceSettings();
        Assert.Equal("Ha", l.ResolvedExpression(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII }));
        Assert.Equal("L", l.ResolvedExpression(new HashSet<ChannelKind> { ChannelKind.Luminance, ChannelKind.Red }));       // Hα missing → L
        Assert.Null(l.ResolvedExpression(new HashSet<ChannelKind> { ChannelKind.Red, ChannelKind.Green, ChannelKind.Blue }));
        l.Enabled = false;
        Assert.Null(l.ResolvedExpression(new HashSet<ChannelKind> { ChannelKind.Ha }));
        l.Enabled = true; l.Preset = LuminancePreset.Custom; l.CustomExpression = "0.5*Ha + 0.5*Hb";
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.Hb }, l.RequiredChannels(new HashSet<ChannelKind> { ChannelKind.Ha }));
        var s = new CombineSettings { Luminance = l };
        Assert.Contains(ChannelKind.Hb, s.AllChannels(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.Hb }));
    }

    [Fact]
    public void RendererUsesMixedLuminance()
    {
        var sources = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha), [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 0.4f, 2), [ChannelKind.SII] = Synth.Source(ChannelKind.SII, 0.3f, 3) };
        var planes = sources.Images();
        foreach (var stage in StretchStages.All)
        {
            var s = new CombineSettings(); s.Select(Palettes.Palette("hoo")!); s.StretchStage = stage;
            s.Luminance.Enabled = false;
            var ctx = RenderContext.Make(s.Normalization, sources);
            var samples = Renderer.AnalysisSamples(sources);
            var plain = Renderer.Render(new RenderRequest(planes, s, ctx, samples));
            s.Luminance.Enabled = true; s.Luminance.Preset = LuminancePreset.HaMostly;
            var mixed = Renderer.Render(new RenderRequest(planes, s, ctx, samples));
            Assert.True(mixed.Image.R.MaxAbsDifference(plain.Image.R) > 0.01f, stage.ToString());
            A.Bounded(mixed.Image);
            // Custom expression that references a missing channel surfaces as a render error.
            s.Luminance.Preset = LuminancePreset.Custom; s.Luminance.CustomExpression = "Ha + Hb";
            Assert.Throws<RenderException>(() => Renderer.Render(new RenderRequest(planes, s, ctx, samples)));
            s.Luminance.CustomExpression = "Ha +";
            Assert.Throws<RenderException>(() => Renderer.Render(new RenderRequest(planes, s, ctx, samples)));
        }
    }

    [Fact]
    public void OldProjectLuminanceKeyMigrates()
    {
        var on = LuminanceSettings.FromJson(SettingsJson.ParseObject("""{"source":"L","amount":0.5}"""));
        Assert.True(on.Enabled); Assert.Equal(LuminancePreset.Lum, on.Preset); Assert.Equal(0.5f, on.Amount);
        var off = LuminanceSettings.FromJson(SettingsJson.ParseObject("""{"source":null,"amount":1}"""));
        Assert.False(off.Enabled);
        var fresh = LuminanceSettings.FromJson(SettingsJson.ParseObject("{}"));
        Assert.Equal(new LuminanceSettings(), fresh);
    }
}

public class MonoLuminanceTests
{
    [Fact]
    public void MonoPalettesIgnoreLuminance()
    {
        var sources = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha), [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 0.4f, 2) };
        var planes = sources.Images();
        var s = new CombineSettings(); s.Select(Palettes.Palette("monochrome")!); s.MonoChannels = [ChannelKind.OIII];
        s.Luminance.Enabled = true; s.Luminance.Preset = LuminancePreset.Ha;
        Assert.False(s.LuminanceApplies);
        Assert.DoesNotContain(ChannelKind.Ha, s.AllChannels(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII }));
        var ctx = RenderContext.Make(s.Normalization, sources);
        var r = Renderer.Render(new RenderRequest(planes, s, ctx, Renderer.AnalysisSamples(sources)));
        var off = s.Clone(); off.Luminance.Enabled = false;
        var r2 = Renderer.Render(new RenderRequest(planes, off, ctx, Renderer.AnalysisSamples(sources)));
        Assert.True(r.Image.R.MaxAbsDifference(r2.Image.R) < 1e-6f);
        s.Select(Palettes.Palette("hoo")!);
        Assert.True(s.LuminanceApplies);
    }
}

public class MonochromeModeTests
{
    [Fact]
    public void MonochromeAveragesAllLoadedChannels()
    {
        var sources = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha), [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 0.4f, 2), [ChannelKind.SII] = Synth.Source(ChannelKind.SII, 0.3f, 3) };
        var planes = sources.Images();
        var s = new CombineSettings(); s.Select(Palettes.Palette("monochrome")!);
        s.Stretch.Method = StretchMethod.None; s.Normalization.Method = NormalizationMethod.None;
        Assert.Equal(CombineMode.Monochrome, s.Mode);
        Assert.Empty(s.RequiredChannels);
        var three = new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII };
        Assert.Equal(three, s.AllChannels(three));
        Assert.Contains(Palettes.Available(new HashSet<ChannelKind> { ChannelKind.Ha }), p => p.IsMonochrome);
        Assert.DoesNotContain(Palettes.Available(new HashSet<ChannelKind>()), p => p.IsMonochrome);
        var ctx = RenderContext.Make(s.Normalization, sources);
        var r = Renderer.Render(new RenderRequest(planes, s, ctx));
        float expected = (planes[ChannelKind.Ha][10, 10] + planes[ChannelKind.OIII][10, 10] + planes[ChannelKind.SII][10, 10]) / 3;
        A.Close(MathF.Min(expected, 1), r.Image.R[10, 10], 1e-6f);
        Assert.Equal(r.Image.R[10, 10], r.Image.G[10, 10]); Assert.Equal(r.Image.R[10, 10], r.Image.B[10, 10]);
        // Weight sliders and the channel subset are honoured.
        s.SetAdjust(ChannelKind.Ha, new ChannelAdjust { Weight = 0.5f });
        s.MonoChannels = [ChannelKind.Ha, ChannelKind.OIII];
        var r2 = Renderer.Render(new RenderRequest(planes, s, ctx));
        A.Close(MathF.Min((0.5f * planes[ChannelKind.Ha][10, 10] + planes[ChannelKind.OIII][10, 10]) / 2, 1), r2.Image.R[10, 10], 1e-6f);
        // Disabled channels drop out of the average.
        s.SetAdjust(ChannelKind.OIII, new ChannelAdjust { Enabled = false }); s.SetAdjust(ChannelKind.Ha, new ChannelAdjust());
        var r3 = Renderer.Render(new RenderRequest(planes, s, ctx));
        A.Close(MathF.Min(planes[ChannelKind.Ha][10, 10], 1), r3.Image.R[10, 10], 1e-6f);
        // Round trip.
        Assert.Equal(s, CombineSettings.Parse(s.Json()));
    }
}

public class ColorBandsAndDetailTests
{
    [Fact]
    public void HslRoundTrip()
    {
        foreach (var (r, g, b) in new (float, float, float)[] { (0.8f, 0.2f, 0.2f), (0.1f, 0.9f, 0.3f), (0.2f, 0.2f, 0.9f), (0.5f, 0.5f, 0.5f), (1, 0.6f, 0), (0.3f, 0.8f, 0.8f) })
        {
            var (h, s, l) = PostProcess.RgbToHsl(r, g, b);
            var (r2, g2, b2) = PostProcess.HslToRgb(h, s, l);
            A.Close(r, r2, 1e-4f); A.Close(g, g2, 1e-4f); A.Close(b, b2, 1e-4f);
        }
        Assert.Equal(0, PostProcess.RgbToHsl(1, 0, 0).h);
        Assert.Equal(120, PostProcess.RgbToHsl(0, 1, 0).h);
        Assert.Equal(240, PostProcess.RgbToHsl(0, 0, 1).h);
    }

    [Fact]
    public void BandWeightsPartitionUnity()
    {
        var centers = ColorBands.All.Select(b => b.Hue()).ToArray();
        for (int h = 0; h < 360; h += 7)
        {
            float sum = 0;
            for (int i = 0; i < centers.Length; i++) sum += PostProcess.BandWeight(h, i, centers);
            A.Close(1, sum, 1e-4f, $"hue {h}");
        }
        Assert.Equal(1, PostProcess.BandWeight(0, 0, centers));
        A.Close(0.5f, PostProcess.BandWeight(15, 0, centers), 1e-5f);
        A.Close(0.5f, PostProcess.BandWeight(330, 0, centers), 1e-5f);
        Assert.Equal(0, PostProcess.BandWeight(120, 0, centers));
    }

    [Fact]
    public void SelectiveColourAffectsOnlyItsBand()
    {
        var img = new RGBImage(3, 1);
        new float[] { 0.8f, 0.2f, 0.5f }.CopyTo(img.R.Data, 0);   // red, blue, grey
        new float[] { 0.2f, 0.2f, 0.5f }.CopyTo(img.G.Data, 0);
        new float[] { 0.2f, 0.9f, 0.5f }.CopyTo(img.B.Data, 0);
        var s = new ColorBandsSettings();
        s.Set(ColorBand.Reds, new ColorBandAdjust { Saturation = -1 });
        PostProcess.ColorBands(img, s);
        A.Close(img.G[0, 0], img.R[0, 0], 1e-4f, "red pixel fully desaturated");
        A.Close(0.9f, img.B[1, 0], 1e-5f, "blue pixel untouched");
        A.Close(0.5f, img.R[2, 0], 1e-5f, "grey untouched");
        var hueShift = new ColorBandsSettings(); hueShift.Set(ColorBand.Blues, new ColorBandAdjust { Hue = 60 });
        var img2 = new RGBImage(1, 1); img2.R.Data[0] = 0.2f; img2.G.Data[0] = 0.2f; img2.B.Data[0] = 0.9f;
        PostProcess.ColorBands(img2, hueShift);
        A.Close(300, PostProcess.RgbToHsl(img2.R[0, 0], img2.G[0, 0], img2.B[0, 0]).h, 0.5f);
        Assert.True(new ColorBandsSettings().IsNeutral);
    }

    [Fact]
    public void TextureAndClarityDoNotDigHalosAroundStars()
    {
        // Flat background with a bright star: local contrast must not darken the ring around it.
        int w = 256, h = 256;
        var img = new RGBImage(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float dx = x - 128, dy = y - 128;
            float v = 0.3f + 0.7f * MathF.Exp(-(dx * dx + dy * dy) / 18);
            img.R[x, y] = v; img.G[x, y] = v; img.B[x, y] = v;
        }
        var d = new DetailSettings { Texture = 1, Clarity = 1, Sharpen = 2, SharpenRadius = 2 };
        PostProcess.Detail(img, d);
        var lum = img.Luma();
        float ringMin = 1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float r = MathF.Sqrt((x - 128) * (x - 128) + (y - 128) * (y - 128));
            if (r > 6 && r < 40) ringMin = MathF.Min(ringMin, lum[x, y]);
        }
        Assert.True(ringMin > 0.3f - 0.01f, $"dark halo around the star ({ringMin})");
        Assert.True(lum[128, 128] > 0.9f);
    }

    [Fact]
    public void DetailChangesLocalContrastAndStripsMatch()
    {
        var sources = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha), [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 0.4f, 2) };
        var planes = sources.Images();
        var s = new CombineSettings(); s.Select(Palettes.Palette("hoo")!);
        var ctx = RenderContext.Make(s.Normalization, sources);
        var samples = Renderer.AnalysisSamples(sources);
        var plain = Renderer.Render(new RenderRequest(planes, s, ctx, samples));
        s.Detail.Clarity = 0.8f; s.Detail.Dehaze = 0.5f;
        var det = Renderer.Render(new RenderRequest(planes, s, ctx, samples));
        Assert.True(det.Image.R.MaxAbsDifference(plain.Image.R) > 0.01f);
        A.Bounded(det.Image);
        // Clarity raises the local contrast: the std-dev of the high-pass grows.
        static float HighPassStd(PlanarImage p) { var b = PostProcess.PyramidBlur(p, 2); float sum = 0; for (int i = 0; i < p.Count; i++) { float d = p.Data[i] - b.Data[i]; sum += d * d; } return MathF.Sqrt(sum / p.Count); }
        var onlyClarity = s.Clone(); onlyClarity.Detail = new DetailSettings { Clarity = 0.8f };
        var cl = Renderer.Render(new RenderRequest(planes, onlyClarity, ctx, samples));
        Assert.True(HighPassStd(cl.Image.Luma()) > HighPassStd(plain.Image.Luma()));
        // Strip-wise export equals the whole render.
        var strips = Renderer.RenderFull(sources, s, ctx, det.PostStretch, stripPixels: 256 * 40)!;
        for (int c = 0; c < 3; c++) Assert.True(det.Image.Planes[c].MaxAbsDifference(strips.Planes[c]) < 1e-4f);
        Assert.Equal(7, PostProcess.BlurLevels(7272, 0.015f));
        Assert.Equal(6, PostProcess.BlurLevels(3636, 0.015f));
        // The three scales are ordered: texture < clarity < dehaze.
        Assert.True(PostProcess.BlurLevels(2000, PostProcess.TextureRadius) < PostProcess.BlurLevels(2000, PostProcess.ClarityRadius));
        Assert.True(PostProcess.BlurLevels(2000, PostProcess.ClarityRadius) < PostProcess.BlurLevels(2000, PostProcess.DehazeRadius));
        var tex = s.Clone(); tex.Detail = new DetailSettings { Texture = 0.8f };
        var tx = Renderer.Render(new RenderRequest(planes, tex, ctx, samples));
        Assert.True(HighPassStd(tx.Image.Luma()) > HighPassStd(plain.Image.Luma()));
        // Settings round trip with bands and sharpening.
        s.ColorBands.Set(ColorBand.Teals, new ColorBandAdjust { Hue = 12 }); s.Detail.Sharpen = 0.7f; s.Detail.SharpenRadius = 2.5f;
        Assert.Equal(s, CombineSettings.Parse(s.Json()));
    }

    [Fact]
    public void SharpenIsAnUnsharpMaskScaledToTheRenderLevel()
    {
        // A soft edge gets steeper; the same radius at half scale (preview level 1) blurs half as much.
        int w = 64, h = 16;
        static RGBImage Edge(int w, int h)
        {
            var img = new RGBImage(w, h);
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
            {
                float v = 0.2f + 0.6f / (1 + MathF.Exp(-(x - 32)));
                img.R[x, y] = v; img.G[x, y] = v; img.B[x, y] = v;
            }
            return img;
        }
        static float Slope(RGBImage img) { var l = img.Luma(); return l[33, 8] - l[31, 8]; }
        var plain = Edge(w, h);
        var d = new DetailSettings { Sharpen = 1, SharpenRadius = 2 };
        var full = Edge(w, h); PostProcess.Detail(full, d, 1);
        var half = Edge(w, h); PostProcess.Detail(half, d, 0.5f);
        var tiny = Edge(w, h); PostProcess.Detail(tiny, d, 0.1f);
        Assert.True(Slope(full) > Slope(plain) * 1.2f);
        Assert.True(Slope(half) > Slope(plain));
        Assert.True(Slope(half) < Slope(full));
        A.Close(Slope(plain), Slope(tiny), 1e-6f, "σ below a quarter pixel: no-op");
        A.Bounded(full);
        // Flat areas are untouched; the Gaussian is normalised.
        var flat = new PlanarImage(32, 32, 0.4f);
        Assert.True(PostProcess.GaussianBlur(flat, 1.5f).MaxAbsDifference(flat) < 1e-5f);
        Assert.True(new DetailSettings().IsNeutral);
        Assert.False(new DetailSettings { Sharpen = 0.5f }.IsNeutral);
        // Masking leaves faint noise alone but still sharpens the strong edge.
        var rng = new SeededRng(7);   // deterministic: the slope margin below is small
        var reference = Edge(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float v = reference.R[x, y] + rng.Uniform(-0.01f, 0.01f);
            reference.R[x, y] = v; reference.G[x, y] = v; reference.B[x, y] = v;
        }
        RGBImage MeanNoiseDelta(float masking)
        {
            var out_ = reference.Copy();
            var m = d.Clone(); m.SharpenMasking = masking;
            PostProcess.Detail(out_, m, 1);
            return out_;
        }
        float NoiseChange(RGBImage img)
        {
            float sum = 0;
            for (int y = 0; y < h; y++) for (int x = 2; x < 18; x++) sum += MathF.Abs(img.R[x, y] - reference.R[x, y]);
            return sum / (16 * h);
        }
        var unmasked = MeanNoiseDelta(0); var masked = MeanNoiseDelta(1);
        Assert.True(NoiseChange(masked) < 0.15f * NoiseChange(unmasked), "masking must spare the flat noisy area");
        // At the very centre of the sigmoid the high-pass is ~0 (masked out), so compare the
        // overshoot across the whole edge instead.
        static float Wide(RGBImage img) { var l = img.Luma(); return l[34, 8] - l[30, 8]; }
        Assert.True(Wide(masked) > Wide(reference) * 1.1f);
    }
}
