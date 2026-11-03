using AstroCore;

namespace AstroCore.Tests;

public class CorrectionsTests
{
    [Fact]
    public void MapResolutionDabAndApplication()
    {
        var m = new CorrectionMap(4096, 3072);
        Assert.Equal(1024, m.Width); Assert.Equal(768, m.Height);
        Assert.True(m.IsEmpty);
        // A soft dab darkens around its centre, fades out at the radius, nothing beyond.
        m.Dab(2048, 1536, 400, -0.5f, 0.3f);
        Assert.False(m.IsEmpty);
        A.Close(-0.5f, m.Value(2048, 1536), 0.02f);
        Assert.True(Math.Abs(m.Value(2048 + 380, 1536)) < 0.05f);
        Assert.Equal(0, m.Value(100, 100));
        // Dabs accumulate and clamp at ±1.
        for (int i = 0; i < 5; i++) m.Dab(2048, 1536, 400, -0.5f, 0.3f);
        A.Close(-1, m.Value(2048, 1536), 0.02f);
        // Applied: 2^(strength × map) — one stop at strength 1, two at strength 2; zero pixels stay zero.
        var img = new PlanarImage(4096, 3072, 0.4f);
        img[10, 10] = 0;
        var out_ = m.Applied(img, 1);
        A.Close(0.2f, out_[2048, 1536], 0.01f);
        A.Close(0.4f, out_[100, 100], 1e-6f);
        Assert.Equal(0, out_[10, 10]);
        var two = m.Applied(img, 2);
        A.Close(0.1f, two[2048, 1536], 0.01f);
        // The region form (a crop at a pyramid level) agrees with the whole-image one.
        var crop = img.Cropped(1800, 1300, 500, 500).Downsampled2x();
        m.Apply(crop, 1, new SampleRect(1800, 1300, 500, 500));
        A.Close(out_[2048, 1536], crop[124, 118], 0.01f);
        // A stroke lays dabs along its path.
        var s = new CorrectionMap(1000, 800);
        s.Stroke(100, 400, 900, 400, 40, 0.3f, 0.5f);
        Assert.True(s.Value(500, 400) > 0.25f);
        Assert.Equal(0, s.Value(500, 600));
        s.Clear(); Assert.True(s.IsEmpty);
    }

    [Fact]
    public void OrientationAndSidecarRoundTrip()
    {
        var m = new CorrectionMap(800, 600);
        m.Dab(100, 500, 60, 0.8f, 0.5f);   // bottom-left
        var rotated = m.Applying(OrientationOp.RotateClockwise);
        Assert.Equal(600, rotated.ImageWidth); Assert.Equal(800, rotated.ImageHeight);
        A.Close(0.8f, rotated.Value(600 - 500, 100), 0.05f, "the dab moved with the image");
        var o = new Orientation(1, true);
        var oriented = m.Applying(OrientationOp.FlipHorizontal).Applying(OrientationOp.RotateClockwise);
        var back = oriented.Unoriented(o);
        A.Close(0.8f, back.Value(100, 500), 0.05f, "unoriented returns to the file's frame");
        // Sidecar: written next to the image, read back with the same values.
        var image = Synth.TempPath("corr.fits");
        FitsWriter.Write([new PlanarImage(800, 600, 0.1f)], image);
        var location = m.Write(image);
        Assert.EndsWith("corr.fits.ccmask.png", location);
        var read = CorrectionMap.Read(image, 800, 600);
        Assert.NotNull(read);
        Assert.True(read!.Plane.MaxAbsDifference(m.Plane) < 1.0f / 65535 * 2);
        Assert.Null(CorrectionMap.Read(image, 801, 600));
        CorrectionMap.Remove(image);
        Assert.Null(CorrectionMap.Read(image, 800, 600));
        // Read-only folder: the fallback in the application data folder takes it.
        var ro = OperatingSystem.IsWindows() ? @"C:\Windows\nonexistent\x.fits" : "/System/Library/nonexistent.fits";
        var fb = m.Write(ro);
        Assert.Contains("Corrections", fb);
        Assert.NotNull(CorrectionMap.Read(ro, 800, 600));
        CorrectionMap.Remove(ro);
        // Settings decode with defaults.
        var old = CorrectionSettings.FromJson(System.Text.Json.Nodes.JsonNode.Parse("{}")!.AsObject());
        Assert.Equal(0.04, old.BrushRadius); Assert.Equal(1, old.StrengthFor(ChannelKind.Ha));
    }

    [Fact]
    public void LiveCorrectionInTheRendererMatchesTheCommittedOne()
    {
        var sources = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha, width: 256, height: 192), [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 0.4f, 2, 256, 192) };
        var s = new CombineSettings(); s.Select(Palettes.Palette("hoo")!);
        var ctx = RenderContext.Make(s.Normalization, sources);
        var map = new CorrectionMap(256, 192);
        map.Dab(128, 96, 50, 0.7f, 0.4f);
        var live = new RenderRequest(sources.ToDictionary(kv => kv.Key, kv => kv.Value.Image), s, ctx) { Correction = new CorrectionInput(map, 1, ChannelKind.Ha, null) };
        var a = Renderer.Render(live);
        var planes = sources.ToDictionary(kv => kv.Key, kv => kv.Value.Image);
        planes[ChannelKind.Ha] = map.Applied(sources[ChannelKind.Ha].Image, 1);
        var b = Renderer.Render(new RenderRequest(planes, s, ctx));
        for (int c = 0; c < 3; c++) Assert.True(a.Image.Planes[c].MaxAbsDifference(b.Image.Planes[c]) < 1e-4f);
        var plain = Renderer.Render(new RenderRequest(sources.ToDictionary(kv => kv.Key, kv => kv.Value.Image), s, ctx));
        Assert.True(a.Image.R[128, 96] > plain.Image.R[128, 96], "brightened where painted (Ha drives red in HOO)");
    }
}
