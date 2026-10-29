namespace AstroCore;

/// <summary>Per-channel user adjustments.</summary>
public sealed partial class ChannelAdjust : IEquatable<ChannelAdjust>
{
    public bool Enabled = true;
    /// <summary>Linear multiplier applied after normalisation, before any stretch.</summary>
    public float Gain = 1;
    /// <summary>Linear pedestal in units of the reference background sigma.</summary>
    public float OffsetSigma = 0;
    /// <summary>Change of the auto-stretch target background for this channel (pre-combine stretch).</summary>
    public float StretchBoost = 0;
    /// <summary>Multiplier applied after the stretch (0…2).</summary>
    public float Weight = 1;
    /// <summary>Black point subtracted after the stretch (0…0.5), output renormalised.</summary>
    public float BlackPoint = 0;
    /// <summary>Gamma applied after the stretch (1 = none).</summary>
    public float Gamma = 1;

    public bool IsNeutral => Equals(new ChannelAdjust());

    public ChannelAdjust Clone() => (ChannelAdjust)MemberwiseClone();
    public bool Equals(ChannelAdjust? o) => o != null && Enabled == o.Enabled && Gain == o.Gain && OffsetSigma == o.OffsetSigma && StretchBoost == o.StretchBoost && Weight == o.Weight && BlackPoint == o.BlackPoint && Gamma == o.Gamma;
    public override bool Equals(object? obj) => Equals(obj as ChannelAdjust);
    public override int GetHashCode() => HashCode.Combine(Enabled, Gain, OffsetSigma, StretchBoost, Weight, BlackPoint, Gamma);
}

public enum CombineMode
{
    Matrix,
    PixelMath,
    /// <summary>Weighted average of channels (see <see cref="CombineSettings.MonoChannels"/>).</summary>
    Monochrome,
}

public enum StretchStage
{
    /// <summary>Each channel is stretched on its own, then combined (classic narrowband workflow).</summary>
    PreCombine,
    /// <summary>Channels are combined linearly, then the colour image is stretched.</summary>
    PostCombine,
}

public static class StretchStages
{
    public static readonly StretchStage[] All = [StretchStage.PreCombine, StretchStage.PostCombine];
    public static string DisplayName(this StretchStage s) => s == StretchStage.PreCombine ? "Stretch channels, then combine" : "Combine linear, then stretch";
    public static string ShortName(this StretchStage s) => s == StretchStage.PreCombine ? "Stretch first" : "Combine first";
}

public enum LuminanceMethod { Ratio, Lab }

public static class LuminanceMethods
{
    public static readonly LuminanceMethod[] All = [LuminanceMethod.Ratio, LuminanceMethod.Lab];
    public static string DisplayName(this LuminanceMethod m) => m == LuminanceMethod.Ratio ? "Luminance ratio (RGB)" : "CIE L*a*b* replacement";
}

/// <summary>Ready-made luminance recipes. Weighted presets drop missing channels and renormalise.</summary>
public enum LuminancePreset { Ha, Lum, HaMostly, HaOiii, Balanced, LumHa, MaxLines, MeanLines, Custom }

public static class LuminancePresets
{
    public static readonly LuminancePreset[] All = [LuminancePreset.Ha, LuminancePreset.Lum, LuminancePreset.HaMostly, LuminancePreset.HaOiii, LuminancePreset.Balanced, LuminancePreset.LumHa, LuminancePreset.MaxLines, LuminancePreset.MeanLines, LuminancePreset.Custom];

    public static string DisplayName(this LuminancePreset p) => p switch
    {
        LuminancePreset.Ha => "Hα",
        LuminancePreset.Lum => "Luminance (L)",
        LuminancePreset.HaMostly => "0.8 Hα + 0.1 OIII + 0.1 SII",
        LuminancePreset.HaOiii => "0.7 Hα + 0.3 OIII (HOO)",
        LuminancePreset.Balanced => "0.5 Hα + 0.25 OIII + 0.25 SII",
        LuminancePreset.LumHa => "0.5 L + 0.5 Hα",
        LuminancePreset.MaxLines => "Max of narrowband lines",
        LuminancePreset.MeanLines => "Mean of narrowband lines",
        _ => "Custom expression…",
    };

    /// <summary>Weights per channel for the weighted presets (null for the others).</summary>
    public static Dictionary<ChannelKind, float>? Weights(this LuminancePreset p) => p switch
    {
        LuminancePreset.Ha => new() { [ChannelKind.Ha] = 1 },
        LuminancePreset.Lum => new() { [ChannelKind.Luminance] = 1 },
        LuminancePreset.HaMostly => new() { [ChannelKind.Ha] = 0.8f, [ChannelKind.OIII] = 0.1f, [ChannelKind.SII] = 0.1f },
        LuminancePreset.HaOiii => new() { [ChannelKind.Ha] = 0.7f, [ChannelKind.OIII] = 0.3f },
        LuminancePreset.Balanced => new() { [ChannelKind.Ha] = 0.5f, [ChannelKind.OIII] = 0.25f, [ChannelKind.SII] = 0.25f },
        LuminancePreset.LumHa => new() { [ChannelKind.Luminance] = 0.5f, [ChannelKind.Ha] = 0.5f },
        _ => null,
    };

    /// <summary>Pixel Math expression for the available channels; null when nothing usable is loaded.</summary>
    public static string? Expression(this LuminancePreset p, IReadOnlySet<ChannelKind> available, string custom)
    {
        switch (p)
        {
            case LuminancePreset.Custom:
            {
                var t = custom.Trim();
                return t.Length == 0 ? null : t;
            }
            case LuminancePreset.MaxLines:
            case LuminancePreset.MeanLines:
            {
                var lines = ChannelKinds.Narrowband.Where(available.Contains).ToList();
                if (lines.Count == 0) return null;
                if (lines.Count == 1) return lines[0].PixelMathName();
                return $"{(p == LuminancePreset.MaxLines ? "max" : "mean")}({string.Join(", ", lines.Select(k => k.PixelMathName()))})";
            }
            default:
            {
                var w = p.Weights();
                if (w == null) return null;
                var present = ChannelKinds.All.Where(k => w.ContainsKey(k) && available.Contains(k)).ToList();
                if (present.Count == 0) return null;
                float total = present.Sum(k => w[k]);
                if (present.Count == 1) return present[0].PixelMathName();
                return string.Join(" + ", present.Select(k => Fmt.G3(w[k] / total) + "*" + k.PixelMathName()));
            }
        }
    }
}

/// <summary>Number formatting matching the macOS build's printf patterns.</summary>
public static class Fmt
{
    /// <summary>"%.3g" (3 significant digits, trailing zeros removed).</summary>
    public static string G3(float v) => G(v, 3);
    public static string G4(float v) => G(v, 4);
    public static string G6(float v) => G(v, 6);

    public static string G(double v, int digits)
    {
        if (v == 0) return "0";
        if (!double.IsFinite(v)) return v.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int exp = (int)Math.Floor(Math.Log10(Math.Abs(v)));
        // Round to the requested significant digits first (the exponent may change after rounding).
        double rounded = Math.Round(v, Math.Max(0, Math.Min(15, digits - 1 - exp)), MidpointRounding.AwayFromZero);
        if (rounded != 0) exp = (int)Math.Floor(Math.Log10(Math.Abs(rounded)));
        if (exp < -5 || exp >= digits)
        {
            var s = rounded.ToString("E" + (digits - 1), System.Globalization.CultureInfo.InvariantCulture);
            // Trim trailing zeros of the mantissa, format the exponent like printf (e+05).
            var parts = s.Split('E');
            var mant = parts[0].Contains('.') ? parts[0].TrimEnd('0').TrimEnd('.') : parts[0];
            int e = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
            return $"{mant}e{(e < 0 ? "-" : "+")}{Math.Abs(e):00}";
        }
        int decimals = Math.Max(0, digits - 1 - exp);
        var f = rounded.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
        if (f.Contains('.')) f = f.TrimEnd('0').TrimEnd('.');
        return f;
    }
}

public sealed partial class LuminanceSettings : IEquatable<LuminanceSettings>
{
    /// <summary>Inject a luminance into the colour result.</summary>
    public bool Enabled = true;
    public LuminancePreset Preset = LuminancePreset.Ha;
    /// <summary>Expression used by the Custom preset (stretched channels in [0,1] for the pre-combine stage).</summary>
    public string CustomExpression = "0.8*Ha + 0.1*OIII + 0.1*SII";
    public float Amount = 1;
    public LuminanceMethod Method = LuminanceMethod.Lab;

    /// <summary>
    /// The expression that will actually be used for the loaded channels (falls back from Hα to L to
    /// nothing when channels are missing).
    /// </summary>
    public string? ResolvedExpression(IReadOnlySet<ChannelKind> available)
    {
        if (!Enabled) return null;
        if (Preset.Expression(available, CustomExpression) is string e) return e;
        if (Preset != LuminancePreset.Custom)
        {
            foreach (var fallback in new[] { LuminancePreset.Ha, LuminancePreset.Lum })
            {
                if (fallback == Preset) continue;
                if (fallback.Expression(available, CustomExpression) is string f) return f;
            }
        }
        return null;
    }

    /// <summary>Channels referenced by the resolved expression.</summary>
    public HashSet<ChannelKind> RequiredChannels(IReadOnlySet<ChannelKind> available)
    {
        var set = new HashSet<ChannelKind>();
        if (ResolvedExpression(available) is not string e || !PixelMath.TryParse(e, out var expr)) return set;
        foreach (var v in expr!.Variables) if (ChannelKinds.FromPixelMathIdentifier(v) is ChannelKind k) set.Add(k);
        return set;
    }

    public LuminanceSettings Clone() => (LuminanceSettings)MemberwiseClone();
    public bool Equals(LuminanceSettings? o) => o != null && Enabled == o.Enabled && Preset == o.Preset && CustomExpression == o.CustomExpression && Amount == o.Amount && Method == o.Method;
    public override bool Equals(object? obj) => Equals(obj as LuminanceSettings);
    public override int GetHashCode() => HashCode.Combine(Enabled, Preset, CustomExpression, Amount, Method);
}

public sealed partial class PostSettings : IEquatable<PostSettings>
{
    public float Saturation = 1;
    public float ScnrGreen = 0;
    public float HueShift = 0;        // degrees
    public float Gamma = 1;
    public float BlackPoint = 0;
    public float WhitePoint = 1;
    public float Vibrance = 0;       // boosts low-saturation pixels only

    public PostSettings Clone() => (PostSettings)MemberwiseClone();
    public bool Equals(PostSettings? o) => o != null && Saturation == o.Saturation && ScnrGreen == o.ScnrGreen && HueShift == o.HueShift && Gamma == o.Gamma && BlackPoint == o.BlackPoint && WhitePoint == o.WhitePoint && Vibrance == o.Vibrance;
    public override bool Equals(object? obj) => Equals(obj as PostSettings);
    public override int GetHashCode() => HashCode.Combine(Saturation, ScnrGreen, HueShift, Gamma, BlackPoint, WhitePoint, Vibrance);
}

public enum StarsBlendMode { Screen, Lighten, Add }

public static class StarsBlendModes
{
    public static readonly StarsBlendMode[] All = [StarsBlendMode.Screen, StarsBlendMode.Lighten, StarsBlendMode.Add];
    public static string DisplayName(this StarsBlendMode m) => m.ToString();
}

/// <summary>Camera-Raw style "selective colour": per hue band hue/saturation/lightness.</summary>
public enum ColorBand { Reds, Oranges, Yellows, Greens, Teals, Blues, Magentas }

public static class ColorBands
{
    public static readonly ColorBand[] All = [ColorBand.Reds, ColorBand.Oranges, ColorBand.Yellows, ColorBand.Greens, ColorBand.Teals, ColorBand.Blues, ColorBand.Magentas];
    public static string DisplayName(this ColorBand b) => b.ToString();
    /// <summary>Centre hue in degrees.</summary>
    public static float Hue(this ColorBand b) => b switch
    {
        ColorBand.Reds => 0, ColorBand.Oranges => 30, ColorBand.Yellows => 60, ColorBand.Greens => 120,
        ColorBand.Teals => 180, ColorBand.Blues => 240, _ => 300,
    };
}

public sealed partial class ColorBandAdjust : IEquatable<ColorBandAdjust>
{
    /// <summary>Hue shift in degrees (−60…60).</summary>
    public float Hue = 0;
    /// <summary>Saturation change (−1…1).</summary>
    public float Saturation = 0;
    /// <summary>Lightness change (−1…1).</summary>
    public float Lightness = 0;
    public bool IsNeutral => Hue == 0 && Saturation == 0 && Lightness == 0;

    public ColorBandAdjust Clone() => (ColorBandAdjust)MemberwiseClone();
    public bool Equals(ColorBandAdjust? o) => o != null && Hue == o.Hue && Saturation == o.Saturation && Lightness == o.Lightness;
    public override bool Equals(object? obj) => Equals(obj as ColorBandAdjust);
    public override int GetHashCode() => HashCode.Combine(Hue, Saturation, Lightness);
}

public sealed partial class ColorBandsSettings : IEquatable<ColorBandsSettings>
{
    public Dictionary<ColorBand, ColorBandAdjust> Bands = new();
    public ColorBandAdjust Adjust(ColorBand b) => Bands.TryGetValue(b, out var a) ? a : new ColorBandAdjust();
    public void Set(ColorBand b, ColorBandAdjust a) { if (a.IsNeutral) Bands.Remove(b); else Bands[b] = a; }
    public bool IsNeutral => Bands.Values.All(v => v.IsNeutral);

    public ColorBandsSettings Clone()
    {
        var c = new ColorBandsSettings();
        foreach (var (k, v) in Bands) c.Bands[k] = v.Clone();
        return c;
    }
    public bool Equals(ColorBandsSettings? o) => o != null && SettingsJson.SameJson(this, o);
    public override bool Equals(object? obj) => Equals(obj as ColorBandsSettings);
    public override int GetHashCode() => SettingsJson.JsonHash(this);
}

/// <summary>Local-contrast operations (Camera Raw "Clarity" and "Dehaze").</summary>
public sealed partial class DetailSettings : IEquatable<DetailSettings>
{
    /// <summary>−1…1: fine-detail local contrast (radius ≈ 0.6 % of the image width).</summary>
    public float Texture = 0;
    /// <summary>−0.5…2: mid-tone local contrast at a broader scale (radius ≈ 6 % of the width).</summary>
    public float Clarity = 0;
    /// <summary>−1…1: removes (or adds) the large-scale haze component (radius ≈ 25 % of the width).</summary>
    public float Dehaze = 0;
    /// <summary>0…2: unsharp-mask amount on the luminance.</summary>
    public float Sharpen = 0;
    /// <summary>Unsharp-mask radius (Gaussian σ) in full-resolution pixels.</summary>
    public float SharpenRadius = 1.5f;
    /// <summary>0…1: protects low-contrast (noise) areas from the sharpening, like Camera Raw's Masking.</summary>
    public float SharpenMasking = 0;
    public bool IsNeutral => Texture == 0 && Clarity == 0 && Dehaze == 0 && Sharpen == 0;

    public DetailSettings Clone() => (DetailSettings)MemberwiseClone();
    public bool Equals(DetailSettings? o) => o != null && Texture == o.Texture && Clarity == o.Clarity && Dehaze == o.Dehaze && Sharpen == o.Sharpen && SharpenRadius == o.SharpenRadius && SharpenMasking == o.SharpenMasking;
    public override bool Equals(object? obj) => Equals(obj as DetailSettings);
    public override int GetHashCode() => HashCode.Combine(Texture, Clarity, Dehaze, Sharpen, SharpenRadius, SharpenMasking);
}

public enum DenoiseMethod { Wavelets, NonLocalMeans, Guided, Bm3d }

public static class DenoiseMethods
{
    public static readonly DenoiseMethod[] All = [DenoiseMethod.Wavelets, DenoiseMethod.NonLocalMeans, DenoiseMethod.Guided, DenoiseMethod.Bm3d];

    public static string DisplayName(this DenoiseMethod m) => m switch
    {
        DenoiseMethod.Wavelets => "Wavelets (starlet)",
        DenoiseMethod.NonLocalMeans => "Non-local means",
        DenoiseMethod.Guided => "Guided filter",
        _ => "BM3D",
    };

    /// <summary>Short description shown under the Denoise controls.</summary>
    public static string Summary(this DenoiseMethod m) => m switch
    {
        DenoiseMethod.Wavelets => "Starlet (à trous) wavelets: fine-scale coefficients below k·σ of the measured noise are shrunk. The classic astro method, fast, keeps stars and edges; blotchy sky when pushed.",
        DenoiseMethod.NonLocalMeans => "Weighted mean of the pixels whose 5×5 patch looks alike within the search window (Buades 2005). Keeps fine texture and faint filaments; cost grows with the search radius squared.",
        DenoiseMethod.Guided => "The output is a local linear function of the image itself (He 2010): flat sky is averaged, edges and gradients stay. Fastest and mildest, for light cleanup.",
        _ => "Similar 8×8 blocks stacked and filtered together (DCT + Haar, hard threshold then Wiener pass; Dabov 2007), on every CPU core. Strongest on fine noise; square artefacts when pushed.",
    };
}

public sealed partial class DenoiseSettings : IEquatable<DenoiseSettings>
{
    public DenoiseMethod Method = DenoiseMethod.Wavelets;
    /// <summary>0…1 luminance noise reduction strength (0 = off).</summary>
    public float Amount = 0;
    /// <summary>Wavelet scales denoised (1…5).</summary>
    public int Layers = 3;
    /// <summary>Non-local means search radius in pixels (3…10).</summary>
    public int Search = 5;
    /// <summary>Guided-filter window radius in pixels (1…8).</summary>
    public int Radius = 3;
    /// <summary>BM3D block-matching window radius in pixels (6…24).</summary>
    public int BlockSearch = 16;
    /// <summary>0…1: keeps bright structures (stars, cores) untouched.</summary>
    public float ProtectBright = 0.5f;
    /// <summary>0…1 chroma (colour mottling) smoothing.</summary>
    public float Color = 0;
    public bool IsNeutral => Amount == 0 && Color == 0;

    public DenoiseSettings Clone() => (DenoiseSettings)MemberwiseClone();
    public bool Equals(DenoiseSettings? o) => o != null && Method == o.Method && Amount == o.Amount && Layers == o.Layers && Search == o.Search && Radius == o.Radius && BlockSearch == o.BlockSearch && ProtectBright == o.ProtectBright && Color == o.Color;
    public override bool Equals(object? obj) => Equals(obj as DenoiseSettings);
    public override int GetHashCode() => HashCode.Combine(Method, Amount, Layers, Search, Radius, BlockSearch, ProtectBright, Color);
}

public enum StarsStretchMode { Auto, None, AutoSTF }

public static class StarsStretchModes
{
    public static readonly StarsStretchMode[] All = [StarsStretchMode.Auto, StarsStretchMode.None, StarsStretchMode.AutoSTF];
    public static string DisplayName(this StarsStretchMode m) => m switch
    {
        StarsStretchMode.Auto => "Automatic",
        StarsStretchMode.None => "Already stretched",
        _ => "Linear (auto-stretch)",
    };
}

public sealed partial class StarsBlendSettings : IEquatable<StarsBlendSettings>
{
    public bool Enabled = false;
    /// <summary>Whether the RGB star channels need stretching before blending.</summary>
    public StarsStretchMode Stretch = StarsStretchMode.Auto;
    public float Amount = 1;
    /// <summary>Luminance threshold above which the RGB image is considered "star".</summary>
    public float Threshold = 0.15f;
    public float Softness = 0.25f;
    public StarsBlendMode Mode = StarsBlendMode.Screen;
    /// <summary>Use the whole RGB image (no mask) — for star-only images from star removal tools.</summary>
    public bool UseMask = true;
    public float Saturation = 1.3f;

    public StarsBlendSettings Clone() => (StarsBlendSettings)MemberwiseClone();
    public bool Equals(StarsBlendSettings? o) => o != null && Enabled == o.Enabled && Stretch == o.Stretch && Amount == o.Amount && Threshold == o.Threshold && Softness == o.Softness && Mode == o.Mode && UseMask == o.UseMask && Saturation == o.Saturation;
    public override bool Equals(object? obj) => Equals(obj as StarsBlendSettings);
    public override int GetHashCode() => HashCode.Combine(Enabled, Stretch, Amount, Threshold, Softness, Mode, UseMask, Saturation);
}

/// <summary>Linear mixing matrix: for every channel, its weights into R, G, B.</summary>
public sealed partial class PaletteMatrix : IEquatable<PaletteMatrix>
{
    public Dictionary<ChannelKind, float[]> Weights = new();

    public PaletteMatrix() { }

    public PaletteMatrix(IDictionary<ChannelKind, float[]> weights)
    {
        foreach (var (k, w) in weights)
        {
            var v = new float[3];
            Array.Copy(w, v, Math.Min(3, w.Length));
            Weights[k] = v;
        }
    }

    public static PaletteMatrix Empty => new();

    public HashSet<ChannelKind> RequiredChannels => Weights.Where(kv => kv.Value.Any(w => w != 0)).Select(kv => kv.Key).ToHashSet();

    public float Weight(ChannelKind kind, int output) => Weights.TryGetValue(kind, out var w) ? w[output] : 0;

    public void Set(ChannelKind kind, int output, float value)
    {
        if (!Weights.TryGetValue(kind, out var w)) { w = new float[3]; Weights[kind] = w; }
        w[output] = value;
    }

    /// <summary>Channels that contribute anywhere, in canonical order.</summary>
    public List<ChannelKind> OrderedChannels => ChannelKinds.All.Where(Weights.ContainsKey).ToList();

    public PaletteMatrix Clone() => new(Weights);
    public bool Equals(PaletteMatrix? o) => o != null && SettingsJson.SameJson(this, o);
    public override bool Equals(object? obj) => Equals(obj as PaletteMatrix);
    public override int GetHashCode() => SettingsJson.JsonHash(this);
}

/// <summary>Everything that defines a combination. Serialisable so projects and presets can be saved.</summary>
public sealed partial class CombineSettings : IEquatable<CombineSettings>
{
    public CombineMode Mode = CombineMode.Matrix;
    public string PaletteID = "sho";
    public PaletteMatrix Matrix = Palettes.Palette("sho")?.Kind is PaletteKind.MatrixKind m ? m.Matrix.Clone() : PaletteMatrix.Empty;
    public PixelMathProgram PixelMath = new("SII", "Ha", "OIII");
    /// <summary>Channels used by the monochrome mode; null = every loaded channel.</summary>
    public HashSet<ChannelKind>? MonoChannels = null;
    public Dictionary<ChannelKind, ChannelAdjust> Channels = new();
    public NormalizationSettings Normalization = new();
    public StretchSettings Stretch = new();
    public StretchStage StretchStage = StretchStage.PreCombine;
    public LuminanceSettings Luminance = new();
    public ColorBandsSettings ColorBands = new();
    public PostSettings Post = new();
    public DetailSettings Detail = new();
    public DenoiseSettings Denoise = new();
    public StarsBlendSettings Stars = new();

    public ChannelAdjust Adjust(ChannelKind k) => Channels.TryGetValue(k, out var a) ? a : new ChannelAdjust();

    public void SetAdjust(ChannelKind k, ChannelAdjust a)
    {
        if (a.IsNeutral) Channels.Remove(k); else Channels[k] = a;
    }

    /// <summary>Selects a preset palette, copying its definition so it can be edited.</summary>
    public void Select(Palette palette)
    {
        PaletteID = palette.Id;
        switch (palette.Kind)
        {
            case PaletteKind.MatrixKind m: Mode = CombineMode.Matrix; Matrix = m.Matrix.Clone(); break;
            case PaletteKind.PixelMathKind p: Mode = CombineMode.PixelMath; PixelMath = p.Program.Clone(); break;
            case PaletteKind.MonochromeKind: Mode = CombineMode.Monochrome; MonoChannels = null; break;
        }
        if (palette.Luminance is ChannelKind l)
        {
            Luminance.Enabled = true;
            Luminance.Preset = l == ChannelKind.Luminance ? LuminancePreset.Lum : (l == ChannelKind.Ha ? LuminancePreset.Ha : LuminancePreset.Custom);
            if (Luminance.Preset == LuminancePreset.Custom) Luminance.CustomExpression = l.PixelMathName();
        }
    }

    /// <summary>Channels used by the current combination (excluding luminance / stars).</summary>
    public HashSet<ChannelKind> RequiredChannels
    {
        get
        {
            switch (Mode)
            {
                case CombineMode.Matrix: return Matrix.RequiredChannels;
                case CombineMode.PixelMath:
                    try { return PixelMath.Compile().RequiredChannels; } catch (PixelMathException) { return new HashSet<ChannelKind>(); }
                default: return MonoChannels != null ? new HashSet<ChannelKind>(MonoChannels) : new HashSet<ChannelKind>();
            }
        }
    }

    /// <summary>Luminance injection is skipped for the mono check palettes (they would all look alike).</summary>
    public bool LuminanceApplies => Luminance.Enabled && Mode != CombineMode.Monochrome && (Palettes.Palette(PaletteID)?.AllowsLuminance ?? true);

    /// <summary>Channels averaged by the monochrome mode for the loaded set.</summary>
    public HashSet<ChannelKind> MonochromeChannels(IReadOnlySet<ChannelKind> available)
    {
        var s = new HashSet<ChannelKind>(MonoChannels ?? (IEnumerable<ChannelKind>)available);
        s.IntersectWith(available);
        return s;
    }

    /// <summary>Every channel the pipeline will read, given what is loaded.</summary>
    public HashSet<ChannelKind> AllChannels(IReadOnlySet<ChannelKind> available)
    {
        var s = RequiredChannels;
        if (Mode == CombineMode.Monochrome) s.UnionWith(MonochromeChannels(available));
        if (LuminanceApplies) s.UnionWith(Luminance.RequiredChannels(available));
        if (Stars.Enabled) { s.Add(ChannelKind.Red); s.Add(ChannelKind.Green); s.Add(ChannelKind.Blue); }
        return s;
    }

    public CombineSettings Clone()
    {
        var c = new CombineSettings
        {
            Mode = Mode, PaletteID = PaletteID, Matrix = Matrix.Clone(), PixelMath = PixelMath.Clone(),
            MonoChannels = MonoChannels == null ? null : new HashSet<ChannelKind>(MonoChannels),
            Normalization = Normalization.Clone(), Stretch = Stretch.Clone(), StretchStage = StretchStage, Luminance = Luminance.Clone(),
            ColorBands = ColorBands.Clone(), Post = Post.Clone(), Detail = Detail.Clone(), Denoise = Denoise.Clone(), Stars = Stars.Clone(),
        };
        foreach (var (k, v) in Channels) c.Channels[k] = v.Clone();
        return c;
    }

    /// <summary>Default settings with a given palette selected (used to detect untouched settings).</summary>
    public static CombineSettings DefaultWith(string paletteId)
    {
        var s = new CombineSettings();
        if (Palettes.Palette(paletteId) is Palette p) s.Select(p);
        return s;
    }

    /// <summary>True when the two settings differ only in the palette definition (thumbnails stay valid).</summary>
    public static bool DifferOnlyInPalette(CombineSettings a, CombineSettings b)
    {
        var x = a.Clone(); var y = b.Clone();
        x.Mode = CombineMode.Matrix; y.Mode = CombineMode.Matrix;
        x.PaletteID = ""; y.PaletteID = "";
        x.Matrix = PaletteMatrix.Empty; y.Matrix = PaletteMatrix.Empty;
        x.PixelMath = new PixelMathProgram("", "", ""); y.PixelMath = new PixelMathProgram("", "", "");
        x.MonoChannels = null; y.MonoChannels = null;
        return x.Equals(y);
    }

    public bool Equals(CombineSettings? o) => o != null && SettingsJson.SameJson(this, o);
    public override bool Equals(object? obj) => Equals(obj as CombineSettings);
    public override int GetHashCode() => SettingsJson.JsonHash(this);
}
