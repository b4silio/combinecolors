using AstroCore;

namespace AstroCore.Tests;

public class DustRemovalTests
{
    /// <summary>Scene with a gentle gradient, nebula blobs and stars (low noise so residuals are measurable).</summary>
    static PlanarImage Scene(int width = 420, int height = 320, float noise = 0.0002f)
        => Synth.Channel(width, height, 0.01f, 0.0015f, 0.3f, noise, 11, 0.002f);

    static float Hypot(float a, float b) => MathF.Sqrt(a * a + b * b);

    /// <summary>Multiplies a scene by a soft disc shadow and a donut.</summary>
    static PlanarImage Shade(PlanarImage truth, (float x, float y, float r, float depth) disc, (float x, float y, float r, float w, float depth)? donut)
    {
        var img = truth.Copy();
        for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float t = 1;
            float d = Hypot(px - disc.x, py - disc.y);
            t *= 1 - disc.depth * MathF.Exp(-MathF.Pow(d / disc.r, 4));
            if (donut is var dn && dn != null)
            {
                float r = Hypot(px - dn.Value.x, py - dn.Value.y);
                t *= 1 - dn.Value.depth * MathF.Exp(-MathF.Pow((r - dn.Value.r) / dn.Value.w, 2));
            }
            img[x, y] = truth[x, y] * t;
        }
        return img;
    }

    /// <summary>Relative error of a against truth averaged over 8×8 blocks inside a disc, stars excluded.</summary>
    static (float median, float max) BlockError(PlanarImage a, PlanarImage truth, float cx, float cy, float radius)
    {
        var errs = new List<float>();
        for (int by = 0; by + 8 <= a.Height; by += 8)
            for (int bx = 0; bx + 8 <= a.Width; bx += 8)
            {
                float sa = 0, st = 0;
                bool inside = true, star = false;
                for (int y = by; y < by + 8; y++) for (int x = bx; x < bx + 8; x++)
                {
                    if (Hypot(x + 0.5f - cx, y + 0.5f - cy) > radius) inside = false;
                    if (truth[x, y] > 0.03f) star = true;
                    sa += a[x, y]; st += truth[x, y];
                }
                if (inside && !star) errs.Add(MathF.Abs(sa / st - 1));
            }
        errs.Sort();
        return (errs.Count == 0 ? 0 : errs[errs.Count / 2], errs.Count == 0 ? 0 : errs[^1]);
    }

    [Fact]
    public void RadialModelRestoresDiscAndDonut()
    {
        var truth = Scene();
        var shaded = Shade(truth, (150, 120, 40, 0.15f), (310, 210, 28, 7, 0.25f));
        var before1 = BlockError(shaded, truth, 150, 120, 36);
        var before2 = BlockError(shaded, truth, 310, 210, 40);
        Assert.True(before1.median > 0.1f); Assert.True(before2.max > 0.1f);
        var spots = new[] { new DustSpot(150, 120, 55, DustModel.Radial, 3, 0.15), new DustSpot(310, 210, 48, DustModel.Radial, 3, 0.15) };
        var healed = DustRemoval.Heal(shaded, spots);
        Assert.NotSame(shaded, healed);
        var after1 = BlockError(healed, truth, 150, 120, 36);
        var after2 = BlockError(healed, truth, 310, 210, 40);
        Assert.True(after1.median < 0.012f, $"disc median error {after1.median}"); Assert.True(after1.max < 0.03f, $"disc max error {after1.max}");
        Assert.True(after2.median < 0.015f, $"donut median error {after2.median}"); Assert.True(after2.max < 0.04f, $"donut max error {after2.max}");
        // Nothing changes beyond the spots.
        for (int y = 0; y < truth.Height; y++) for (int x = 0; x < truth.Width; x++)
        {
            bool out1 = Hypot(x + 0.5f - 150, y + 0.5f - 120) > 55, out2 = Hypot(x + 0.5f - 310, y + 0.5f - 210) > 48;
            if (out1 && out2) Assert.Equal(shaded[x, y], healed[x, y]);
        }
    }

    [Fact]
    public void StarsUnderTheSpotKeepTheirBrightness()
    {
        var truth = Scene();
        // Brightest star inside the disc.
        (int x, int y, float v) best = (0, 0, 0);
        for (int y = 90; y < 150; y++) for (int x = 120; x < 180; x++) if (truth[x, y] > best.v && Hypot(x - 150, y - 120) < 28) best = (x, y, truth[x, y]);
        if (best.v <= 0.05f) return;   // no star under the test spot
        var shaded = Shade(truth, (150, 120, 40, 0.2f), null);
        var healed = DustRemoval.Heal(shaded, [new DustSpot(150, 120, 55)]);
        A.Close(1, healed[best.x, best.y] / best.v, 0.05f, "star peak restored by the same gain as the sky");
    }

    [Fact]
    public void SurfaceModelFollowsAnEllipticalMote()
    {
        var truth = Scene();
        var img = truth.Copy();
        for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++)
        {
            float dx = (x + 0.5f - 320) / 36, dy = (y + 0.5f - 240) / 18;
            img[x, y] = truth[x, y] * (1 - 0.2f * MathF.Exp(-MathF.Pow(dx * dx + dy * dy, 2)));
        }
        var radial = DustRemoval.Heal(img, [new DustSpot(320, 240, 62, DustModel.Radial, 3, 0.15)]);
        // A sharp-edged mote wants little smoothing (the Gaussian blurs the edge of the shadow estimate).
        var surface = DustRemoval.Heal(img, [new DustSpot(320, 240, 62, DustModel.Surface, 2.5, 0.15)]);
        var er = BlockError(radial, truth, 320, 240, 40); var es = BlockError(surface, truth, 320, 240, 40);
        Assert.True(es.median < 0.015f, $"surface median {es.median}"); Assert.True(es.max < 0.04f, $"surface max {es.max}");
        Assert.True(es.max < er.max, "the surface model must beat the radial one on an elongated mote");
    }

    [Fact]
    public void FitFindsCentreAndExtent()
    {
        var truth = Scene();
        var shaded = Shade(truth, (150, 120, 40, 0.12f), (310, 210, 28, 7, 0.25f));
        // Click off-centre on the disc with a small starting radius.
        var s1 = DustRemoval.Fit(shaded, 158, 113, 15);
        Assert.NotNull(s1);
        A.Close(150, s1!.X, 3); A.Close(120, s1.Y, 3);
        Assert.True(s1.Radius > 42); Assert.True(s1.Radius < 62);
        // Donut: click near the centre where the shadow is weak.
        var s2 = DustRemoval.Fit(shaded, 306, 214, 20);
        Assert.NotNull(s2);
        A.Close(310, s2!.X, 5); A.Close(210, s2.Y, 5);
        Assert.True(s2.Radius > 38); Assert.True(s2.Radius < 56);
        // Nothing on plain sky (without nebulosity: a nebular bump next to the click is a shallow
        // "shadow" to the fit, which only matters for the radius it proposes).
        var plain = Synth.Channel(300, 200, 0.01f, 0, 0.3f, 0.0002f, 5, 0.002f);
        Assert.Null(DustRemoval.Fit(plain, 140, 100, 20));
    }

    [Fact]
    public void SpotsAtTheBorderAndOutsideAreHandled()
    {
        var truth = Scene();
        var shaded = Shade(truth, (12, 160, 30, 0.15f), null);
        var healed = DustRemoval.Heal(shaded, [new DustSpot(12, 160, 42), new DustSpot(-500, 10, 20)]);
        Assert.True(BlockError(healed, truth, 12, 160, 26).median < 0.03f);
        var z = new PlanarImage(64, 64);
        Assert.False(DustRemoval.HealInPlace(z, new DustSpot(32, 32, 10)), "all-zero window has no background");
    }

    [Fact]
    public void OrientationMapsSpotsLikePixels()
    {
        int w = 9, h = 5;
        foreach (var flip in new[] { false, true }) for (int rot = 0; rot < 4; rot++)
        {
            var o = new Orientation(rot, flip);
            var img = new PlanarImage(w, h);
            img[6, 1] = 1;
            var out_ = o.Apply(img);
            var s = new DustSpot(6.5, 1.5, 2).Applying(o, w, h);
            Assert.Equal(1, out_[(int)s.X, (int)s.Y]);
            Assert.Equal(2, s.Radius);
        }
        // Step-wise ops agree with the composed orientation.
        var sp = new DustSpot(6.5, 1.5, 2);
        int ww = w, hh = h;
        sp = sp.Applying(OrientationOp.FlipHorizontal, ww, hh);
        sp = sp.Applying(OrientationOp.RotateClockwise, ww, hh); (ww, hh) = (hh, ww);
        sp = sp.Applying(OrientationOp.FlipVertical, ww, hh);
        var oo = Orientation.Identity.Applying(OrientationOp.FlipHorizontal).Applying(OrientationOp.RotateClockwise).Applying(OrientationOp.FlipVertical);
        var c = new DustSpot(6.5, 1.5, 2, id: sp.Id).Applying(oo, w, h);
        Assert.Equal(sp, c);
    }

    [Fact]
    public void DocumentRoundTripAndDefaults()
    {
        var doc = new ProjectDocument();
        doc.Dust.Spots[ChannelKind.OIII] = [new DustSpot(10.5, 20.25, 33, DustModel.Radial, 6, 0.1)];
        doc.Dust.Model = DustModel.Radial;
        doc.Dust.Smoothness = 6;
        var back = ProjectDocument.Parse(doc.Json());
        Assert.Equal(doc.Dust, back.Dust);
        var old = ProjectDocument.Parse("{\"version\":1}");
        Assert.True(old.Dust.IsEmpty); Assert.Equal(DustModel.Surface, old.Dust.Model);
        var bare = DustSpot.Parse("{\"x\":1,\"y\":2,\"radius\":3}");
        Assert.Equal(DustModel.Surface, bare.Model); Assert.Equal(3, bare.Smoothness); Assert.Equal(0.2, bare.Feather);
    }

    /// <summary>Two touching shadows: each background fit must ignore the other spot or both end up biased.</summary>
    [Fact]
    public void NeighbouringSpotsAreExcludedFromEachOthersBackground()
    {
        var truth = Synth.Channel(420, 320, 0.01f, 0, 0.3f, 0.0002f, 11, 0.002f);
        var img = truth.Copy();
        for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++)
        {
            float px = x + 0.5f, py = y + 0.5f;
            float t1 = 1 - 0.1f * MathF.Exp(-MathF.Pow(Hypot(px - 150, py - 160) / 40, 4)), t2 = 1 - 0.1f * MathF.Exp(-MathF.Pow(Hypot(px - 250, py - 160) / 40, 4));
            img[x, y] = truth[x, y] * t1 * t2;
        }
        var spots = new[] { new DustSpot(150, 160, 50), new DustSpot(250, 160, 50) };
        var healed = DustRemoval.Heal(img, spots);
        var errs = new List<float>();
        for (int y = 130; y < 190; y++) for (int x = 120; x < 280; x++)
            if (truth[x, y] < 0.03f && (Hypot(x + 0.5f - 150, y + 0.5f - 160) < 35 || Hypot(x + 0.5f - 250, y + 0.5f - 160) < 35))
                errs.Add(MathF.Abs(healed[x, y] / truth[x, y] - 1));
        errs.Sort();
        Assert.True(errs[errs.Count / 2] < 0.025f, $"median error under the pair {errs[errs.Count / 2]}");
        // Healing one alone, with the other spot not excluded, is clearly worse.
        var alone = img.Copy();
        DustRemoval.HealInPlace(alone, spots[0]);
        var e2 = new List<float>(); var e1 = new List<float>();
        for (int y = 130; y < 190; y++) for (int x = 120; x < 180; x++)
            if (truth[x, y] < 0.03f && Hypot(x + 0.5f - 150, y + 0.5f - 160) < 35)
            {
                e2.Add(MathF.Abs(alone[x, y] / truth[x, y] - 1));
                e1.Add(MathF.Abs(healed[x, y] / truth[x, y] - 1));
            }
        e2.Sort(); e1.Sort();
        Assert.True(e1[e1.Count / 2] < e2[e2.Count / 2]);
    }

    /// <summary>
    /// With a small smoothing scale the noise must pass through the correction unchanged: the
    /// transmission map keeps the shadow's structure but not the noise, so the grain under the spot
    /// matches the sky around it.
    /// </summary>
    [Fact]
    public void LocalFlatKeepsTheNoiseGrain()
    {
        float noise = 0.0005f;
        var truth = Synth.Channel(420, 320, 0.01f, 0, 0.3f, noise, 21, 0.001f);
        var img = truth.Copy();
        for (int y = 0; y < img.Height; y++) for (int x = 0; x < img.Width; x++)
        {
            float d = Hypot(x + 0.5f - 200, y + 0.5f - 160);
            img[x, y] = truth[x, y] * (1 - 0.05f * MathF.Exp(-MathF.Pow(d / 50, 4)));
        }
        var healed = DustRemoval.Heal(img, [new DustSpot(200, 160, 70, DustModel.Surface, 1, 0.2)]);
        var res = new List<float>(); var tru = new List<float>();
        for (int y = 130; y < 190; y++) for (int x = 170; x < 230; x++) if (truth[x, y] < 0.03f) { res.Add(healed[x, y] - truth[x, y]); tru.Add(truth[x, y]); }
        static float Std(List<float> v) { float m = v.Average(); return MathF.Sqrt(v.Sum(a => (a - m) * (a - m)) / v.Count); }
        // Plain σ = 1 smoothing would leave ≈ 28 % of the noise std in the residual (its coarse part).
        Assert.True(Std(res) < 0.12f * noise, $"residual against the truth {Std(res)} vs {0.12f * noise}");
        A.Close(0, Std(tru) > 0 ? Std(res) / Std(tru) : 1, 0.12f);
    }
}
