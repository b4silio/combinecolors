using AstroCore;

namespace AstroCore.Tests;

public class PlanarImageTests
{
    [Fact]
    public void DownsampleAveragesBlocks()
    {
        var img = new PlanarImage(4, 2, [1, 3, 5, 7, 2, 4, 6, 8]);
        var d = img.Downsampled2x();
        Assert.Equal(2, d.Width); Assert.Equal(1, d.Height);
        A.Close(2.5f, d[0, 0], 1e-6f);
        A.Close(6.5f, d[1, 0], 1e-6f);
    }

    [Fact]
    public void DownsampleOddSizesReplicateEdges()
    {
        var img = new PlanarImage(3, 3, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
        var d = img.Downsampled2x();
        Assert.Equal(2, d.Width); Assert.Equal(2, d.Height);
        A.Close(3, d[0, 0], 1e-6f);          // (1+2+4+5)/4
        A.Close(4.5f, d[1, 0], 1e-6f);       // (3+3+6+6)/4
        A.Close(9, d[1, 1], 1e-6f);          // (9+9+9+9)/4
    }

    [Fact]
    public void CropAndFlip()
    {
        var img = new PlanarImage(4, 3, Enumerable.Range(0, 12).Select(i => (float)i).ToArray());
        var c = img.Cropped(1, 1, 2, 2);
        Assert.Equal(new float[] { 5, 6, 9, 10 }, c.ToArray());
        img.FlipVertically();
        Assert.Equal(new float[] { 8, 9, 10, 11 }, img.ToArray().Take(4).ToArray());
        var clamped = img.Cropped(-5, -5, 100, 100);
        Assert.Equal(4, clamped.Width); Assert.Equal(3, clamped.Height);
    }

    [Fact]
    public void PyramidLevels()
    {
        var img = new PlanarImage(1000, 600, 0.5f);
        var p = new Pyramid(img, 64);
        Assert.Equal(new[] { 1000, 500, 250, 125, 63 }, p.Levels.Select(l => l.Width).ToArray());
        Assert.Equal(1, p.LevelIndex(200_000));
        Assert.Equal(2, p.LevelIndex(100_000));
        Assert.Equal(0, p.LevelIndexForScale(1));
        Assert.Equal(1, p.LevelIndexForScale(0.5));
        Assert.Equal(1, p.LevelIndexForScale(0.3));
        Assert.Equal(2, p.LevelIndexForScale(0.25));
        A.Close(0.5f, p.Level(3).Mean(), 1e-6f);
    }

    [Fact]
    public void Resize()
    {
        var img = new PlanarImage(64, 32, 0.25f);
        var r = img.Resized(16, 8);
        Assert.Equal(16, r.Width);
        A.Close(0.25f, r.Mean(), 1e-5f);
        // Up-scaling a smooth ramp stays a ramp.
        var ramp = new PlanarImage(8, 2);
        for (int x = 0; x < 8; x++) { ramp[x, 0] = x; ramp[x, 1] = x; }
        var up = ramp.Resized(16, 4);
        Assert.Equal(16, up.Width);
        for (int x = 1; x < 16; x++) Assert.True(up[x, 1] >= up[x - 1, 1]);
    }

    [Fact]
    public void Rotated90CW()
    {
        // 3×2 image:  0 1 2       rotated CW (2×3):  3 0
        //             3 4 5                          4 1
        //                                            5 2
        var img = new PlanarImage(3, 2, [0, 1, 2, 3, 4, 5]);
        var r = img.Rotated90CW();
        Assert.Equal(2, r.Width); Assert.Equal(3, r.Height);
        Assert.Equal(new float[] { 3, 0, 4, 1, 5, 2 }, r.ToArray());
        // Four turns give the original back.
        var back = r.Rotated90CW().Rotated90CW().Rotated90CW();
        Assert.Equal(img.ToArray(), back.ToArray());
        // A rectangle follows the pixels it covers.
        var rect = new PixelRect(1, 0, 2, 1);   // pixels 1,2
        var rr = rect.Rotated90CW(3, 2);
        Assert.Equal(new PixelRect(1, 1, 1, 2), rr);
        Assert.Equal(new float[] { 1, 2 }, new[] { r[rr.X, rr.Y], r[rr.X, rr.Y + 1] });
        // Larger image (exercises the blocked transpose).
        var big = new PlanarImage(300, 130, Enumerable.Range(0, 300 * 130).Select(i => (float)i).ToArray());
        var rb = big.Rotated90CW();
        for (int y = 0; y < 130; y += 7) for (int x = 0; x < 300; x += 11) Assert.Equal(big[x, y], rb[129 - y, x]);
    }

    [Fact]
    public void FlipsAndOrientationComposition()
    {
        var img = new PlanarImage(3, 2, [0, 1, 2, 3, 4, 5]);
        Assert.Equal(new float[] { 2, 1, 0, 5, 4, 3 }, img.FlippedHorizontally().ToArray());
        Assert.Equal(new float[] { 3, 4, 5, 0, 1, 2 }, img.FlippedVertically().ToArray());
        var rect = new PixelRect(0, 0, 2, 1);   // pixels 0,1
        Assert.Equal(new PixelRect(1, 0, 2, 1), rect.FlippedHorizontally(3));
        Assert.Equal(new PixelRect(0, 1, 2, 1), rect.FlippedVertically(2));
        // Any sequence of operations, replayed step by step on the image and the rectangle, matches the
        // composed orientation applied once to the originals (all 8 group elements get visited).
        var rng = new Random(12345);
        var baseImg = new PlanarImage(5, 3, Enumerable.Range(0, 15).Select(i => (float)i).ToArray());
        var baseRect = new PixelRect(1, 0, 2, 2);
        var seen = new HashSet<Orientation>();
        for (int trial = 0; trial < 40; trial++)
        {
            var o = Orientation.Identity;
            var cur = baseImg; var r = baseRect;
            int steps = rng.Next(0, 7);
            for (int s = 0; s < steps; s++)
            {
                var op = OrientationOps.All[rng.Next(3)];
                o = o.Applying(op);
                r = r.Applying(op, cur.Width, cur.Height);
                cur = cur.Applying(op);
                seen.Add(o);
            }
            Assert.Equal(cur.ToArray(), o.Apply(baseImg).ToArray());
            Assert.Equal(r, o.Apply(baseRect, baseImg.Width, baseImg.Height));
            Assert.Equal(cur.Width != baseImg.Width, o.SwapsAxes);
        }
        Assert.Equal(8, seen.Count);
        Assert.Equal(Orientation.Identity, Orientation.Identity.Applying(OrientationOp.FlipHorizontal).Applying(OrientationOp.FlipHorizontal));
        Assert.Equal(2, Orientation.Identity.Applying(OrientationOp.FlipVertical).Applying(OrientationOp.FlipHorizontal).Rotation);
    }
}
