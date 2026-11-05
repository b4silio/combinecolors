namespace AstroCore;

/// <summary>Shape of the guide the user draws over a gradient.</summary>
public enum GradientShape
{
    /// <summary>The background varies along the line and is constant across it (light pollution from one side).</summary>
    Line,
    /// <summary>The background is a function of the elliptical radius around the centre (vignetting, a glow from a bright source that may lie outside the frame).</summary>
    Ellipse,
    /// <summary>No guide: a plain 2-D polynomial of the chosen degree (the classic parametric model).</summary>
    Polynomial,
    /// <summary>Background samples (discs on sky) with a smoothing spline or kriging surface through them.</summary>
    Samples,
}

public static class GradientShapes
{
    public static readonly GradientShape[] All = [GradientShape.Line, GradientShape.Ellipse, GradientShape.Polynomial, GradientShape.Samples];
    public static string DisplayName(this GradientShape s) => s switch { GradientShape.Line => "Line", GradientShape.Ellipse => "Ellipse", GradientShape.Polynomial => "Polynomial", _ => "Samples" };
}

/// <summary>How the estimated background is taken out.</summary>
public enum GradientMode
{
    /// <summary>Background subtracted, overall level kept (sky glow, light pollution, stretched data).</summary>
    Subtract,
    /// <summary>Pixels divided by the background normalised to its brightest level (vignetting / missing flats on linear data: stars at the edge get their flux back).</summary>
    Divide,
}

public static class GradientModes
{
    public static readonly GradientMode[] All = [GradientMode.Subtract, GradientMode.Divide];
    public static string DisplayName(this GradientMode m) => m == GradientMode.Subtract ? "Subtract" : "Divide";
}

/// <summary>Low-order 2-D term fitted after the guided profile, for whatever the profile cannot explain (a sky gradient on top of the vignetting).</summary>
public enum GradientResidual { None, Plane, Quadratic }

public static class GradientResiduals
{
    public static readonly GradientResidual[] All = [GradientResidual.None, GradientResidual.Plane, GradientResidual.Quadratic];
    public static string DisplayName(this GradientResidual r) => r switch { GradientResidual.None => "None", GradientResidual.Plane => "Plane", _ => "Quadratic" };
    public static int Degree(this GradientResidual r) => r switch { GradientResidual.None => 0, GradientResidual.Plane => 1, _ => 2 };
}

/// <summary>
/// A guide drawn on the image, in oriented image pixels. Line: from (X, Y) to (X2, Y2). Ellipse:
/// centre (X, Y) — it may lie outside the frame —, semi-axes A (along Angle) and B, Angle in
/// radians measured from the +x axis towards +y (clockwise on screen). Polynomial: geometry unused.
/// </summary>
public sealed partial class GradientGesture : IEquatable<GradientGesture>
{
    public GradientShape Shape;
    public double X, Y, X2, Y2;
    public double A = 1, B = 1, Angle;

    public GradientGesture(GradientShape shape) { Shape = shape; }

    public static GradientGesture Line(double x0, double y0, double x1, double y1) => new(GradientShape.Line) { X = x0, Y = y0, X2 = x1, Y2 = y1 };
    public static GradientGesture Ellipse(double cx, double cy, double a, double b, double angle = 0) => new(GradientShape.Ellipse) { X = cx, Y = cy, A = a, B = b, Angle = angle };
    public static GradientGesture Polynomial() => new(GradientShape.Polynomial);
    public static GradientGesture Samples() => new(GradientShape.Samples);

    public double LineLength => Math.Sqrt((X2 - X) * (X2 - X) + (Y2 - Y) * (Y2 - Y));

    public GradientGesture Clone() => (GradientGesture)MemberwiseClone();

    /// <summary>The same guide converted to another shape (so the drawing survives a shape switch).</summary>
    public GradientGesture ConvertedTo(GradientShape s)
    {
        if (s == Shape) return this;
        var g = Clone();
        g.Shape = s;
        if (Shape == GradientShape.Line && s == GradientShape.Ellipse)
        {
            double l = Math.Max(LineLength, 1);
            g.A = l; g.B = l; g.Angle = Math.Atan2(Y2 - Y, X2 - X);
        }
        else if (Shape == GradientShape.Ellipse && s == GradientShape.Line)
        {
            g.X2 = X + A * Math.Cos(Angle); g.Y2 = Y + A * Math.Sin(Angle);
        }
        return g;
    }

    /// <summary>The guide after the image (imageWidth × imageHeight) has undergone <paramref name="op"/>.</summary>
    public GradientGesture Applying(OrientationOp op, int imageWidth, int imageHeight)
    {
        var g = Clone();
        switch (op)
        {
            case OrientationOp.RotateClockwise: g.X = imageHeight - Y; g.Y = X; g.X2 = imageHeight - Y2; g.Y2 = X2; g.Angle = Angle + Math.PI / 2; break;
            case OrientationOp.FlipHorizontal: g.X = imageWidth - X; g.X2 = imageWidth - X2; g.Angle = Math.PI - Angle; break;
            case OrientationOp.FlipVertical: g.Y = imageHeight - Y; g.Y2 = imageHeight - Y2; g.Angle = -Angle; break;
        }
        return g;
    }

    /// <summary>The guide after a fine rotation of the image by <paramref name="degrees"/> (<see cref="FineRotation"/>).</summary>
    public GradientGesture RotatedByDegrees(double degrees, int imageWidth, int imageHeight)
    {
        var g = Clone();
        (g.X, g.Y) = FineRotation.Rotate(X, Y, degrees, imageWidth, imageHeight);
        (g.X2, g.Y2) = FineRotation.Rotate(X2, Y2, degrees, imageWidth, imageHeight);
        g.Angle = Angle + degrees * Math.PI / 180;
        return g;
    }

    /// <summary>The guide after an unoriented image is oriented (flip, then quarter turns).</summary>
    public GradientGesture Applying(Orientation o, int imageWidth, int imageHeight)
    {
        var g = o.Flipped ? Applying(OrientationOp.FlipHorizontal, imageWidth, imageHeight) : Clone();
        int w = imageWidth, h = imageHeight;
        for (int i = 0; i < o.Rotation; i++) { g = g.Applying(OrientationOp.RotateClockwise, w, h); (w, h) = (h, w); }
        return g;
    }

    public bool Equals(GradientGesture? o) => o != null && Shape == o.Shape && X == o.X && Y == o.Y && X2 == o.X2 && Y2 == o.Y2 && A == o.A && B == o.B && Angle == o.Angle;
    public override bool Equals(object? obj) => Equals(obj as GradientGesture);
    public override int GetHashCode() => HashCode.Combine(Shape, X, Y, X2, Y2, A, B, Angle);
}

/// <summary>An elliptical region (axis-aligned) whose pixels are left out of the background estimate: the nebula, a galaxy, anything that is not sky.</summary>
public sealed partial class GradientRegion : IEquatable<GradientRegion>
{
    public Guid Id;
    public double X, Y, A, B;

    public GradientRegion(double x, double y, double a, double b, Guid? id = null) { Id = id ?? Guid.NewGuid(); X = x; Y = y; A = Math.Max(a, 1); B = Math.Max(b, 1); }

    public bool Contains(double px, double py) { double dx = (px - X) / A, dy = (py - Y) / B; return dx * dx + dy * dy <= 1; }

    public GradientRegion Clone() => (GradientRegion)MemberwiseClone();

    public GradientRegion Applying(OrientationOp op, int imageWidth, int imageHeight)
    {
        var r = Clone();
        switch (op)
        {
            case OrientationOp.RotateClockwise: r.X = imageHeight - Y; r.Y = X; r.A = B; r.B = A; break;
            case OrientationOp.FlipHorizontal: r.X = imageWidth - X; break;
            case OrientationOp.FlipVertical: r.Y = imageHeight - Y; break;
        }
        return r;
    }

    /// <summary>The region after a fine rotation of the image by <paramref name="degrees"/>: the centre turns;
    /// the region stays axis-aligned and grows to the bounding box of the turned ellipse (it protects a
    /// little more, never less).</summary>
    public GradientRegion RotatedByDegrees(double degrees, int imageWidth, int imageHeight)
    {
        var r = Clone();
        (r.X, r.Y) = FineRotation.Rotate(X, Y, degrees, imageWidth, imageHeight);
        double t = degrees * Math.PI / 180, c = Math.Cos(t), s = Math.Sin(t);
        r.A = Math.Sqrt(A * A * c * c + B * B * s * s);
        r.B = Math.Sqrt(A * A * s * s + B * B * c * c);
        return r;
    }

    public GradientRegion Applying(Orientation o, int imageWidth, int imageHeight)
    {
        var r = o.Flipped ? Applying(OrientationOp.FlipHorizontal, imageWidth, imageHeight) : Clone();
        int w = imageWidth, h = imageHeight;
        for (int i = 0; i < o.Rotation; i++) { r = r.Applying(OrientationOp.RotateClockwise, w, h); (w, h) = (h, w); }
        return r;
    }

    public bool Equals(GradientRegion? o) => o != null && Id == o.Id && X == o.X && Y == o.Y && A == o.A && B == o.B;
    public override bool Equals(object? obj) => Equals(obj as GradientRegion);
    public override int GetHashCode() => HashCode.Combine(Id, X, Y, A, B);
}

/// <summary>The gradient model of one channel: the guide plus the estimation parameters.</summary>
public sealed partial class GradientSpec : IEquatable<GradientSpec>
{
    public GradientGesture Gesture;
    public GradientMode Mode = GradientMode.Subtract;
    public GradientResidual Residual = GradientResidual.Plane;
    /// <summary>Angular sectors of the elliptical profile (1 = one profile for every direction; 4–12 let the fall-off differ with the direction).</summary>
    public int Sectors = 1;
    /// <summary>Smoothing of the profile along the guide, in bins (of 120 over the frame).</summary>
    public double Smoothness = 1.5;
    /// <summary>Degree of the polynomial shape (1…6).</summary>
    public int Degree = 4;
    /// <summary>Optimise the guide's geometry from the data before estimating the profile.</summary>
    public bool Refine = true;
    /// <summary>Background samples (Samples shape).</summary>
    public List<BackgroundSample> Samples = new();
    /// <summary>Radius of the sample discs in full-resolution pixels (0 = automatic).</summary>
    public double SampleRadius = 0;
    /// <summary>Surface through the samples.</summary>
    public SampleSurface Surface = SampleSurface.Spline;
    /// <summary>Smoothing of the surface (0…1).</summary>
    public double SampleSmoothing = 0.5;
    /// <summary>Pick the smoothing by cross-validation instead of SampleSmoothing.</summary>
    public bool AutoSmoothing = true;
    /// <summary>Grid generator parameters (samples per row; tolerance in sample noises above the sky model). A Samples model without samples generates its grid with these when fitted.</summary>
    public int GridPerRow = 14;
    public double GridTolerance = 2.5;

    public GradientSpec(GradientGesture gesture) { Gesture = gesture; }

    public GradientSpec Clone() { var c = (GradientSpec)MemberwiseClone(); c.Gesture = Gesture.Clone(); c.Samples = Samples.Select(s => s.Clone()).ToList(); return c; }

    public GradientSpec Applying(OrientationOp op, int imageWidth, int imageHeight)
    {
        var s = Clone();
        s.Gesture = Gesture.Applying(op, imageWidth, imageHeight);
        s.Samples = Samples.Select(p => p.Applying(op, imageWidth, imageHeight)).ToList();
        return s;
    }

    public GradientSpec RotatedByDegrees(double degrees, int imageWidth, int imageHeight)
    {
        var s = Clone();
        s.Gesture = Gesture.RotatedByDegrees(degrees, imageWidth, imageHeight);
        s.Samples = Samples.Select(p => p.RotatedByDegrees(degrees, imageWidth, imageHeight)).ToList();
        return s;
    }

    /// <summary>Sample radius actually used.</summary>
    public double ResolvedSampleRadius(int width, int height) => SampleRadius > 0 ? SampleRadius : BackgroundSamples.DefaultRadius(width, height);

    public bool Equals(GradientSpec? o) => o != null && Gesture.Equals(o.Gesture) && Mode == o.Mode && Residual == o.Residual && Sectors == o.Sectors && Smoothness == o.Smoothness && Degree == o.Degree && Refine == o.Refine
        && Samples.SequenceEqual(o.Samples) && SampleRadius == o.SampleRadius && Surface == o.Surface && SampleSmoothing == o.SampleSmoothing && AutoSmoothing == o.AutoSmoothing && GridPerRow == o.GridPerRow && GridTolerance == o.GridTolerance;
    public override bool Equals(object? obj) => Equals(obj as GradientSpec);
    public override int GetHashCode() => HashCode.Combine(HashCode.Combine(Gesture, Mode, Residual, Sectors, Smoothness, Degree, Refine), Samples.Count, SampleRadius, Surface, SampleSmoothing, AutoSmoothing, GridPerRow, GridTolerance);
}

/// <summary>
/// Gradient models per channel plus the shared protected regions and the parameters given to new
/// models. Stored in the project; applied to the loaded channel after dust healing and before the
/// statistics, normalisation and everything else.
/// </summary>
public sealed partial class GradientSettings : IEquatable<GradientSettings>
{
    public Dictionary<ChannelKind, GradientSpec> Specs = new();
    /// <summary>Regions left out of every channel's background estimate (the object is at the same place in every channel).</summary>
    public List<GradientRegion> Protect = new();
    public GradientShape Shape = GradientShape.Ellipse;
    public GradientMode Mode = GradientMode.Subtract;
    public GradientResidual Residual = GradientResidual.Plane;
    public int Sectors = 1;
    public double Smoothness = 1.5;
    public int Degree = 4;
    public bool Refine = true;
    public SampleSurface Surface = SampleSurface.Spline;
    public double SampleSmoothing = 0.5;
    public bool AutoSmoothing = true;
    public double SampleRadius = 0;
    /// <summary>Grid generator: samples per row and the tolerance (in sample noises above the sky model).</summary>
    public int GridPerRow = 14;
    public double GridTolerance = 2.5;

    public bool IsEmpty => Specs.Count == 0;
    public GradientSpec? SpecFor(ChannelKind kind) => Specs.TryGetValue(kind, out var s) ? s : null;
    /// <summary>A model with the parameters for new models around a drawn guide.</summary>
    public GradientSpec NewSpec(GradientGesture gesture) => new(gesture)
    {
        Mode = Mode, Residual = Residual, Sectors = Sectors, Smoothness = Smoothness, Degree = Degree, Refine = Refine,
        SampleRadius = SampleRadius, Surface = Surface, SampleSmoothing = SampleSmoothing, AutoSmoothing = AutoSmoothing, GridPerRow = GridPerRow, GridTolerance = GridTolerance,
    };

    public GradientSettings Clone()
    {
        var c = new GradientSettings
        {
            Shape = Shape, Mode = Mode, Residual = Residual, Sectors = Sectors, Smoothness = Smoothness, Degree = Degree, Refine = Refine,
            Surface = Surface, SampleSmoothing = SampleSmoothing, AutoSmoothing = AutoSmoothing, SampleRadius = SampleRadius, GridPerRow = GridPerRow, GridTolerance = GridTolerance,
        };
        foreach (var (k, v) in Specs) c.Specs[k] = v.Clone();
        c.Protect = Protect.Select(r => r.Clone()).ToList();
        return c;
    }

    /// <summary>Everything mapped through an orientation operation of the imageWidth × imageHeight image.</summary>
    public GradientSettings Applying(OrientationOp op, int imageWidth, int imageHeight)
    {
        var g = Clone();
        g.Specs = Specs.ToDictionary(kv => kv.Key, kv => kv.Value.Applying(op, imageWidth, imageHeight));
        g.Protect = Protect.Select(r => r.Applying(op, imageWidth, imageHeight)).ToList();
        return g;
    }

    /// <summary>Everything turned with a fine rotation of the image by <paramref name="degrees"/>.</summary>
    public GradientSettings RotatedByDegrees(double degrees, int imageWidth, int imageHeight)
    {
        if (degrees == 0) return this;
        var g = Clone();
        g.Specs = Specs.ToDictionary(kv => kv.Key, kv => kv.Value.RotatedByDegrees(degrees, imageWidth, imageHeight));
        g.Protect = Protect.Select(r => r.RotatedByDegrees(degrees, imageWidth, imageHeight)).ToList();
        return g;
    }

    public bool Equals(GradientSettings? o) => o != null && SettingsJson.SameJson(this, o);
    public override bool Equals(object? obj) => Equals(obj as GradientSettings);
    public override int GetHashCode() => SettingsJson.JsonHash(this);
}

/// <summary>
/// Removes a smooth background (sky gradient, glow, vignetting) from a channel, guided by a shape
/// the user draws over it: a robust 1-D profile along the guide coordinate (median of the
/// star-masked cell medians per bin, smoothed), optional angular sectors, optional residual plane
/// or quadratic, Nelder–Mead refinement of the drawn geometry (several starts for the ellipse),
/// subtracted (level kept) or divided out (normalised to the brightest level). Zero pixels stay zero.
/// The same algorithm as the macOS build (see GradientRemoval.swift).
/// </summary>
public static class GradientRemoval
{
    /// <summary>Background samples: star-masked medians of small cells, in full-resolution coordinates.</summary>
    public sealed class Samples
    {
        public float[] X = [], Y = [], V = [];
        /// <summary>Robust noise of one sample (from the differences of neighbouring cells).</summary>
        public float Noise;
        public int Width, Height;
        public int Count => V.Length;

        internal Samples Subsampled(int maxCount)
        {
            int step = Math.Max(1, Count / maxCount);
            if (step <= 1) return this;
            int n = (Count + step - 1) / step;
            var s = new Samples { Noise = Noise, Width = Width, Height = Height, X = new float[n], Y = new float[n], V = new float[n] };
            for (int i = 0, j = 0; i < Count; i += step, j++) { s.X[j] = X[i]; s.Y[j] = Y[i]; s.V[j] = V[i]; }
            return s;
        }
    }

    const int Bins = 120;
    const int Cell = 4;
    const int LevelPixels = 4_000_000;
    const float MinimumTransmission = 0.05f;

    /// <summary>Star-masked cell medians of <paramref name="image"/>, leaving out zero pixels and the protected regions.</summary>
    public static Samples GetSamples(PlanarImage image, IReadOnlyList<GradientRegion>? protect = null)
    {
        protect ??= [];
        var level = image;
        int scale = 1;
        while (level.Width * level.Height > LevelPixels && level.Width > 64 && level.Height > 64) { level = level.Downsampled2x(); scale *= 2; }
        var mask = StarMask.Build(level, new StarMask.Options { ThresholdSigma = 3, Dilation = 2, BackgroundLevels = 3, MaxSamples = 200_000 });
        int w = level.Width, h = level.Height, c = Cell, cw = w / c, ch = h / c;
        var xs = new List<float>(cw * ch); var ys = new List<float>(cw * ch); var vs = new List<float>(cw * ch);
        var buf = new float[c * c];
        int minCount = Math.Max(3, c * c * 2 / 5);
        var ld = level.Data; var md = mask.Data;
        for (int cy = 0; cy < ch; cy++)
            for (int cx = 0; cx < cw; cx++)
            {
                float px = (cx + 0.5f) * c * scale, py = (cy + 0.5f) * c * scale;
                bool skip = false;
                foreach (var r in protect) if (r.Contains(px, py)) { skip = true; break; }
                if (skip) continue;
                int n = 0;
                for (int y = cy * c; y < cy * c + c; y++)
                {
                    int row = y * w;
                    for (int x = cx * c; x < cx * c + c; x++) { int i = row + x; float v = ld[i]; if (v != 0 && md[i] == 0) buf[n++] = v; }
                }
                if (n < minCount) continue;
                Array.Sort(buf, 0, n);
                float med = n % 2 == 1 ? buf[n / 2] : 0.5f * (buf[n / 2 - 1] + buf[n / 2]);
                xs.Add(px); ys.Add(py); vs.Add(med);
            }
        var diffs = new List<float>(vs.Count);
        for (int i = 1; i < vs.Count; i++) if (ys[i] == ys[i - 1] && xs[i] - xs[i - 1] <= c * scale * 1.5f) diffs.Add(Math.Abs(vs[i] - vs[i - 1]));
        float noise = 0;
        if (diffs.Count >= 8) { var d = diffs.ToArray(); Array.Sort(d); noise = 1.4826f * Statistics.Percentile(d, 0.5) / MathF.Sqrt(2); }
        return new Samples { X = xs.ToArray(), Y = ys.ToArray(), V = vs.ToArray(), Noise = Math.Max(noise, 1e-12f), Width = image.Width, Height = image.Height };
    }

    /// <summary>Robust profile f(u[, φ]) over equal-width bins of the guide coordinate (sector-wise when Sectors > 1).</summary>
    internal sealed class Profile
    {
        public float Lo, Hi;
        public int SectorCount;
        public float[] Values = [];   // sectors × Bins

        public float Eval(float u, float phi)
        {
            int nb = Bins;
            float t = (u - Lo) / (Hi - Lo) * nb - 0.5f;
            int i0 = (int)MathF.Floor(t);
            int ia = Math.Clamp(i0, 0, nb - 1), ib = Math.Clamp(i0 + 1, 0, nb - 1);
            float f = Math.Clamp(t - i0, 0, 1);
            if (SectorCount == 1) return Values[ia] + (Values[ib] - Values[ia]) * f;
            float s = (phi / (2 * MathF.PI) + 1) * SectorCount - 0.5f;
            s -= MathF.Floor(s / SectorCount) * SectorCount;
            int s0 = (int)MathF.Floor(s); float sf = s - s0;
            int ka = ((s0 % SectorCount) + SectorCount) % SectorCount, kb = (ka + 1) % SectorCount;
            float va = Values[ka * nb + ia] + (Values[ka * nb + ib] - Values[ka * nb + ia]) * f;
            float vb = Values[kb * nb + ia] + (Values[kb * nb + ib] - Values[kb * nb + ia]) * f;
            return va + (vb - va) * sf;
        }

        public static Profile? Fit(float[] u, float[] phi, float[] v, int sectors, double smoothness)
        {
            int n = v.Length;
            if (n < 16) return null;
            int nb = Bins;
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (var x in u) { lo = Math.Min(lo, x); hi = Math.Max(hi, x); }
            if (!(hi > lo)) return null;
            hi += (hi - lo) * 1e-4f;
            sectors = Math.Max(1, sectors);
            var key = new int[n]; var counts = new int[sectors * nb];
            for (int i = 0; i < n; i++)
            {
                int b = Math.Clamp((int)((u[i] - lo) / (hi - lo) * nb), 0, nb - 1);
                int s = 0;
                if (sectors > 1)
                {
                    float t = (phi[i] / (2 * MathF.PI) + 1) * sectors;
                    t -= MathF.Floor(t / sectors) * sectors;
                    s = Math.Clamp((int)t, 0, sectors - 1);
                }
                key[i] = s * nb + b; counts[s * nb + b]++;
            }
            var starts = new int[sectors * nb + 1];
            for (int k = 0; k < sectors * nb; k++) starts[k + 1] = starts[k] + counts[k];
            var sorted = new float[n]; var fill = (int[])starts.Clone();
            for (int i = 0; i < n; i++) sorted[fill[key[i]]++] = v[i];
            var med = new float[sectors * nb]; Array.Fill(med, float.NaN);
            for (int k = 0; k < sectors * nb; k++)
                if (counts[k] >= 3)
                {
                    var slice = sorted.AsSpan(starts[k], counts[k]);
                    slice.Sort();
                    med[k] = Statistics.Percentile(slice, 0.5);
                }
            var global = new float[nb]; Array.Fill(global, float.NaN);
            var acc = new List<float>(sectors);
            for (int b = 0; b < nb; b++)
            {
                acc.Clear();
                for (int s = 0; s < sectors; s++) if (!float.IsNaN(med[s * nb + b])) acc.Add(med[s * nb + b]);
                if (acc.Count > 0) { var a = acc.ToArray(); Array.Sort(a); global[b] = Statistics.Percentile(a, 0.5); }
            }
            InterpolateGaps(global);
            if (global.All(float.IsNaN)) return null;
            var weights = new float[sectors * nb];
            for (int s = 0; s < sectors; s++)
                for (int b = 0; b < nb; b++)
                {
                    int k = s * nb + b;
                    if (float.IsNaN(med[k])) { med[k] = global[b]; weights[k] = 0.25f; } else weights[k] = counts[k];
                }
            float su = (float)Math.Max(smoothness, 0);
            int ru = (int)MathF.Ceiling(3 * su);
            float sphi = sectors > 1 ? 0.7f : 0;
            int rphi = sectors > 1 ? Math.Min(sectors / 2, (int)MathF.Ceiling(3 * sphi)) : 0;
            var outv = new float[sectors * nb];
            for (int s = 0; s < sectors; s++)
                for (int b = 0; b < nb; b++)
                {
                    float num = 0, den = 0;
                    for (int ds = -rphi; ds <= rphi; ds++)
                    {
                        int ss = ((s + ds) % sectors + sectors) % sectors;
                        float wp = sphi > 0 ? MathF.Exp(-0.5f * ds * ds / (sphi * sphi)) : 1;
                        for (int db = -ru; db <= ru; db++)
                        {
                            int bb = Math.Clamp(b + db, 0, nb - 1);
                            float wu = su > 0 ? MathF.Exp(-0.5f * db * db / (su * su)) : (db == 0 ? 1 : 0);
                            float wgt = wp * wu * weights[ss * nb + bb];
                            num += wgt * med[ss * nb + bb]; den += wgt;
                        }
                    }
                    outv[s * nb + b] = den > 0 ? num / den : med[s * nb + b];
                }
            return new Profile { Lo = lo, Hi = hi, SectorCount = sectors, Values = outv };
        }

        /// <summary>Linear interpolation across NaN runs, flat extension at the ends.</summary>
        static void InterpolateGaps(float[] a)
        {
            int n = a.Length;
            int first = Array.FindIndex(a, x => !float.IsNaN(x));
            if (first < 0) return;
            for (int i = 0; i < first; i++) a[i] = a[first];
            int p = first;
            while (p < n)
            {
                if (float.IsNaN(a[p]))
                {
                    int j = p;
                    while (j < n && float.IsNaN(a[j])) j++;
                    float left = a[p - 1];
                    if (j < n) { float right = a[j]; for (int k = p; k < j; k++) a[k] = left + (right - left) * (k - p + 1) / (float)(j - p + 1); }
                    else for (int k = p; k < n; k++) a[k] = left;
                    p = j;
                }
                else p++;
            }
        }
    }

    /// <summary>The fitted model of one channel.</summary>
    public sealed class Fit
    {
        /// <summary>The guide actually used (refined when asked).</summary>
        public GradientGesture Gesture = GradientGesture.Polynomial();
        public GradientSpec Spec = new(GradientGesture.Polynomial());
        internal Profile? Profile;
        internal float[] Poly = [];
        internal int PolyDegree;
        /// <summary>Level the corrected background is brought to (subtract: median of the model over the samples; divide: its brightest level).</summary>
        public float Reference;
        /// <summary>RMS of the sample residuals in units of the sample noise.</summary>
        public float ResidualSigma;
        public int Width, Height;
        /// <summary>Surface through the background samples (Samples shape) and what became of each sample.</summary>
        public BackgroundSamples.Surface? Surface;
        public IReadOnlyList<SampleResult> SampleResults => Surface?.Results ?? (IReadOnlyList<SampleResult>)[];

        public float Background(float x, float y)
        {
            if (Surface != null) return Surface.Value(x, y);
            float v = 0;
            if (Profile != null) { var (u, phi) = Coordinate(Gesture, x, y); v = Profile.Eval(u, phi); }
            if (Poly.Length > 0) v += Polynomial(Poly, PolyDegree, x / Width, y / Height);
            return v;
        }

        /// <summary>The model evaluated at every pixel.</summary>
        public PlanarImage BackgroundImage()
        {
            var out_ = PlanarImage.Uninitialized(Width, Height);
            var d = out_.Data; int w = Width;
            Par.Rows(Height, w * 20, (y0, y1) =>
            {
                for (int y = y0; y < y1; y++) { float py = y + 0.5f; int row = y * w; for (int x = 0; x < w; x++) d[row + x] = Background(x + 0.5f, py); }
            });
            return out_;
        }
    }

    /// <summary>Guide coordinate of a pixel: (u, φ).</summary>
    public static (float u, float phi) Coordinate(GradientGesture g, float x, float y)
    {
        switch (g.Shape)
        {
            case GradientShape.Line:
            {
                float dx = (float)(g.X2 - g.X), dy = (float)(g.Y2 - g.Y);
                float l2 = Math.Max(dx * dx + dy * dy, 1e-6f);
                return (((x - (float)g.X) * dx + (y - (float)g.Y) * dy) / l2, 0);
            }
            case GradientShape.Ellipse:
            {
                float ct = (float)Math.Cos(g.Angle), st = (float)Math.Sin(g.Angle);
                float dx = x - (float)g.X, dy = y - (float)g.Y;
                float xr = (dx * ct + dy * st) / (float)Math.Max(g.A, 1), yr = (-dx * st + dy * ct) / (float)Math.Max(g.B, 1);
                return (MathF.Sqrt(xr * xr + yr * yr), MathF.Atan2(yr, xr));
            }
            default: return (0, 0);
        }
    }

    static int PolynomialTerms(int degree) => (degree + 1) * (degree + 2) / 2;

    internal static float Polynomial(float[] c, int degree, float nx, float ny)
    {
        float v = 0; int k = 0; float xi = 1;
        for (int i = 0; i <= degree; i++)
        {
            float yj = 1;
            for (int j = 0; j <= degree - i; j++) { v += c[k] * xi * yj; yj *= ny; k++; }
            xi *= nx;
        }
        return v;
    }

    /// <summary>Robust (2.5σ-clipped, 3 rounds) least-squares polynomial in normalised coordinates.</summary>
    static float[]? FitPolynomial(float[] x, float[] y, float[] v, int degree, int width, int height, bool[] use)
    {
        int m = PolynomialTerms(degree), n = v.Length;
        if (n < 2 * m) return null;
        var coef = new float[m]; var row = new double[m];
        float fw = width, fh = height;
        for (int round = 0; round < 3; round++)
        {
            var ata = new double[m * m]; var atb = new double[m];
            for (int s = 0; s < n; s++)
            {
                if (!use[s]) continue;
                double nx = x[s] / fw, ny = y[s] / fh, z = v[s];
                int k = 0; double xi = 1;
                for (int i = 0; i <= degree; i++) { double yj = 1; for (int j = 0; j <= degree - i; j++) { row[k++] = xi * yj; yj *= ny; } xi *= nx; }
                for (int i = 0; i < m; i++) { atb[i] += row[i] * z; for (int j = i; j < m; j++) ata[i * m + j] += row[i] * row[j]; }
            }
            for (int i = 0; i < m; i++) for (int j = 0; j < i; j++) ata[i * m + j] = ata[j * m + i];
            var c = Solve(ata, atb, m);
            if (c == null) return null;
            coef = c;
            if (round == 2) break;
            var res = new List<float>(n / 4 + 1);
            for (int s = 0; s < n; s += 4) if (use[s]) res.Add(Math.Abs(v[s] - Polynomial(coef, degree, x[s] / fw, y[s] / fh)));
            if (res.Count < 8) break;
            var ra = res.ToArray(); Array.Sort(ra);
            float sigma = 1.4826f * Statistics.Percentile(ra, 0.5);
            if (!(sigma > 0)) break;
            float limit = 2.5f * sigma;
            for (int s = 0; s < n; s++) if (use[s] && Math.Abs(v[s] - Polynomial(coef, degree, x[s] / fw, y[s] / fh)) > limit) use[s] = false;
        }
        return coef;
    }

    /// <summary>Gaussian elimination with partial pivoting for a small dense system.</summary>
    internal static float[]? Solve(double[] a, double[] b, int n)
    {
        var m = (double[])a.Clone(); var v = (double[])b.Clone();
        for (int col = 0; col < n; col++)
        {
            int piv = col;
            for (int r = col + 1; r < n; r++) if (Math.Abs(m[r * n + col]) > Math.Abs(m[piv * n + col])) piv = r;
            if (Math.Abs(m[piv * n + col]) <= 1e-18) return null;
            if (piv != col) { for (int j = 0; j < n; j++) (m[col * n + j], m[piv * n + j]) = (m[piv * n + j], m[col * n + j]); (v[col], v[piv]) = (v[piv], v[col]); }
            for (int r = col + 1; r < n; r++)
            {
                double f = m[r * n + col] / m[col * n + col];
                if (f == 0) continue;
                for (int j = col; j < n; j++) m[r * n + j] -= f * m[col * n + j];
                v[r] -= f * v[col];
            }
        }
        var x = new double[n];
        for (int r = n - 1; r >= 0; r--)
        {
            double s = v[r];
            for (int j = r + 1; j < n; j++) s -= m[r * n + j] * x[j];
            x[r] = s / m[r * n + r];
        }
        return x.Select(d => (float)d).ToArray();
    }

    /// <summary>Profile (+ residual polynomial) for a given guide: the two are alternated so each explains what the other leaves.</summary>
    static (Profile? profile, float[] poly, int degree)? FitModel(GradientGesture gesture, Samples s, GradientSpec spec, int iterations)
    {
        int n = s.Count;
        if (gesture.Shape == GradientShape.Polynomial)
        {
            var use = new bool[n]; Array.Fill(use, true);
            int d = Math.Clamp(spec.Degree, 1, 6);
            var c = FitPolynomial(s.X, s.Y, s.V, d, s.Width, s.Height, use);
            return c == null ? null : (null, c, d);
        }
        var u = new float[n]; var phi = new float[n];
        for (int i = 0; i < n; i++) (u[i], phi[i]) = Coordinate(gesture, s.X[i], s.Y[i]);
        int sectors = gesture.Shape == GradientShape.Ellipse ? Math.Max(1, spec.Sectors) : 1;
        int rd = spec.Residual.Degree();
        float[] poly = [];
        Profile? prof = null;
        var work = new float[n];
        float fw = s.Width, fh = s.Height;
        int iters = rd > 0 ? iterations : 1;
        for (int it = 0; it < iters; it++)
        {
            if (poly.Length == 0) Array.Copy(s.V, work, n); else for (int i = 0; i < n; i++) work[i] = s.V[i] - Polynomial(poly, rd, s.X[i] / fw, s.Y[i] / fh);
            var p = Profile.Fit(u, phi, work, sectors, spec.Smoothness);
            if (p == null) return null;
            prof = p;
            if (rd == 0 || it >= iterations) break;
            for (int i = 0; i < n; i++) work[i] = s.V[i] - p.Eval(u[i], phi[i]);
            var use = new bool[n]; Array.Fill(use, true);
            var c = FitPolynomial(s.X, s.Y, work, rd, s.Width, s.Height, use);
            if (c == null) break;
            poly = c;
        }
        return (prof, poly, rd);
    }

    /// <summary>Mean Huber loss of the model residuals (k = 2 sample noises).</summary>
    static double Cost(GradientGesture gesture, Samples s, GradientSpec spec)
    {
        var fm = FitModel(gesture, s, spec, 2);
        if (fm == null) return double.MaxValue;
        var (prof, poly, rd) = fm.Value;
        float k = 2 * s.Noise;
        double total = 0;
        float fw = s.Width, fh = s.Height;
        for (int i = 0; i < s.Count; i++)
        {
            float m = 0;
            if (prof != null) { var (u, phi) = Coordinate(gesture, s.X[i], s.Y[i]); m = prof.Eval(u, phi); }
            if (poly.Length > 0) m += Polynomial(poly, rd, s.X[i] / fw, s.Y[i] / fh);
            float r = Math.Abs(s.V[i] - m);
            total += r < k ? 0.5 * r * r : k * (r - 0.5 * k);
        }
        return total / Math.Max(s.Count, 1);
    }

    /// <summary>Nelder–Mead minimisation of f from start with an initial simplex of the given steps.</summary>
    static (double[] point, double value) NelderMead(double[] start, double[] steps, int maxIterations, double tolerance, Func<double[], double> f)
    {
        int n = start.Length;
        var simplex = new List<double[]> { (double[])start.Clone() };
        for (int i = 0; i < n; i++) { var p = (double[])start.Clone(); p[i] += steps[i]; simplex.Add(p); }
        var values = simplex.Select(f).ToList();
        for (int iter = 0; iter < maxIterations; iter++)
        {
            var order = Enumerable.Range(0, n + 1).OrderBy(i => values[i]).ToArray();
            simplex = order.Select(i => simplex[i]).ToList(); values = order.Select(i => values[i]).ToList();
            double spread = 0;
            for (int i = 1; i <= n; i++) for (int j = 0; j < n; j++) spread = Math.Max(spread, Math.Abs(simplex[i][j] - simplex[0][j]) / Math.Max(Math.Abs(steps[j]), 1e-12));
            if (spread < tolerance) break;
            var centroid = new double[n];
            for (int i = 0; i < n; i++) for (int j = 0; j < n; j++) centroid[j] += simplex[i][j] / n;
            double[] Point(double t) { var p = new double[n]; for (int j = 0; j < n; j++) p[j] = centroid[j] + t * (simplex[n][j] - centroid[j]); return p; }
            var xr = Point(-1); double fr = f(xr);
            if (fr < values[0])
            {
                var xe = Point(-2); double fe = f(xe);
                if (fe < fr) { simplex[n] = xe; values[n] = fe; } else { simplex[n] = xr; values[n] = fr; }
            }
            else if (fr < values[n - 1]) { simplex[n] = xr; values[n] = fr; }
            else
            {
                var xc = fr < values[n] ? Point(-0.5) : Point(0.5);
                double fc = f(xc);
                if (fc < Math.Min(fr, values[n])) { simplex[n] = xc; values[n] = fc; }
                else
                    for (int i = 1; i <= n; i++)
                    {
                        for (int j = 0; j < n; j++) simplex[i][j] = simplex[0][j] + 0.5 * (simplex[i][j] - simplex[0][j]);
                        values[i] = f(simplex[i]);
                    }
            }
        }
        int best = 0;
        for (int i = 1; i <= n; i++) if (values[i] < values[best]) best = i;
        return (simplex[best], values[best]);
    }

    /// <summary>The guide's geometry optimised from the samples: the line's direction; the ellipse's centre, axis ratio and angle (several starting centres, the best kept).</summary>
    public static GradientGesture Refine(GradientGesture gesture, Samples full, GradientSpec spec)
    {
        var s = full.Subsampled(3000);
        if (s.Count < 50) return gesture;
        double scale = Math.Max(full.Width, full.Height);
        switch (gesture.Shape)
        {
            case GradientShape.Polynomial: return gesture;
            case GradientShape.Line:
            {
                double l = Math.Max(gesture.LineLength, 1), th0 = Math.Atan2(gesture.Y2 - gesture.Y, gesture.X2 - gesture.X);
                GradientGesture Make(double[] p) { var g = gesture.Clone(); g.X2 = g.X + l * Math.Cos(th0 + p[0]); g.Y2 = g.Y + l * Math.Sin(th0 + p[0]); return g; }
                var (p, _) = NelderMead([0], [0.3], 60, 1e-3, q => Cost(Make(q), s, spec));
                return Make(p);
            }
            default:
            {
                double a0 = Math.Max(gesture.A, 1), b0 = Math.Max(gesture.B, 1);
                GradientGesture Make((double, double) c, double[] p)
                {
                    var g = gesture.Clone();
                    g.X = c.Item1 + p[0] * scale; g.Y = c.Item2 + p[1] * scale;
                    g.A = a0; g.B = a0 * (b0 / a0) * Math.Exp(p[2]); g.Angle = gesture.Angle + p[3];
                    return g;
                }
                double ct = Math.Cos(gesture.Angle), st = Math.Sin(gesture.Angle);
                (double, double)[] starts =
                [
                    (gesture.X, gesture.Y),
                    (gesture.X + 0.6 * a0 * ct, gesture.Y + 0.6 * a0 * st), (gesture.X - 0.6 * a0 * ct, gesture.Y - 0.6 * a0 * st),
                    (gesture.X - 0.6 * b0 * st, gesture.Y + 0.6 * b0 * ct), (gesture.X + 0.6 * b0 * st, gesture.Y - 0.6 * b0 * ct),
                ];
                var results = new (GradientGesture g, double v)[starts.Length];
                Parallel.For(0, starts.Length, i =>
                {
                    var c = starts[i];
                    var (p, v) = NelderMead([0, 0, 0, 0], [0.08, 0.08, 0.3, 0.3], 150, 2e-2, q => Cost(Make(c, q), s, spec));
                    results[i] = (Make(c, p), v);
                });
                var best = results.OrderBy(r => r.v).First().g;
                if (best.B > best.A) { (best.A, best.B) = (best.B, best.A); best.Angle += Math.PI / 2; }
                best.Angle = Math.Atan2(Math.Sin(best.Angle), Math.Cos(best.Angle));
                return best;
            }
        }
    }

    /// <summary>Fits the model of <paramref name="spec"/> to the samples (refining the guide when asked). Null when the samples cannot support it.</summary>
    public static Fit? FitSamples(Samples s, GradientSpec spec, IReadOnlyList<GradientRegion>? protect = null)
    {
        if (s.Count < 50) return null;
        if (spec.Gesture.Shape == GradientShape.Samples) return FitBackgroundSamples(s, spec, protect);
        var g = spec.Gesture.Clone();
        if (g.Shape == GradientShape.Line && g.LineLength < 1) return null;
        if (g.Shape == GradientShape.Ellipse) { g.A = Math.Max(g.A, 1); g.B = Math.Max(g.B, 1); }
        if (spec.Refine) g = Refine(g, s, spec);
        var fm = FitModel(g, s, spec, 3);
        if (fm == null) return null;
        var (prof, poly, rd) = fm.Value;
        var fit = new Fit { Gesture = g, Spec = spec, Profile = prof, Poly = poly, PolyDegree = rd, Width = s.Width, Height = s.Height };
        var model = new float[s.Count];
        double sq = 0;
        for (int i = 0; i < s.Count; i++) { model[i] = fit.Background(s.X[i], s.Y[i]); double r = s.V[i] - model[i]; sq += r * r; }
        fit.ResidualSigma = (float)Math.Sqrt(sq / s.Count) / s.Noise;
        Array.Sort(model);
        fit.Reference = spec.Mode == GradientMode.Divide ? Statistics.Percentile(model, 0.995) : Statistics.Percentile(model, 0.5);
        return fit;
    }

    /// <summary>Surface through the background samples; the reference level is read off the surface like the other models.</summary>
    static Fit? FitBackgroundSamples(Samples s, GradientSpec spec, IReadOnlyList<GradientRegion>? protect)
    {
        double radius = spec.ResolvedSampleRadius(s.Width, s.Height);
        var sp = spec;
        if (sp.Samples.Count == 0)
        {
            if (sp.GridPerRow <= 0) return null;   // cleared by hand: no model until samples are placed
            sp = spec.Clone();
            sp.Samples = BackgroundSamples.Grid(s, sp.GridPerRow, radius, sp.GridTolerance, protect);
        }
        var surface = BackgroundSamples.Fit(sp.Samples, radius, s, sp.Surface, sp.AutoSmoothing ? null : sp.SampleSmoothing);
        if (surface == null) return null;
        var fit = new Fit { Gesture = sp.Gesture, Spec = sp, Profile = null, Poly = [], PolyDegree = 0, ResidualSigma = surface.ResidualSigma, Width = s.Width, Height = s.Height, Surface = surface };
        var model = surface.Plane.ToArray();
        Array.Sort(model);
        fit.Reference = sp.Mode == GradientMode.Divide ? Statistics.Percentile(model, 0.995) : Statistics.Percentile(model, 0.5);
        return fit;
    }

    /// <summary>Corrected copy of <paramref name="image"/> (zero pixels stay zero).</summary>
    public static PlanarImage Apply(PlanarImage image, Fit fit)
    {
        int w = image.Width, h = image.Height;
        var out_ = PlanarImage.Uninitialized(w, h);
        bool divide = fit.Spec.Mode == GradientMode.Divide;
        float refv = fit.Reference;
        var src = image.Data; var dst = out_.Data;
        Par.Rows(h, w * 20, (y0, y1) =>
        {
            for (int y = y0; y < y1; y++)
            {
                float py = y + 0.5f; int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float v = src[row + x];
                    if (v == 0) { dst[row + x] = 0; continue; }
                    float b = fit.Background(x + 0.5f, py);
                    if (divide) { float t = refv > 0 ? Math.Max(b / refv, MinimumTransmission) : 1; dst[row + x] = v / t; }
                    else dst[row + x] = v - b + refv;
                }
            }
        });
        return out_;
    }

    /// <summary>Samples, fit and correction in one go (null when nothing can be done).</summary>
    public static (PlanarImage image, Fit fit)? Correct(PlanarImage image, GradientSpec spec, IReadOnlyList<GradientRegion>? protect = null)
    {
        var s = GetSamples(image, protect);
        var f = FitSamples(s, spec, protect);
        return f == null ? null : (Apply(image, f), f);
    }

    /// <summary>A channel's correction (the same instance when it has no model or the model cannot be fitted).</summary>
    public static PlanarImage Correct(PlanarImage image, ChannelKind kind, GradientSettings settings)
    {
        var spec = settings.SpecFor(kind);
        if (spec == null) return image;
        return Correct(image, spec, settings.Protect)?.image ?? image;
    }
}
