using System.Diagnostics;

namespace AstroCore;

/// <summary>A loaded channel with its pyramid, statistics and star mask. Immutable once built.</summary>
public sealed class ChannelSource
{
    public ChannelKind Kind { get; }
    public Pyramid Pyramid { get; }
    public ChannelStats Stats { get; }
    public ChannelStats MaskedStats { get; }
    public PlanarImage Mask { get; }
    public int MaskLevel { get; }
    public string FileName { get; }
    public string? Path { get; }
    public string FormatDescription { get; }
    public double AnalysisMillis { get; }
    /// <summary>Region used for statistics (null = whole image).</summary>
    public PixelRect? Region { get; }

    public int Width => Pyramid.Base.Width;
    public int Height => Pyramid.Base.Height;
    public PlanarImage Image => Pyramid.Base;

    public ChannelSource(ChannelKind kind, PlanarImage image, string fileName, string? path = null, string formatDescription = "",
                         StarMask.Options? starMaskOptions = null, int maskMaxPixels = 3_000_000, PixelRect? region = null)
    {
        var t0 = Stopwatch.StartNew();
        Kind = kind;
        FileName = fileName;
        Path = path;
        FormatDescription = formatDescription;
        var pyr = new Pyramid(image);
        Pyramid = pyr;
        int ml = pyr.LevelIndex(maskMaxPixels);
        MaskLevel = ml;
        var m = StarMask.Build(pyr.Level(ml), starMaskOptions ?? new StarMask.Options());
        Mask = m;
        var r = region?.Clamped(image.Width, image.Height);
        Region = r;
        (Stats, MaskedStats) = ComputeStats(image, m, ml, r);
        AnalysisMillis = t0.Elapsed.TotalMilliseconds;
    }

    /// <summary>Re-uses the pyramid and star mask of another source but recomputes statistics for a region.</summary>
    public ChannelSource(ChannelSource other, PixelRect? region)
    {
        var t0 = Stopwatch.StartNew();
        Kind = other.Kind; Pyramid = other.Pyramid; Mask = other.Mask; MaskLevel = other.MaskLevel;
        FileName = other.FileName; Path = other.Path; FormatDescription = other.FormatDescription;
        var r = region?.Clamped(other.Width, other.Height);
        Region = r;
        (Stats, MaskedStats) = ComputeStats(other.Image, other.Mask, other.MaskLevel, r);
        AnalysisMillis = t0.Elapsed.TotalMilliseconds;
    }

    /// <summary>
    /// Same source over a different image (new pyramid) but with the statistics and the star mask of
    /// <paramref name="other"/>: for quick previews of local edits (dust spots) that leave the global statistics as they are.
    /// </summary>
    public ChannelSource(ChannelSource other, PlanarImage image)
    {
        Kind = other.Kind; Pyramid = new Pyramid(image); Mask = other.Mask; MaskLevel = other.MaskLevel;
        FileName = other.FileName; Path = other.Path; FormatDescription = other.FormatDescription;
        Region = other.Region; Stats = other.Stats; MaskedStats = other.MaskedStats; AnalysisMillis = other.AnalysisMillis;
    }

    /// <summary>Same source with a different channel kind (no recomputation).</summary>
    public ChannelSource(ChannelSource other, ChannelKind kind)
    {
        Kind = kind; Pyramid = other.Pyramid; Mask = other.Mask; MaskLevel = other.MaskLevel;
        FileName = other.FileName; Path = other.Path; FormatDescription = other.FormatDescription;
        Region = other.Region; Stats = other.Stats; MaskedStats = other.MaskedStats; AnalysisMillis = other.AnalysisMillis;
    }

    static (ChannelStats, ChannelStats) ComputeStats(PlanarImage image, PlanarImage mask, int maskLevel, PixelRect? region)
    {
        var opts = new StatsOptions { Region = region };
        var stats = Statistics.Compute(image, opts);
        opts.Mask = mask;
        opts.MaskLevel = maskLevel;
        var masked = Statistics.Compute(image, opts);
        return (stats, masked);
    }

    public NormalizationInput NormalizationInput => new(Kind, Image, Stats, MaskedStats, Mask, MaskLevel, Region);

    /// <summary>Auto-stretched grayscale preview of the channel, limited to the source's region (crop) when one is set.</summary>
    public PlanarImage DisplayPlane(int maxPixels, StretchSettings? settings = null)
    {
        settings ??= new StretchSettings();
        PlanarImage plane;
        if (Region is PixelRect region && region != PixelRect.Full(Width, Height))
        {
            // Pick the level at which the region (not the whole image) fits the budget.
            int level = 0;
            while (level + 1 < Pyramid.Count && region.Scaled(level).Count > maxPixels) level++;
            var r = region.Scaled(level);
            plane = Pyramid.Level(level).Cropped(r.X, r.Y, r.Width, r.Height);
        }
        else plane = Pyramid.Level(Pyramid.LevelIndex(maxPixels)).Copy();
        Stretch.Resolve(settings, Stats).Apply(plane);
        return plane;
    }
}
