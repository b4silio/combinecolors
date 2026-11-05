using AstroCore;

namespace AstroCore.Tests;

public class FineRotationTests
{
    /// <summary>A smooth picture with a few Gaussian blobs ("stars").</summary>
    static PlanarImage Blobs((double X, double Y)[] points, int width = 200, int height = 150, double sigma = 2.5)
    {
        var img = new PlanarImage(width, height);
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            double v = 0.05 + 0.02 * x / width;
            foreach (var (px, py) in points)
            {
                double dx = x + 0.5 - px, dy = y + 0.5 - py;
                v += Math.Exp(-(dx * dx + dy * dy) / (2 * sigma * sigma));
            }
            img[x, y] = (float)v;
        }
        return img;
    }

    /// <summary>Intensity-weighted centre of the pixels above threshold within r of (x, y).</summary>
    static (double X, double Y) Centroid(PlanarImage img, double x, double y, int r = 8, float threshold = 0.2f)
    {
        double sx = 0, sy = 0, sw = 0;
        for (int j = Math.Max(0, (int)y - r); j < Math.Min(img.Height, (int)y + r); j++)
            for (int i = Math.Max(0, (int)x - r); i < Math.Min(img.Width, (int)x + r); i++)
            {
                double v = Math.Max(img[i, j] - threshold, 0);
                sx += v * (i + 0.5); sy += v * (j + 0.5); sw += v;
            }
        return (sx / sw, sy / sw);
    }

    [Fact]
    public void ZeroAngleIsACopy()
    {
        var img = Blobs([(50, 40)]);
        var r = img.RotatedByDegrees(0);
        Assert.NotSame(img, r);
        Assert.Equal(img.ToArray(), r.ToArray());
    }

    /// <summary>The pixels move where FineRotation.Rotate sends their coordinates (clockwise on screen).</summary>
    [Fact]
    public void StarsLandWhereThePointsGo()
    {
        (double, double)[] stars = [(60, 40), (150, 100), (100, 75)];
        var img = Blobs(stars);
        foreach (double deg in new[] { 7.5, -20, 33 })
        {
            var r = img.RotatedByDegrees(deg);
            foreach (var (x, y) in stars)
            {
                var e = FineRotation.Rotate(x, y, deg, 200, 150);
                var c = Centroid(r, e.X, e.Y);
                Assert.True(Math.Abs(c.X - e.X) < 0.15, $"{deg}° star {x},{y}: x {c.X} vs {e.X}");
                Assert.True(Math.Abs(c.Y - e.Y) < 0.15, $"{deg}° star {x},{y}: y {c.Y} vs {e.Y}");
            }
        }
        // Clockwise on screen (y down): a point right of the centre moves down.
        Assert.True(FineRotation.Rotate(150, 75, 10, 200, 150).Y > 75);
        // Corners that come in from outside are zero.
        Assert.Equal(0f, img.RotatedByDegrees(30)[0, 0]);
    }

    [Fact]
    public void TurnAndBackKeepsTheMiddleAndNoRinging()
    {
        var img = Blobs([(100, 75), (80, 60), (120, 90)], sigma: 1.5);
        var back = img.RotatedByDegrees(12).RotatedByDegrees(-12);
        float worst = 0;
        for (int y = 40; y < 110; y++) for (int x = 50; x < 150; x++) worst = MathF.Max(worst, MathF.Abs(back[x, y] - img[x, y]));
        Assert.True(worst < 0.12f, "two Lanczos passes keep the stars");
        // No value outside the input's range (the clamp stops the Lanczos overshoot).
        var r = img.RotatedByDegrees(17);
        float lo = img.ToArray().Min(), hi = img.ToArray().Max();
        foreach (var v in r.ToArray()) if (v != 0) { Assert.True(v >= lo - 1e-6f); Assert.True(v <= hi + 1e-6f); }
    }

    [Fact]
    public void OrientationCarriesTheAngle()
    {
        var o = new Orientation(1, false, 3.5);
        Assert.Equal(3.5, o.Applying(OrientationOp.RotateClockwise).Angle);
        Assert.Equal(-3.5, o.Applying(OrientationOp.FlipHorizontal).Angle);
        Assert.Equal(-3.5, o.Applying(OrientationOp.FlipVertical).Angle);
        Assert.False(o.IsIdentity);
        Assert.Equal(new Orientation(1), o.WithoutAngle);
        // Old documents (no angle) still decode; the angle round-trips.
        var old = ProjectDocument.Parse("""{"version": 1, "rotation": 2, "flipped": true}""");
        Assert.Equal(new Orientation(2, true), old.Orientation);
        var doc = new ProjectDocument { Orientation = o };
        Assert.Equal(o, ProjectDocument.Parse(doc.Json()).Orientation);
        // A flip after a fine turn is the mirrored turn: the pixels agree with flip-then-turn(−θ).
        var img = Blobs([(60, 40), (140, 110)]);
        var a = img.RotatedByDegrees(9).FlippedHorizontally();
        var b = img.FlippedHorizontally().RotatedByDegrees(-9);
        float worst = 0;
        for (int y = 20; y < 130; y++) for (int x = 30; x < 170; x++) worst = MathF.Max(worst, MathF.Abs(a[x, y] - b[x, y]));
        Assert.True(worst < 1e-3f, $"worst {worst}");
    }

    [Fact]
    public void ThingsWithCoordinatesTurnWithThePicture()
    {
        var spot = new DustSpot(150, 40, 12);
        var s = spot.RotatedByDegrees(20, 200, 150);
        var e = FineRotation.Rotate(150, 40, 20, 200, 150);
        A.Close(e.X, s.X, 1e-9); A.Close(e.Y, s.Y, 1e-9); Assert.Equal(12, s.Radius);
        Assert.Equal(spot.Id, s.Id);
        var g = GradientGesture.Ellipse(100, 75, 50, 30);
        var gr = g.RotatedByDegrees(30, 200, 150);
        A.Close(g.Angle + Math.PI / 6, gr.Angle, 1e-12);
        A.Close(100, gr.X, 1e-9); A.Close(75, gr.Y, 1e-9);
        // A protected region grows to the bounding box of its turned ellipse (never protects less).
        var r = new GradientRegion(100, 75, 40, 10).RotatedByDegrees(90, 200, 150);
        A.Close(10, r.A, 1e-9); A.Close(40, r.B, 1e-9);
        var r45 = new GradientRegion(100, 75, 40, 10).RotatedByDegrees(45, 200, 150);
        Assert.True(r45.A > 10); Assert.True(r45.B > 10);
        // A painted map turns its paint along and back again.
        var m = new CorrectionMap(200, 150);
        m.Dab(100, 75, 20, 1, 0.5f);
        var orient = new Orientation(1, true, 8);
        var back = m.Oriented(orient).Unoriented(orient);
        Assert.Equal(200, back.ImageWidth); Assert.Equal(150, back.ImageHeight);
        A.Close(m.Plane[m.Plane.Width / 2, m.Plane.Height / 2], back.Plane[back.Plane.Width / 2, back.Plane.Height / 2], 0.05f);
    }
}

public class CropTurnTests
{
    [Fact]
    public void ConstraintFitsInsideTheTurnedFrame()
    {
        foreach (double angle in new[] { -30.0, -5, 4, 12, 45 })
        {
            var r = CropTurn.Constrained(new PixelRect(10, 10, 180, 130), angle, 200, 150);
            double t = angle * Math.PI / 180;
            foreach (var (x, y) in new[] { (r.X, r.Y), (r.X + r.Width, r.Y), (r.X + r.Width, r.Y + r.Height), (r.X, r.Y + r.Height) })
            {
                double dx = x - 100.0, dy = y - 75.0;
                Assert.True(Math.Abs(Math.Cos(t) * dx + Math.Sin(t) * dy) <= 100.5, $"{angle}°");
                Assert.True(Math.Abs(-Math.Sin(t) * dx + Math.Cos(t) * dy) <= 75.5, $"{angle}°");
            }
        }
        Assert.Equal(new PixelRect(1, 2, 30, 40), CropTurn.Constrained(new PixelRect(1, 2, 30, 40), 0, 200, 150));
    }

    [Fact]
    public void SplitAndBounds()
    {
        Assert.Equal((1, 5.0), CropTurn.Split(95));
        Assert.Equal((2, 0.0), CropTurn.Split(180));
        Assert.Equal((3, 0.0), CropTurn.Split(-90));
        Assert.Equal((0, -44.0), CropTurn.Split(-44));
        Assert.Equal(-170, CropTurn.Normalized(190));
        Assert.Equal(180, CropTurn.Normalized(-180));
        Assert.Equal((25.0, -25.0, 150.0, 200.0), CropTurn.EditBounds(92, 200, 150));
        Assert.Equal((0.0, 0.0, 200.0, 150.0), CropTurn.EditBounds(170, 200, 150));
    }
}
