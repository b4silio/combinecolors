using AstroCore;

namespace AstroCore.Tests;

public class BackgroundSamplesTests
{
    const int W = 480, H = 360;
    const float Sky = 0.01f, Noise = 0.0003f;

    /// <summary>Flat sky + noise + a nebula blob + stars; flat is the truth without background structure.</summary>
    static (PlanarImage flat, PlanarImage nebula, List<(int, int)> stars) Scene(ulong seed = 7)
    {
        (float x, float y, float s, float amp) nb = (200, 160, 60, 0.004f);
        var flat = new PlanarImage(W, H); var neb = new PlanarImage(W, H);
        var rng = new Synth.LCG(seed);
        var stars = new List<(int, int)>();
        for (int sy = 24; sy < H; sy += 48) for (int sx = 24; sx < W; sx += 48) stars.Add((sx + (int)(rng.Next() * 20) - 10, sy + (int)(rng.Next() * 20) - 10));
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            float v = Sky + Noise * rng.Gaussian();
            float dx = x - nb.x, dy = y - nb.y;
            float n = nb.amp * MathF.Exp(-(dx * dx + dy * dy) / (2 * nb.s * nb.s));
            neb[x, y] = n; v += n;
            foreach (var (sx, sy) in stars)
            {
                float ddx = x - sx, ddy = y - sy, d2 = ddx * ddx + ddy * ddy;
                if (d2 < 30) v += 0.2f * MathF.Exp(-d2 / 2.5f);
            }
            flat[x, y] = v;
        }
        return (flat, neb, stars);
    }

    /// <summary>Light pollution: a tilted plane (10 noise across the frame) plus, when asked, a compact glow in the top-right corner.</summary>
    static float Background(int x, int y, bool glow = true)
    {
        float fx = x / (float)W, fy = y / (float)H;
        float v = 0.003f * fx - 0.002f * fy;
        if (glow) { float dx = fx - 1, dy = fy; v += 0.004f * MathF.Exp(-(dx * dx + dy * dy) / (2 * 0.22f * 0.22f)); }
        return v;
    }

    static PlanarImage Add(PlanarImage a, Func<int, int, float> f)
    {
        var o = new PlanarImage(a.Width, a.Height);
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) o[x, y] = a[x, y] + f(x, y);
        return o;
    }

    /// <summary>RMS of a − b over the sky (stars and nebula left out) after removing the mean difference, in noise units.</summary>
    static float SkyResidualSigma(PlanarImage a, PlanarImage b, List<(int, int)> stars, PlanarImage? exclude = null)
    {
        double sum = 0, sq = 0; int n = 0;
        var nearStar = new bool[a.Width * a.Height];
        foreach (var (sx, sy) in stars) for (int dy = -6; dy <= 6; dy++) for (int dx = -6; dx <= 6; dx++)
        {
            int x = sx + dx, y = sy + dy;
            if (x >= 0 && y >= 0 && x < a.Width && y < a.Height) nearStar[y * a.Width + x] = true;
        }
        for (int y = 2; y < a.Height - 2; y += 3) for (int x = 2; x < a.Width - 2; x += 3)
        {
            if (nearStar[y * a.Width + x]) continue;
            if (exclude != null && exclude[x, y] > Noise * 0.5f) continue;
            double d = a[x, y] - b[x, y]; sum += d; sq += d * d; n++;
        }
        double mean = sum / n;
        return (float)Math.Sqrt(sq / n - mean * mean) / Noise;
    }

    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    [Fact]
    public void GridLeavesOutTheNebulaAndCoversTheSky()
    {
        var (flat, _, _) = Scene();
        var shaded = Add(flat, (x, y) => Background(x, y, glow: false));
        var s = GradientRemoval.GetSamples(shaded);
        double radius = BackgroundSamples.DefaultRadius(W, H);
        var grid = BackgroundSamples.Grid(s, 12, radius, 2.5);
        Assert.True(grid.Count > 50, $"most of the 12 × 9 grid is sky ({grid.Count})");
        Assert.DoesNotContain(grid, p => Hypot(p.X - 200, p.Y - 160) < 80);
        Assert.Contains(grid, p => p.X > 400 && p.Y < 60);
        Assert.Contains(grid, p => p.X < 80 && p.Y > 300);
        // A compact corner glow is not planar: dropped at the default tolerance, kept at a large one.
        var glowing = Add(flat, (x, y) => Background(x, y, glow: true));
        var gs = GradientRemoval.GetSamples(glowing);
        var g2 = BackgroundSamples.Grid(gs, 12, radius, 2.5);
        Assert.True(g2.Count > 45, $"{g2.Count}");
        Assert.DoesNotContain(g2, p => p.X > 430 && p.Y < 40);
        var g3 = BackgroundSamples.Grid(gs, 12, radius, 60);
        Assert.Contains(g3, p => p.X > 430 && p.Y < 40);
        // A disc on padding gives no level; a disc on sky does.
        var padded = shaded.Copy();
        for (int y = 0; y < H; y++) for (int x = 0; x < 60; x++) padded[x, y] = 0;
        var ps = GradientRemoval.GetSamples(padded);
        Assert.Null(BackgroundSamples.Measure(ps, 20, 100, radius));
        Assert.NotNull(BackgroundSamples.Measure(ps, 300, 100, radius));
    }

    [Fact]
    public void SplineAndKrigingRecoverASmoothGradientAndKeepTheNebula()
    {
        var (flat, neb, stars) = Scene();
        var shaded = Add(flat, (x, y) => Background(x, y));
        Assert.True(SkyResidualSigma(shaded, flat, stars, neb) > 3);
        var s = GradientRemoval.GetSamples(shaded);
        double radius = BackgroundSamples.DefaultRadius(W, H);
        var points = BackgroundSamples.Grid(s, 14, radius, 2.5);
        points.AddRange([new BackgroundSample(462, 18), new BackgroundSample(430, 18), new BackgroundSample(462, 50), new BackgroundSample(400, 18), new BackgroundSample(462, 85), new BackgroundSample(425, 50)]);
        foreach (var surface in SampleSurfaces.All)
        {
            var spec = new GradientSpec(GradientGesture.Samples()) { Mode = GradientMode.Subtract, Samples = points.Select(p => p.Clone()).ToList(), Surface = surface, SampleSmoothing = 0.5, AutoSmoothing = false, GridPerRow = 14, GridTolerance = 2.5 };
            var result = GradientRemoval.Correct(shaded, spec);
            Assert.NotNull(result);
            var (corrected, fit) = result.Value;
            float err = SkyResidualSigma(corrected, flat, stars, neb);
            Assert.True(err < 1.2f, $"{surface}: sky flat to the noise ({err}σ)");
            float dc = 0, dr = 0; int dcn = 0, drn = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                double d = Hypot(x - 200, y - 160);
                if (d < 10) { dc += corrected[x, y] - flat[x, y]; dcn++; } else if (d > 150 && d < 160) { dr += corrected[x, y] - flat[x, y]; drn++; }
            }
            Assert.True(Math.Abs(dc / dcn) < 2.5f * Noise, $"{surface}: the nebula centre keeps its brightness to the noise");
            Assert.True(Math.Abs(dr / drn) < 3 * Noise, $"{surface}: the nebula's surroundings keep their level");
            Assert.True(fit.SampleResults.Count > 60);
            Assert.True(fit.ResidualSigma < 2);
        }
    }

    [Fact]
    public void SamplesAboveTheSkyAreRejectedByTheFit()
    {
        var (flat, _, _) = Scene();
        var shaded = Add(flat, (x, y) => Background(x, y, glow: false));
        var s = GradientRemoval.GetSamples(shaded);
        double radius = BackgroundSamples.DefaultRadius(W, H);
        var points = BackgroundSamples.Grid(s, 12, radius, 2.5);
        var onNebula = new[] { new BackgroundSample(200, 160), new BackgroundSample(215, 150) };
        points.AddRange(onNebula);
        var surface = BackgroundSamples.Fit(points, radius, s, SampleSurface.Spline, null);
        Assert.NotNull(surface);
        foreach (var p in onNebula)
        {
            var r = surface.Results.First(t => t.Id == p.Id);
            Assert.True(r.IsRejected, $"sample on the nebula rejected (weight {r.Weight})");
        }
        int used = surface.Results.Count(r => !r.IsRejected);
        Assert.True(used > points.Count - 6);
        var alone = BackgroundSamples.Fit(points.Take(points.Count - 2).ToList(), radius, s, SampleSurface.Spline, null);
        Assert.NotNull(alone);
        A.Close(alone.Value(200, 160), surface.Value(200, 160), 0.5f * Noise, "samples on the nebula do not lift the surface");
        Assert.True(surface.Value(200, 160) < (surface.Results.First(t => t.Id == onNebula[0].Id).Value ?? 1) - 0.5f * 0.004f);
    }

    [Fact]
    public void SamplesSpecRoundTripAndOrientation()
    {
        var spec = new GradientSpec(GradientGesture.Samples()) { Mode = GradientMode.Divide, SampleRadius = 40, Surface = SampleSurface.Kriging, SampleSmoothing = 0.3, GridPerRow = 10, GridTolerance = 3 };
        spec.Samples = [new BackgroundSample(10, 20), new BackgroundSample(470, 350)];
        var settings = new GradientSettings { Surface = SampleSurface.Kriging, GridPerRow = 10 };
        settings.Specs[ChannelKind.Ha] = spec;
        var back = GradientSettings.FromJson(SettingsJson.ParseObject(settings.ToJson().ToJsonString()));
        Assert.Equal(settings, back);
        Assert.Equal(spec, back.Specs[ChannelKind.Ha]);
        var rotated = spec.Applying(OrientationOp.RotateClockwise, 480, 360);
        Assert.Equal(340, rotated.Samples[0].X); Assert.Equal(10, rotated.Samples[0].Y);
        var flipped = spec.Applying(OrientationOp.FlipHorizontal, 480, 360);
        Assert.Equal(10, flipped.Samples[1].X);
        // Old documents without the new keys decode to the defaults.
        var o = GradientSpec.Parse("""{"gesture":{"shape":"polynomial"},"mode":"subtract"}""");
        Assert.Equal(SampleSurface.Spline, o.Surface); Assert.Empty(o.Samples); Assert.Equal(14, o.GridPerRow);
    }
}
