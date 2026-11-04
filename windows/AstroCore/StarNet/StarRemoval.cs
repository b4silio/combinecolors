namespace AstroCore;

/// <summary>How the data are brightened for StarNet2, whose network was trained on stretched images.</summary>
public enum StarRemovalMethod
{
    /// <summary>StarNet2's own linear mode (<c>--linear</c>) with the project's Auto STF values: it stretches each
    /// channel with a midtones transfer function, removes the stars and undoes the stretch exactly.</summary>
    StarnetLinear,
    /// <summary>The project's stretch (whatever method is chosen) applied here, the starless result
    /// un-stretched afterwards by inverting that stretch numerically.</summary>
    AppStretch,
}

/// <summary>How a file was actually processed.</summary>
public enum StarRemovalProcessing
{
    StarnetLinear,
    AppStretch,
    /// <summary>The data already looked stretched: StarNet2 ran on them as they are, and the outputs are stretched too.</summary>
    AlreadyStretched,
}

public static class StarRemovalNames
{
    public static readonly StarRemovalMethod[] AllMethods = [StarRemovalMethod.StarnetLinear, StarRemovalMethod.AppStretch];

    /// <summary>"starnetLinear" / "appStretch" (the Swift raw values, also the CLI's <c>--method</c> values).</summary>
    public static string RawValue(this StarRemovalMethod m) => EnumRaw.Name(m);

    public static string DisplayName(this StarRemovalMethod m) => m switch
    {
        StarRemovalMethod.StarnetLinear => "StarNet2 linear mode, with this project's Auto STF values (recommended)",
        _ => "This project's stretch, undone afterwards",
    };

    public static string DisplayName(this StarRemovalProcessing p) => p switch
    {
        StarRemovalProcessing.StarnetLinear => "linear, StarNet2 stretch",
        StarRemovalProcessing.AppStretch => "linear, project stretch",
        _ => "already stretched",
    };
}

public sealed class StarRemovalOptions
{
    public StarRemovalMethod Method { get; set; } = StarRemovalMethod.StarnetLinear;
    /// <summary>The project's stretch: Auto STF values (target background, shadows clipping) for
    /// <see cref="StarRemovalMethod.StarnetLinear"/>, the whole stretch for <see cref="StarRemovalMethod.AppStretch"/>.</summary>
    public StretchSettings Stretch { get; set; } = new();
    /// <summary>StarNet2's intermediate 2× upsampling (slower; allows images down to 256 px).</summary>
    public bool Upsample { get; set; }

    public StarRemovalOptions() { }
    public StarRemovalOptions(StarRemovalMethod method, StretchSettings? stretch = null, bool upsample = false)
    {
        Method = method; Stretch = stretch ?? new StretchSettings(); Upsample = upsample;
    }
}

/// <param name="Stars">Linear data: original − starless (add it back). Already-stretched data: an unscreen layer (Screen it back).</param>
public sealed record StarRemovalResult(IReadOnlyList<PlanarImage> Starless, IReadOnlyList<PlanarImage> Stars, StarRemovalProcessing Processing);

/// <summary>Files written for one source image.</summary>
public sealed record StarRemovalFileResult(string Source, string Starless, string Stars, StarRemovalProcessing Processing);

/// <summary>Outcome for one file of <see cref="StarRemoval.ProcessFiles"/>: a result, or the error message.</summary>
public sealed record StarRemovalFileOutcome(string Source, StarRemovalFileResult? Result, string? Error);

public static class StarRemoval
{
    /// <summary>Same rule as the app's "already stretched" hint: the sky sits far above a linear level.</summary>
    public static bool LooksStretched(IReadOnlyList<float> medians) => medians.Count > 0 && medians.Average() > 0.08f;

    /// <summary><c>&lt;name&gt;_bg.&lt;ext&gt;</c> and <c>&lt;name&gt;_stars.&lt;ext&gt;</c> next to the source (or in <paramref name="folder"/>).
    /// FITS and XISF sources give 32-bit float FITS, everything else TIFF.</summary>
    public static (string Starless, string Stars) OutputPaths(string source, string? folder = null)
    {
        var stem = Path.GetFileNameWithoutExtension(source);
        var ext = WritesFits(source) ? "fits" : "tif";
        var dir = folder ?? Path.GetDirectoryName(Path.GetFullPath(source)) ?? ".";
        return (Path.Combine(dir, $"{stem}_bg.{ext}"), Path.Combine(dir, $"{stem}_stars.{ext}"));
    }

    internal static bool WritesFits(string source)
    {
        var ext = ImageLoader.Extension(source);
        return ImageLoader.FitsExtensions.Contains(ext) || ImageLoader.XisfExtensions.Contains(ext);
    }

    /// <summary>True for a file this tool wrote (<c>…_bg</c>): running it again would only blur the image.</summary>
    public static bool IsStarless(string path) => Path.GetFileNameWithoutExtension(path).EndsWith("_bg", StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads a file, removes its stars (all three planes together for a colour file) and writes
    /// the <c>_bg</c> and <c>_stars</c> files. The source is never modified. Progress in [0, 1] is reported on a worker thread.</summary>
    public static StarRemovalFileResult ProcessFile(string path, StarRemovalOptions options, StarNetTool tool, string? folder = null,
                                                    Action<double>? progress = null, StarNetCancellation? cancellation = null)
    {
        var f = ImageLoader.Load(path);
        var planes = f.IsColor ? f.Planes.Take(3).ToList() : [f.Planes[0]];
        var work = Path.Combine(Path.GetTempPath(), $"CombineColors-StarNet-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var r = Run(planes, options, tool, work, progress, cancellation);
            var (starless, stars) = OutputPaths(path, folder);
            if (folder != null) Directory.CreateDirectory(folder);
            if (WritesFits(path))
            {
                var cards = CopiedCards(f.Header);
                FitsWriter.Write(r.Starless, starless, FitsWriter.Depth.Float32, [.. cards, ("IMAGETYP", "'Starless'", "StarNet2 via CombineColors")]);
                FitsWriter.Write(r.Stars, stars, FitsWriter.Depth.Float32,
                    [.. cards, ("IMAGETYP", r.Processing == StarRemovalProcessing.AlreadyStretched ? "'Stars (unscreen)'" : "'Stars'", "StarNet2 via CombineColors")]);
            }
            else
            {
                var format = r.Processing == StarRemovalProcessing.AlreadyStretched ? RasterFormat.Tiff16 : RasterFormat.Tiff32;
                RasterWriter.Write(r.Starless, starless, format);
                RasterWriter.Write(r.Stars, stars, format);
            }
            return new StarRemovalFileResult(path, starless, stars, r.Processing);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Runs <see cref="ProcessFile"/> on several files one after the other (duplicates — the three channels of a split
    /// colour file — once). <paramref name="progress"/> gets the index of the file being processed and the overall fraction
    /// done. A file that fails is reported in its outcome and the others go on; stopping throws <see cref="StarNetError"/>
    /// (Cancelled) and leaves the files written so far in place.</summary>
    public static IReadOnlyList<StarRemovalFileOutcome> ProcessFiles(IEnumerable<string> paths, StarRemovalOptions options, StarNetTool tool, string? folder = null,
                                                                     Action<int, double>? progress = null, StarNetCancellation? cancellation = null)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var files = paths.Select(Path.GetFullPath).Distinct(comparer).ToList();
        var outcomes = new List<StarRemovalFileOutcome>();
        for (int i = 0; i < files.Count; i++)
        {
            if (cancellation?.IsCancelled == true) throw StarNetError.Cancelled();
            int index = i;
            progress?.Invoke(index, (double)index / files.Count);
            try
            {
                var r = ProcessFile(files[i], options, tool, folder, p => progress?.Invoke(index, (index + p) / files.Count), cancellation);
                outcomes.Add(new StarRemovalFileOutcome(files[i], r, null));
            }
            catch (StarNetError e) when (e.Kind == StarNetErrorKind.Cancelled) { throw; }
            catch (Exception e) { outcomes.Add(new StarRemovalFileOutcome(files[i], null, e.Message)); }
        }
        progress?.Invoke(Math.Max(files.Count - 1, 0), 1);
        return outcomes;
    }

    /// <summary>Acquisition keywords carried over to the outputs (the filter name keeps channel detection working).</summary>
    internal static List<(string key, string value, string? comment)> CopiedCards(FitsHeader? header)
    {
        var list = new List<(string, string, string?)>();
        if (header == null) return list;
        var keep = new HashSet<string> { "FILTER", "OBJECT", "TELESCOP", "INSTRUME", "EXPTIME", "EXPOSURE", "DATE-OBS", "RA", "DEC",
                                         "OBJCTRA", "OBJCTDEC", "FOCALLEN", "XPIXSZ", "YPIXSZ", "XBINNING", "YBINNING", "GAIN", "OFFSET", "CCD-TEMP" };
        var seen = new HashSet<string>();
        foreach (var c in header.Cards)
            if (keep.Contains(c.Key) && !string.IsNullOrEmpty(c.Value) && seen.Add(c.Key)) list.Add((c.Key, c.Value, c.Comment));
        return list;
    }

    /// <summary>Removes the stars of one grey plane or of three RGB planes (values as loaded: [0, 1], or
    /// float ADU up to 65535). The inputs are left untouched. <paramref name="workDirectory"/> receives StarNet2's
    /// intermediate files (input.fits, starless.fits, stars.fits).</summary>
    public static StarRemovalResult Run(IReadOnlyList<PlanarImage> planes, StarRemovalOptions options, StarNetTool tool, string workDirectory,
                                        Action<double>? progress = null, StarNetCancellation? cancellation = null)
    {
        if (planes.Count != 1 && planes.Count != 3) throw StarNetError.UnsupportedPlanes(planes.Count);
        int w = planes[0].Width, h = planes[0].Height;
        int minSide = options.Upsample ? StarNet.MinimumSize / 2 : StarNet.MinimumSize;
        if (w < minSide || h < minSide) throw StarNetError.ImageTooSmall(w, h);

        // StarNet2 reads float FITS as normalised [0, 1]: float ADU data are scaled down and back up.
        float maxValue = planes.Max(p => p.MinMax().max);
        float scale = maxValue > 1 ? (maxValue <= 65535 ? 65535 : maxValue) : 1;
        var input = scale == 1 ? planes.ToList() : planes.Select(p => { var c = p.Copy(); c.Affine(1 / scale, 0); return c; }).ToList();
        var stats = input.Select(p => Statistics.Compute(p)).ToList();

        StarRemovalProcessing processing;
        if (LooksStretched(stats.Select(s => s.Median).ToList())) processing = StarRemovalProcessing.AlreadyStretched;
        else if (options.Method == StarRemovalMethod.AppStretch && options.Stretch.Method != StretchMethod.None) processing = StarRemovalProcessing.AppStretch;
        else processing = StarRemovalProcessing.StarnetLinear;

        Directory.CreateDirectory(workDirectory);
        var inPath = Path.Combine(workDirectory, "input.fits");
        var outPath = Path.Combine(workDirectory, "starless.fits");
        var starsPath = Path.Combine(workDirectory, "stars.fits");
        foreach (var stale in new[] { outPath, starsPath }) if (File.Exists(stale)) File.Delete(stale);
        var cmd = new StarNetCommand(inPath, outPath) { Upsample = options.Upsample };

        List<PlanarImage> starless, stars;
        switch (processing)
        {
            case StarRemovalProcessing.AlreadyStretched:
            {
                var clamped = input.Select(p => { var c = p.Copy(); c.Clamp(0, 1); return c; }).ToList();
                FitsWriter.Write(clamped, inPath);
                cmd.Unscreen = starsPath;
                tool.Run(cmd, progress, cancellation);
                starless = ReadPlanes(outPath, planes.Count);
                stars = ReadPlanes(starsPath, planes.Count);
                break;
            }
            case StarRemovalProcessing.StarnetLinear:
                FitsWriter.Write(input, inPath);
                cmd.Mask = starsPath;
                cmd.Linear = true;
                cmd.ShadowsClipping = options.Stretch.ShadowsClipping;
                cmd.TargetBackground = options.Stretch.TargetBackground;
                tool.Run(cmd, progress, cancellation);
                starless = ReadPlanes(outPath, planes.Count);
                stars = ReadPlanes(starsPath, planes.Count);
                break;
            default:
            {
                var resolved = options.Stretch.Linked && input.Count > 1
                    ? Enumerable.Repeat(Stretch.ResolveLinked(options.Stretch, stats), input.Count).ToList()
                    : stats.Select(s => Stretch.Resolve(options.Stretch, s)).ToList();
                var stretched = input.Zip(resolved, (p, r) => { var c = p.Copy(); r.Apply(c); c.Clamp(0, 1); return c; }).ToList();
                FitsWriter.Write(stretched, inPath);
                tool.Run(cmd, progress, cancellation);
                starless = ReadPlanes(outPath, planes.Count);
                stars = [];
                for (int i = 0; i < resolved.Count; i++)
                {
                    new InverseStretch(resolved[i]).Apply(starless[i]);
                    RestoreClippedShadows(starless[i], input[i], resolved[i].C0);
                    stars.Add(Difference(input[i], starless[i]));
                }
                break;
            }
        }
        if (scale != 1)
        {
            foreach (var p in starless) p.Affine(scale, 0);
            if (processing != StarRemovalProcessing.AlreadyStretched) foreach (var p in stars) p.Affine(scale, 0);
        }
        return new StarRemovalResult(starless, stars, processing);
    }

    internal static List<PlanarImage> ReadPlanes(string path, int count)
    {
        if (!File.Exists(path)) throw StarNetError.MissingOutput(Path.GetFileName(path));
        var f = FitsReader.Read(path);
        if (f.Planes.Count < count) throw StarNetError.MissingOutput(Path.GetFileName(path));
        return f.Planes.Take(count).ToList();
    }

    /// <summary>Samples at or below the stretch's black point all became 0: give them their original value back.</summary>
    internal static void RestoreClippedShadows(PlanarImage starless, PlanarImage original, float blackPoint)
    {
        var s = starless.Data; var o = original.Data;
        for (int i = 0; i < s.Length; i++) if (o[i] <= blackPoint) s[i] = o[i];
    }

    /// <summary>max(a − b, 0).</summary>
    internal static PlanarImage Difference(PlanarImage a, PlanarImage b)
    {
        var d = new PlanarImage(a.Width, a.Height);
        Vec.Sub(a.Data, b.Data, d.Data);
        d.Clamp(0, float.MaxValue);
        return d;
    }
}

/// <summary>
/// Numerical inverse of a resolved stretch: a table of the linear value for evenly spaced
/// stretched values, found by bisection on <see cref="ResolvedStretch.Evaluate"/> (every method is monotonic).
/// Stretched values at 0 map to the black point and values at 1 to the white point.
/// </summary>
public sealed class InverseStretch
{
    readonly float[] table;
    public bool IsIdentity { get; }

    public InverseStretch(ResolvedStretch r, int samples = 1 << 18)
    {
        IsIdentity = r.Method == StretchMethod.None && (r.Gamma == 1 || r.Gamma <= 0);
        if (IsIdentity) { table = []; return; }
        float lo = r.Method == StretchMethod.None ? 0 : r.C0;
        float hi = r.Method == StretchMethod.None ? 1 : r.C1;
        var t = new float[samples];
        int n = samples;
        Parallel.For(0, 16, chunk =>
        {
            int start = chunk * n / 16, end = (chunk + 1) * n / 16;
            for (int i = start; i < end; i++)
            {
                float y = (float)i / (n - 1);
                float a = lo, b = hi;
                for (int k = 0; k < 40; k++)
                {
                    float m = (a + b) / 2;
                    if (r.Evaluate(m) < y) a = m; else b = m;
                }
                t[i] = (a + b) / 2;
            }
        });
        t[0] = lo;
        table = t;
    }

    public float Evaluate(float y)
    {
        if (IsIdentity) return y;
        int n = table.Length;
        float pos = Math.Clamp(y, 0, 1) * (n - 1);
        int i = Math.Min((int)pos, n - 2);
        float f = pos - i;
        return table[i] + f * (table[i + 1] - table[i]);
    }

    public void Apply(PlanarImage plane)
    {
        if (IsIdentity) return;
        var p = plane.Data;
        int count = p.Length;
        int chunks = Math.Max(1, Math.Min(32, count / 65536));
        Parallel.For(0, chunks, c =>
        {
            int start = (int)((long)c * count / chunks), end = (int)((long)(c + 1) * count / chunks);
            for (int i = start; i < end; i++) p[i] = Evaluate(p[i]);
        });
    }
}
