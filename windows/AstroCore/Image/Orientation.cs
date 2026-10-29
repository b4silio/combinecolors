namespace AstroCore;

public enum OrientationOp
{
    RotateClockwise,
    FlipHorizontal,
    FlipVertical,
}

public static class OrientationOps
{
    public static readonly OrientationOp[] All = [OrientationOp.RotateClockwise, OrientationOp.FlipHorizontal, OrientationOp.FlipVertical];

    public static string RawValue(this OrientationOp op) => op switch
    {
        OrientationOp.RotateClockwise => "rotateClockwise",
        OrientationOp.FlipHorizontal => "flipHorizontal",
        _ => "flipVertical",
    };
}

/// <summary>
/// One of the eight ways an image can be turned or mirrored, expressed as an optional horizontal
/// flip followed by <see cref="Rotation"/> quarter turns clockwise. Composition stays in this form,
/// so any sequence of rotate / flip operations can be replayed on channels loaded later.
/// </summary>
public readonly record struct Orientation
{
    /// <summary>Quarter turns clockwise applied after the flip (0…3).</summary>
    public int Rotation { get; }
    /// <summary>Horizontal mirror applied before the rotation.</summary>
    public bool Flipped { get; }

    public Orientation(int rotation = 0, bool flipped = false)
    {
        Rotation = ((rotation % 4) + 4) % 4;
        Flipped = flipped;
    }

    public static readonly Orientation Identity = new();
    public bool IsIdentity => Rotation == 0 && !Flipped;
    /// <summary>Whether width and height are swapped.</summary>
    public bool SwapsAxes => Rotation % 2 == 1;

    /// <summary>
    /// The orientation after applying <paramref name="op"/> to an image that already carries this
    /// orientation. With R the clockwise turn and H the mirror: H·R = R⁻¹·H and a vertical flip is R²·H.
    /// </summary>
    public Orientation Applying(OrientationOp op) => op switch
    {
        OrientationOp.RotateClockwise => new Orientation(Rotation + 1, Flipped),
        OrientationOp.FlipHorizontal => new Orientation(-Rotation, !Flipped),
        OrientationOp.FlipVertical => new Orientation(2 - Rotation, !Flipped),
        _ => this,
    };

    public PlanarImage Apply(PlanarImage image)
    {
        var img = Flipped ? image.FlippedHorizontally() : image;
        return img.Rotated90CW(Rotation);
    }

    /// <summary>The rectangle after the image (imageWidth × imageHeight) is oriented.</summary>
    public PixelRect Apply(PixelRect rect, int imageWidth, int imageHeight)
    {
        var r = Flipped ? rect.FlippedHorizontally(imageWidth) : rect;
        int w = imageWidth, h = imageHeight;
        for (int i = 0; i < Rotation; i++) { r = r.Rotated90CW(w, h); (w, h) = (h, w); }
        return r;
    }

    public override string ToString() => $"Orientation(rotation: {Rotation}, flipped: {Flipped})";
}

public static class OrientationExtensions
{
    /// <summary>Applies a single operation (used on the loaded channels; <see cref="Orientation"/> records the result).</summary>
    public static PlanarImage Applying(this PlanarImage image, OrientationOp op) => op switch
    {
        OrientationOp.RotateClockwise => image.Rotated90CW(),
        OrientationOp.FlipHorizontal => image.FlippedHorizontally(),
        _ => image.FlippedVertically(),
    };
}
