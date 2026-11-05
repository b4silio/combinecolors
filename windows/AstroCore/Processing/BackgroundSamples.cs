namespace AstroCore;

/// <summary>
/// A background sample: a disc placed on sky (by the user or the grid generator) whose robust level
/// anchors the background surface. Full-resolution, oriented coordinates.
/// </summary>
public sealed partial class BackgroundSample : IEquatable<BackgroundSample>
{
    public Guid Id;
    public double X, Y;

    public BackgroundSample(double x, double y, Guid? id = null) { X = x; Y = y; Id = id ?? Guid.NewGuid(); }

    public BackgroundSample Clone() => (BackgroundSample)MemberwiseClone();

    public BackgroundSample Applying(OrientationOp op, int imageWidth, int imageHeight)
    {
        var s = Clone();
        switch (op)
        {
            case OrientationOp.RotateClockwise: s.X = imageHeight - Y; s.Y = X; break;
            case OrientationOp.FlipHorizontal: s.X = imageWidth - X; break;
            case OrientationOp.FlipVertical: s.Y = imageHeight - Y; break;
        }
        return s;
    }

    /// <summary>The sample after a fine rotation of the image by <paramref name="degrees"/> (<see cref="FineRotation"/>).</summary>
    public BackgroundSample RotatedByDegrees(double degrees, int imageWidth, int imageHeight)
    {
        var s = Clone();
        (s.X, s.Y) = FineRotation.Rotate(X, Y, degrees, imageWidth, imageHeight);
        return s;
    }

    public bool Equals(BackgroundSample? o) => o != null && Id == o.Id && X == o.X && Y == o.Y;
    public override bool Equals(object? obj) => Equals(obj as BackgroundSample);
    public override int GetHashCode() => HashCode.Combine(Id, X, Y);
}

/// <summary>Surface fitted through the background samples.</summary>
public enum SampleSurface
{
    /// <summary>Thin-plate spline with smoothing (DBE-like): the minimum-bending surface through the samples, relaxed by the smoothing.</summary>
    Spline,
    /// <summary>Ordinary kriging with a Gaussian covariance (GraXpert-like): a Gaussian-process mean around a constant.</summary>
    Kriging,
}

public static class SampleSurfaces
{
    public static readonly SampleSurface[] All = [SampleSurface.Spline, SampleSurface.Kriging];
    public static string DisplayName(this SampleSurface s) => s == SampleSurface.Spline ? "Spline" : "Kriging";
}

/// <summary>What became of one sample in a fit (shown in the editor).</summary>
public sealed class SampleResult
{
    public Guid Id;
    /// <summary>Robust level measured in the disc (null: not enough sky pixels — on padding, stars or a protected region).</summary>
    public float? Value;
    /// <summary>Level of the fitted surface at the sample.</summary>
    public float Model;
    /// <summary>Weight the fit gave the sample (0…1; below the rejected weight it was treated as not sky).</summary>
    public float Weight;
    public bool IsRejected => Value == null || Weight < BackgroundSamples.RejectedWeight;
}

/// <summary>
/// Background estimation from samples: robust disc statistics on the star-masked cell medians,
/// an automatic grid with nebula rejection, and a smoothing thin-plate spline or Gaussian kriging
/// surface with asymmetric robust reweighting (sky is a lower envelope: samples that sit above the
/// surface — nebulosity, halos — lose their weight faster than samples below it).
/// The same algorithm as the macOS build (see BackgroundSamples.swift).
/// </summary>
public static class BackgroundSamples
{
    /// <summary>Weight below which a sample counts as rejected.</summary>
    public const float RejectedWeight = 0.2f;
    /// <summary>Fewest cells a disc needs to give a level.</summary>
    const int MinimumCells = 6;
    /// <summary>Pixels on the long side of the evaluated surface (bilinear between them).</summary>
    const int SurfaceSize = 512;
    /// <summary>Most samples a fit takes (the dense solve is cubic).</summary>
    const int MaximumSamples = 1500;

    /// <summary>Radius given to samples when the model asks for the automatic one: a disc of a few cells.</summary>
    public static double DefaultRadius(int width, int height) => Math.Max(12, Math.Max(width, height) / 80.0);

    // Disc statistics

    /// <summary>Cell medians grouped by rows of cells, for fast disc look-ups.</summary>
    internal sealed class Index
    {
        readonly GradientRemoval.Samples samples;
        readonly int[] order;
        readonly int[] rowStart;
        readonly float[] rowY;

        public Index(GradientRemoval.Samples s)
        {
            samples = s;
            order = Enumerable.Range(0, s.Count).ToArray();
            Array.Sort(order, (a, b) => s.Y[a] != s.Y[b] ? s.Y[a].CompareTo(s.Y[b]) : s.X[a].CompareTo(s.X[b]));
            var starts = new List<int>(); var ys = new List<float>();
            float last = float.NaN;
            for (int i = 0; i < order.Length; i++)
            {
                float y = s.Y[order[i]];
                if (y != last) { starts.Add(i); ys.Add(y); last = y; }
            }
            rowStart = starts.ToArray(); rowY = ys.ToArray();
        }

        /// <summary>Values of the cells within <paramref name="radius"/> of (x, y).</summary>
        public List<float> Values(float x, float y, float radius)
        {
            var out_ = new List<float>();
            float r2 = radius * radius;
            int lo = 0, hi = rowY.Length;
            while (lo < hi) { int m = (lo + hi) / 2; if (rowY[m] < y - radius) lo = m + 1; else hi = m; }
            int r = lo;
            while (r < rowY.Length && rowY[r] <= y + radius)
            {
                int end = r + 1 < rowStart.Length ? rowStart[r + 1] : order.Length;
                float dy = rowY[r] - y;
                for (int i = rowStart[r]; i < end; i++)
                {
                    int o = order[i];
                    float dx = samples.X[o] - x;
                    if (dx * dx + dy * dy <= r2) out_.Add(samples.V[o]);
                }
                r++;
            }
            return out_;
        }
    }

    public readonly record struct Measurement(float Value, float Scatter, float Skew, int Cells);

    /// <summary>Robust level of a disc: median of the star-masked cell medians inside it.</summary>
    internal static Measurement? Measure(Index index, double x, double y, double radius)
    {
        var list = index.Values((float)x, (float)y, (float)radius);
        if (list.Count < MinimumCells) return null;
        var v = list.ToArray(); Array.Sort(v);
        float med = Statistics.Percentile(v, 0.5);
        float mean = 0; foreach (var t in v) mean += t; mean /= v.Length;
        var dev = v.Select(t => Math.Abs(t - med)).ToArray(); Array.Sort(dev);
        float scatter = 1.4826f * Statistics.Percentile(dev, 0.5);
        return new Measurement(med, scatter, scatter > 0 ? (mean - med) / scatter : 0, v.Length);
    }

    public static Measurement? Measure(GradientRemoval.Samples s, double x, double y, double radius) => Measure(new Index(s), x, y, radius);

    /// <summary>Median of d over the k nearest other points of each point.</summary>
    internal static double[] NeighbourMedians(double[] x, double[] y, double[] d, int k = 8)
    {
        int n = x.Length;
        if (n <= 1) return (double[])d.Clone();
        var out_ = new double[n];
        for (int i = 0; i < n; i++)
        {
            var dist = new List<(double, int)>(n - 1);
            for (int j = 0; j < n; j++) if (j != i) { double dx = x[i] - x[j], dy = y[i] - y[j]; dist.Add((dx * dx + dy * dy, j)); }
            dist.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            var vals = dist.Take(Math.Min(k, dist.Count)).Select(t => d[t.Item2]).ToArray();
            Array.Sort(vals);
            out_[i] = vals.Length % 2 == 1 ? vals[vals.Length / 2] : 0.5 * (vals[vals.Length / 2 - 1] + vals[vals.Length / 2]);
        }
        return out_;
    }

    // Grid

    /// <summary>
    /// A regular grid of samples over the frame, keeping those that look like sky: enough star-free
    /// cells, little structure inside the disc, outside the protected regions, and not standing above
    /// a planar lower-envelope sky model by more than <paramref name="tolerance"/> sample noises.
    /// </summary>
    public static List<BackgroundSample> Grid(GradientRemoval.Samples s, int perRow, double radius, double tolerance, IReadOnlyList<GradientRegion>? protect = null)
    {
        double w = s.Width, h = s.Height;
        int cols = Math.Max(2, Math.Min(perRow, 64));
        double spacing = w / cols;
        int rows = Math.Max(2, (int)Math.Round(h / spacing));
        var index = new Index(s);
        var candidates = new List<(double x, double y, Measurement m)>();
        double shift = Math.Min(radius, spacing / 4);
        (double, double)[] offsets = [(0, 0), (shift, shift), (-shift, shift), (shift, -shift), (-shift, -shift)];
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                double x0 = (c + 0.5) * spacing, y0 = (r + 0.5) * (h / rows);
                (double x, double y, Measurement m)? best = null;
                foreach (var (ox, oy) in offsets)
                {
                    double x = x0 + ox, y = y0 + oy;
                    if (x < radius || y < radius || x > w - radius || y > h - radius) continue;
                    if (protect != null && protect.Any(p => p.Contains(x, y))) continue;
                    var m = Measure(index, x, y, radius);
                    if (m == null) continue;
                    if (best == null || m.Value.Value < best.Value.m.Value) best = (x, y, m.Value);
                }
                if (best != null) candidates.Add(best.Value);
            }
        }
        if (candidates.Count < 6) return candidates.Select(c => new BackgroundSample(c.x, c.y)).ToList();
        float noise = Math.Max(s.Noise, 1e-12f);
        var scatters = candidates.Select(c => c.m.Scatter).ToArray(); Array.Sort(scatters);
        float scatterLimit = Math.Max(3 * noise, Statistics.Percentile(scatters, 0.5) * 2.5f);
        const float skewLimit = 0.35f;
        // Sky model by asymmetric least squares (residuals above weigh 0.02, below 1): the simplest of
        // plane / plane + r² + r⁴ / + elliptical and tilted quadratic terms that explains the sky
        // (a richer one only when it keeps ≥ 1.25× and ≥ 15 % of the grid more samples: vignetting of
        // frames without flats, where a plane kept only the corners). The tolerance unit is the sample
        // noise, or the model's misfit (median − 16th percentile of the residuals) above 5 noises.
        int nc = candidates.Count;
        var use = new bool[nc]; Array.Fill(use, true);
        var xs = candidates.Select(c => (float)c.x).ToArray(); var ys = candidates.Select(c => (float)c.y).ToArray(); var vs = candidates.Select(c => c.m.Value).ToArray();
        float fw = s.Width, fh = s.Height;
        float Model(float[] c, int i) => SkyModel(c, xs[i] / fw, ys[i] / fh);
        (float[] coef, bool[] keep)? Envelope(int terms)
        {
            float[]? coef = null;
            var weights = new double[nc]; Array.Fill(weights, 1.0);
            for (int round = 0; round < 16; round++)
            {
                var c = WeightedSkyModel(xs, ys, vs, weights, fw, fh, terms);
                if (c == null) break;
                coef = c;
                bool changed = false;
                for (int i = 0; i < nc; i++)
                {
                    float r = vs[i] - Model(c, i);
                    double nw = r > 0 ? 0.02 : 1;
                    if (nw != weights[i]) { changed = true; weights[i] = nw; }
                }
                if (!changed) break;
            }
            if (coef == null) return null;
            var res = new float[nc];
            for (int i = 0; i < nc; i++) res[i] = vs[i] - Model(coef, i);
            var near = res.Where(t => Math.Abs(t) <= 3 * noise).ToArray(); Array.Sort(near);
            if (near.Length >= 4)
            {
                float sh = Statistics.Percentile(near, 0.5);
                coef[0] += sh;
                for (int i = 0; i < nc; i++) res[i] -= sh;
            }
            var sorted = (float[])res.Clone(); Array.Sort(sorted);
            float misfit = Statistics.Percentile(sorted, 0.5) - Statistics.Percentile(sorted, 0.16);
            float scale = misfit > 5 * noise ? misfit : noise;
            float keep = (float)tolerance * scale;
            return (coef, res.Select(r => r <= keep && r >= -4 * keep).ToArray());
        }
        (float[] coef, bool[] keep)? chosen = null;
        foreach (int terms in new[] { 3, 5, 7 })
        {
            if (nc <= terms * 2) continue;
            var e = Envelope(terms);
            if (e == null) continue;
            int kept = e.Value.keep.Count(k => k);
            if (chosen != null)
            {
                int before = chosen.Value.keep.Count(k => k);
                if (kept < 1.25 * before || kept - before < Math.Max(3, nc * 15 / 100)) continue;
            }
            chosen = e;
        }
        float[]? coef = chosen?.coef;
        if (chosen != null) use = chosen.Value.keep;
        // Spatial consistency against the eight nearest neighbours (after the trend).
        if (coef != null)
        {
            var c = coef;
            var res = new double[nc];
            for (int i = 0; i < nc; i++) res[i] = vs[i] - Model(c, i);
            var nb = NeighbourMedians(candidates.Select(t => t.x).ToArray(), candidates.Select(t => t.y).ToArray(), res);
            var diffs = new double[nc];
            for (int i = 0; i < nc; i++) diffs[i] = res[i] - nb[i];
            var dev = diffs.Select(Math.Abs).ToArray(); Array.Sort(dev);
            double sigma = Math.Max(noise, 1.4826 * dev[dev.Length / 2]);
            for (int i = 0; i < nc; i++) if (diffs[i] > tolerance * sigma) use[i] = false;
        }
        var out_ = new List<BackgroundSample>();
        for (int i = 0; i < nc; i++)
            if (use[i] && candidates[i].m.Scatter <= scatterLimit && candidates[i].m.Skew <= skewLimit) out_.Add(new BackgroundSample(candidates[i].x, candidates[i].y));
        return out_;
    }

    // Surface

    public sealed class Surface
    {
        /// <summary>The surface over the frame at a coarse resolution (Scale surface pixels per image pixel).</summary>
        public required PlanarImage Plane;
        public float Scale;
        public required List<SampleResult> Results;
        /// <summary>Robust noise of the kept samples around the surface (leave-one-out), in units of the sample noise.</summary>
        public float ResidualSigma;
        /// <summary>Smoothing used (the one chosen by cross-validation when automatic).</summary>
        public double Smoothing;

        /// <summary>Bilinear value at a full-resolution point.</summary>
        public float Value(float x, float y)
        {
            int w = Plane.Width, h = Plane.Height;
            float fx = Math.Min(Math.Max(x * Scale - 0.5f, 0), w - 1), fy = Math.Min(Math.Max(y * Scale - 0.5f, 0), h - 1);
            int x0 = (int)fx, y0 = (int)fy;
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            float tx = fx - x0, ty = fy - y0;
            var p = Plane.Data;
            float a = p[y0 * w + x0] + (p[y0 * w + x1] - p[y0 * w + x0]) * tx;
            float b = p[y1 * w + x0] + (p[y1 * w + x1] - p[y1 * w + x0]) * tx;
            return a + (b - a) * ty;
        }
    }

    /// <summary>
    /// Fits the surface through the samples (measured on <paramref name="s"/>, discs of <paramref name="radius"/>).
    /// smoothing 0…1 (0 follows the samples closely, 1 is a very stiff surface); null picks it by
    /// generalised cross-validation.
    /// </summary>
    public static Surface? Fit(IReadOnlyList<BackgroundSample> points, double radius, GradientRemoval.Samples s, SampleSurface kind, double? smoothing)
    {
        var index = new Index(s);
        var results = new List<SampleResult>();
        var px = new List<double>(); var py = new List<double>(); var pv = new List<double>(); var pw = new List<double>();
        var ids = new List<int>();
        double noise = Math.Max(s.Noise, 1e-12f);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var m = Measure(index, p.X, p.Y, radius);
            if (m != null && px.Count < MaximumSamples)
            {
                results.Add(new SampleResult { Id = p.Id, Value = m.Value.Value, Model = m.Value.Value, Weight = 1 });
                px.Add(p.X); py.Add(p.Y); pv.Add(m.Value.Value);
                double q = m.Value.Scatter / noise / 3;
                pw.Add(1 / (1 + q * q));
                ids.Add(i);
            }
            else results.Add(new SampleResult { Id = p.Id, Value = null, Model = 0, Weight = 0 });
        }
        int n = px.Count;
        if (n < 4) return null;
        double L = Math.Max(s.Width, s.Height);
        var u = px.Select(t => t / L).ToArray(); var v = py.Select(t => t / L).ToArray();
        double mean = pv.Sum() / n;
        var z = pv.Select(t => t - mean).ToArray();
        double spacing = TypicalSpacing(u, v);
        int np = kind == SampleSurface.Spline ? 3 : 1;
        var model = new Model(kind, u, v, np, spacing);
        var pw0 = pw.ToArray();
        if (n >= 6)
        {
            var nb = NeighbourMedians(u, v, z, 4);
            var diffs = new double[n];
            for (int i = 0; i < n; i++) diffs[i] = z[i] - nb[i];
            var dev = diffs.Select(Math.Abs).ToArray(); Array.Sort(dev);
            double sg = Math.Max(noise, 1.4826 * dev[dev.Length / 2]);
            for (int i = 0; i < n; i++) if (diffs[i] > 4 * sg) pw0[i] *= 0.05;
        }
        var weights = (double[])pw0.Clone();
        Model.Solution? solution = null;
        double chosen = smoothing ?? 0.5;
        for (int round = 0; round < 4; round++)
        {
            if (smoothing == null)
            {
                double bestScore = double.PositiveInfinity, bestValue = 0.5;
                for (double t = 0; t <= 1.0 + 1e-9; t += 0.125)
                {
                    var sol = model.Solve(z, weights, t);
                    if (sol == null) continue;
                    double score = 0, cnt = 0;
                    for (int i = 0; i < n; i++) if (weights[i] >= RejectedWeight) { score += sol.Loo[i] * sol.Loo[i]; cnt += 1; }
                    score /= Math.Max(cnt, 1);
                    if (score < bestScore) { bestScore = score; bestValue = t; }
                }
                chosen = bestValue;
            }
            var solved = model.Solve(z, weights, chosen);
            if (solved == null) return null;
            solution = solved;
            if (round == 3) break;
            var kept = Enumerable.Range(0, n).Where(i => weights[i] >= RejectedWeight).ToList();
            var dev2 = kept.Select(i => Math.Abs(solved.Loo[i])).ToArray(); Array.Sort(dev2);
            double sigma = Math.Max(noise, dev2.Length >= 4 ? 1.4826 * dev2[dev2.Length / 2] : noise);
            for (int i = 0; i < n; i++)
            {
                double r = solved.Loo[i];
                double k = r > 0 ? 1.5 * sigma : 3.5 * sigma;
                double huber = Math.Abs(r) <= k ? 1.0 : k / Math.Abs(r);
                weights[i] = pw0[i] * huber * huber;
            }
        }
        if (solution == null) return null;
        var sol2 = solution;
        double sq = 0; int cnt2 = 0;
        for (int k = 0; k < ids.Count; k++)
        {
            int i = ids[k];
            double m = sol2.Fitted[k] + mean;
            results[i].Model = (float)m;
            results[i].Weight = (float)weights[k];
            if (weights[k] >= RejectedWeight) { double r = sol2.Loo[k]; sq += r * r; cnt2++; }
        }
        float residualSigma = (float)(Math.Sqrt(sq / Math.Max(cnt2, 1)) / noise);
        float scale = SurfaceSize / (float)Math.Max(s.Width, s.Height);
        int cw = Math.Max(2, (int)Math.Round(s.Width * scale)), ch = Math.Max(2, (int)Math.Round(s.Height * scale));
        var plane = PlanarImage.Uninitialized(cw, ch);
        var d = plane.Data;
        Par.Rows(ch, cw * (long)n * 8, (y0, y1) =>
        {
            for (int yy = y0; yy < y1; yy++)
            {
                double pyy = (yy + 0.5) / scale / L;
                for (int xx = 0; xx < cw; xx++)
                {
                    double pxx = (xx + 0.5) / scale / L;
                    d[yy * cw + xx] = (float)(model.Evaluate(sol2, pxx, pyy) + mean);
                }
            }
        }, 1);
        return new Surface { Plane = plane, Scale = scale, Results = results, ResidualSigma = residualSigma, Smoothing = chosen };
    }

    /// <summary>The radial-basis model: kernel matrix, polynomial part, and a solver that also returns the leave-one-out residuals through the hat matrix.</summary>
    sealed class Model
    {
        readonly SampleSurface kind;
        readonly double[] u, v;
        readonly int np;
        readonly double spacing;
        double[] K = [];
        double kMean = 1;
        double ell;
        double built = double.NaN;

        public Model(SampleSurface kind, double[] u, double[] v, int np, double spacing) { this.kind = kind; this.u = u; this.v = v; this.np = np; this.spacing = spacing; }

        double Length(double smoothing) => spacing * (1.5 + 2.5 * Math.Min(Math.Max(smoothing, 0), 1));

        double Kernel(double r2)
        {
            if (kind == SampleSurface.Kriging) return Math.Exp(-r2 / (2 * ell * ell));
            return r2 > 1e-24 ? 0.5 * r2 * Math.Log(r2) : 0;
        }

        void Build(double smoothing)
        {
            double l = kind == SampleSurface.Kriging ? Length(smoothing) : 0;
            if (built == smoothing || (kind == SampleSurface.Spline && K.Length > 0)) return;
            ell = l;
            int n = u.Length;
            var k2 = new double[n * n];
            double sum = 0;
            for (int i = 0; i < n; i++)
                for (int j = 0; j < n; j++)
                {
                    double dx = u[i] - u[j], dy = v[i] - v[j];
                    double k = Kernel(dx * dx + dy * dy);
                    k2[i * n + j] = k; sum += Math.Abs(k);
                }
            K = k2;
            kMean = Math.Max(sum / (n * (double)n), 1e-12);
            built = smoothing;
        }

        double Lambda(double smoothing) => kMean * Math.Pow(10, -4 + 4 * Math.Min(Math.Max(smoothing, 0), 1));

        public sealed class Solution
        {
            public required double[] Coefficients, Fitted, Loo;
            public double Ell;
        }

        public Solution? Solve(double[] z, double[] weights, double smoothing)
        {
            Build(smoothing);
            int n = u.Length, dim = n + np;
            double lambda = Lambda(smoothing);
            var A = new double[dim * dim];
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++) A[i * dim + j] = K[i * n + j];
                A[i * dim + i] += weights[i] < RejectedWeight ? kMean * 1e6 : lambda / weights[i];
                A[i * dim + n] = 1; A[n * dim + i] = 1;
                if (np == 3) { A[i * dim + n + 1] = u[i]; A[(n + 1) * dim + i] = u[i]; A[i * dim + n + 2] = v[i]; A[(n + 2) * dim + i] = v[i]; }
            }
            var lu = LU.Decompose(A, dim);
            if (lu == null) return null;
            var rhs = new double[dim];
            for (int i = 0; i < n; i++) rhs[i] = z[i];
            var c = lu.Solve(rhs);
            var fitted = new double[n]; var loo = new double[n];
            var e = new double[dim];
            for (int i = 0; i < n; i++)
            {
                double f = c[n];
                if (np == 3) f += c[n + 1] * u[i] + c[n + 2] * v[i];
                for (int j = 0; j < n; j++) f += K[i * n + j] * c[j];
                fitted[i] = f;
                e[i] = 1;
                var col = lu.Solve(e);
                e[i] = 0;
                double h = col[n];
                if (np == 3) h += col[n + 1] * u[i] + col[n + 2] * v[i];
                for (int j = 0; j < n; j++) h += K[i * n + j] * col[j];
                double r = z[i] - f;
                loo[i] = r / Math.Max(1 - Math.Min(h, 0.999), 1e-3);
            }
            return new Solution { Coefficients = c, Fitted = fitted, Loo = loo, Ell = ell };
        }

        public double Evaluate(Solution sol, double x, double y)
        {
            int n = u.Length; var c = sol.Coefficients;
            double s = c[n];
            if (np == 3) s += c[n + 1] * x + c[n + 2] * y;
            double l2 = 2 * sol.Ell * sol.Ell;
            for (int i = 0; i < n; i++)
            {
                double dx = x - u[i], dy = y - v[i];
                double r2 = dx * dx + dy * dy;
                s += c[i] * (kind == SampleSurface.Kriging ? Math.Exp(-r2 / l2) : (r2 > 1e-24 ? 0.5 * r2 * Math.Log(r2) : 0));
            }
            return s;
        }
    }

    /// <summary>LU decomposition with partial pivoting (Doolittle), for several right-hand sides.</summary>
    sealed class LU
    {
        readonly double[] m;
        readonly int[] perm;
        readonly int n;

        LU(double[] m, int[] perm, int n) { this.m = m; this.perm = perm; this.n = n; }

        public static LU? Decompose(double[] a, int n)
        {
            var m = (double[])a.Clone();
            var perm = Enumerable.Range(0, n).ToArray();
            for (int col = 0; col < n; col++)
            {
                int piv = col; double best = Math.Abs(m[col * n + col]);
                for (int r = col + 1; r < n; r++) { double t = Math.Abs(m[r * n + col]); if (t > best) { best = t; piv = r; } }
                if (best <= 1e-14) return null;
                if (piv != col)
                {
                    for (int j = 0; j < n; j++) (m[col * n + j], m[piv * n + j]) = (m[piv * n + j], m[col * n + j]);
                    (perm[col], perm[piv]) = (perm[piv], perm[col]);
                }
                double d = m[col * n + col];
                for (int r = col + 1; r < n; r++)
                {
                    double f = m[r * n + col] / d;
                    m[r * n + col] = f;
                    if (f == 0) continue;
                    int rowR = r * n, rowC = col * n;
                    for (int j = col + 1; j < n; j++) m[rowR + j] -= f * m[rowC + j];
                }
            }
            return new LU(m, perm, n);
        }

        public double[] Solve(double[] b)
        {
            var x = new double[n];
            for (int i = 0; i < n; i++) x[i] = b[perm[i]];
            for (int i = 0; i < n; i++) { double s = x[i]; for (int j = 0; j < i; j++) s -= m[i * n + j] * x[j]; x[i] = s; }
            for (int i = n - 1; i >= 0; i--) { double s = x[i]; for (int j = i + 1; j < n; j++) s -= m[i * n + j] * x[j]; x[i] = s / m[i * n + i]; }
            return x;
        }
    }

    /// <summary>The grid's sky model at normalised coordinates: plane (3 terms), + r² and r⁴ from the frame
    /// centre (5), + the elliptical and tilted quadratic terms (7); c.Length selects the model.</summary>
    internal static float SkyModel(float[] c, float x, float y)
    {
        float v = c[0] + c[1] * y + c[2] * x;
        if (c.Length >= 5)
        {
            float dx = x - 0.5f, dy = y - 0.5f, r2 = dx * dx + dy * dy;
            v += c[3] * r2 + c[4] * r2 * r2;
            if (c.Length >= 7) v += c[5] * (dx * dx - dy * dy) + c[6] * dx * dy;
        }
        return v;
    }

    internal static float[]? WeightedSkyModel(float[] x, float[] y, float[] v, double[] w, float width, float height, int terms)
    {
        int n = terms;
        var a = new double[n * n]; var b = new double[n];
        var row = new double[n];
        for (int i = 0; i < v.Length; i++)
        {
            double nx = x[i] / width, ny = y[i] / height;
            row[0] = 1; row[1] = ny; row[2] = nx;
            if (n >= 5)
            {
                double dx = nx - 0.5, dy = ny - 0.5, r2 = dx * dx + dy * dy;
                row[3] = r2; row[4] = r2 * r2;
                if (n >= 7) { row[5] = dx * dx - dy * dy; row[6] = dx * dy; }
            }
            double z = v[i], wi = w[i];
            for (int p = 0; p < n; p++) { b[p] += wi * row[p] * z; for (int q = 0; q < n; q++) a[p * n + q] += wi * row[p] * row[q]; }
        }
        return GradientRemoval.Solve(a, b, n);
    }

    /// <summary>Weighted least-squares plane c0 + c1·(y/h) + c2·(x/w) (the polynomial's coefficient order).</summary>
    internal static float[]? WeightedPlane(float[] x, float[] y, float[] v, double[] w, float width, float height)
    {
        var a = new double[9]; var b = new double[3];
        for (int i = 0; i < v.Length; i++)
        {
            double[] row = [1.0, y[i] / height, x[i] / width]; double z = v[i], wi = w[i];
            for (int p = 0; p < 3; p++) { b[p] += wi * row[p] * z; for (int q = 0; q < 3; q++) a[p * 3 + q] += wi * row[p] * row[q]; }
        }
        return GradientRemoval.Solve(a, b, 3);
    }

    /// <summary>Median distance to the nearest other sample (normalised coordinates).</summary>
    static double TypicalSpacing(double[] u, double[] v)
    {
        int n = u.Length;
        if (n < 2) return 0.1;
        var d = new double[n];
        for (int i = 0; i < n; i++)
        {
            double best = double.PositiveInfinity;
            for (int j = 0; j < n; j++) if (j != i) { double dx = u[i] - u[j], dy = v[i] - v[j]; best = Math.Min(best, dx * dx + dy * dy); }
            d[i] = Math.Sqrt(best);
        }
        Array.Sort(d);
        return Math.Max(d[n / 2], 1e-3);
    }
}
