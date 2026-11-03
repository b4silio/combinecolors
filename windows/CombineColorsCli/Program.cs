using System.Diagnostics;
using System.Globalization;
using AstroCore;

// A small headless front-end used for batch rendering and for validating the pipeline on real data.
//
//   combinecolors-cli info <files…>
//   combinecolors-cli palettes
//   combinecolors-cli render [options] <files…>
//      --out <dir>            output directory (default: .\TestOutput)
//      --palette <id|all>     palette id (default: sho) or "all"
//      --norm <method>        none | backgroundEqualize | linearFit | backgroundDispersion | backgroundSignal | noiseWeighted | histogramMatch
//      --stage <pre|post>     stretch before or after combining
//      --stretch <method>     autoSTF | arcsinh | ghs | log | mtf | none
//      --linked               linked stretch
//      --max <pixels>         preview pixel budget (default 2000000); 0 = full resolution
//      --scnr <0..1>          green removal amount
//      --sat <factor>         saturation
//      --hue <deg>            hue shift
//      --texture <-1..1>      fine local contrast;  --clarity <-0.5..2>  mid-scale;  --dehaze <-1..1>  large-scale
//      --micro <-1..1>        micro-structure contrast (0.2 % radius);  --lowfreq <-0.5..2>  largest structures (50 %)
//      --sharpen <0..2>       unsharp mask amount;  --sharpen-radius <px>  its radius in full-resolution pixels (default 1.5)
//      --sharpen-masking <0..1>  protect low-contrast (noise) areas from the sharpening
//      --denoise <0..1>       luminance noise reduction;  --denoise-method wavelets | nonLocalMeans | guided | bm3d
//      --denoise-color <0..1> chroma smoothing
//      --lum <preset|off>     luminance: ha | lum | haMostly | haOiii | balanced | lumHa | maxLines | meanLines | off (default ha)
//      --stars                blend RGB stars (from the R, G, B channels, or from --stars-file)
//      --stars-file <image>   RGB image kept whole and blended as stars;  --stars-blend screen | lighten | add
//                             --stars-brightness <stops>;  --stars-contrast <-1..1>;  --stars-white (brightest stars to white)
//      --format <fmt>         png8 | png16 | tiff16 | tiff32 | fits32 | fits16 | jpeg (default png8)
//      --project <file.json>  load CombineSettings from a saved project / settings file
//      --assign <file>=<kind> force a channel assignment (kind: L R G B Ha OIII SII Hb)
//      --crop x,y,w,h         shared crop rectangle (full-resolution pixels); "auto" trims empty borders
//      --save-project <file>  write a project file (channel paths, crop, settings) instead of/besides rendering
//      --dust <kind>:x,y,r[;x,y,r…]  heal dust spots on a channel before anything else (repeatable; a project's
//                             spots are applied too);  --dust-model surface | radial;  --dust-smooth <px>;  --dust-feather <0..0.5>
//                             (the three parameters apply to the spots given with --dust)
//      --gradient <kind>:line:x0,y0,x1,y1 | <kind>:ellipse:cx,cy,a,b[,angle°] | <kind>:poly[:degree] | <kind>:samples[:perRow[,tolerance]]
//                             (samples: a grid of background samples with a spline / kriging surface;  --gradient-surface spline | kriging;
//                             --gradient-sample-smooth <0..1>;  --gradient-sample-radius <px>)
//                             remove a gradient from a channel guided by the drawn shape (repeatable; a project's models are
//                             applied too);  --gradient-mode subtract | divide;  --gradient-residual none | plane | quadratic
//                             --gradient-sectors <1|4|8|12>;  --gradient-smooth <bins>;  --gradient-norefine;  --gradient-protect cx,cy,a,b

var args_ = new List<string>(args);
if (args_.Count == 0) { Console.WriteLine("usage: combinecolors-cli (info|render|palettes) …"); return 1; }
var command = args_[0];
args_.RemoveAt(0);

string? Option(string name)
{
    int i = args_.IndexOf(name);
    if (i < 0 || i + 1 >= args_.Count) return null;
    var v = args_[i + 1]; args_.RemoveRange(i, 2); return v;
}
bool Flag(string name)
{
    int i = args_.IndexOf(name);
    if (i < 0) return false;
    args_.RemoveAt(i); return true;
}
static float F(string? s, float d) => s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : d;
static string Fmt(float v) => AstroCore.Fmt.G6(v);

static void ApplyLum(string l, CombineSettings s)
{
    if (l == "off" || l == "none") { s.Luminance.Enabled = false; return; }
    s.Luminance.Enabled = true;
    if (EnumRaw.Parse<LuminancePreset>(l) is LuminancePreset p) s.Luminance.Preset = p;
    else { s.Luminance.Preset = LuminancePreset.Custom; s.Luminance.CustomExpression = l; }
}

static ChannelKind? ParseKind(string s) => ChannelKinds.FromRawValue(s) ?? ChannelKinds.FromPixelMathIdentifier(s);

switch (command)
{
    case "palettes":
        foreach (var p in Palettes.All)
            Console.WriteLine($"{p.Id,-18} {p.Group,-11} {p.Name}  [{string.Join(",", p.RequiredChannels.Select(k => k.RawValue()).OrderBy(k => k, StringComparer.Ordinal))}]");
        return 0;

    case "info":
        foreach (var path in args_)
        {
            try
            {
                var t0 = Stopwatch.StartNew();
                var f = ImageLoader.Load(path);
                var load = t0.Elapsed;
                Console.WriteLine($"{f.FileName}: {f.Width}×{f.Height} {f.FormatDescription} planes={f.Planes.Count} filter={f.FilterName ?? "-"} detected={f.DetectedKind?.RawValue() ?? "?"} load={(int)load.TotalMilliseconds}ms");
                for (int i = 0; i < f.Planes.Count; i++)
                {
                    var t1 = Stopwatch.StartNew();
                    var src = new ChannelSource(f.DetectedKind ?? ChannelKind.Luminance, f.Planes[i], f.FileName);
                    var s = src.Stats; var m = src.MaskedStats;
                    Console.WriteLine($"  plane {i}: min={Fmt(s.Min)} max={Fmt(s.Max)} mean={Fmt(s.Mean)} median={Fmt(s.Median)} madn={Fmt(s.Madn)} bg={Fmt(s.Background)} bgσ={Fmt(s.BackgroundSigma)} noise={Fmt(s.Noise)} valid={Fmt(s.ValidFraction)} p99.5={Fmt(s.Percentile(0.995))}");
                    Console.WriteLine($"    masked: median={Fmt(m.Median)} madn={Fmt(m.Madn)} bg={Fmt(m.Background)} p99.5={Fmt(m.Percentile(0.995))} signal={Fmt(m.SignalStrength)} maskCoverage={Fmt(StarMask.Coverage(src.Mask))} levels={src.Pyramid.Count} analysis={(int)t1.Elapsed.TotalMilliseconds}ms");
                }
            }
            catch (Exception e) { Console.WriteLine($"{path}: {e.Message}"); }
        }
        return 0;

    case "render":
    {
        var outDir = Option("--out") ?? "TestOutput";
        var paletteArg = Option("--palette") ?? "sho";
        var normArg = Option("--norm");
        var stageArg = Option("--stage");
        var stretchArg = Option("--stretch");
        bool linked = Flag("--linked");
        int maxPixels = int.TryParse(Option("--max") ?? "2000000", out var mp) ? mp : 2_000_000;
        float scnr = F(Option("--scnr"), 0), sat = F(Option("--sat"), 1), hue = F(Option("--hue"), 0);
        float texture = F(Option("--texture"), 0), clarity = F(Option("--clarity"), 0), dehaze = F(Option("--dehaze"), 0);
        float micro = F(Option("--micro"), 0), lowFrequency = F(Option("--lowfreq"), 0);
        float sharpen = F(Option("--sharpen"), 0), sharpenRadius = F(Option("--sharpen-radius"), 1.5f), sharpenMasking = F(Option("--sharpen-masking"), 0);
        float denoise = F(Option("--denoise"), 0);
        var denoiseMethod = EnumRaw.Parse<DenoiseMethod>(Option("--denoise-method") ?? "wavelets") ?? DenoiseMethod.Wavelets;
        float denoiseColor = F(Option("--denoise-color"), 0);
        var lum = Option("--lum");
        var starsFile = Option("--stars-file");
        bool stars = Flag("--stars") || starsFile != null;
        var starsBlend = EnumRaw.Parse<StarsBlendMode>(Option("--stars-blend") ?? "");
        float starsBrightness = F(Option("--stars-brightness"), 0), starsContrast = F(Option("--stars-contrast"), 0);
        bool starsWhite = Flag("--stars-white");
        var formatArg = Option("--format") ?? "png8";
        var projectArg = Option("--project");
        var cropArg = Option("--crop");
        var saveProject = Option("--save-project");
        var assignments = new Dictionary<string, ChannelKind>(StringComparer.OrdinalIgnoreCase);
        while (Option("--assign") is string a)
        {
            var parts = a.Split('=', 2);
            if (parts.Length == 2 && ParseKind(parts[1]) is ChannelKind k) assignments[parts[0]] = k;
        }
        // Dust spots: from the project file, then from --dust.
        ProjectDocument? projectDoc = null;
        if (projectArg != null && File.Exists(projectArg)) { try { projectDoc = ProjectDocument.Load(projectArg); } catch { } }
        var dust = projectDoc?.Dust ?? new DustSettings();
        if (Option("--dust-model") is string dm && EnumRaw.Parse<DustModel>(dm) is DustModel m) dust.Model = m;
        if (Option("--dust-smooth") is string ds && double.TryParse(ds, NumberStyles.Float, CultureInfo.InvariantCulture, out var dsv)) dust.Smoothness = dsv;
        if (Option("--dust-feather") is string df && double.TryParse(df, NumberStyles.Float, CultureInfo.InvariantCulture, out var dfv)) dust.Feather = dfv;
        while (Option("--dust") is string d)
        {
            var parts = d.Split(':', 2);
            if (parts.Length != 2 || ParseKind(parts[0]) is not ChannelKind k) { Console.WriteLine($"bad --dust {d}"); continue; }
            foreach (var item in parts[1].Split(';'))
            {
                var v = item.Split(',').Select(t => double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN).ToArray();
                if (v.Length == 3 && v.All(double.IsFinite))
                {
                    if (!dust.Spots.TryGetValue(k, out var list)) dust.Spots[k] = list = new List<DustSpot>();
                    list.Add(dust.NewSpot(v[0], v[1], v[2]));
                }
                else Console.WriteLine($"bad --dust item {item}");
            }
        }
        // Gradient models: from the project file, then from --gradient.
        var gradient = projectDoc?.Gradient ?? new GradientSettings();
        if (Option("--gradient-mode") is string gm && EnumRaw.Parse<GradientMode>(gm) is GradientMode gmv) gradient.Mode = gmv;
        if (Option("--gradient-residual") is string gr && EnumRaw.Parse<GradientResidual>(gr) is GradientResidual grv) gradient.Residual = grv;
        if (Option("--gradient-sectors") is string gs && int.TryParse(gs, out var gsv)) gradient.Sectors = gsv;
        if (Option("--gradient-smooth") is string gsm && double.TryParse(gsm, NumberStyles.Float, CultureInfo.InvariantCulture, out var gsmv)) gradient.Smoothness = gsmv;
        if (Flag("--gradient-norefine")) gradient.Refine = false;
        if (Option("--gradient-surface") is string gsf && EnumRaw.Parse<SampleSurface>(gsf) is SampleSurface gsfv) gradient.Surface = gsfv;
        if (Option("--gradient-sample-smooth") is string gss && double.TryParse(gss, NumberStyles.Float, CultureInfo.InvariantCulture, out var gssv)) { gradient.SampleSmoothing = gssv; gradient.AutoSmoothing = false; }
        if (Option("--gradient-sample-radius") is string gsr && double.TryParse(gsr, NumberStyles.Float, CultureInfo.InvariantCulture, out var gsrv)) gradient.SampleRadius = gsrv;
        while (Option("--gradient-protect") is string gp)
        {
            var v = gp.Split(',').Select(t => double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN).ToArray();
            if (v.Length == 4 && v.All(double.IsFinite)) gradient.Protect.Add(new GradientRegion(v[0], v[1], v[2], v[3])); else Console.WriteLine($"bad --gradient-protect {gp}");
        }
        while (Option("--gradient") is string g)
        {
            var parts = g.Split(':', 3);
            if (parts.Length < 2 || ParseKind(parts[0]) is not ChannelKind gk) { Console.WriteLine($"bad --gradient {g}"); continue; }
            var v = parts.Length == 3 ? parts[2].Split(',').Select(t => double.TryParse(t.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : double.NaN).Where(double.IsFinite).ToArray() : [];
            GradientGesture? gesture = parts[1] switch
            {
                "line" when v.Length == 4 => GradientGesture.Line(v[0], v[1], v[2], v[3]),
                "ellipse" when v.Length >= 4 => GradientGesture.Ellipse(v[0], v[1], v[2], v[3], (v.Length > 4 ? v[4] : 0) * Math.PI / 180),
                "poly" or "polynomial" => GradientGesture.Polynomial(),
                "samples" => GradientGesture.Samples(),
                _ => null,
            };
            if (gesture == null) { Console.WriteLine($"bad --gradient {g}"); continue; }
            if (gesture.Shape == GradientShape.Polynomial && v.Length > 0) gradient.Degree = (int)v[0];
            if (gesture.Shape == GradientShape.Samples) { if (v.Length > 0) gradient.GridPerRow = (int)v[0]; if (v.Length > 1) gradient.GridTolerance = v[1]; }
            gradient.Specs[gk] = gradient.NewSpec(gesture);
        }
        if (Environment.GetEnvironmentVariable("CC_NO_GPU") == "1") GpuDenoise.Enabled = false;
        if (denoise > 0) Console.WriteLine($"GPU denoise: {(GpuDenoise.IsAvailable ? GpuDenoise.DeviceName : "off (CPU): " + GpuDenoise.UnavailableReason)}");
        Directory.CreateDirectory(outDir);

        // Load channels (in parallel).
        var sources = new Dictionary<ChannelKind, ChannelSource>();
        var t0 = Stopwatch.StartNew();
        var files = args_.ToArray();
        Parallel.For(0, files.Length, i =>
        {
            var path = files[i];
            try
            {
                var f = ImageLoader.Load(path);
                ChannelKind? kind = assignments.TryGetValue(Path.GetFileName(path), out var ak) ? ak : (assignments.TryGetValue(path, out var ak2) ? ak2 : f.DetectedKind);
                if (f.IsColor)
                {
                    var kinds = new[] { ChannelKind.Red, ChannelKind.Green, ChannelKind.Blue };
                    for (int c = 0; c < 3; c++)
                    {
                        var src = new ChannelSource(kinds[c], f.Planes[c], f.FileName, path);
                        lock (sources) sources[kinds[c]] = src;
                    }
                }
                else if (kind is ChannelKind k)
                {
                    var spots = dust.SpotsFor(k);
                    var t1 = Stopwatch.StartNew();
                    var plane = DustRemoval.Heal(f.Planes[0], spots);
                    int healMs = (int)t1.Elapsed.TotalMilliseconds;
                    string gradientNote = "";
                    if (gradient.SpecFor(k) is GradientSpec spec)
                    {
                        var t2 = Stopwatch.StartNew();
                        if (GradientRemoval.Correct(plane, spec, gradient.Protect) is var (corrected, fit))
                        {
                            plane = corrected;
                            var gg = fit.Gesture;
                            string geo = gg.Shape switch
                            {
                                GradientShape.Line => FormattableString.Invariant($"line {gg.X:0},{gg.Y:0} → {gg.X2:0},{gg.Y2:0}"),
                                GradientShape.Ellipse => FormattableString.Invariant($"ellipse centre {gg.X:0},{gg.Y:0} axes {gg.A:0}×{gg.B:0} angle {gg.Angle * 180 / Math.PI:0.0}°"),
                                GradientShape.Samples => $"{EnumRaw.Name(fit.Spec.Surface)} surface through {fit.SampleResults.Count(r => !r.IsRejected)} of {fit.SampleResults.Count} samples",
                                _ => $"polynomial degree {spec.Degree}",
                            };
                            gradientNote = FormattableString.Invariant($", gradient removed in {(int)t2.Elapsed.TotalMilliseconds} ms ({geo}, {EnumRaw.Name(spec.Mode)}, residual {fit.ResidualSigma:0.00}σ)");
                        }
                        else gradientNote = ", gradient model could not be fitted";
                    }
                    var src = new ChannelSource(k, plane, f.FileName, path, f.FormatDescription);
                    lock (sources) sources[k] = src;
                    Console.WriteLine($"loaded {f.FileName} → {k.RawValue()} ({(int)src.AnalysisMillis} ms analysis{(spots.Count == 0 ? "" : $", {spots.Count} dust spots healed in {healMs} ms")}{gradientNote})");
                }
                else Console.WriteLine($"skipped {f.FileName}: channel not recognised (use --assign)");
            }
            catch (Exception e) { Console.WriteLine($"{Path.GetFileName(path)}: {e.Message}"); }
        });
        Console.WriteLine($"loaded {sources.Count} channels in {(int)t0.Elapsed.TotalMilliseconds} ms: [{string.Join(", ", sources.Keys.SortedByRaw().Select(k => k.RawValue()))}]");
        if (sources.Count == 0) return 1;

        PixelRect? crop = null;
        if (cropArg != null)
        {
            if (cropArg == "auto")
            {
                var t = Stopwatch.StartNew();
                crop = AutoCrop.Detect(sources.Values.Select(s => s.Image).ToList());
                Console.WriteLine($"auto crop: {(crop is PixelRect c ? $"{c.X},{c.Y},{c.Width},{c.Height}" : "none")} in {(int)t.Elapsed.TotalMilliseconds} ms");
            }
            else
            {
                var p = cropArg.Split(',').Select(s => int.TryParse(s, out var v) ? v : int.MinValue).ToArray();
                if (p.Length == 4 && p.All(v => v != int.MinValue)) crop = new PixelRect(p[0], p[1], p[2], p[3]);
            }
            if (crop is PixelRect r) sources = sources.ToDictionary(kv => kv.Key, kv => new ChannelSource(kv.Value, r));
        }

        var settings = new CombineSettings();
        if (projectArg != null && File.Exists(projectArg))
        {
            try
            {
                var obj = SettingsJson.ParseObject(File.ReadAllText(projectArg));
                settings = obj.ContainsKey("settings") ? ProjectDocument.FromJson(obj).Settings : CombineSettings.FromJson(obj);
            }
            catch (Exception e) { Console.WriteLine($"project {projectArg}: {e.Message}"); }
        }
        if (normArg != null && EnumRaw.Parse<NormalizationMethod>(normArg) is NormalizationMethod nm) settings.Normalization.Method = nm;
        if (stageArg != null) settings.StretchStage = stageArg == "post" ? StretchStage.PostCombine : StretchStage.PreCombine;
        if (stretchArg != null && EnumRaw.Parse<StretchMethod>(stretchArg) is StretchMethod sm) settings.Stretch.Method = sm;
        settings.Stretch.Linked = linked;
        settings.Post.ScnrGreen = scnr;
        settings.Post.Saturation = sat;
        settings.Post.HueShift = hue;
        settings.Detail.Texture = texture; settings.Detail.Clarity = clarity; settings.Detail.Dehaze = dehaze;
        settings.Detail.Micro = micro; settings.Detail.LowFrequency = lowFrequency;
        settings.Detail.Sharpen = sharpen; settings.Detail.SharpenRadius = sharpenRadius; settings.Detail.SharpenMasking = sharpenMasking;
        settings.Denoise.Amount = denoise; settings.Denoise.Method = denoiseMethod; settings.Denoise.Color = denoiseColor;
        if (lum != null) ApplyLum(lum, settings);
        settings.Stars.Enabled = stars;
        if (starsBlend is StarsBlendMode sbm) settings.Stars.Mode = sbm;
        settings.Stars.Brightness = starsBrightness; settings.Stars.Contrast = starsContrast; settings.Stars.Normalize = starsWhite;
        StarsLayer? starsLayer = null;
        if (starsFile != null)
        {
            try
            {
                var f = ImageLoader.Load(starsFile);
                if (!f.IsColor) { Console.WriteLine($"{starsFile}: not a colour image"); return 1; }
                starsLayer = new StarsLayer(f.Planes.Take(3).ToList(), f.FileName, f.Path, f.FormatDescription);
                if (settings.Stars.UseMask && f.FileName.Contains("star", StringComparison.OrdinalIgnoreCase)) settings.Stars.UseMask = false;
                Console.WriteLine($"stars layer {f.FileName} ({f.FormatDescription})");
            }
            catch (Exception e) { Console.WriteLine($"{starsFile}: {e.Message}"); return 1; }
        }

        var t2 = Stopwatch.StartNew();
        var context = RenderContext.Make(settings.Normalization, sources);
        Console.WriteLine($"normalisation ({EnumRaw.Name(settings.Normalization.Method)}, ref={context.Reference?.RawValue() ?? "-"}) in {(int)t2.Elapsed.TotalMilliseconds} ms");
        foreach (var (k, t) in context.Transforms.OrderBy(kv => kv.Key.RawValue(), StringComparer.Ordinal)) Console.WriteLine($"  {k.RawValue()}: {t.Description}");

        if (saveProject != null)
        {
            var doc = new ProjectDocument();
            var s = settings.Clone();
            if (Palettes.Palette(paletteArg) is Palette pal) s.Select(pal);
            if (lum != null) ApplyLum(lum, s);
            doc.Settings = s;
            doc.Crop = crop;
            doc.Dust = dust;
            doc.Gradient = gradient;
            doc.Channels = sources.Values.OrderBy(v => v.Kind.RawValue(), StringComparer.Ordinal).Where(v => v.Path != null).Select(v => new ProjectDocument.ChannelRef(v.Kind, v.Path!)).ToList();
            doc.Save(saveProject);
            Console.WriteLine($"project written to {saveProject}");
        }

        var available = sources.Keys.ToHashSet();
        var palettes = paletteArg == "all" ? Palettes.Available(available) : (Palettes.Palette(paletteArg) is Palette one ? [one] : new List<Palette>());
        var format = RasterFormats.All.FirstOrDefault(f => f.RawValue().ToLowerInvariant().Replace(" ", "").StartsWith(formatArg.ToLowerInvariant()) || f.RawValue().ToLowerInvariant().Contains(formatArg.ToLowerInvariant()), RasterFormat.Png8);

        foreach (var palette in palettes)
        {
            var s = settings.Clone();
            s.Select(palette);
            if (lum != null) ApplyLum(lum, s);
            var t3 = Stopwatch.StartNew();
            try
            {
                RGBImage image;
                if (maxPixels > 0)
                {
                    var any = sources.Values.First();
                    int lvl = any.Pyramid.LevelIndex(maxPixels);
                    var planes = new Dictionary<ChannelKind, PlanarImage>();
                    var analysis = Renderer.AnalysisSamples(sources);
                    foreach (var (k, src) in sources)
                    {
                        var l = src.Pyramid.Level(lvl);
                        planes[k] = crop is PixelRect c ? l.Cropped(c.Scaled(lvl)) : l;
                    }
                    var req = new RenderRequest(planes, s, context, analysis, scale: 1f / (1 << lvl));
                    if (starsLayer is StarsLayer stl)
                    {
                        var r = crop is PixelRect cr ? cr.Scaled(lvl) : new PixelRect(0, 0, stl.Pyramids[0].Level(lvl).Width, stl.Pyramids[0].Level(lvl).Height);
                        req.Stars = stl.Input(lvl, r.X, r.Y, r.Width, r.Height);
                    }
                    var res = Renderer.Render(req);
                    image = res.Image;
                    Console.WriteLine($"{palette.Id}: level {lvl} {image.Width}×{image.Height} rendered in {(int)res.Millis} ms");
                }
                else
                {
                    image = Renderer.RenderFull(sources, s, context, null, crop, stars: starsLayer)!;
                    Console.WriteLine($"{palette.Id}: full {image.Width}×{image.Height} rendered in {(int)t3.Elapsed.TotalMilliseconds} ms");
                }
                var outPath = Path.Combine(outDir, $"{palette.Id}.{format.FileExtension()}");
                RasterWriter.Write(image.Planes, outPath, format);
            }
            catch (Exception e) { Console.WriteLine($"{palette.Id}: {e.Message}"); }
        }
        return 0;
    }

    default:
        Console.WriteLine($"unknown command {command}");
        return 1;
}
