using AstroCore;

namespace AstroCore.Tests;

public class GradientRemovalTests
{
    const int W = 480, H = 360;
    const float Sky = 0.01f, Noise = 0.0003f;

    /// <summary>Flat sky + noise + a nebula blob + a grid of stars; `flat` has no background structure, `nebula` is the blob alone.</summary>
    static (PlanarImage flat, PlanarImage nebula, List<(int, int)> stars) Scene(ulong seed = 7, (float x, float y, float s, float amp)? nb = null, bool noNebula = false)
    {
        nb ??= noNebula ? null : (200, 160, 60, 0.004f);
        var flat = new PlanarImage(W, H); var neb = new PlanarImage(W, H);
        var rng = new Synth.LCG(seed);
        var stars = new List<(int, int)>();
        for (int sy = 24; sy < H; sy += 48) for (int sx = 24; sx < W; sx += 48) stars.Add((sx + (int)(rng.Next() * 20) - 10, sy + (int)(rng.Next() * 20) - 10));
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            float v = Sky + Noise * rng.Gaussian();
            if (nb is var b && b != null)
            {
                float dx = x - b.Value.x, dy = y - b.Value.y;
                float n = b.Value.amp * MathF.Exp(-(dx * dx + dy * dy) / (2 * b.Value.s * b.Value.s));
                neb[x, y] = n; v += n;
            }
            foreach (var (sx, sy) in stars)
            {
                float dx = x - sx, dy = y - sy, d2 = dx * dx + dy * dy;
                if (d2 < 30) v += 0.2f * MathF.Exp(-d2 / 2.5f);
            }
            flat[x, y] = v;
        }
        return (flat, neb, stars);
    }

    /// <summary>Knee-shaped elliptical vignetting (flat inside, steep past the knee), 1 at the centre.</summary>
    static PlanarImage Vignetting(float cx = 200, float cy = 160, float a = 300, float b = 240, float angle = 0.2f, float depth = 0.35f)
    {
        var t = new PlanarImage(W, H);
        float ct = MathF.Cos(angle), st = MathF.Sin(angle);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            float dx = x + 0.5f - cx, dy = y + 0.5f - cy;
            float xr = (dx * ct + dy * st) / a, yr = (-dx * st + dy * ct) / b;
            float r = MathF.Sqrt(xr * xr + yr * yr);
            t[x, y] = MathF.Max(0.3f, 1 - depth * (1 - MathF.Exp(-MathF.Pow(r / 0.7f, 6))));
        }
        return t;
    }

    static PlanarImage Multiply(PlanarImage a, PlanarImage t, float px = 0, float py = 0)
    {
        var o = new PlanarImage(a.Width, a.Height);
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) o[x, y] = a[x, y] * t[x, y] + px * x / a.Width + py * y / a.Height;
        return o;
    }

    /// <summary>RMS over the sky (stars and nebula left out) of a − b after removing the mean difference, in units of the pixel noise.</summary>
    static float SkyResidualSigma(PlanarImage a, PlanarImage b, List<(int, int)> stars, PlanarImage? exclude = null)
    {
        var near = new bool[a.Width * a.Height];
        foreach (var (sx, sy) in stars) for (int dy = -6; dy <= 6; dy++) for (int dx = -6; dx <= 6; dx++)
        {
            int x = sx + dx, y = sy + dy;
            if (x >= 0 && y >= 0 && x < a.Width && y < a.Height) near[y * a.Width + x] = true;
        }
        double sum = 0, sq = 0; int n = 0;
        for (int y = 2; y < a.Height - 2; y += 3) for (int x = 2; x < a.Width - 2; x += 3)
        {
            if (near[y * a.Width + x]) continue;
            if (exclude != null && exclude[x, y] > Noise * 0.5f) continue;
            double d = a[x, y] - b[x, y]; sum += d; sq += d * d; n++;
        }
        double mean = sum / n;
        return (float)Math.Sqrt(sq / n - mean * mean) / Noise;
    }

    [Fact]
    public void EllipseGuideBeatsThePolynomialOnVignetting()
    {
        var (flat, neb, stars) = Scene();
        var t = Vignetting();
        var shaded = Multiply(flat, t, 0.0015f, -0.0008f);
        var protect = new[] { new GradientRegion(200, 160, 110, 110) };
        Assert.True(SkyResidualSigma(shaded, flat, stars, neb) > 2.5f);
        var spec = new GradientSpec(GradientGesture.Ellipse(230, 180, 280, 280)) { Mode = GradientMode.Subtract, Residual = GradientResidual.Plane, Refine = true };
        var guided = GradientRemoval.Correct(shaded, spec, protect);
        Assert.NotNull(guided);
        var poly = GradientRemoval.Correct(shaded, new GradientSpec(GradientGesture.Polynomial()) { Residual = GradientResidual.None, Degree = 4, Refine = false }, protect);
        Assert.NotNull(poly);
        var truth = new PlanarImage(W, H);
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++) truth[x, y] = flat[x, y] - neb[x, y] + neb[x, y] * t[x, y];
        float guidedErr = SkyResidualSigma(guided.Value.image, truth, stars, neb), polyErr = SkyResidualSigma(poly.Value.image, truth, stars, neb);
        Assert.True(guidedErr < 1.2f, $"guided {guidedErr}");
        Assert.True(guidedErr < polyErr * 0.8f, $"guided {guidedErr} vs poly {polyErr}");
        var g = guided.Value.fit.Gesture;
        Assert.InRange(g.X, 175, 225); Assert.InRange(g.Y, 135, 185);
        Assert.InRange(g.B / g.A, 240.0 / 300 - 0.1, 240.0 / 300 + 0.1);
    }

    [Fact]
    public void LineGuideRecoversAOneDimensionalGradient()
    {
        var (flat, neb, stars) = Scene();
        var shaded = new PlanarImage(W, H);
        float dx = 0.92f, dy = 0.39f;
        for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
        {
            float u = (x * dx + y * dy) / 520;
            shaded[x, y] = flat[x, y] + 0.006f / (1 + MathF.Exp(-(u - 0.55f) * 12)) + 0.001f * u;
        }
        var spec = new GradientSpec(GradientGesture.Line(80, 220, 380, 220)) { Residual = GradientResidual.None, Refine = true };
        var r = GradientRemoval.Correct(shaded, spec, [new GradientRegion(200, 160, 110, 110)]);
        Assert.NotNull(r);
        float err = SkyResidualSigma(r.Value.image, flat, stars, neb);
        Assert.True(err < 1.1f, $"err {err}");
        var g = r.Value.fit.Gesture;
        Assert.Equal(Math.Atan2(dy, dx), Math.Atan2(g.Y2 - g.Y, g.X2 - g.X), 0.05);
        var fixedSpec = spec.Clone(); fixedSpec.Refine = false;
        var un = GradientRemoval.Correct(shaded, fixedSpec)!.Value.image;
        Assert.True(SkyResidualSigma(un, flat, stars, neb) > err * 1.5f);
    }

    [Fact]
    public void ProtectedRegionKeepsTheNebula()
    {
        var (flat, _, _) = Scene(nb: (240, 180, 70, 0.005f));
        var shaded = Multiply(flat, Vignetting(240, 180));
        float NebulaLeft(PlanarImage img)
        {
            float c = 0, s = 0; int cn = 0, sn = 0;
            for (int y = 0; y < H; y++) for (int x = 0; x < W; x++)
            {
                float d = MathF.Sqrt((x - 240) * (x - 240) + (y - 180) * (y - 180));
                if (d < 15) { c += img[x, y]; cn++; } else if (d > 170 && d < 185 && x > 10 && x < W - 10) { s += img[x, y]; sn++; }
            }
            return (c / cn - s / sn) / 0.005f;
        }
        var spec = new GradientSpec(GradientGesture.Ellipse(240, 180, 300, 240)) { Mode = GradientMode.Divide, Residual = GradientResidual.None, Refine = false };
        var unprotected = GradientRemoval.Correct(shaded, spec)!.Value.image;
        var protectd = GradientRemoval.Correct(shaded, spec, [new GradientRegion(240, 180, 160, 160)])!.Value.image;
        Assert.True(NebulaLeft(protectd) > 0.85f);
        Assert.True(NebulaLeft(protectd) > NebulaLeft(unprotected) + 0.1f);
    }

    [Fact]
    public void DivideRestoresStarFluxAndKeepsZeroPadding()
    {
        var (flat, _, stars) = Scene(noNebula: true);
        var shaded = Multiply(flat, Vignetting());
        for (int y = 0; y < H; y++) for (int x = 0; x < 20; x++) shaded[x, y] = 0;
        var spec = new GradientSpec(GradientGesture.Ellipse(200, 160, 300, 240, 0.2)) { Mode = GradientMode.Divide, Residual = GradientResidual.None, Refine = false };
        var outImg = GradientRemoval.Correct(shaded, spec)!.Value.image;
        float Flux(PlanarImage img, (int, int) s)
        {
            float sum = 0, bg = 0; int n = 0;
            for (int dy = -4; dy <= 4; dy++) for (int dx = -4; dx <= 4; dx++) sum += img[s.Item1 + dx, s.Item2 + dy];
            for (int dy = -9; dy <= 9; dy++) for (int dx = -9; dx <= 9; dx++) if (Math.Max(Math.Abs(dx), Math.Abs(dy)) > 6) { bg += img[s.Item1 + dx, s.Item2 + dy]; n++; }
            return sum - 81 * bg / n;
        }
        var edge = stars.First(s => s.Item1 > 420 && s.Item2 > 300);
        var centre = stars.OrderBy(s => (s.Item1 - 200) * (s.Item1 - 200) + (s.Item2 - 160) * (s.Item2 - 160)).First();
        Assert.True(Flux(shaded, edge) / Flux(shaded, centre) < 0.8f);
        Assert.Equal(1, Flux(outImg, edge) / Flux(outImg, centre), 0.12);
        for (int y = 0; y < H; y += 7) Assert.Equal(0, outImg[5, y]);
        Assert.True(outImg[25, 100] > 0);
    }

    [Fact]
    public void GesturesAndRegionsFollowTheOrientation()
    {
        int w = 90, h = 60;
        var g = GradientGesture.Ellipse(20.5, 10.5, 30, 12, 0.4);
        var l = GradientGesture.Line(5, 7, 60, 40);
        var r = new GradientRegion(33, 21, 9, 4);
        (float, float)[] probes = [(70.5f, 50.5f), (12.5f, 33.5f), (44.5f, 5.5f)];
        var img = new PlanarImage(w, h);
        foreach (var (px, py) in probes) img[(int)px, (int)py] = 1;
        for (int rot = 0; rot < 4; rot++) foreach (var flip in new[] { false, true })
        {
            var o = new Orientation(rot, flip);
            var oriented = o.Apply(img);
            var og = g.Applying(o, w, h); var ol = l.Applying(o, w, h); var orr = r.Applying(o, w, h);
            foreach (var (px, py) in probes)
            {
                (int, int)? found = null;
                for (int y = 0; y < oriented.Height; y++) for (int x = 0; x < oriented.Width; x++)
                {
                    if (oriented[x, y] != 1) continue;
                    float qx = x + 0.5f, qy = y + 0.5f;
                    var (u0, _) = GradientRemoval.Coordinate(g, px, py); var (u1, _) = GradientRemoval.Coordinate(og, qx, qy);
                    var (v0, _) = GradientRemoval.Coordinate(l, px, py); var (v1, _) = GradientRemoval.Coordinate(ol, qx, qy);
                    if (Math.Abs(u0 - u1) < 1e-3 && Math.Abs(v0 - v1) < 1e-3) found = (x, y);
                }
                Assert.NotNull(found);
                Assert.Equal(r.Contains(px, py), orr.Contains(found.Value.Item1 + 0.5, found.Value.Item2 + 0.5));
            }
        }
        var s = g.Applying(OrientationOp.FlipHorizontal, w, h).Applying(OrientationOp.RotateClockwise, w, h);
        var once = g.Applying(new Orientation(1, true), w, h);
        Assert.Equal(once.X, s.X, 1e-9); Assert.Equal(once.Y, s.Y, 1e-9); Assert.Equal(once.Angle, s.Angle, 1e-9);
        var e = l.ConvertedTo(GradientShape.Ellipse);
        Assert.Equal(GradientShape.Ellipse, e.Shape); Assert.Equal(Math.Sqrt(55 * 55 + 33 * 33), e.A, 1e-9); Assert.Equal(Math.Atan2(33, 55), e.Angle, 1e-9);
        var back = e.ConvertedTo(GradientShape.Line);
        Assert.Equal(60, back.X2, 1e-9); Assert.Equal(40, back.Y2, 1e-9);
    }

    [Fact]
    public void SettingsRoundTripAndOldDocuments()
    {
        var g = new GradientSettings { Shape = GradientShape.Line };
        g.Specs[ChannelKind.Ha] = new GradientSpec(GradientGesture.Ellipse(-200, 3000, 1500, 1200, 0.3)) { Mode = GradientMode.Divide, Residual = GradientResidual.Quadratic, Sectors = 8, Smoothness = 2, Degree = 4, Refine = false };
        g.Specs[ChannelKind.OIII] = new GradientSpec(GradientGesture.Line(1, 2, 300, 400));
        g.Protect = [new GradientRegion(100, 120, 50, 40)];
        var doc = new ProjectDocument { Gradient = g };
        var back = ProjectDocument.Parse(doc.Json());
        Assert.Equal(g, back.Gradient);
        var old = ProjectDocument.Parse("{\"version\":1,\"channels\":[]}");
        Assert.True(old.Gradient.IsEmpty); Assert.Equal(GradientMode.Subtract, old.Gradient.Mode);
        var bare = GradientSpec.Parse("{\"gesture\":{\"shape\":\"line\",\"x2\":10}}");
        Assert.Equal(GradientShape.Line, bare.Gesture.Shape); Assert.Equal(10, bare.Gesture.X2); Assert.Equal(GradientResidual.Plane, bare.Residual); Assert.True(bare.Refine);
        // The same JSON layout as the macOS build (lower-case first letter identifiers).
        Assert.Contains("\"shape\": \"line\"", doc.Json());
        Assert.Contains("\"mode\": \"divide\"", doc.Json());
    }

    [Fact]
    public void TooSmallOrDegenerateInputsGiveNoFit()
    {
        var tiny = new PlanarImage(12, 12, 1);
        Assert.Null(GradientRemoval.Correct(tiny, new GradientSpec(GradientGesture.Polynomial())));
        var (flat, _, _) = Scene(noNebula: true);
        Assert.Null(GradientRemoval.Correct(flat, new GradientSpec(GradientGesture.Line(10, 10, 10, 10))));
        Assert.Same(flat, GradientRemoval.Correct(flat, ChannelKind.Ha, new GradientSettings()));
    }
}
