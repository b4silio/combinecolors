using AstroCore;

namespace AstroCore.Tests;

public class StatisticsTests
{
    [Fact]
    public void MedianMadPercentiles()
    {
        var s = Statistics.FromSample([5, 1, 3, 2, 4]);
        Assert.Equal(3, s.Median);
        Assert.Equal(1, s.Mad);
        A.Close(1.4826f, s.Madn, 1e-4f);
        Assert.Equal(1, s.Min); Assert.Equal(5, s.Max);
        Assert.Equal(1, s.Percentile(0)); Assert.Equal(5, s.Percentile(1));
        Assert.Equal(2, s.Percentile(0.25));
        var even = Statistics.FromSample([1, 2, 3, 4]);
        Assert.Equal(2.5f, even.Median);
    }

    [Fact]
    public void MadTwoPointerMatchesNaive()
    {
        var rng = new Synth.LCG(42);
        foreach (var n in new[] { 1, 2, 3, 10, 101, 1000 })
        {
            var a = new float[n];
            for (int i = 0; i < n; i++) a[i] = rng.Gaussian();
            Array.Sort(a);
            float med = Statistics.Percentile(a, 0.5);
            float fast = Statistics.MedianAbsoluteDeviation(a, med);
            var dev = a.Select(v => MathF.Abs(v - med)).OrderBy(v => v).ToArray();
            float naive = Statistics.Percentile(dev, 0.5);
            A.Close(naive, fast, 1e-6f, $"n={n}");
        }
    }

    [Fact]
    public void ZerosAreExcluded()
    {
        var img = new PlanarImage(10, 10, 0.5f);
        for (int x = 0; x < 5; x++) for (int y = 0; y < 10; y++) img[x, y] = 0;
        var s = Statistics.Compute(img);
        Assert.Equal(0.5f, s.Median);
        A.Close(0.5f, s.ValidFraction, 0.01f);
    }

    [Fact]
    public void NoiseEstimate()
    {
        var img = new PlanarImage(400, 300);
        var rng = new Synth.LCG(7);
        for (int i = 0; i < img.Count; i++) img.Data[i] = 0.5f + 0.01f * rng.Gaussian();
        var s = Statistics.Compute(img);
        A.Close(0.01f, s.Noise, 0.0015f);
        A.Close(0.01f, s.Madn, 0.0015f);
        A.Close(0.5f, s.Background, 0.001f);
        A.Close(0.01f, s.BackgroundSigma, 0.002f);
    }

    [Fact]
    public void BackgroundIgnoresBrightOutliers()
    {
        var values = new float[10_000];
        Array.Fill(values, 0.1f);
        var rng = new Synth.LCG(3);
        for (int i = 0; i < values.Length; i++) values[i] += 0.001f * rng.Gaussian();
        for (int i = 0; i < 500; i++) values[i] = 0.9f;   // stars
        var s = Statistics.FromSample(values);
        A.Close(0.1f, s.Background, 0.0005f);
        Assert.True(s.BackgroundSigma < 0.002f);
    }

    [Fact]
    public void MultiSampleAlignment()
    {
        var a = new PlanarImage(50, 40); var b = new PlanarImage(50, 40);
        for (int i = 0; i < a.Count; i++) { a.Data[i] = i + 1; b.Data[i] = 2 * (i + 1); }
        a.Data[0] = 0;  // invalid in a only
        var s = Statistics.MultiSample([a, b], 500);
        Assert.Equal(s[0].Length, s[1].Length);
        Assert.True(s[0].Length > 100);
        for (int i = 0; i < s[0].Length; i++) Assert.Equal(2 * s[0][i], s[1][i]);
    }

    [Fact]
    public void Histogram()
    {
        var img = new PlanarImage(10, 10, 0.5f);
        var h = Statistics.Histogram(img, 4);
        Assert.Equal(new[] { 0, 0, 100, 0 }, h);
    }

    [Fact]
    public void StatsAffineTransform()
    {
        var s = Statistics.FromSample([0.1f, 0.2f, 0.3f, 0.4f, 0.5f]);
        var t = s.Affine(2, 1);
        A.Close(1.6f, t.Median, 1e-6f);
        A.Close(s.Madn * 2, t.Madn, 1e-6f);
        Assert.Equal(new float[] { 1.2f, 1.4f, 1.6f, 1.8f, 2.0f }, t.SortedSample);
    }

    [Fact]
    public void StarMaskCoversStars()
    {
        var img = Synth.Channel(256, 192, stars: 0.5f, noise: 0.0005f);
        var mask = StarMask.Build(img);
        float cov = StarMask.Coverage(mask);
        Assert.True(cov > 0.01f);
        Assert.True(cov < 0.5f);
        // Brightest pixel must be masked.
        int best = 0;
        for (int i = 0; i < img.Count; i++) if (img.Data[i] > img.Data[best]) best = i;
        Assert.Equal(1, mask.Data[best]);
    }
}

public class CropTests
{
    [Fact]
    public void RegionRestrictsStatistics()
    {
        var img = new PlanarImage(100, 100, 0.2f);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 50; x++) img[x, y] = 0.8f;
        var o = new StatsOptions { Region = new PixelRect(50, 0, 50, 100) };
        Assert.Equal(0.2f, Statistics.Compute(img, o).Median);
        o.Region = new PixelRect(0, 0, 50, 100);
        Assert.Equal(0.8f, Statistics.Compute(img, o).Median);
        A.Close(0.2f, Statistics.Compute(img).Median, 0.31f); // mixed
        var m = Statistics.MultiSample([img], 1000, new PixelRect(0, 0, 50, 100));
        Assert.All(m[0], v => Assert.Equal(0.8f, v));
        Assert.Null(new PixelRect(200, 0, 10, 10).Clamped(100, 100));
        Assert.Equal(new PixelRect(0, 0, 15, 15), new PixelRect(-5, -5, 20, 20).Clamped(100, 100));
        Assert.Equal(new PixelRect(0, 1, 4, 3), new PixelRect(3, 5, 10, 10).Scaled(2));
    }

    [Fact]
    public void AutoCropFindsPaddedBorders()
    {
        var a = Synth.Channel(200, 150); var b = Synth.Channel(200, 150, seed: 9);
        // Zero borders: a has 10 px left / 5 px top, b has 7 px right / 12 px bottom.
        for (int y = 0; y < 150; y++) { for (int x = 0; x < 10; x++) a[x, y] = 0; for (int x = 193; x < 200; x++) b[x, y] = 0; }
        for (int x = 0; x < 200; x++) { for (int y = 0; y < 5; y++) a[x, y] = 0; for (int y = 138; y < 150; y++) b[x, y] = 0; }
        var r = AutoCrop.Detect([a, b])!.Value;
        Assert.Equal(new PixelRect(10, 5, 183, 133), r);
        Assert.Equal(PixelRect.Full(50, 40), AutoCrop.Detect([Synth.Channel(50, 40)]));
    }

    [Fact]
    public void ChannelSourceRegionAndRenderFullCrop()
    {
        var ha = Synth.Source(ChannelKind.Ha); var o3 = Synth.Source(ChannelKind.OIII, 0.5f, 2);
        var crop = new PixelRect(20, 10, 100, 80);
        var sources = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = new(ha, crop), [ChannelKind.OIII] = new(o3, crop) };
        Assert.Equal(crop, sources[ChannelKind.Ha].Region);
        Assert.Same(ha.Pyramid, sources[ChannelKind.Ha].Pyramid);
        var s = new CombineSettings(); s.Select(Palettes.Palette("hoo")!);
        var ctx = RenderContext.Make(s.Normalization, sources);
        var full = Renderer.RenderFull(sources, s, ctx, null)!;
        var cropped = Renderer.RenderFull(sources, s, ctx, null, crop, 100 * 7)!;
        Assert.Equal(100, cropped.Width); Assert.Equal(80, cropped.Height);
        for (int c = 0; c < 3; c++) Assert.True(full.Planes[c].Cropped(crop).MaxAbsDifference(cropped.Planes[c]) < 1e-6f);
        var renamed = new ChannelSource(ha, ChannelKind.SII);
        Assert.Equal(ChannelKind.SII, renamed.Kind); Assert.Equal(ha.Stats, renamed.Stats);
    }
}
