namespace AstroCore;

/// <summary>
/// An RGB image kept whole (not split into R, G and B channels) and blended into the result as
/// stars: a stars-only image from a star-removal tool, or any RGB frame whose stars are wanted on
/// top of a narrowband combination. Oriented like the channels, same size.
/// </summary>
public sealed class StarsLayer
{
    /// <summary>Identity of this layer for render caches.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>R, G, B planes (full resolution, oriented).</summary>
    public IReadOnlyList<PlanarImage> Planes { get; }
    public IReadOnlyList<Pyramid> Pyramids { get; }
    /// <summary>Whole-image statistics per plane (stretch decision, peak for the normalisation).</summary>
    public IReadOnlyList<ChannelStats> Stats { get; }
    public string FileName { get; }
    public string? Path { get; }
    public string FormatDescription { get; }
    /// <summary>Already processed stars (the render of the app's Stars tab): blended as they are, never stretched again.</summary>
    public bool Processed { get; }

    public int Width => Planes[0].Width;
    public int Height => Planes[0].Height;

    public StarsLayer(IReadOnlyList<PlanarImage> planes, string fileName, string? path = null, string formatDescription = "", bool processed = false)
    {
        if (planes.Count != 3) throw new ArgumentException("a stars layer needs three planes");
        Planes = planes;
        Pyramids = planes.Select(p => new Pyramid(p)).ToList();
        Stats = planes.Select(p => Statistics.Compute(p)).ToList();
        FileName = fileName; Path = path; FormatDescription = formatDescription; Processed = processed;
    }

    /// <summary>The layer after an orientation operation.</summary>
    public StarsLayer Applying(OrientationOp op) => new(Planes.Select(p => p.Applying(op)).ToList(), FileName, Path, FormatDescription, Processed);

    /// <summary>The planes at a pyramid level, cropped (the request a render needs).</summary>
    public StarsInput Input(int level, int x, int y, int width, int height)
    {
        var lvl = Pyramids.Select(p => p.Level(level)).ToList();
        bool whole = x == 0 && y == 0 && width == lvl[0].Width && height == lvl[0].Height;
        return new StarsInput(whole ? lvl : lvl.Select(p => p.Cropped(x, y, width, height)).ToList(), Stats, Processed);
    }

    public StarsInput Input(PixelRect? region)
    {
        if (region is not PixelRect r) return new StarsInput(Planes, Stats, Processed);
        return new StarsInput(Planes.Select(p => p.Cropped(r.X, r.Y, r.Width, r.Height)).ToList(), Stats, Processed);
    }
}

/// <summary>
/// The RGB star planes a render blends (a region of the stars layer at the render level) with the
/// whole-image statistics of the layer.
/// </summary>
public sealed class StarsInput
{
    public IReadOnlyList<PlanarImage> Planes { get; }
    public IReadOnlyList<ChannelStats> Stats { get; }
    /// <summary>Processed stars (see <see cref="StarsLayer.Processed"/>): no stretch.</summary>
    public bool Processed { get; }
    public StarsInput(IReadOnlyList<PlanarImage> planes, IReadOnlyList<ChannelStats> stats, bool processed = false) { Planes = planes; Stats = stats; Processed = processed; }
}
