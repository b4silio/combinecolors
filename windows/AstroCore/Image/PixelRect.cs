namespace AstroCore;

/// <summary>An integer rectangle in full-resolution pixel coordinates (origin top-left).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    /// <summary>From a real-valued rectangle: edges rounded to the nearest pixel, at least 1×1.</summary>
    public static PixelRect FromDouble(double minX, double minY, double maxX, double maxY)
    {
        int x0 = (int)Math.Round(minX, MidpointRounding.AwayFromZero), y0 = (int)Math.Round(minY, MidpointRounding.AwayFromZero);
        int x1 = (int)Math.Round(maxX, MidpointRounding.AwayFromZero), y1 = (int)Math.Round(maxY, MidpointRounding.AwayFromZero);
        return new PixelRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    public int MaxX => X + Width;
    public int MaxY => Y + Height;
    public int Count => Width * Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Clamped to an image of the given size; null when nothing remains.</summary>
    public PixelRect? Clamped(int w, int h)
    {
        int x0 = Math.Max(0, Math.Min(X, w)), y0 = Math.Max(0, Math.Min(Y, h));
        int x1 = Math.Max(x0, Math.Min(MaxX, w)), y1 = Math.Max(y0, Math.Min(MaxY, h));
        if (x1 <= x0 || y1 <= y0) return null;
        return new PixelRect(x0, y0, x1 - x0, y1 - y0);
    }

    public bool Contains(int px, int py) => px >= X && px < MaxX && py >= Y && py < MaxY;

    public static PixelRect Full(int width, int height) => new(0, 0, width, height);

    /// <summary>The same rectangle after the image (imageWidth × imageHeight) is rotated 90° clockwise.</summary>
    public PixelRect Rotated90CW(int imageWidth, int imageHeight) => new(imageHeight - (Y + Height), X, Height, Width);

    /// <summary>Scaled to a pyramid level (divides by 2^level, rounding outwards).</summary>
    public PixelRect Scaled(int level)
    {
        if (level <= 0) return this;
        int s = 1 << level;
        int x0 = X / s, y0 = Y / s;
        int x1 = (MaxX + s - 1) / s, y1 = (MaxY + s - 1) / s;
        return new PixelRect(x0, y0, Math.Max(1, x1 - x0), Math.Max(1, y1 - y0));
    }

    public PixelRect Applying(OrientationOp op, int imageWidth, int imageHeight) => op switch
    {
        OrientationOp.RotateClockwise => Rotated90CW(imageWidth, imageHeight),
        OrientationOp.FlipHorizontal => FlippedHorizontally(imageWidth),
        OrientationOp.FlipVertical => FlippedVertically(imageHeight),
        _ => this,
    };

    /// <summary>Mirrored left-right within an image of <paramref name="imageWidth"/>.</summary>
    public PixelRect FlippedHorizontally(int imageWidth) => new(imageWidth - (X + Width), Y, Width, Height);

    /// <summary>Mirrored top-bottom within an image of <paramref name="imageHeight"/>.</summary>
    public PixelRect FlippedVertically(int imageHeight) => new(X, imageHeight - (Y + Height), Width, Height);

    public override string ToString() => $"({X}, {Y}, {Width}×{Height})";
}

/// <summary>Finds the rectangle that excludes the empty (zero) borders left by registration.</summary>
public static class AutoCrop
{
    /// <summary>Shrinks each side until the row/column is at least <paramref name="minValidFraction"/> valid in every image.</summary>
    public static PixelRect? Detect(IReadOnlyList<PlanarImage> images, float minValidFraction = 0.98f)
    {
        if (images.Count == 0) return null;
        var first = images[0];
        int w = first.Width, h = first.Height;

        bool RowValid(int y, int x0, int x1)
        {
            int n = x1 - x0;
            if (n <= 0) return false;
            foreach (var img in images)
            {
                int valid = 0;
                var p = img.Data;
                int row = y * w;
                for (int x = x0; x < x1; x++) if (p[row + x] != 0) valid++;
                if (valid < minValidFraction * n) return false;
            }
            return true;
        }
        bool ColValid(int x, int y0, int y1)
        {
            int n = y1 - y0;
            if (n <= 0) return false;
            foreach (var img in images)
            {
                int valid = 0;
                var p = img.Data;
                for (int y = y0; y < y1; y++) if (p[y * w + x] != 0) valid++;
                if (valid < minValidFraction * n) return false;
            }
            return true;
        }

        int bx0 = 0, by0 = 0, bx1 = w, by1 = h;
        // Alternate sides so irregular (rotated) borders converge to a good inner rectangle.
        bool changed = true;
        while (changed && bx1 - bx0 > 16 && by1 - by0 > 16)
        {
            changed = false;
            if (!RowValid(by0, bx0, bx1)) { by0++; changed = true; }
            if (!RowValid(by1 - 1, bx0, bx1)) { by1--; changed = true; }
            if (!ColValid(bx0, by0, by1)) { bx0++; changed = true; }
            if (!ColValid(bx1 - 1, by0, by1)) { bx1--; changed = true; }
        }
        // Grow back sides that were trimmed only because of another side's border.
        changed = true;
        while (changed)
        {
            changed = false;
            if (by0 > 0 && RowValid(by0 - 1, bx0, bx1)) { by0--; changed = true; }
            if (by1 < h && RowValid(by1, bx0, bx1)) { by1++; changed = true; }
            if (bx0 > 0 && ColValid(bx0 - 1, by0, by1)) { bx0--; changed = true; }
            if (bx1 < w && ColValid(bx1, by0, by1)) { bx1++; changed = true; }
        }
        if (bx1 <= bx0 || by1 <= by0) return null;
        return new PixelRect(bx0, by0, bx1 - bx0, by1 - by0);
    }
}
