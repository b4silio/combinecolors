using System.Security.Cryptography;
using System.Text;

namespace AstroCore;

/// <summary>
/// Hand-painted brightness corrections of a channel: a smooth map at a reduced resolution with
/// values in −1…1 (negative darkens, positive brightens), applied to the linear data as an
/// exposure change, out = in × 2^(strength × m). Corrections are feathered and smooth by nature,
/// so the map's long side is at most <see cref="MaximumSize"/> pixels and it is sampled bilinearly.
/// </summary>
public sealed class CorrectionMap
{
    public const int MaximumSize = 1024;
    public int Width { get; }
    public int Height { get; }
    /// <summary>Size of the image the map belongs to (oriented).</summary>
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public PlanarImage Plane { get; }
    /// <summary>Bumped by every edit (change detection without comparing pixels).</summary>
    public int Version { get; private set; }

    public CorrectionMap(int imageWidth, int imageHeight, PlanarImage? plane = null)
    {
        ImageWidth = Math.Max(imageWidth, 1); ImageHeight = Math.Max(imageHeight, 1);
        int lng = Math.Max(ImageWidth, ImageHeight);
        double scale = (double)Math.Min(MaximumSize, lng) / lng;
        if (plane != null) { Width = plane.Width; Height = plane.Height; Plane = plane; }
        else
        {
            Width = Math.Max(1, (int)Math.Round(ImageWidth * scale));
            Height = Math.Max(1, (int)Math.Round(ImageHeight * scale));
            Plane = new PlanarImage(Width, Height);
        }
    }

    /// <summary>Map pixels per image pixel.</summary>
    public double Scale => (double)Width / ImageWidth;

    public bool IsEmpty
    {
        get { var (lo, hi) = Vec.MinMax(Plane.Data); return lo == 0 && hi == 0; }
    }

    public CorrectionMap Copy() => new(ImageWidth, ImageHeight, Plane.Copy()) { Version = Version };

    /// <summary>Value at an image point (bilinear).</summary>
    public float Value(double x, double y)
    {
        double fx = Math.Min(Math.Max(x * Scale - 0.5, 0), Width - 1), fy = Math.Min(Math.Max(y * Scale - 0.5, 0), Height - 1);
        int x0 = (int)fx, y0 = (int)fy, x1 = Math.Min(x0 + 1, Width - 1), y1 = Math.Min(y0 + 1, Height - 1);
        float tx = (float)(fx - x0), ty = (float)(fy - y0);
        var p = Plane.Data;
        float a = p[y0 * Width + x0] + (p[y0 * Width + x1] - p[y0 * Width + x0]) * tx;
        float b = p[y1 * Width + x0] + (p[y1 * Width + x1] - p[y1 * Width + x0]) * tx;
        return a + (b - a) * ty;
    }

    // Painting

    /// <summary>
    /// One brush dab at an image point: <paramref name="amount"/> (± flow) added inside
    /// <paramref name="radius"/> (image pixels) with a smooth fall-off beyond hardness × radius; the
    /// map stays in −1…1.
    /// </summary>
    public void Dab(double x, double y, double radius, float amount, float hardness)
    {
        double r = Math.Max(radius * Scale, 0.75);
        double cx = x * Scale - 0.5, cy = y * Scale - 0.5;
        int x0 = Math.Max(0, (int)Math.Floor(cx - r)), x1 = Math.Min(Width - 1, (int)Math.Ceiling(cx + r));
        int y0 = Math.Max(0, (int)Math.Floor(cy - r)), y1 = Math.Min(Height - 1, (int)Math.Ceiling(cy + r));
        if (x0 > x1 || y0 > y1) return;
        double h = Math.Min(Math.Max(hardness, 0), 0.98);
        var p = Plane.Data;
        for (int yy = y0; yy <= y1; yy++)
        {
            double dy = yy - cy;
            for (int xx = x0; xx <= x1; xx++)
            {
                double dx = xx - cx;
                double t = Math.Sqrt(dx * dx + dy * dy) / r;
                if (t >= 1) continue;
                float w = 1;
                if (t > h) { float u = (float)((t - h) / (1 - h)); w = 1 - u * u * (3 - 2 * u); }
                int i = yy * Width + xx;
                p[i] = Math.Min(Math.Max(p[i] + amount * w, -1), 1);
            }
        }
        Version++;
    }

    /// <summary>A stroke segment: dabs every quarter radius from a to b (both image points).</summary>
    public void Stroke(double ax, double ay, double bx, double by, double radius, float amount, float hardness)
    {
        double d = Math.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay));
        double step = Math.Max(radius / 4, 0.5);
        int n = Math.Max(1, (int)Math.Ceiling(d / step));
        for (int i = 1; i <= n; i++)
        {
            double t = (double)i / n;
            Dab(ax + (bx - ax) * t, ay + (by - ay) * t, radius, amount, hardness);
        }
    }

    public void Clear() { Array.Clear(Plane.Data); Version++; }

    /// <summary>The map after the image has undergone op.</summary>
    public CorrectionMap Applying(OrientationOp op)
    {
        bool swaps = op == OrientationOp.RotateClockwise;
        return new CorrectionMap(swaps ? ImageHeight : ImageWidth, swaps ? ImageWidth : ImageHeight, Plane.Applying(op)) { Version = Version };
    }

    /// <summary>The map of the unoriented image (what the sidecar stores): the orientation undone.</summary>
    public CorrectionMap Unoriented(Orientation o)
    {
        var m = this;
        for (int i = 0; i < (4 - o.Rotation) % 4; i++) m = m.Applying(OrientationOp.RotateClockwise);
        if (o.Flipped) m = m.Applying(OrientationOp.FlipHorizontal);
        return m;
    }

    // Application

    /// <summary>
    /// image × 2^(strength × map) for the region <paramref name="rect"/> of the image (full-resolution
    /// pixels) that the image covers (null: the whole image); zero pixels stay zero. In place.
    /// </summary>
    public void Apply(PlanarImage image, float strength, SampleRect? rect = null)
    {
        if (strength == 0) return;
        var r = rect ?? new SampleRect(0, 0, ImageWidth, ImageHeight);
        double s = Scale;
        var sampled = Plane.Sampled(r.X * s, r.Y * s, r.Width * s, r.Height * s, image.Width, image.Height);
        var m = sampled.Data; var d = image.Data;
        Par.Rows(image.Height, image.Width * 4, (y0, y1) =>
        {
            for (int i = y0 * image.Width; i < y1 * image.Width; i++) d[i] *= MathF.Pow(2, strength * m[i]);
        });
    }

    /// <summary>A corrected copy of the whole image.</summary>
    public PlanarImage Applied(PlanarImage image, float strength)
    {
        var out_ = image.Copy();
        Apply(out_, strength);
        return out_;
    }

    // Storage

    /// <summary>Sidecar next to the image: &lt;image&gt;.ccmask.png (16-bit grey, 32768 = no change).</summary>
    public static string SidecarPath(string imagePath) => imagePath + ".ccmask.png";

    /// <summary>Fallback when the image's folder is not writable: the application data folder, keyed by the image path.</summary>
    public static string FallbackPath(string imagePath)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(baseDir)) baseDir = System.IO.Path.GetTempPath();
        var dir = System.IO.Path.Combine(baseDir, "CombineColors", "Corrections");
        var full = System.IO.Path.GetFullPath(imagePath);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full))).ToLowerInvariant()[..32];
        return System.IO.Path.Combine(dir, key + ".ccmask.png");
    }

    /// <summary>Writes the map (16-bit PNG). Tries the sidecar, then the fallback; returns where it went.</summary>
    public string Write(string imagePath)
    {
        var data = PlanarImage.Uninitialized(Width, Height);
        Vec.ScaleAdd(Plane.Data, 0.5f, 0.5f, data.Data);
        var sidecar = SidecarPath(imagePath);
        try
        {
            RasterWriter.Write([data], sidecar, RasterFormat.Png16);
            return sidecar;
        }
        catch
        {
            var fb = FallbackPath(imagePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fb)!);
            RasterWriter.Write([data], fb, RasterFormat.Png16);
            return fb;
        }
    }

    /// <summary>Removes the stored map (sidecar and fallback).</summary>
    public static void Remove(string imagePath)
    {
        foreach (var p in new[] { SidecarPath(imagePath), FallbackPath(imagePath) }) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }

    /// <summary>Reads the stored map of an image of the given (unoriented) size, if any.</summary>
    public static CorrectionMap? Read(string imagePath, int imageWidth, int imageHeight)
    {
        foreach (var u in new[] { SidecarPath(imagePath), FallbackPath(imagePath) })
        {
            if (!File.Exists(u)) continue;
            RasterReader.Result r;
            try { r = RasterReader.Read(u); } catch { continue; }
            if (r.Planes.Count == 0) continue;
            var p = r.Planes[0];
            var expected = new CorrectionMap(imageWidth, imageHeight);
            if (p.Width != expected.Width || p.Height != expected.Height) continue;
            Vec.ScaleAdd(p.Data, 2, -1, p.Data);
            return new CorrectionMap(imageWidth, imageHeight, p);
        }
        return null;
    }
}

/// <summary>
/// A correction map applied live to one channel of a render: <see cref="Rect"/> is the region
/// (full-resolution pixels) the rendered planes cover (null: the whole image).
/// </summary>
public sealed class CorrectionInput
{
    public CorrectionMap Map { get; }
    public float Strength { get; }
    public ChannelKind Kind { get; }
    public SampleRect? Rect { get; }
    public CorrectionInput(CorrectionMap map, float strength, ChannelKind kind, SampleRect? rect) { Map = map; Strength = strength; Kind = kind; Rect = rect; }
}

/// <summary>
/// Per-channel strength of the painted corrections and the brush defaults (stored in the project;
/// the maps themselves live next to the images).
/// </summary>
public sealed partial class CorrectionSettings : IEquatable<CorrectionSettings>
{
    /// <summary>Exposure range of the map in stops: the map's ±1 means ±strength EV.</summary>
    public Dictionary<ChannelKind, double> Strength = new();
    /// <summary>Brush radius as a fraction of the image's long side (0.005…0.2).</summary>
    public double BrushRadius = 0.04;
    /// <summary>Amount added per dab (0.02…1).</summary>
    public double BrushFlow = 0.15;
    /// <summary>Fraction of the radius over which the dab fades (0 hard … 1 soft).</summary>
    public double BrushFeather = 0.7;
    public bool Darken = true;

    public const double DefaultStrength = 1.0;
    public double StrengthFor(ChannelKind kind) => Strength.TryGetValue(kind, out var s) ? s : DefaultStrength;

    public CorrectionSettings Clone() => new() { Strength = new Dictionary<ChannelKind, double>(Strength), BrushRadius = BrushRadius, BrushFlow = BrushFlow, BrushFeather = BrushFeather, Darken = Darken };
    public bool Equals(CorrectionSettings? o) => o != null && BrushRadius == o.BrushRadius && BrushFlow == o.BrushFlow && BrushFeather == o.BrushFeather && Darken == o.Darken
        && Strength.Count == o.Strength.Count && Strength.All(kv => o.Strength.TryGetValue(kv.Key, out var v) && v == kv.Value);
    public override bool Equals(object? obj) => Equals(obj as CorrectionSettings);
    public override int GetHashCode() => HashCode.Combine(BrushRadius, BrushFlow, BrushFeather, Darken, Strength.Count);
}
