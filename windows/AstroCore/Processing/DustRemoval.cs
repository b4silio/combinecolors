namespace AstroCore;

/// <summary>How the shadow of a spot is modelled from the pixels under it.</summary>
public enum DustModel
{
    /// <summary>
    /// Free-form 2-D transmission (star-masked smoothing of the local ratio): follows elongated or
    /// irregular motes and the fringe pattern of a mote, but also flattens nebular detail under the
    /// spot at the smoothing scale. The default.
    /// </summary>
    Surface,
    /// <summary>
    /// Transmission as a function of the distance to the centre (median per annulus): robust to
    /// nebulosity and noise, right for round motes and donuts.
    /// </summary>
    Radial,
}

public static class DustModels
{
    public static readonly DustModel[] All = [DustModel.Surface, DustModel.Radial];
    public static string DisplayName(this DustModel m) => m == DustModel.Radial ? "Radial profile" : "Local flat";
}

/// <summary>
/// A circular region around a dust shadow (mote or donut) to be flattened. Coordinates are in
/// oriented image pixels with the pixel-edge convention: pixel (i, j) covers [i, i+1) × [j, j+1).
/// </summary>
public sealed partial class DustSpot : IEquatable<DustSpot>
{
    public Guid Id;
    public double X;
    public double Y;
    public double Radius;
    /// <summary>How this spot's shadow is modelled.</summary>
    public DustModel Model;
    /// <summary>Finest scale of the shadow estimate in pixels (Gaussian σ for Surface, profile window for Radial).</summary>
    public double Smoothness;
    /// <summary>Fraction of the radius over which the correction fades out beyond the shadow.</summary>
    public double Feather;

    public DustSpot(double x, double y, double radius, DustModel model = DustModel.Surface, double smoothness = 3, double feather = 0.2, Guid? id = null)
    {
        Id = id ?? Guid.NewGuid(); X = x; Y = y; Radius = radius;
        Model = model; Smoothness = smoothness; Feather = feather;
    }

    internal DustRemoval.Options Options => new(Model, Smoothness, Feather);

    public DustSpot Clone() => (DustSpot)MemberwiseClone();

    /// <summary>The spot after the image (imageWidth × imageHeight) has undergone <paramref name="op"/>.</summary>
    public DustSpot Applying(OrientationOp op, int imageWidth, int imageHeight)
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

    /// <summary>The spot after an unoriented image (imageWidth × imageHeight) is oriented (flip, then quarter turns).</summary>
    public DustSpot Applying(Orientation o, int imageWidth, int imageHeight)
    {
        var s = o.Flipped ? Applying(OrientationOp.FlipHorizontal, imageWidth, imageHeight) : Clone();
        int w = imageWidth, h = imageHeight;
        for (int i = 0; i < o.Rotation; i++) { s = s.Applying(OrientationOp.RotateClockwise, w, h); (w, h) = (h, w); }
        return s;
    }

    public bool Equals(DustSpot? o) => o != null && Id == o.Id && X == o.X && Y == o.Y && Radius == o.Radius && Model == o.Model && Smoothness == o.Smoothness && Feather == o.Feather;
    public override bool Equals(object? obj) => Equals(obj as DustSpot);
    public override int GetHashCode() => HashCode.Combine(Id, X, Y, Radius, Model, Smoothness, Feather);
}

/// <summary>
/// Dust spots per channel (each with its own parameters) plus the parameters given to new spots.
/// Stored in the project, applied to the loaded channel once (before statistics, normalisation and
/// everything else).
/// </summary>
public sealed partial class DustSettings : IEquatable<DustSettings>
{
    public Dictionary<ChannelKind, List<DustSpot>> Spots = new();
    /// <summary>Model given to new spots.</summary>
    public DustModel Model = DustModel.Surface;
    /// <summary>Smoothness given to new spots.</summary>
    public double Smoothness = 3;
    /// <summary>Feather given to new spots.</summary>
    public double Feather = 0.2;

    public bool IsEmpty => Spots.Values.All(l => l.Count == 0);
    public List<DustSpot> SpotsFor(ChannelKind kind) => Spots.TryGetValue(kind, out var l) ? l : new List<DustSpot>();
    /// <summary>A spot with the parameters for new spots.</summary>
    public DustSpot NewSpot(double x, double y, double radius) => new(x, y, radius, Model, Smoothness, Feather);

    public DustSettings Clone()
    {
        var c = new DustSettings { Model = Model, Smoothness = Smoothness, Feather = Feather };
        foreach (var (k, v) in Spots) c.Spots[k] = v.Select(s => s.Clone()).ToList();
        return c;
    }
    public bool Equals(DustSettings? o) => o != null && SettingsJson.SameJson(this, o);
    public override bool Equals(object? obj) => Equals(obj as DustSettings);
    public override int GetHashCode() => SettingsJson.JsonHash(this);
}

/// <summary>
/// Flattens the shadows of dust motes (sensor window, filters) and dust donuts (reflectors, out of
/// focus motes) in a stacked channel that was not, or not correctly, flat-field calibrated.
///
/// A mote attenuates the light reaching the sensor, so the stacked image is the true scene times a
/// transmission map T ≤ 1 — the same multiplicative error a flat frame would have divided out. For
/// each spot the expected unshadowed level B(x, y) is a plane fitted (sigma-clipped, star-masked) to
/// the annulus around the spot; the ratio image / B under the spot is then summarised as the
/// transmission — either a radial profile (median per annulus, right for round motes and donuts and
/// robust to nebulosity) or a star-masked multi-scale surface (irregular motes) — and the pixels are
/// divided by it (stars included: they were dimmed by the same shadow). The correction fades out
/// over the outer <c>feather</c> fraction of the radius so the edge is seamless; pixels beyond the
/// radius are untouched, zero (padding) pixels stay zero.
/// </summary>
public static class DustRemoval
{
    public readonly record struct Options(DustModel Model = DustModel.Surface, double Smoothness = 3, double Feather = 0.2);

    /// <summary>Transmission range accepted for a shadow (dust never brightens; a little over 1 is allowed for the diffraction rim of a donut).</summary>
    const float TransmissionLo = 0.05f, TransmissionHi = 1.5f;

    /// <summary>Healed copy of <paramref name="image"/> (the same instance when there is nothing to do).</summary>
    public static PlanarImage Heal(PlanarImage image, IReadOnlyList<DustSpot> spots)
    {
        if (spots.Count == 0) return image;
        var out_ = image.Copy();
        foreach (var s in spots) HealInPlace(out_, s, spots.Where(o => o.Id != s.Id).ToList());
        return out_;
    }

    /// <summary>
    /// Divides the pixels under <paramref name="spot"/> by the estimated transmission. Pixels under
    /// <paramref name="others"/> (the remaining spots, typically neighbours in a cluster) are left out
    /// of the background fit. False when the spot cannot be analysed (outside the image or too few
    /// usable pixels around it).
    /// </summary>
    public static bool HealInPlace(PlanarImage image, DustSpot spot, IReadOnlyList<DustSpot>? others = null)
    {
        var sh = Shadow(image, spot, others ?? []);
        if (sh == null) return false;
        var win = sh.Window; var t = sh.Transmission;
        for (int y = 0; y < win.Height; y++)
        {
            int row = (win.Y + y) * image.Width + win.X;
            int trow = y * t.Width;
            for (int x = 0; x < win.Width; x++)
            {
                float g = t.Data[trow + x];
                float v = image.Data[row + x];
                if (v != 0 && g != 1) image.Data[row + x] = v / g;
            }
        }
        return true;
    }

    /// <summary>Window around a spot and the effective transmission inside it (1 where nothing changes).</summary>
    public sealed class ShadowMap
    {
        public PixelRect Window;
        public PlanarImage Transmission;
        public ShadowMap(PixelRect window, PlanarImage transmission) { Window = window; Transmission = transmission; }
    }

    /// <summary>
    /// Estimates the transmission map of a spot.
    ///
    /// Radial model: the profile is taken inside the circle and feathered out at its edge. Surface
    /// model: the circle only bounds the search — the shadow's own outline is found as the connected
    /// region (seeded at the centre, holes filled) where the smoothed ratio sits below the sky by more
    /// than its noise, so elongated and irregular motes are corrected to their real edge; the
    /// correction fades out over a band of feather × radius outside that outline, and the sky plane
    /// is refitted away from it.
    /// </summary>
    public static ShadowMap? Shadow(PlanarImage image, DustSpot spot, IReadOnlyList<DustSpot>? others = null)
    {
        others ??= [];
        double r = Math.Max(spot.Radius, 3);
        var options = spot.Options;
        bool surfaceModel = options.Model == DustModel.Surface;
        var a = Analysis.Create(image, spot.X, spot.Y, r, others, surfaceModel ? 1.8 : 1.7);
        if (a == null) return null;
        int w = a.Window.Width, h = a.Window.Height, n = w * h;
        var t = new PlanarImage(w, h, 1);
        var mask = a.Mask;
        PlanarImage? raw = null, weight = null, exclude = null;
        double sigma = Math.Max(0.5, options.Smoothness);
        float featherPx = (float)Math.Max(2, options.Feather * r);
        // Two passes: the first star mask may flag the bright side of a deep shadow edge; masking the
        // flattened window again gives a clean mask (and, for the surface model, the plane is refitted
        // away from the shadow found in the first pass).
        for (int pass = 0; pass < 2; pass++)
        {
            if (pass == 1 && raw != null)
            {
                var flat = a.Crop.Copy();
                Vec.Div(flat.Data, raw.Data, flat.Data);
                mask = a.StarMaskOf(flat, 4);
            }
            var q = a.Ratio(mask, exclude);
            if (q == null) return null;
            if (options.Model == DustModel.Radial)
            {
                var p = RadialProfile(q, mask, a.Rad, r, Math.Max(2, options.Smoothness));
                if (p == null) return null;
                raw = Expand(p, a.Rad, r);
            }
            else
            {
                // Noise of the ratio, measured on the sky around the spot (not under it).
                var dev = new List<float>(n / 5 + 1);
                float skyFrom = (float)r * 1.1f;
                for (int i = 0; i < n; i += 5)
                    if (mask.Data[i] == 0 && q.Data[i] > 0 && a.Rad.Data[i] >= skyFrom && (exclude == null || exclude.Data[i] == 0)) dev.Add(MathF.Abs(q.Data[i] - 1));
                if (dev.Count < 16) return null;
                var devArr = dev.ToArray(); Array.Sort(devArr);
                float pixelSigma = 1.4826f * Statistics.Percentile(devArr, 0.5);
                var sm = Surface(q, mask, a.Rad, a.Outer, sigma, pixelSigma);
                // Noise of the smoothed ratio at the finest scale (Σk² = 1 / 4πσ²).
                float smoothSigma = pixelSigma / (2 * MathF.Sqrt(MathF.PI) * (float)sigma);
                float threshold = 1 - MathF.Max(2.5f * smoothSigma, 0.0005f);
                var region = ShadowRegion(sm, a.Rad, threshold, (float)r * 0.5f, (float)r * 1.3f);
                var dist = DistanceOutside(region);
                var wgt = new PlanarImage(w, h);
                var ex = new PlanarImage(w, h);
                float band = featherPx + 2 * (float)sigma;
                for (int i = 0; i < n; i++)
                {
                    float d = dist.Data[i];
                    wgt.Data[i] = d <= 0 ? 1 : MathF.Max(0, 1 - d / featherPx);
                    ex.Data[i] = d < band ? 1 : 0;
                }
                raw = sm; weight = wgt; exclude = ex;
            }
        }
        if (raw == null) return null;
        if (weight != null)
        {
            for (int i = 0; i < n; i++)
            {
                float f = weight.Data[i];
                if (f <= 0) continue;
                float v = MathF.Min(MathF.Max(raw.Data[i], TransmissionLo), TransmissionHi);
                t.Data[i] = 1 + (v - 1) * f;
            }
        }
        else
        {
            // Feather towards the edge of the circle and clamp.
            float fr = (float)r, inner = fr * (float)(1 - Math.Min(Math.Max(options.Feather, 0), 0.9));
            for (int i = 0; i < n; i++)
            {
                float d = a.Rad.Data[i];
                if (d >= fr) continue;
                float f = d <= inner ? 1 : 0.5f * (1 + MathF.Cos(MathF.PI * (d - inner) / MathF.Max(fr - inner, 1e-3f)));
                float v = MathF.Min(MathF.Max(raw.Data[i], TransmissionLo), TransmissionHi);
                t.Data[i] = 1 + (v - 1) * f;
            }
        }
        return new ShadowMap(a.Window, t);
    }

    /// <summary>
    /// Connected region of pixels below <paramref name="threshold"/> that touches the disc of
    /// <paramref name="seedRadius"/> around the centre, limited to <paramref name="limit"/>, with its
    /// holes filled (1 inside, 0 outside).
    /// </summary>
    static PlanarImage ShadowRegion(PlanarImage s, PlanarImage rad, float threshold, float seedRadius, float limit)
    {
        int w = s.Width, h = s.Height, n = w * h;
        var region = new PlanarImage(w, h);
        var stack = new Stack<int>();
        for (int i = 0; i < n; i++) if (rad.Data[i] <= seedRadius && s.Data[i] < threshold) { region.Data[i] = 1; stack.Push(i); }
        static void Grow(Stack<int> stack, PlanarImage out_, int w, int h, Func<int, bool> accept)
        {
            ReadOnlySpan<(int dx, int dy)> dirs = [(1, 0), (-1, 0), (0, 1), (0, -1)];
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                int x = i % w, y = i / w;
                foreach (var (dx, dy) in dirs)
                {
                    int xx = x + dx, yy = y + dy;
                    if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                    int j = yy * w + xx;
                    if (out_.Data[j] == 0 && accept(j)) { out_.Data[j] = 1; stack.Push(j); }
                }
            }
        }
        Grow(stack, region, w, h, j => s.Data[j] < threshold && rad.Data[j] <= limit);
        // Holes: whatever the outside (grown from the window border) does not reach is inside.
        var outside = new PlanarImage(w, h);
        var border = new Stack<int>();
        for (int x = 0; x < w; x++) foreach (var y in new[] { 0, h - 1 }) { int i = y * w + x; if (region.Data[i] == 0) { outside.Data[i] = 1; border.Push(i); } }
        for (int y = 0; y < h; y++) foreach (var x in new[] { 0, w - 1 }) { int i = y * w + x; if (region.Data[i] == 0 && outside.Data[i] == 0) { outside.Data[i] = 1; border.Push(i); } }
        Grow(border, outside, w, h, j => region.Data[j] == 0);
        for (int i = 0; i < n; i++) if (outside.Data[i] == 0) region.Data[i] = 1;
        return region;
    }

    /// <summary>Distance of every pixel to the nearest region pixel (0 inside), chamfer 3-4 approximation.</summary>
    static PlanarImage DistanceOutside(PlanarImage region)
    {
        int w = region.Width, h = region.Height;
        float big = (w + h) * 2;
        var d = new PlanarImage(w, h);
        for (int i = 0; i < w * h; i++) d.Data[i] = region.Data[i] > 0 ? 0 : big;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float v = d[x, y];
            if (x > 0) v = MathF.Min(v, d[x - 1, y] + 1);
            if (y > 0) { v = MathF.Min(v, d[x, y - 1] + 1); if (x > 0) v = MathF.Min(v, d[x - 1, y - 1] + 1.4142f); if (x + 1 < w) v = MathF.Min(v, d[x + 1, y - 1] + 1.4142f); }
            d[x, y] = v;
        }
        for (int y = h - 1; y >= 0; y--) for (int x = w - 1; x >= 0; x--)
        {
            float v = d[x, y];
            if (x + 1 < w) v = MathF.Min(v, d[x + 1, y] + 1);
            if (y + 1 < h) { v = MathF.Min(v, d[x, y + 1] + 1); if (x + 1 < w) v = MathF.Min(v, d[x + 1, y + 1] + 1.4142f); if (x > 0) v = MathF.Min(v, d[x - 1, y + 1] + 1.4142f); }
            d[x, y] = v;
        }
        return d;
    }

    /// <summary>
    /// Refines a rough click: finds the centre and the extent of the shadow around (x, y), searching
    /// out to several times <paramref name="r0"/> (the search widens while nothing is found, so the
    /// starting radius may be well below the real one). Null when no shadow is found there.
    ///
    /// Shadows of stacked data can be very shallow (a few ‰ of the sky level) yet obvious after
    /// stretching, so the detection threshold is set by the bin-to-bin noise of the profile rather
    /// than by a fixed depth.
    /// </summary>
    public static DustSpot? Fit(PlanarImage image, double x, double y, double r0, double smoothness = 3)
    {
        double cx = x, cy = y, r = Math.Max(r0, 12);
        bool found = false;
        // The search never grows past a fraction of the image (a window of the whole frame is slow
        // and only finds vignetting), nor past ten times the starting radius.
        double maxSearch = Math.Min(Math.Min(Math.Min(image.Width, image.Height) / 6.0, 900), r * 10);
        for (int iteration = 0; iteration < 4; iteration++)
        {
            // Extent: the radial profile out to a few times the current radius; the shadow ends where
            // the profile comes back to its outer level.
            (float[] p, float outer, float depth)? hit = null;
            foreach (var factor in iteration == 0 ? new[] { 2.5, 5, 10 } : new[] { 1.6 })
            {
                double search = Math.Min(r * factor, maxSearch);
                var a = Analysis.Create(image, cx, cy, search);
                if (a == null) break;
                var q = a.Ratio(a.Mask);
                if (q == null) break;
                var pc = RadialProfileWithCounts(q, a.Mask, a.Rad, search, Math.Max(2, smoothness));
                if (pc == null || pc.Value.profile.Length < 10) break;
                var (p, counts) = pc.Value;
                var tail = p.Skip(p.Length * 4 / 5).ToArray(); Array.Sort(tail);
                float outer = Statistics.Percentile(tail, 0.5);
                // Per-pixel noise of the ratio, then per-bin noise of a median over `counts` pixels:
                // the dip must be significant where it is deepest (the innermost bins hold few pixels).
                var dev = new List<float>(q.Count / 7 + 1);
                for (int i = 0; i < q.Count; i += 7) if (a.Mask.Data[i] == 0 && q.Data[i] > 0) dev.Add(MathF.Abs(q.Data[i] - 1));
                if (dev.Count < 16) break;
                var devArr = dev.ToArray(); Array.Sort(devArr);
                float pixelSigma = MathF.Max(1.4826f * Statistics.Percentile(devArr, 0.5), 1e-6f);
                (float depth, float significance) best = (0, 0);
                for (int i = 0; i < p.Length; i++)
                {
                    if (counts[i] <= 0) continue;
                    float d = outer - p[i];
                    float sig = d / (1.2533f * pixelSigma / MathF.Sqrt(counts[i]));
                    if (sig > best.significance) best = (d, sig);
                }
                float depth = best.depth;
                if (depth > 0.0015f && best.significance > 6) { hit = (p, outer, depth); break; }
                if (search >= maxSearch) break;
            }
            if (hit == null) break;
            var (prof, outerLevel, dep) = hit.Value;
            int edge = -1;
            for (int i = prof.Length - 1; i >= 0; i--) if (prof[i] < outerLevel - 0.15f * dep) { edge = i; break; }
            if (edge < 3) break;
            double newR = Math.Min((edge + 1) * 1.25 + 2, maxSearch * 0.8);
            // Centre: centroid of the pixels darker than half the depth, measured with the background
            // fitted close around the new radius (a far annulus extrapolates nebulosity badly). The
            // binary weight is far less sensitive to gradients than the darkening itself.
            var b = Analysis.Create(image, cx, cy, newR);
            if (b == null) break;
            var qb = b.Ratio(b.Mask);
            if (qb == null) break;
            double sx = 0, sy = 0, sw = 0;
            int bw = b.Window.Width, bh = b.Window.Height;
            float half = outerLevel - 0.5f * dep;
            for (int yy = 0; yy < bh; yy++) for (int xx = 0; xx < bw; xx++)
            {
                int i = yy * bw + xx;
                if (b.Mask.Data[i] != 0 || b.Rad.Data[i] > newR || qb.Data[i] >= half) continue;
                sx += b.Window.X + xx + 0.5; sy += b.Window.Y + yy + 0.5; sw += 1;
            }
            double ncx = sw > 0 ? sx / sw : cx, ncy = sw > 0 ? sy / sw : cy;
            double moved = Math.Sqrt((ncx - cx) * (ncx - cx) + (ncy - cy) * (ncy - cy));
            cx = ncx; cy = ncy;
            found = true;
            if (Math.Abs(newR - r) < 1 && moved < 0.5) { r = newR; break; }
            r = newR;
        }
        return found ? new DustSpot(cx, cy, r) : null;
    }

    // Pieces

    /// <summary>The window around a spot: crop, distance map, star mask and the fitted background surface.</summary>
    sealed class Analysis
    {
        public PixelRect Window;
        public PlanarImage Crop = null!;
        /// <summary>Distance of each pixel centre to the spot centre.</summary>
        public PlanarImage Rad = null!;
        public PlanarImage Mask = null!;
        public double Radius, Outer, Cx, Cy;
        /// <summary>1 under the other spots (null when none overlaps the window).</summary>
        public PlanarImage? Excluded;

        public static Analysis? Create(PlanarImage image, double cx, double cy, double radius, IReadOnlyList<DustSpot>? others = null, double outerFactor = 1.7)
        {
            others ??= [];
            double outer = radius * outerFactor;
            int x0 = (int)Math.Floor(cx - outer), y0 = (int)Math.Floor(cy - outer);
            int x1 = (int)Math.Ceiling(cx + outer), y1 = (int)Math.Ceiling(cy + outer);
            var win = new PixelRect(x0, y0, x1 - x0, y1 - y0).Clamped(image.Width, image.Height);
            if (win is not PixelRect wn || wn.Width < 8 || wn.Height < 8) return null;
            var a = new Analysis { Window = wn, Crop = image.Cropped(wn), Radius = radius, Outer = outer, Cx = cx, Cy = cy };
            int w = wn.Width, h = wn.Height;
            var rad = PlanarImage.Uninitialized(w, h);
            float lcx = (float)(cx - wn.X), lcy = (float)(cy - wn.Y);
            for (int y = 0; y < h; y++)
            {
                float dy = y + 0.5f - lcy;
                for (int x = 0; x < w; x++) { float dx = x + 0.5f - lcx; rad[x, y] = MathF.Sqrt(dx * dx + dy * dy); }
            }
            a.Rad = rad;
            var m = BuildStarMask(a.Crop, radius, 6);
            // Other spots overlapping the window are masked so that their shadows do not bias the
            // background fit (the mask is also reused for the second pass through `Excluded`).
            var ex = new PlanarImage(w, h);
            bool anyExcluded = false;
            foreach (var o in others)
            {
                float ox = (float)(o.X - wn.X), oy = (float)(o.Y - wn.Y), orad = (float)o.Radius;
                if (!(ox + orad > 0 && oy + orad > 0 && ox - orad < w && oy - orad < h)) continue;
                anyExcluded = true;
                for (int y = 0; y < h; y++)
                {
                    float dy = y + 0.5f - oy;
                    for (int x = 0; x < w; x++)
                    {
                        if (ex[x, y] != 0) continue;
                        float dx = x + 0.5f - ox;
                        if (dx * dx + dy * dy < orad * orad) ex[x, y] = 1;
                    }
                }
            }
            a.Excluded = anyExcluded ? ex : null;
            if (a.Excluded != null) Vec.Max(m.Data, a.Excluded.Data, m.Data);
            a.Mask = m;
            return a;
        }

        /// <summary>Star mask of <paramref name="img"/> combined with the exclusion of the other spots.</summary>
        public PlanarImage StarMaskOf(PlanarImage img, float thresholdSigma)
        {
            var m = BuildStarMask(img, Radius, thresholdSigma);
            if (Excluded != null) Vec.Max(m.Data, Excluded.Data, m.Data);
            return m;
        }

        static PlanarImage BuildStarMask(PlanarImage img, double radius, float thresholdSigma)
        {
            var o = new StarMask.Options
            {
                ThresholdSigma = thresholdSigma,
                Dilation = 2,
                BackgroundLevels = Math.Min(4, Math.Max(1, (int)Math.Log2(Math.Max(radius / 4, 2)))),
                MaxSamples = 40_000,
            };
            return StarMask.Build(img, o);
        }

        /// <summary>
        /// crop / B where B is the background surface fitted to the annulus outside the spot — or, with
        /// <paramref name="exclude"/>, to every unmasked pixel outside the excluded area (1 where undefined).
        /// </summary>
        public PlanarImage? Ratio(PlanarImage mask, PlanarImage? exclude = null)
        {
            var b = BackgroundSurface(mask, exclude);
            if (b == null) return null;
            int n = Window.Width * Window.Height;
            var q = new PlanarImage(Window.Width, Window.Height, 1);
            for (int i = 0; i < n; i++)
            {
                float v = Crop.Data[i], bb = b.Data[i];
                if (v > 0 && bb > 0) q.Data[i] = v / bb;
            }
            return q;
        }

        /// <summary>
        /// Least-squares plane (a constant when few samples) through the unmasked annulus pixels, with
        /// three rounds of 3σ clipping. A plane extrapolates nebulosity more safely than a quadratic,
        /// which over-shoots under the spot.
        /// </summary>
        PlanarImage? BackgroundSurface(PlanarImage mask, PlanarImage? exclude)
        {
            int w = Window.Width, h = Window.Height;
            float inner = exclude == null ? (float)(Radius * 1.1) : 0, outF = (float)Outer;
            float lcx = (float)(Cx - Window.X), lcy = (float)(Cy - Window.Y), scale = 1 / (float)Outer;
            var su = new List<float>(); var sv = new List<float>(); var sz = new List<float>();
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f - lcy) * scale;
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    float d = Rad.Data[i];
                    if (!(d >= inner && d <= outF && mask.Data[i] == 0 && Crop.Data[i] != 0 && (exclude == null || exclude.Data[i] == 0))) continue;
                    su.Add((x + 0.5f - lcx) * scale); sv.Add(v); sz.Add(Crop.Data[i]);
                }
            }
            int n = su.Count;
            if (n < 12) return null;
            int terms = n >= 20 ? 3 : 1;
            var use = new bool[n]; Array.Fill(use, true);
            var coef = new float[3];
            for (int round = 0; round < 3; round++)
            {
                var ata = new float[9]; var atb = new float[3];
                for (int k = 0; k < n; k++)
                {
                    if (!use[k]) continue;
                    float u = su[k], v = sv[k], z = sz[k];
                    ata[0] += 1; atb[0] += z;
                    if (terms == 3)
                    {
                        ata[1] += u; ata[2] += v; ata[4] += u * u; ata[5] += u * v; ata[8] += v * v;
                        atb[1] += u * z; atb[2] += v * z;
                    }
                }
                ata[3] = ata[1]; ata[6] = ata[2]; ata[7] = ata[5];
                float[] c;
                if (terms == 3)
                {
                    var sol = Solve(ata, atb, 3);
                    if (sol == null) return null;
                    c = sol;
                }
                else c = [atb[0] / ata[0], 0, 0];
                coef = c;
                if (round == 2) break;
                // Noise of the residuals from a sample (sorting every pixel of a large window is slow).
                var res = new List<float>(n / 4 + 1);
                for (int k = 0; k < n; k += 4) if (use[k]) res.Add(MathF.Abs(sz[k] - (coef[0] + coef[1] * su[k] + coef[2] * sv[k])));
                if (res.Count < 8) break;
                var resArr = res.ToArray(); Array.Sort(resArr);
                float sigma = 1.4826f * Statistics.Percentile(resArr, 0.5);
                if (sigma <= 0) break;
                float limit = 3 * sigma;
                for (int k = 0; k < n; k++) if (use[k] && MathF.Abs(sz[k] - (coef[0] + coef[1] * su[k] + coef[2] * sv[k])) > limit) use[k] = false;
            }
            var b = PlanarImage.Uninitialized(w, h);
            for (int y = 0; y < h; y++)
            {
                float v = (y + 0.5f - lcy) * scale;
                int row = y * w;
                for (int x = 0; x < w; x++) b.Data[row + x] = coef[0] + coef[1] * (x + 0.5f - lcx) * scale + coef[2] * v;
            }
            return b;
        }

        /// <summary>Gaussian elimination with partial pivoting for a small dense system.</summary>
        static float[]? Solve(float[] a, float[] b, int n)
        {
            var m = (float[])a.Clone(); var v = (float[])b.Clone();
            for (int col = 0; col < n; col++)
            {
                int piv = col;
                for (int r = col + 1; r < n; r++) if (MathF.Abs(m[r * n + col]) > MathF.Abs(m[piv * n + col])) piv = r;
                if (MathF.Abs(m[piv * n + col]) <= 1e-12f) return null;
                if (piv != col)
                {
                    for (int j = 0; j < n; j++) (m[col * n + j], m[piv * n + j]) = (m[piv * n + j], m[col * n + j]);
                    (v[col], v[piv]) = (v[piv], v[col]);
                }
                for (int r = col + 1; r < n; r++)
                {
                    float f = m[r * n + col] / m[col * n + col];
                    if (f == 0) continue;
                    for (int j = col; j < n; j++) m[r * n + j] -= f * m[col * n + j];
                    v[r] -= f * v[col];
                }
            }
            var x = new float[n];
            for (int r = n - 1; r >= 0; r--)
            {
                float s = v[r];
                for (int j = r + 1; j < n; j++) s -= m[r * n + j] * x[j];
                x[r] = s / m[r * n + r];
            }
            return x;
        }
    }

    /// <summary>
    /// Median of the ratio per 1-px annulus, each taken over a sliding window of <paramref name="window"/> px;
    /// annuli without usable pixels are filled from their neighbours.
    /// </summary>
    static float[]? RadialProfile(PlanarImage q, PlanarImage mask, PlanarImage rad, double radius, double window)
        => RadialProfileWithCounts(q, mask, rad, radius, window)?.profile;

    /// <summary>The profile and the number of pixels behind each bin.</summary>
    static (float[] profile, int[] counts)? RadialProfileWithCounts(PlanarImage q, PlanarImage mask, PlanarImage rad, double radius, double window)
    {
        int nb = (int)Math.Ceiling(radius) + 1;
        var bins = new List<float>[nb];
        for (int i = 0; i < nb; i++) bins[i] = new List<float>();
        for (int i = 0; i < q.Count; i++)
        {
            float d = rad.Data[i];
            if (!(d < nb && mask.Data[i] == 0 && q.Data[i] > 0)) continue;
            bins[(int)d].Add(q.Data[i]);
        }
        int half = Math.Max(1, (int)Math.Round(window / 2, MidpointRounding.AwayFromZero));
        var p = new float[nb]; Array.Fill(p, float.NaN);
        var counts = new int[nb];
        bool any = false;
        for (int i = 0; i < nb; i++)
        {
            var v = new List<float>();
            for (int j = Math.Max(0, i - half); j <= Math.Min(nb - 1, i + half); j++) v.AddRange(bins[j]);
            if (v.Count < 4) continue;
            var arr = v.ToArray(); Array.Sort(arr);
            p[i] = Statistics.Percentile(arr, 0.5);
            counts[i] = arr.Length;
            any = true;
        }
        if (!any) return null;
        // Fill gaps from the nearest defined bins.
        float last = float.NaN;
        for (int i = 0; i < nb; i++) { if (float.IsNaN(p[i])) p[i] = last; else last = p[i]; }
        last = float.NaN;
        for (int i = nb - 1; i >= 0; i--) { if (float.IsNaN(p[i])) p[i] = last; else last = p[i]; }
        return (p, counts);
    }

    /// <summary>Per-pixel transmission from a radial profile (linear interpolation between bin centres, 1 beyond the radius).</summary>
    static PlanarImage Expand(float[] p, PlanarImage rad, double radius)
    {
        var t = new PlanarImage(rad.Width, rad.Height, 1);
        float fr = (float)radius;
        for (int i = 0; i < rad.Count; i++)
        {
            float d = rad.Data[i];
            if (d >= fr) continue;
            float u = MathF.Max(0, d - 0.5f);
            int k = Math.Min(p.Length - 2, (int)u);
            float f = MathF.Min(1, u - k);
            t.Data[i] = p[k] * (1 - f) + p[k + 1] * f;
        }
        return t;
    }

    /// <summary>
    /// Star-masked estimate of the transmission from the ratio, 1 beyond the radius.
    ///
    /// Plain smoothing at a small σ keeps the fringes of a mote but also absorbs the coarse part of
    /// the noise, and dividing by it then flattens the grain under the spot. Instead the ratio is
    /// decomposed into scales σ, 2σ, 4σ and 8σ (normalised Gaussian convolutions); each detail layer
    /// is kept only where it exceeds its own noise (non-negative garrote at 2.5 σ_layer, with the
    /// layer noise known analytically from <paramref name="pixelSigma"/>), so the shadow's structure
    /// survives and the noise passes through the correction untouched.
    /// </summary>
    static PlanarImage Surface(PlanarImage q, PlanarImage mask, PlanarImage rad, double radius, double sigma, float pixelSigma)
    {
        int w = q.Width, h = q.Height, n = w * h;
        var num = new PlanarImage(w, h); var den = new PlanarImage(w, h);
        for (int i = 0; i < n; i++) if (mask.Data[i] == 0 && q.Data[i] > 0) { num.Data[i] = q.Data[i]; den.Data[i] = 1; }
        PlanarImage Smoothed(double sg)
        {
            var bn = Gaussian(num, sg); var bd = Gaussian(den, sg);
            var t = new PlanarImage(w, h, 1);
            for (int i = 0; i < n; i++) if (bd.Data[i] > 1e-3f) t.Data[i] = bn.Data[i] / bd.Data[i];
            return t;
        }
        var scales = Enumerable.Range(0, 4).Select(k => sigma * Math.Pow(2, k)).ToArray();
        var levels = scales.Select(Smoothed).ToArray();
        var tt = levels[3].Copy();
        for (int k = 0; k < 3; k++)
        {
            // Noise of (T_k − T_{k+1}) for white noise through two Gaussians, σ_{k+1} = 2 σ_k:
            // var = s² [1/(4πσ²) + 1/(16πσ²) − 1/(π · 5σ²)] = 0.45 s² / (4πσ²).
            float layerSigma = pixelSigma * MathF.Sqrt(0.45f / (4 * MathF.PI)) / (float)scales[k];
            float thr = 2.5f * layerSigma, thr2 = thr * thr;
            for (int i = 0; i < n; i++)
            {
                float d = levels[k].Data[i] - levels[k + 1].Data[i];
                float d2 = d * d;
                if (d2 > thr2) tt.Data[i] += d * (1 - thr2 / d2);
            }
        }
        float fr = (float)radius;
        for (int i = 0; i < n; i++) if (rad.Data[i] >= fr) tt.Data[i] = 1;
        return tt;
    }

    /// <summary>Separable Gaussian blur, zero outside the image (the caller normalises).</summary>
    static PlanarImage Gaussian(PlanarImage img, double sigma)
    {
        int rk = Math.Max(1, (int)Math.Ceiling(3 * sigma));
        var kernel = new float[2 * rk + 1];
        double ks = 0;
        for (int i = -rk; i <= rk; i++) { kernel[i + rk] = (float)Math.Exp(-(double)(i * i) / (2 * sigma * sigma)); ks += kernel[i + rk]; }
        for (int i = 0; i < kernel.Length; i++) kernel[i] = (float)(kernel[i] / ks);
        return Convolve.Separable(img, kernel, false);
    }
}
