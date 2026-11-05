namespace AstroCore;

/// <summary>
/// Rotation by an arbitrary angle about the centre of the image, on the same canvas (the corners
/// that leave it are lost, the ones that come in are zero, like registration borders). Positive
/// angles turn the picture clockwise on screen (image y points down), like RotateClockwise.
/// <para>
/// Coordinates use the pixel-edge convention (pixel (i, j) covers [i, i+1) × [j, j+1), centre at
/// (i + ½, j + ½)), so a point and the pixel under it move together.
/// </para>
/// </summary>
public static class FineRotation
{
    /// <summary>Where the point (x, y) of a width × height image lands after a rotation by degrees.</summary>
    public static (double X, double Y) Rotate(double x, double y, double degrees, int width, int height)
    {
        if (degrees == 0) return (x, y);
        double t = degrees * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        double cx = width / 2.0, cy = height / 2.0;
        double dx = x - cx, dy = y - cy;
        return (cx + c * dx - s * dy, cy + s * dx + c * dy);
    }

    /// <summary>Lanczos-3 kernel sampled finely (the weights are looked up, not computed: 36 taps per pixel).</summary>
    internal const int LutSteps = 1024;
    internal static readonly float[] Lanczos = Enumerable.Range(0, 3 * LutSteps + 1).Select(i =>
    {
        double x = (double)i / LutSteps;
        if (x == 0) return 1f;
        if (x >= 3) return 0f;
        double px = Math.PI * x;
        return (float)(3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px));
    }).ToArray();

    /// <summary>
    /// The image turned by <paramref name="degrees"/> (clockwise on screen) about its centre on the same
    /// canvas, resampled with Lanczos-3. Samples are clamped to the range of the 2 × 2 nearest pixels,
    /// so stars do not get dark rings and zero borders do not ring into the picture; pixels whose
    /// source lies outside the image are zero.
    /// </summary>
    public static PlanarImage RotatedByDegrees(this PlanarImage img, double degrees)
    {
        if (degrees % 360 == 0) return img.Copy();
        int w = img.Width, h = img.Height;
        var output = new PlanarImage(w, h);
        double t = degrees * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        double cx = w / 2.0, cy = h / 2.0;
        var src = img.Data; var dst = output.Data;
        var k = Lanczos;
        const float steps = LutSteps;
        Par.Rows(h, (long)w * 40, (y0, y1) =>
        {
            Span<float> wx = stackalloc float[6], wy = stackalloc float[6];
            for (int j = y0; j < y1; j++)
            {
                double qy = j + 0.5 - cy;
                for (int i = 0; i < w; i++)
                {
                    double qx = i + 0.5 - cx;
                    // Inverse rotation: where this output pixel comes from (pixel-edge coordinates).
                    double sx = cx + c * qx + s * qy, sy = cy - s * qx + c * qy;
                    if (!(sx >= 0 && sy >= 0 && sx < w && sy < h)) { dst[j * w + i] = 0; continue; }
                    // Pixel-centre coordinates of the sample.
                    double ux = sx - 0.5, uy = sy - 0.5;
                    int x0 = (int)Math.Floor(ux), yy0 = (int)Math.Floor(uy);
                    float fx = (float)(ux - x0), fy = (float)(uy - yy0);
                    float sumX = 0, sumY = 0;
                    for (int n = 0; n < 6; n++)
                    {
                        float dxn = MathF.Abs(n - 2 - fx), dyn = MathF.Abs(n - 2 - fy);
                        wx[n] = k[Math.Min((int)(dxn * steps), 3 * LutSteps)];
                        wy[n] = k[Math.Min((int)(dyn * steps), 3 * LutSteps)];
                        sumX += wx[n]; sumY += wy[n];
                    }
                    float acc = 0, lo = float.MaxValue, hi = float.MinValue;
                    for (int m = 0; m < 6; m++)
                    {
                        int yy = Math.Min(Math.Max(yy0 - 2 + m, 0), h - 1);
                        int row = yy * w;
                        float r = 0;
                        for (int n = 0; n < 6; n++)
                        {
                            int xx = Math.Min(Math.Max(x0 - 2 + n, 0), w - 1);
                            float v = src[row + xx];
                            r += wx[n] * v;
                            if ((m == 2 || m == 3) && (n == 2 || n == 3)) { lo = MathF.Min(lo, v); hi = MathF.Max(hi, v); }
                        }
                        acc += wy[m] * r;
                    }
                    float val = acc / (sumX * sumY);
                    dst[j * w + i] = MathF.Min(MathF.Max(val, lo), hi);
                }
            }
        });
        return output;
    }
}

/// <summary>Geometry of the crop editor's turn (the picture turns under an upright crop).</summary>
public static class CropTurn
{
    /// <summary>An angle as whole quarter turns (0…3, clockwise) plus the rest in −45…45°.</summary>
    public static (int Quarters, double Remainder) Split(double degrees)
    {
        int q = (int)Math.Round(degrees / 90, MidpointRounding.AwayFromZero);
        return (((q % 4) + 4) % 4, degrees - q * 90.0);
    }

    /// <summary>An angle kept in −180…180, to a hundredth of a degree.</summary>
    public static double Normalized(double degrees)
    {
        double a = degrees % 360;
        if (a > 180) a -= 360; else if (a <= -180) a += 360;
        return Math.Round(a * 100, MidpointRounding.AwayFromZero) / 100;
    }

    /// <summary>
    /// Where the canvas of the picture turned by <paramref name="degrees"/> lies while the crop is edited, in
    /// the coordinates of the shown width × height picture: centred on it, sideways after an odd number of
    /// quarter turns. The crop being edited lives in this space (it may reach outside the shown picture).
    /// </summary>
    public static (double X, double Y, double Width, double Height) EditBounds(double degrees, int width, int height)
    {
        int q = Split(degrees).Quarters;
        double w = q % 2 == 0 ? width : height, h = q % 2 == 0 ? height : width;
        return ((width - w) / 2, (height - h) / 2, w, h);
    }

    /// <summary>
    /// <paramref name="rect"/> scaled about its centre (never up) so that it fits inside the width × height
    /// frame turned by <paramref name="angle"/> degrees about the frame's centre, then kept inside
    /// <paramref name="bounds"/> (default: the frame).
    /// </summary>
    public static PixelRect Constrained(PixelRect rect, double angle, int width, int height, (double X, double Y, double Width, double Height)? bounds = null)
    {
        if (angle == 0) return rect;
        double t = angle * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        double cx = width / 2.0, cy = height / 2.0, hw = width / 2.0, hh = height / 2.0;
        double px = rect.X + rect.Width / 2.0, py = rect.Y + rect.Height / 2.0;
        // The centre must be inside the turned frame; otherwise start from the frame's centre.
        (double, double) Local(double x, double y) { double dx = x - cx, dy = y - cy; return (c * dx + s * dy, -s * dx + c * dy); }
        var (lx, ly) = Local(px, py);
        if (Math.Abs(lx) >= hw * 0.98 || Math.Abs(ly) >= hh * 0.98) { px = cx; py = cy; }
        var (ox, oy) = Local(px, py);
        double scale = 1;
        foreach (var (sx, sy) in new[] { (-1.0, -1.0), (1.0, -1.0), (1.0, 1.0), (-1.0, 1.0) })
        {
            double dx = sx * rect.Width / 2.0, dy = sy * rect.Height / 2.0;
            // The corner in the frame's own axes: (ox, oy) + k·(ux, uy), k = scale.
            double ux = c * dx + s * dy, uy = -s * dx + c * dy;
            foreach (var (o, u, h) in new[] { (ox, ux, hw), (oy, uy, hh) })
            {
                if (Math.Abs(u) <= 1e-9) continue;
                double limit = ((u > 0 ? h : -h) - o) / u;
                scale = Math.Min(scale, Math.Max(limit, 0));
            }
        }
        int w = Math.Max(1, (int)Math.Floor(rect.Width * scale)), hgt = Math.Max(1, (int)Math.Floor(rect.Height * scale));
        int x = (int)Math.Round(px - w / 2.0, MidpointRounding.AwayFromZero), y = (int)Math.Round(py - hgt / 2.0, MidpointRounding.AwayFromZero);
        var b = bounds ?? (0, 0, width, height);
        int bx0 = (int)Math.Round(b.X, MidpointRounding.AwayFromZero), by0 = (int)Math.Round(b.Y, MidpointRounding.AwayFromZero);
        int bx1 = (int)Math.Round(b.X + b.Width, MidpointRounding.AwayFromZero), by1 = (int)Math.Round(b.Y + b.Height, MidpointRounding.AwayFromZero);
        return new PixelRect(Math.Min(Math.Max(x, bx0), Math.Max(bx0, bx1 - w)), Math.Min(Math.Max(y, by0), Math.Max(by0, by1 - hgt)),
                             Math.Min(w, bx1 - bx0), Math.Min(hgt, by1 - by0));
    }
}
