using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AstroCore;

/// <summary>Types that serialise to the project-file JSON (the layout shared with the macOS build).</summary>
public interface IJsonSettings
{
    JsonObject ToJson();
}

/// <summary>Enum ↔ identifier mapping: the identifiers are the C# names with a lower-case first letter ("autoSTF", "nonLocalMeans", …).</summary>
public static class EnumRaw
{
    public static string Name<T>(T v) where T : struct, Enum
    {
        var s = v.ToString();
        return s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];
    }

    public static T? Parse<T>(string? s) where T : struct, Enum
    {
        if (s == null) return null;
        foreach (var v in Enum.GetValues<T>()) if (Name(v) == s) return v;
        return null;
    }
}

/// <summary>
/// Reading and writing helpers. Values are decoded leniently so that projects saved by older (or
/// newer) versions still open: missing or unreadable keys fall back to the defaults.
/// </summary>
public static class SettingsJson
{
    static readonly DateTime ReferenceDate = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static bool SameJson(IJsonSettings a, IJsonSettings b) => ReferenceEquals(a, b) || Canonical(a) == Canonical(b);
    public static int JsonHash(IJsonSettings a) => Canonical(a).GetHashCode();
    public static string Canonical(IJsonSettings a) => Serialize(a.ToJson(), pretty: false);

    /// <summary>Serialises with the keys of every object sorted (ordinal), like the macOS build.</summary>
    public static string Serialize(JsonNode node, bool pretty) => SortKeys(node)!.ToJsonString(pretty ? Pretty : Compact);

    static JsonNode? SortKeys(JsonNode? n)
    {
        switch (n)
        {
            case JsonObject o:
            {
                var sorted = new JsonObject();
                foreach (var kv in o.OrderBy(kv => kv.Key, StringComparer.Ordinal)) sorted[kv.Key] = SortKeys(kv.Value);
                return sorted;
            }
            case JsonArray a:
            {
                var arr = new JsonArray();
                foreach (var e in a) arr.Add(SortKeys(e));
                return arr;
            }
            case JsonValue v: return v.DeepClone();
            default: return null;
        }
    }

    public static JsonObject ParseObject(string json)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        return node as JsonObject ?? throw new JsonException("Expected a JSON object");
    }

    // Reading

    public static JsonObject? Obj(JsonObject? o, string key) => o != null && o.TryGetPropertyValue(key, out var v) ? v as JsonObject : null;
    public static JsonArray? Arr(JsonObject? o, string key) => o != null && o.TryGetPropertyValue(key, out var v) ? v as JsonArray : null;
    public static bool Has(JsonObject? o, string key) => o != null && o.ContainsKey(key);
    public static bool IsNull(JsonObject? o, string key) => o != null && o.TryGetPropertyValue(key, out var v) && v == null;

    static bool TryDouble(JsonNode? n, out double d)
    {
        d = 0;
        if (n is not JsonValue v) return false;
        // Values parsed from text answer TryGetValue<double>; values built in memory keep their CLR type.
        if (v.TryGetValue<double>(out d)) return true;
        if (v.TryGetValue<float>(out var f)) { d = f; return true; }
        if (v.TryGetValue<long>(out var l)) { d = l; return true; }
        if (v.TryGetValue<int>(out var i)) { d = i; return true; }
        if (v.TryGetValue<decimal>(out var m)) { d = (double)m; return true; }
        return false;
    }

    public static float F(JsonObject? o, string key, float fallback)
        => o != null && o.TryGetPropertyValue(key, out var v) && TryDouble(v, out var d) ? (float)d : fallback;

    public static double D(JsonObject? o, string key, double fallback)
        => o != null && o.TryGetPropertyValue(key, out var v) && TryDouble(v, out var d) ? d : fallback;

    public static int I(JsonObject? o, string key, int fallback)
        => o != null && o.TryGetPropertyValue(key, out var v) && TryDouble(v, out var d) && d == Math.Floor(d) && Math.Abs(d) < int.MaxValue ? (int)d : fallback;

    public static bool B(JsonObject? o, string key, bool fallback)
        => o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<bool>(out var b) ? b : fallback;

    public static string S(JsonObject? o, string key, string fallback)
        => o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : fallback;

    public static string? SOpt(JsonObject? o, string key, string? fallback)
    {
        if (o == null || !o.TryGetPropertyValue(key, out var v)) return fallback;
        if (v == null) return null;
        return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? s : fallback;
    }

    public static T E<T>(JsonObject? o, string key, T fallback) where T : struct, Enum
        => o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? EnumRaw.Parse<T>(s) ?? fallback : fallback;

    public static ChannelKind K(JsonObject? o, string key, ChannelKind fallback)
        => o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) ? ChannelKinds.FromRawValue(s) ?? fallback : fallback;

    /// <summary>Optional channel: missing → fallback, null → null, invalid → fallback.</summary>
    public static ChannelKind? KOpt(JsonObject? o, string key, ChannelKind? fallback)
    {
        if (o == null || !o.TryGetPropertyValue(key, out var v)) return fallback;
        if (v == null) return null;
        return v is JsonValue jv && jv.TryGetValue<string>(out var s) ? ChannelKinds.FromRawValue(s) ?? fallback : fallback;
    }

    public static Guid G(JsonObject? o, string key, Guid? fallback = null)
        => o != null && o.TryGetPropertyValue(key, out var v) && v is JsonValue jv && jv.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g) ? g : fallback ?? Guid.NewGuid();

    /// <summary>Dates are seconds since 2001-01-01 UTC (Foundation's reference date), as the macOS build writes them.</summary>
    public static DateTime Date(JsonObject? o, string key, DateTime fallback)
        => o != null && o.TryGetPropertyValue(key, out var v) && TryDouble(v, out var d) ? ReferenceDate.AddSeconds(d) : fallback;

    public static byte[]? Bytes(JsonObject? o, string key)
    {
        if (o == null || !o.TryGetPropertyValue(key, out var v) || v is not JsonValue jv || !jv.TryGetValue<string>(out var s)) return null;
        try { return Convert.FromBase64String(s); } catch { return null; }
    }

    public static float[] Floats(JsonObject? o, string key, float[] fallback)
    {
        var a = Arr(o, key);
        if (a == null) return (float[])fallback.Clone();
        var list = new List<float>();
        foreach (var e in a) { if (TryDouble(e, out var d)) list.Add((float)d); else return (float[])fallback.Clone(); }
        return list.ToArray();
    }

    // Writing

    public static JsonValue V(float f) => JsonValue.Create(f);
    public static JsonValue V(double d) => JsonValue.Create(d);
    public static JsonValue V(int i) => JsonValue.Create(i);
    public static JsonValue V(bool b) => JsonValue.Create(b);
    public static JsonValue V(string s) => JsonValue.Create(s)!;
    public static JsonValue V<T>(T e) where T : struct, Enum => JsonValue.Create(EnumRaw.Name(e))!;
    public static JsonValue V(ChannelKind k) => JsonValue.Create(k.RawValue())!;
    public static JsonValue V(Guid g) => JsonValue.Create(g.ToString().ToUpperInvariant())!;
    public static JsonValue V(DateTime d) => JsonValue.Create((d.ToUniversalTime() - ReferenceDate).TotalSeconds);
    public static JsonArray V(IEnumerable<float> fs) { var a = new JsonArray(); foreach (var f in fs) a.Add(V(f)); return a; }
}

// ---- Per-type encoders / tolerant decoders ------------------------------------------------------

public sealed partial class ChannelAdjust : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["enabled"] = SettingsJson.V(Enabled), ["gain"] = SettingsJson.V(Gain), ["offsetSigma"] = SettingsJson.V(OffsetSigma),
        ["stretchBoost"] = SettingsJson.V(StretchBoost), ["weight"] = SettingsJson.V(Weight), ["blackPoint"] = SettingsJson.V(BlackPoint), ["gamma"] = SettingsJson.V(Gamma),
    };

    public static ChannelAdjust FromJson(JsonObject? c)
    {
        var d = new ChannelAdjust();
        return new ChannelAdjust
        {
            Enabled = SettingsJson.B(c, "enabled", d.Enabled), Gain = SettingsJson.F(c, "gain", d.Gain), OffsetSigma = SettingsJson.F(c, "offsetSigma", d.OffsetSigma),
            StretchBoost = SettingsJson.F(c, "stretchBoost", d.StretchBoost), Weight = SettingsJson.F(c, "weight", d.Weight),
            BlackPoint = SettingsJson.F(c, "blackPoint", d.BlackPoint), Gamma = SettingsJson.F(c, "gamma", d.Gamma),
        };
    }
}

public sealed partial class NormalizationSettings : IJsonSettings
{
    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["method"] = SettingsJson.V(Method), ["useStarMask"] = SettingsJson.V(UseStarMask), ["signalPercentile"] = SettingsJson.V(SignalPercentile),
            ["fitRejectSigma"] = SettingsJson.V(FitRejectSigma), ["fitIterations"] = SettingsJson.V(FitIterations), ["histogramClip"] = SettingsJson.V(HistogramClip),
        };
        if (Reference is ChannelKind r) o["reference"] = SettingsJson.V(r);
        return o;
    }

    public static NormalizationSettings FromJson(JsonObject? c)
    {
        var d = new NormalizationSettings();
        return new NormalizationSettings
        {
            Method = SettingsJson.E(c, "method", d.Method), Reference = SettingsJson.KOpt(c, "reference", d.Reference),
            UseStarMask = SettingsJson.B(c, "useStarMask", d.UseStarMask), SignalPercentile = SettingsJson.F(c, "signalPercentile", d.SignalPercentile),
            FitRejectSigma = SettingsJson.F(c, "fitRejectSigma", d.FitRejectSigma), FitIterations = SettingsJson.I(c, "fitIterations", d.FitIterations),
            HistogramClip = SettingsJson.F(c, "histogramClip", d.HistogramClip),
        };
    }
}

public sealed partial class StretchSettings : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["method"] = SettingsJson.V(Method), ["targetBackground"] = SettingsJson.V(TargetBackground), ["shadowsClipping"] = SettingsJson.V(ShadowsClipping),
        ["highlightsPercentile"] = SettingsJson.V(HighlightsPercentile), ["linked"] = SettingsJson.V(Linked),
        ["mtfShadows"] = SettingsJson.V(MtfShadows), ["mtfMidtones"] = SettingsJson.V(MtfMidtones), ["mtfHighlights"] = SettingsJson.V(MtfHighlights),
        ["arcsinhStretch"] = SettingsJson.V(ArcsinhStretch),
        ["ghsLogD"] = SettingsJson.V(GhsLogD), ["ghsB"] = SettingsJson.V(GhsB), ["ghsSP"] = SettingsJson.V(GhsSP), ["ghsLP"] = SettingsJson.V(GhsLP), ["ghsHP"] = SettingsJson.V(GhsHP),
        ["logK"] = SettingsJson.V(LogK), ["gamma"] = SettingsJson.V(Gamma),
    };

    public static StretchSettings FromJson(JsonObject? c)
    {
        var d = new StretchSettings();
        return new StretchSettings
        {
            Method = SettingsJson.E(c, "method", d.Method), TargetBackground = SettingsJson.F(c, "targetBackground", d.TargetBackground),
            ShadowsClipping = SettingsJson.F(c, "shadowsClipping", d.ShadowsClipping), HighlightsPercentile = SettingsJson.F(c, "highlightsPercentile", d.HighlightsPercentile),
            Linked = SettingsJson.B(c, "linked", d.Linked),
            MtfShadows = SettingsJson.F(c, "mtfShadows", d.MtfShadows), MtfMidtones = SettingsJson.F(c, "mtfMidtones", d.MtfMidtones), MtfHighlights = SettingsJson.F(c, "mtfHighlights", d.MtfHighlights),
            ArcsinhStretch = SettingsJson.F(c, "arcsinhStretch", d.ArcsinhStretch),
            GhsLogD = SettingsJson.F(c, "ghsLogD", d.GhsLogD), GhsB = SettingsJson.F(c, "ghsB", d.GhsB), GhsSP = SettingsJson.F(c, "ghsSP", d.GhsSP),
            GhsLP = SettingsJson.F(c, "ghsLP", d.GhsLP), GhsHP = SettingsJson.F(c, "ghsHP", d.GhsHP),
            LogK = SettingsJson.F(c, "logK", d.LogK), Gamma = SettingsJson.F(c, "gamma", d.Gamma),
        };
    }
}

public sealed partial class LuminanceSettings : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["enabled"] = SettingsJson.V(Enabled), ["preset"] = SettingsJson.V(Preset), ["customExpression"] = SettingsJson.V(CustomExpression),
        ["amount"] = SettingsJson.V(Amount), ["method"] = SettingsJson.V(Method),
    };

    public static LuminanceSettings FromJson(JsonObject? c)
    {
        var d = new LuminanceSettings();
        var l = new LuminanceSettings
        {
            Enabled = SettingsJson.B(c, "enabled", d.Enabled), Preset = SettingsJson.E(c, "preset", d.Preset), CustomExpression = SettingsJson.S(c, "customExpression", d.CustomExpression),
            Amount = SettingsJson.F(c, "amount", d.Amount), Method = SettingsJson.E(c, "method", d.Method),
        };
        // Projects from the first version stored a single optional channel.
        if (SettingsJson.Has(c, "source"))
        {
            if (SettingsJson.KOpt(c, "source", null) is ChannelKind k)
            {
                l.Enabled = true;
                l.Preset = k == ChannelKind.Luminance ? LuminancePreset.Lum : (k == ChannelKind.Ha ? LuminancePreset.Ha : LuminancePreset.Custom);
                if (l.Preset == LuminancePreset.Custom) l.CustomExpression = k.PixelMathName();
            }
            else l.Enabled = false;
        }
        return l;
    }
}

public sealed partial class PostSettings : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["saturation"] = SettingsJson.V(Saturation), ["scnrGreen"] = SettingsJson.V(ScnrGreen), ["hueShift"] = SettingsJson.V(HueShift),
        ["gamma"] = SettingsJson.V(Gamma), ["blackPoint"] = SettingsJson.V(BlackPoint), ["whitePoint"] = SettingsJson.V(WhitePoint), ["vibrance"] = SettingsJson.V(Vibrance),
    };

    public static PostSettings FromJson(JsonObject? c)
    {
        var d = new PostSettings();
        return new PostSettings
        {
            Saturation = SettingsJson.F(c, "saturation", d.Saturation), ScnrGreen = SettingsJson.F(c, "scnrGreen", d.ScnrGreen), HueShift = SettingsJson.F(c, "hueShift", d.HueShift),
            Gamma = SettingsJson.F(c, "gamma", d.Gamma), BlackPoint = SettingsJson.F(c, "blackPoint", d.BlackPoint), WhitePoint = SettingsJson.F(c, "whitePoint", d.WhitePoint),
            Vibrance = SettingsJson.F(c, "vibrance", d.Vibrance),
        };
    }
}

public sealed partial class StarsBlendSettings : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["enabled"] = SettingsJson.V(Enabled), ["stretch"] = SettingsJson.V(Stretch), ["amount"] = SettingsJson.V(Amount),
        ["threshold"] = SettingsJson.V(Threshold), ["softness"] = SettingsJson.V(Softness), ["mode"] = SettingsJson.V(Mode),
        ["useMask"] = SettingsJson.V(UseMask), ["saturation"] = SettingsJson.V(Saturation),
    };

    public static StarsBlendSettings FromJson(JsonObject? c)
    {
        var d = new StarsBlendSettings();
        return new StarsBlendSettings
        {
            Enabled = SettingsJson.B(c, "enabled", d.Enabled), Stretch = SettingsJson.E(c, "stretch", d.Stretch), Amount = SettingsJson.F(c, "amount", d.Amount),
            Threshold = SettingsJson.F(c, "threshold", d.Threshold), Softness = SettingsJson.F(c, "softness", d.Softness), Mode = SettingsJson.E(c, "mode", d.Mode),
            UseMask = SettingsJson.B(c, "useMask", d.UseMask), Saturation = SettingsJson.F(c, "saturation", d.Saturation),
        };
    }
}

public sealed partial class PixelMathProgram : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["red"] = SettingsJson.V(Red), ["green"] = SettingsJson.V(Green), ["blue"] = SettingsJson.V(Blue), ["luminance"] = SettingsJson.V(Luminance),
    };

    public static PixelMathProgram FromJson(JsonObject? c)
        => new(SettingsJson.S(c, "red", "0"), SettingsJson.S(c, "green", "0"), SettingsJson.S(c, "blue", "0"), SettingsJson.S(c, "luminance", ""));
}

public sealed partial class PaletteMatrix : IJsonSettings
{
    public JsonObject ToJson()
    {
        var w = new JsonObject();
        foreach (var (k, v) in Weights) w[k.RawValue()] = SettingsJson.V(v);
        return new JsonObject { ["weights"] = w };
    }

    public static PaletteMatrix FromJson(JsonObject? c)
    {
        var m = new PaletteMatrix();
        var w = SettingsJson.Obj(c, "weights");
        if (w == null) return m;
        foreach (var kv in w)
        {
            if (ChannelKinds.FromRawValue(kv.Key) is not ChannelKind k) continue;
            var arr = SettingsJson.Floats(w, kv.Key, []);
            var v = new float[3];
            Array.Copy(arr, v, Math.Min(3, arr.Length));
            m.Weights[k] = v;
        }
        return m;
    }
}

public sealed partial class ColorBandAdjust : IJsonSettings
{
    public JsonObject ToJson() => new() { ["hue"] = SettingsJson.V(Hue), ["saturation"] = SettingsJson.V(Saturation), ["lightness"] = SettingsJson.V(Lightness) };
    public static ColorBandAdjust FromJson(JsonObject? c) => new() { Hue = SettingsJson.F(c, "hue", 0), Saturation = SettingsJson.F(c, "saturation", 0), Lightness = SettingsJson.F(c, "lightness", 0) };
}

public sealed partial class ColorBandsSettings : IJsonSettings
{
    public JsonObject ToJson()
    {
        var b = new JsonObject();
        foreach (var (k, v) in Bands) b[EnumRaw.Name(k)] = v.ToJson();
        return new JsonObject { ["bands"] = b };
    }

    public static ColorBandsSettings FromJson(JsonObject? c)
    {
        var s = new ColorBandsSettings();
        var b = SettingsJson.Obj(c, "bands");
        if (b == null) return s;
        foreach (var kv in b)
            if (EnumRaw.Parse<ColorBand>(kv.Key) is ColorBand band && kv.Value is JsonObject o) s.Bands[band] = ColorBandAdjust.FromJson(o);
        return s;
    }
}

public sealed partial class DetailSettings : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["texture"] = SettingsJson.V(Texture), ["clarity"] = SettingsJson.V(Clarity), ["dehaze"] = SettingsJson.V(Dehaze),
        ["sharpen"] = SettingsJson.V(Sharpen), ["sharpenRadius"] = SettingsJson.V(SharpenRadius), ["sharpenMasking"] = SettingsJson.V(SharpenMasking),
    };

    public static DetailSettings FromJson(JsonObject? c) => new()
    {
        Texture = SettingsJson.F(c, "texture", 0), Clarity = SettingsJson.F(c, "clarity", 0), Dehaze = SettingsJson.F(c, "dehaze", 0),
        Sharpen = SettingsJson.F(c, "sharpen", 0), SharpenRadius = SettingsJson.F(c, "sharpenRadius", 1.5f), SharpenMasking = SettingsJson.F(c, "sharpenMasking", 0),
    };
}

public sealed partial class DenoiseSettings : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["method"] = SettingsJson.V(Method), ["amount"] = SettingsJson.V(Amount), ["layers"] = SettingsJson.V(Layers), ["search"] = SettingsJson.V(Search),
        ["radius"] = SettingsJson.V(Radius), ["blockSearch"] = SettingsJson.V(BlockSearch), ["protectBright"] = SettingsJson.V(ProtectBright), ["color"] = SettingsJson.V(Color),
    };

    public static DenoiseSettings FromJson(JsonObject? c)
    {
        var d = new DenoiseSettings();
        return new DenoiseSettings
        {
            Method = SettingsJson.E(c, "method", d.Method), Amount = SettingsJson.F(c, "amount", d.Amount), Layers = SettingsJson.I(c, "layers", d.Layers),
            Search = SettingsJson.I(c, "search", d.Search), Radius = SettingsJson.I(c, "radius", d.Radius), BlockSearch = SettingsJson.I(c, "blockSearch", d.BlockSearch),
            ProtectBright = SettingsJson.F(c, "protectBright", d.ProtectBright), Color = SettingsJson.F(c, "color", d.Color),
        };
    }
}

public sealed partial class DualBandSettings : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["preset"] = SettingsJson.V(Preset), ["firstWeights"] = SettingsJson.V(FirstWeights), ["secondWeights"] = SettingsJson.V(SecondWeights),
        ["crosstalkSecond"] = SettingsJson.V(CrosstalkSecond), ["crosstalkFirst"] = SettingsJson.V(CrosstalkFirst),
        ["customFirst"] = SettingsJson.V(CustomFirst), ["customSecond"] = SettingsJson.V(CustomSecond),
    };

    public static DualBandSettings FromJson(JsonObject? c)
    {
        var d = new DualBandSettings();
        var s = new DualBandSettings
        {
            Preset = SettingsJson.E(c, "preset", d.Preset), FirstWeights = SettingsJson.Floats(c, "firstWeights", d.FirstWeights), SecondWeights = SettingsJson.Floats(c, "secondWeights", d.SecondWeights),
            CrosstalkSecond = SettingsJson.F(c, "crosstalkSecond", d.CrosstalkSecond), CrosstalkFirst = SettingsJson.F(c, "crosstalkFirst", d.CrosstalkFirst),
            CustomFirst = SettingsJson.K(c, "customFirst", d.CustomFirst), CustomSecond = SettingsJson.K(c, "customSecond", d.CustomSecond),
        };
        if (s.FirstWeights.Length != 3) s.FirstWeights = d.FirstWeights;
        if (s.SecondWeights.Length != 3) s.SecondWeights = d.SecondWeights;
        return s;
    }
}

public sealed partial class CombineSettings : IJsonSettings
{
    public JsonObject ToJson()
    {
        var channels = new JsonObject();
        foreach (var (k, v) in Channels) channels[k.RawValue()] = v.ToJson();
        var o = new JsonObject
        {
            ["mode"] = SettingsJson.V(Mode), ["paletteID"] = SettingsJson.V(PaletteID), ["matrix"] = Matrix.ToJson(), ["pixelMath"] = PixelMath.ToJson(),
            ["channels"] = channels, ["normalization"] = Normalization.ToJson(), ["stretch"] = Stretch.ToJson(), ["stretchStage"] = SettingsJson.V(StretchStage),
            ["luminance"] = Luminance.ToJson(), ["colorBands"] = ColorBands.ToJson(), ["post"] = Post.ToJson(), ["detail"] = Detail.ToJson(),
            ["denoise"] = Denoise.ToJson(), ["stars"] = Stars.ToJson(),
        };
        if (MonoChannels != null)
        {
            var a = new JsonArray();
            foreach (var k in MonoChannels.SortedByRaw()) a.Add(SettingsJson.V(k));
            o["monoChannels"] = a;
        }
        return o;
    }

    public static CombineSettings FromJson(JsonObject? c)
    {
        var d = new CombineSettings();
        var s = new CombineSettings
        {
            Mode = SettingsJson.E(c, "mode", d.Mode), PaletteID = SettingsJson.S(c, "paletteID", d.PaletteID),
            Matrix = SettingsJson.Obj(c, "matrix") is JsonObject mo ? PaletteMatrix.FromJson(mo) : d.Matrix,
            PixelMath = SettingsJson.Obj(c, "pixelMath") is JsonObject po ? PixelMathProgram.FromJson(po) : d.PixelMath,
            Normalization = SettingsJson.Obj(c, "normalization") is JsonObject no ? NormalizationSettings.FromJson(no) : d.Normalization,
            Stretch = SettingsJson.Obj(c, "stretch") is JsonObject so ? StretchSettings.FromJson(so) : d.Stretch,
            StretchStage = SettingsJson.E(c, "stretchStage", d.StretchStage),
            Luminance = SettingsJson.Obj(c, "luminance") is JsonObject lo ? LuminanceSettings.FromJson(lo) : d.Luminance,
            Post = SettingsJson.Obj(c, "post") is JsonObject pso ? PostSettings.FromJson(pso) : d.Post,
            Stars = SettingsJson.Obj(c, "stars") is JsonObject sto ? StarsBlendSettings.FromJson(sto) : d.Stars,
            ColorBands = SettingsJson.Obj(c, "colorBands") is JsonObject cbo ? ColorBandsSettings.FromJson(cbo) : d.ColorBands,
            Detail = SettingsJson.Obj(c, "detail") is JsonObject deo ? DetailSettings.FromJson(deo) : d.Detail,
            Denoise = SettingsJson.Obj(c, "denoise") is JsonObject dno ? DenoiseSettings.FromJson(dno) : d.Denoise,
        };
        if (SettingsJson.Obj(c, "channels") is JsonObject ch)
            foreach (var kv in ch)
                if (ChannelKinds.FromRawValue(kv.Key) is ChannelKind k && kv.Value is JsonObject ao) s.Channels[k] = ChannelAdjust.FromJson(ao);
        if (SettingsJson.Has(c, "monoChannels") && !SettingsJson.IsNull(c, "monoChannels"))
        {
            if (SettingsJson.Arr(c, "monoChannels") is JsonArray arr)
            {
                var set = new HashSet<ChannelKind>();
                foreach (var e in arr) if (e is JsonValue jv && jv.TryGetValue<string>(out var str) && ChannelKinds.FromRawValue(str) is ChannelKind k) set.Add(k);
                s.MonoChannels = set;
            }
        }
        return s;
    }

    public static CombineSettings Parse(string json) => FromJson(SettingsJson.ParseObject(json));
    public string Json(bool pretty = true) => SettingsJson.Serialize(ToJson(), pretty);
}

public sealed partial class DustSpot : IJsonSettings
{
    public JsonObject ToJson() => new()
    {
        ["id"] = SettingsJson.V(Id), ["x"] = SettingsJson.V(X), ["y"] = SettingsJson.V(Y), ["radius"] = SettingsJson.V(Radius),
        ["model"] = SettingsJson.V(Model), ["smoothness"] = SettingsJson.V(Smoothness), ["feather"] = SettingsJson.V(Feather),
    };

    public static DustSpot FromJson(JsonObject? c) => new(
        SettingsJson.D(c, "x", 0), SettingsJson.D(c, "y", 0), SettingsJson.D(c, "radius", 30),
        SettingsJson.E(c, "model", DustModel.Surface), SettingsJson.D(c, "smoothness", 3), SettingsJson.D(c, "feather", 0.2),
        SettingsJson.G(c, "id"));

    public static DustSpot Parse(string json) => FromJson(SettingsJson.ParseObject(json));
}

public sealed partial class DustSettings : IJsonSettings
{
    public JsonObject ToJson()
    {
        var spots = new JsonObject();
        foreach (var (k, list) in Spots)
        {
            var a = new JsonArray();
            foreach (var s in list) a.Add(s.ToJson());
            spots[k.RawValue()] = a;
        }
        return new JsonObject { ["spots"] = spots, ["model"] = SettingsJson.V(Model), ["smoothness"] = SettingsJson.V(Smoothness), ["feather"] = SettingsJson.V(Feather) };
    }

    public static DustSettings FromJson(JsonObject? c)
    {
        var d = new DustSettings();
        var s = new DustSettings { Model = SettingsJson.E(c, "model", d.Model), Smoothness = SettingsJson.D(c, "smoothness", d.Smoothness), Feather = SettingsJson.D(c, "feather", d.Feather) };
        if (SettingsJson.Obj(c, "spots") is JsonObject so)
            foreach (var kv in so)
                if (ChannelKinds.FromRawValue(kv.Key) is ChannelKind k && kv.Value is JsonArray arr)
                    s.Spots[k] = arr.OfType<JsonObject>().Select(DustSpot.FromJson).ToList();
        return s;
    }
}

public static class PixelRectJson
{
    public static JsonObject ToJson(this PixelRect r) => new() { ["x"] = SettingsJson.V(r.X), ["y"] = SettingsJson.V(r.Y), ["width"] = SettingsJson.V(r.Width), ["height"] = SettingsJson.V(r.Height) };

    public static PixelRect? FromJson(JsonObject? c)
    {
        if (c == null) return null;
        if (!(SettingsJson.Has(c, "x") && SettingsJson.Has(c, "y") && SettingsJson.Has(c, "width") && SettingsJson.Has(c, "height"))) return null;
        return new PixelRect(SettingsJson.I(c, "x", 0), SettingsJson.I(c, "y", 0), SettingsJson.I(c, "width", 0), SettingsJson.I(c, "height", 0));
    }
}

public sealed partial class SettingsPreset : IJsonSettings
{
    public JsonObject ToJson()
    {
        var o = new JsonObject { ["id"] = SettingsJson.V(Id), ["name"] = SettingsJson.V(Name), ["settings"] = Settings.ToJson(), ["date"] = SettingsJson.V(Date) };
        if (ThumbnailPng != null) o["thumbnailPNG"] = SettingsJson.V(Convert.ToBase64String(ThumbnailPng));
        return o;
    }

    public static SettingsPreset FromJson(JsonObject? c) => new(SettingsJson.S(c, "name", "Preset"), CombineSettings.FromJson(SettingsJson.Obj(c, "settings")), SettingsJson.Bytes(c, "thumbnailPNG"))
    {
        Id = SettingsJson.G(c, "id"), Date = SettingsJson.Date(c, "date", DateTime.UtcNow),
    };
}

public sealed partial class ProjectDocument : IJsonSettings
{
    public JsonObject ToJson()
    {
        var channels = new JsonArray();
        foreach (var ch in Channels)
        {
            var o = new JsonObject { ["kind"] = SettingsJson.V(ch.Kind), ["path"] = SettingsJson.V(ch.Path) };
            if (ch.Bookmark != null) o["bookmark"] = SettingsJson.V(Convert.ToBase64String(ch.Bookmark));
            channels.Add(o);
        }
        var presets = new JsonArray();
        foreach (var p in Presets) presets.Add(p.ToJson());
        var doc = new JsonObject
        {
            ["version"] = SettingsJson.V(Version), ["channels"] = channels, ["settings"] = Settings.ToJson(), ["presets"] = presets,
            ["rotation"] = SettingsJson.V(Rotation), ["flipped"] = SettingsJson.V(Flipped), ["dust"] = Dust.ToJson(),
        };
        if (StarsFile != null) doc["starsFile"] = SettingsJson.V(StarsFile);
        if (Crop is PixelRect c) doc["crop"] = c.ToJson();
        return doc;
    }

    public static ProjectDocument FromJson(JsonObject? c)
    {
        var d = new ProjectDocument();
        var doc = new ProjectDocument
        {
            Version = SettingsJson.I(c, "version", d.Version),
            Settings = SettingsJson.Obj(c, "settings") is JsonObject so ? CombineSettings.FromJson(so) : d.Settings,
            StarsFile = SettingsJson.SOpt(c, "starsFile", d.StarsFile),
            Crop = SettingsJson.Has(c, "crop") ? PixelRectJson.FromJson(SettingsJson.Obj(c, "crop")) : d.Crop,
            Rotation = ((SettingsJson.I(c, "rotation", d.Rotation) % 4) + 4) % 4,
            Flipped = SettingsJson.B(c, "flipped", d.Flipped),
            Dust = SettingsJson.Obj(c, "dust") is JsonObject du ? DustSettings.FromJson(du) : d.Dust,
        };
        if (SettingsJson.Arr(c, "channels") is JsonArray chs)
            foreach (var e in chs.OfType<JsonObject>())
                if (ChannelKinds.FromRawValue(SettingsJson.S(e, "kind", "")) is ChannelKind k && SettingsJson.S(e, "path", "") is { Length: > 0 } path)
                    doc.Channels.Add(new ChannelRef(k, path, SettingsJson.Bytes(e, "bookmark")));
        if (SettingsJson.Arr(c, "presets") is JsonArray ps)
            foreach (var e in ps.OfType<JsonObject>()) doc.Presets.Add(SettingsPreset.FromJson(e));
        return doc;
    }
}
