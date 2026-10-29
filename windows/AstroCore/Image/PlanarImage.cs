namespace AstroCore;

/// <summary>A single-plane 32-bit float image, row-major, origin top-left.</summary>
public sealed class PlanarImage
{
    public int Width { get; }
    public int Height { get; }
    /// <summary>The pixels (exactly Width × Height floats).</summary>
    public float[] Data { get; }

    public int Count => Width * Height;
    public Span<float> Span => Data.AsSpan();

    public PlanarImage(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("PlanarImage requires positive dimensions");
        Width = width; Height = height;
        Data = new float[width * height];
    }

    public PlanarImage(int width, int height, float fill) : this(width, height)
    {
        if (fill != 0) Array.Fill(Data, fill);
    }

    public PlanarImage(int width, int height, float[] pixels)
    {
        if (pixels.Length != width * height) throw new ArgumentException("pixel count does not match the size");
        if (width <= 0 || height <= 0) throw new ArgumentException("PlanarImage requires positive dimensions");
        Width = width; Height = height;
        Data = (float[])pixels.Clone();
    }

    PlanarImage(int width, int height, float[] data, bool takeOwnership)
    {
        Width = width; Height = height; Data = data;
    }

    /// <summary>Wraps an existing array without copying (the array must not be shared).</summary>
    public static PlanarImage Wrap(int width, int height, float[] data)
    {
        if (data.Length != width * height) throw new ArgumentException("pixel count does not match the size");
        return new PlanarImage(width, height, data, true);
    }

    /// <summary>Allocates without zero-filling (for buffers that are fully overwritten).</summary>
    public static PlanarImage Uninitialized(int width, int height)
        => new(width, height, GC.AllocateUninitializedArray<float>(width * height), true);

    public float this[int x, int y]
    {
        get => Data[y * Width + x];
        set => Data[y * Width + x] = value;
    }

    public PlanarImage Copy()
    {
        var out_ = Uninitialized(Width, Height);
        Array.Copy(Data, out_.Data, Count);
        return out_;
    }

    public float[] ToArray() => (float[])Data.Clone();

    /// <summary>Returns a sub-image. The rectangle is clamped to the image bounds.</summary>
    public PlanarImage Cropped(int x, int y, int w, int h)
    {
        int x0 = Math.Max(0, Math.Min(x, Width - 1)), y0 = Math.Max(0, Math.Min(y, Height - 1));
        int x1 = Math.Max(x0 + 1, Math.Min(x + w, Width)), y1 = Math.Max(y0 + 1, Math.Min(y + h, Height));
        int cw = x1 - x0, ch = y1 - y0;
        var out_ = Uninitialized(cw, ch);
        for (int row = 0; row < ch; row++)
            Array.Copy(Data, (y0 + row) * Width + x0, out_.Data, row * cw, cw);
        return out_;
    }

    public PlanarImage Cropped(PixelRect r) => Cropped(r.X, r.Y, r.Width, r.Height);

    /// <summary>Rotated by 90° clockwise: pixel (x, y) of the source lands at (height − 1 − y, x).</summary>
    public PlanarImage Rotated90CW()
    {
        int w = Width, h = Height;
        var out_ = Uninitialized(h, w);
        var s = Data; var d = out_.Data;
        const int B = 64;
        Par.Rows(h, w, (ya, yb) =>
        {
            for (int by = ya; by < yb; by += B)
            {
                int ye = Math.Min(yb, by + B);
                for (int bx = 0; bx < w; bx += B)
                {
                    int xe = Math.Min(w, bx + B);
                    for (int y = by; y < ye; y++)
                    {
                        int nx = h - 1 - y;
                        int row = y * w;
                        for (int x = bx; x < xe; x++) d[x * h + nx] = s[row + x];
                    }
                }
            }
        });
        return out_;
    }

    /// <summary>Rotated by <paramref name="times"/> quarter turns clockwise (0 returns this).</summary>
    public PlanarImage Rotated90CW(int times)
    {
        var img = this;
        int t = ((times % 4) + 4) % 4;
        for (int i = 0; i < t; i++) img = img.Rotated90CW();
        return img;
    }

    /// <summary>Mirrored left-right.</summary>
    public PlanarImage FlippedHorizontally()
    {
        var out_ = Uninitialized(Width, Height);
        var s = Data; var d = out_.Data; int w = Width;
        Par.Rows(Height, w, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++) d[row + x] = s[row + w - 1 - x];
            }
        });
        return out_;
    }

    /// <summary>Mirrored top-bottom (a copy; see <see cref="FlipVertically"/> for in place).</summary>
    public PlanarImage FlippedVertically()
    {
        var out_ = Copy();
        out_.FlipVertically();
        return out_;
    }

    /// <summary>Vertical flip in place (used for FITS BOTTOM-UP row order).</summary>
    public void FlipVertically()
    {
        int w = Width;
        var tmp = new float[w];
        for (int y = 0; y < Height / 2; y++)
        {
            int a = y * w, b = (Height - 1 - y) * w;
            Array.Copy(Data, a, tmp, 0, w);
            Array.Copy(Data, b, Data, a, w);
            Array.Copy(tmp, 0, Data, b, w);
        }
    }

    /// <summary>
    /// 2×2 box-filter down-sample. Odd trailing rows/columns are averaged with their only
    /// neighbour (edge replication), so every level keeps full coverage.
    /// </summary>
    public PlanarImage Downsampled2x()
    {
        int w = Width, h = Height;
        int ow = (w + 1) / 2, oh = (h + 1) / 2;
        var out_ = Uninitialized(ow, oh);
        var s = Data; var d = out_.Data;
        Par.Rows(oh, w * 2, (ya, yb) =>
        {
            var rowSum = new float[w + 1];
            for (int oy = ya; oy < yb; oy++)
            {
                int y0 = 2 * oy, y1 = Math.Min(2 * oy + 1, h - 1);
                Vec.Add(s.AsSpan(y0 * w, w), s.AsSpan(y1 * w, w), rowSum.AsSpan(0, w));
                rowSum[w] = rowSum[w - 1];
                int o = oy * ow;
                for (int ox = 0; ox < ow; ox++) d[o + ox] = (rowSum[2 * ox] + rowSum[2 * ox + 1]) * 0.25f;
            }
        });
        return out_;
    }

    /// <summary>Resample to an arbitrary size (bilinear, box-reduced first when shrinking a lot).</summary>
    public PlanarImage Resized(int nw, int nh) => Resample.Resize(this, nw, nh);

    // Simple whole-image arithmetic helpers (in place)

    public void Clamp(float lo, float hi) => Vec.Clip(Data, lo, hi, Data);

    public void Affine(float scale, float offset) => Vec.ScaleAdd(Data, scale, offset, Data);

    public void Fill(float v) => Array.Fill(Data, v);

    public (float min, float max) MinMax() => Vec.MinMax(Data);

    public float Mean() => Vec.Mean(Data);
}

/// <summary>Three planes of identical size.</summary>
public sealed class RGBImage
{
    public PlanarImage R { get; }
    public PlanarImage G { get; }
    public PlanarImage B { get; }
    public int Width => R.Width;
    public int Height => R.Height;
    public PlanarImage[] Planes => [R, G, B];

    public RGBImage(PlanarImage r, PlanarImage g, PlanarImage b)
    {
        if (r.Width != g.Width || g.Width != b.Width || r.Height != g.Height || g.Height != b.Height)
            throw new ArgumentException("RGB planes must have the same size");
        R = r; G = g; B = b;
    }

    public RGBImage(int width, int height)
    {
        R = new PlanarImage(width, height);
        G = new PlanarImage(width, height);
        B = new PlanarImage(width, height);
    }

    public RGBImage Copy() => new(R.Copy(), G.Copy(), B.Copy());

    public void Clamp01() { foreach (var p in Planes) p.Clamp(0, 1); }

    public RGBImage Downsampled2x() => new(R.Downsampled2x(), G.Downsampled2x(), B.Downsampled2x());

    /// <summary>Rec. 709 luma.</summary>
    public PlanarImage Luma()
    {
        var out_ = PlanarImage.Uninitialized(Width, Height);
        Vec.Scale(R.Data, 0.2126f, out_.Data);
        Vec.MulAdd(G.Data, 0.7152f, out_.Data);
        Vec.MulAdd(B.Data, 0.0722f, out_.Data);
        return out_;
    }
}

/// <summary>A multi-level 2× pyramid of a plane. Level 0 is the original.</summary>
public sealed class Pyramid
{
    public IReadOnlyList<PlanarImage> Levels { get; }

    public Pyramid(PlanarImage baseImage, int minSize = 64)
    {
        var ls = new List<PlanarImage> { baseImage };
        var cur = baseImage;
        while (cur.Width > minSize && cur.Height > minSize)
        {
            cur = cur.Downsampled2x();
            ls.Add(cur);
        }
        Levels = ls;
    }

    public PlanarImage Base => Levels[0];
    public int Count => Levels.Count;

    public PlanarImage Level(int i) => Levels[Math.Max(0, Math.Min(i, Levels.Count - 1))];

    /// <summary>Level whose pixel count does not exceed <paramref name="maxPixels"/> (coarsest fallback).</summary>
    public int LevelIndex(int maxPixels)
    {
        for (int i = 0; i < Levels.Count; i++) if (Levels[i].Count <= maxPixels) return i;
        return Levels.Count - 1;
    }

    /// <summary>
    /// Finest level whose scale (2^-i) is still ≥ the requested screen scale, i.e. the level that
    /// has at least one source pixel per displayed pixel.
    /// </summary>
    public int LevelIndexForScale(double scale)
    {
        int i = 0;
        while (i + 1 < Levels.Count && Math.Pow(2.0, -(i + 1)) >= scale) i++;
        return i;
    }
}
