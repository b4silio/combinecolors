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
/// How the loaded channels are turned: an optional horizontal flip, then <see cref="Rotation"/> quarter
/// turns clockwise, then a fine rotation by <see cref="Angle"/> degrees about the centre on the same
/// canvas (<see cref="FineRotation"/>; positive = clockwise on screen). Composition stays in this form,
/// so any sequence of rotate / flip operations can be replayed on channels loaded later.
/// </summary>
public readonly record struct Orientation
{
    /// <summary>Quarter turns clockwise applied after the flip (0…3).</summary>
    public int Rotation { get; }
    /// <summary>Horizontal mirror applied before the rotation.</summary>
    public bool Flipped { get; }
    /// <summary>Fine rotation in degrees (−45…45), applied last.</summary>
    public double Angle { get; }

    public Orientation(int rotation = 0, bool flipped = false, double angle = 0)
    {
        Rotation = ((rotation % 4) + 4) % 4;
        Flipped = flipped;
        Angle = angle;
    }

    public static readonly Orientation Identity = new();
    public bool IsIdentity => Rotation == 0 && !Flipped && Angle == 0;
    /// <summary>Whether width and height are swapped.</summary>
    public bool SwapsAxes => Rotation % 2 == 1;
    /// <summary>The same without the fine rotation (what the quarter turns and the flip alone do).</summary>
    public Orientation WithoutAngle => new(Rotation, Flipped);

    /// <summary>
    /// The orientation after applying <paramref name="op"/> to an image that already carries this
    /// orientation. With R the clockwise turn and H the mirror: H·R = R⁻¹·H and a vertical flip is R²·H.
    /// The fine rotation commutes with the quarter turns (both turn about the centre) and changes sign
    /// under a mirror.
    /// </summary>
    public Orientation Applying(OrientationOp op) => op switch
    {
        OrientationOp.RotateClockwise => new Orientation(Rotation + 1, Flipped, Angle),
        OrientationOp.FlipHorizontal => new Orientation(-Rotation, !Flipped, -Angle),
        OrientationOp.FlipVertical => new Orientation(2 - Rotation, !Flipped, -Angle),
        _ => this,
    };

    public PlanarImage Apply(PlanarImage image)
    {
        var img = Flipped ? image.FlippedHorizontally() : image;
        img = img.Rotated90CW(Rotation);
        if (Angle != 0) img = img.RotatedByDegrees(Angle);
        return img;
    }

    /// <summary>The rectangle after the image (imageWidth × imageHeight) is flipped and quarter-turned
    /// (the fine rotation is not applied: a turned rectangle is no longer a rectangle).</summary>
    public PixelRect Apply(PixelRect rect, int imageWidth, int imageHeight)
    {
        var r = Flipped ? rect.FlippedHorizontally(imageWidth) : rect;
        int w = imageWidth, h = imageHeight;
        for (int i = 0; i < Rotation; i++) { r = r.Rotated90CW(w, h); (w, h) = (h, w); }
        return r;
    }

    public override string ToString() => $"Orientation(rotation: {Rotation}, flipped: {Flipped}, angle: {Angle})";
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
