namespace AstroCore;

public abstract record PaletteKind
{
    public sealed record MatrixKind(PaletteMatrix Matrix) : PaletteKind;
    public sealed record PixelMathKind(PixelMathProgram Program) : PaletteKind;
    /// <summary>Weighted average of every loaded channel (weights = the per-channel Weight sliders).</summary>
    public sealed record MonochromeKind : PaletteKind;

    public static PaletteKind OfMatrix(PaletteMatrix m) => new MatrixKind(m);
    public static PaletteKind OfPixelMath(PixelMathProgram p) => new PixelMathKind(p);
    public static readonly PaletteKind Monochrome = new MonochromeKind();
}

public sealed class Palette
{
    public string Id { get; }
    public string Name { get; }
    public string Group { get; }
    public PaletteKind Kind { get; }
    public string Description { get; }
    /// <summary>Suggested luminance source (null = none).</summary>
    public ChannelKind? Luminance { get; }
    /// <summary>Mono check palettes must show the raw channel: no luminance injection.</summary>
    public bool AllowsLuminance => Group != "Mono";

    public Palette(string id, string name, string group, PaletteKind kind, string description, ChannelKind? luminance = null)
    {
        Id = id; Name = name; Group = group; Kind = kind; Description = description; Luminance = luminance;
    }

    public HashSet<ChannelKind> RequiredChannels
    {
        get
        {
            switch (Kind)
            {
                case PaletteKind.MatrixKind m: return m.Matrix.RequiredChannels;
                case PaletteKind.PixelMathKind p:
                    try { return p.Program.Compile().RequiredChannels; } catch (PixelMathException) { return new HashSet<ChannelKind>(); }
                default: return new HashSet<ChannelKind>();
            }
        }
    }

    public bool IsPixelMath => Kind is PaletteKind.PixelMathKind;
    public bool IsMonochrome => Kind is PaletteKind.MonochromeKind;
}

public static class Palettes
{
    static PaletteMatrix M(Dictionary<ChannelKind, float> r, Dictionary<ChannelKind, float> g, Dictionary<ChannelKind, float> b)
    {
        var w = new Dictionary<ChannelKind, float[]>();
        var keys = new HashSet<ChannelKind>(r.Keys); keys.UnionWith(g.Keys); keys.UnionWith(b.Keys);
        foreach (var k in keys)
            w[k] = [r.GetValueOrDefault(k), g.GetValueOrDefault(k), b.GetValueOrDefault(k)];
        return new PaletteMatrix(w);
    }

    static Dictionary<ChannelKind, float> D(params (ChannelKind, float)[] items) => items.ToDictionary(i => i.Item1, i => i.Item2);

    public static readonly string[] Groups = ["Narrowband", "Blends", "Dynamic", "Broadband", "Mono"];

    public static readonly IReadOnlyList<Palette> All =
    [
        // --- Classic narrowband permutations ---
        new("sho", "SHO (Hubble)", "Narrowband", PaletteKind.OfMatrix(M(D((ChannelKind.SII, 1)), D((ChannelKind.Ha, 1)), D((ChannelKind.OIII, 1)))),
            "SII → red, Hα → green, OIII → blue. The Hubble palette; usually needs green removal (SCNR) or a hue shift to reach the classic gold/blue look."),
        new("hoo", "HOO", "Narrowband", PaletteKind.OfMatrix(M(D((ChannelKind.Ha, 1)), D((ChannelKind.OIII, 1)), D((ChannelKind.OIII, 1)))),
            "Hα → red, OIII → green and blue. Natural-looking bicolour palette."),
        new("hso", "HSO", "Narrowband", PaletteKind.OfMatrix(M(D((ChannelKind.Ha, 1)), D((ChannelKind.SII, 1)), D((ChannelKind.OIII, 1)))),
            "Hα → red, SII → green, OIII → blue."),
        new("hos", "HOS (CFHT)", "Narrowband", PaletteKind.OfMatrix(M(D((ChannelKind.Ha, 1)), D((ChannelKind.OIII, 1)), D((ChannelKind.SII, 1)))),
            "Hα → red, OIII → green, SII → blue. Canada-France-Hawaii Telescope palette."),
        new("ohs", "OHS", "Narrowband", PaletteKind.OfMatrix(M(D((ChannelKind.OIII, 1)), D((ChannelKind.Ha, 1)), D((ChannelKind.SII, 1)))),
            "OIII → red, Hα → green, SII → blue."),
        new("osh", "OSH", "Narrowband", PaletteKind.OfMatrix(M(D((ChannelKind.OIII, 1)), D((ChannelKind.SII, 1)), D((ChannelKind.Ha, 1)))),
            "OIII → red, SII → green, Hα → blue."),
        new("soh", "SOH", "Narrowband", PaletteKind.OfMatrix(M(D((ChannelKind.SII, 1)), D((ChannelKind.OIII, 1)), D((ChannelKind.Ha, 1)))),
            "SII → red, OIII → green, Hα → blue."),
        new("hho", "HHO", "Narrowband", PaletteKind.OfMatrix(M(D((ChannelKind.Ha, 1)), D((ChannelKind.Ha, 1)), D((ChannelKind.OIII, 1)))),
            "Hα → red and green, OIII → blue. Yellow/blue bicolour."),
        new("sho_natural", "Natural (line colours)", "Narrowband",
            PaletteKind.OfMatrix(M(D((ChannelKind.Ha, 0.85f), (ChannelKind.SII, 0.65f)), D((ChannelKind.Ha, 0.10f), (ChannelKind.OIII, 0.80f)), D((ChannelKind.OIII, 0.60f)))),
            "Each line is tinted with the approximate colour of its wavelength (Hα 656 nm, SII 672 nm, OIII 501 nm). Add Hβ in the matrix editor if you have it."),

        // --- Blends ---
        new("sho_gold", "SHO Golden", "Blends",
            PaletteKind.OfMatrix(M(D((ChannelKind.SII, 0.55f), (ChannelKind.Ha, 0.45f)), D((ChannelKind.Ha, 0.75f), (ChannelKind.OIII, 0.25f)), D((ChannelKind.OIII, 1)))),
            "Hα feeds both red and green for golden nebulosity, SII warms the reds, OIII stays blue/teal. Close to the classic processed SHO look without a hue shift."),
        new("sho_soft", "SHO Soft", "Blends",
            PaletteKind.OfMatrix(M(D((ChannelKind.SII, 0.7f), (ChannelKind.Ha, 0.3f)), D((ChannelKind.Ha, 0.7f), (ChannelKind.OIII, 0.3f)), D((ChannelKind.OIII, 0.8f), (ChannelKind.Ha, 0.2f)))),
            "Every output gets a little of its neighbours. Less saturated, more pastel version of SHO."),
        new("hoo_s", "HOO + SII in red", "Blends",
            PaletteKind.OfMatrix(M(D((ChannelKind.Ha, 0.7f), (ChannelKind.SII, 0.3f)), D((ChannelKind.OIII, 1)), D((ChannelKind.OIII, 1)))),
            "HOO with 30 % SII mixed into the red channel."),
        new("hoo_warm", "HOO Warm", "Blends",
            PaletteKind.OfMatrix(M(D((ChannelKind.Ha, 1)), D((ChannelKind.OIII, 0.75f), (ChannelKind.Ha, 0.25f)), D((ChannelKind.OIII, 1)))),
            "HOO with a touch of Hα in green: oranges instead of pure reds."),
        new("sho_hgreen", "SHO (green reduced)", "Blends",
            PaletteKind.OfMatrix(M(D((ChannelKind.SII, 0.8f), (ChannelKind.Ha, 0.2f)), D((ChannelKind.Ha, 0.55f), (ChannelKind.OIII, 0.45f)), D((ChannelKind.OIII, 1)))),
            "SHO with the Hα green diluted by OIII — a matrix-only approximation of SCNR."),

        // --- Dynamic (pixel math) ---
        new("foraxx_sho", "Foraxx SHO", "Dynamic",
            PaletteKind.OfPixelMath(new PixelMathProgram(
                "(OIII^~OIII)*SII + ~(OIII^~OIII)*Ha",
                "((OIII*Ha)^~(OIII*Ha))*Ha + ~((OIII*Ha)^~(OIII*Ha))*OIII",
                "OIII")),
            "Foraxx dynamic palette (Paulyman Astro): the mix of SII/Hα in red and Hα/OIII in green depends on the local OIII strength. Expects stretched [0,1] channels."),
        new("foraxx_hoo", "Foraxx HOO", "Dynamic",
            PaletteKind.OfPixelMath(new PixelMathProgram(
                "Ha",
                "((OIII*Ha)^~(OIII*Ha))*Ha + ~((OIII*Ha)^~(OIII*Ha))*OIII",
                "OIII")),
            "Foraxx bicolour variant: green is a dynamic mix of Hα and OIII."),
        new("dyn_sho_ratio", "SHO Line-ratio", "Dynamic",
            PaletteKind.OfPixelMath(new PixelMathProgram(
                "max(SII,Ha,OIII) * 3*SII/(SII+Ha+OIII+0.001)",
                "max(SII,Ha,OIII) * 3*Ha/(SII+Ha+OIII+0.001)",
                "max(SII,Ha,OIII) * 3*OIII/(SII+Ha+OIII+0.001)")),
            "Hue is given by the proportion of each line, brightness by the strongest line. Very saturated, emphasises where each element dominates."),
        new("dyn_sho_oprotect", "SHO OIII-protected", "Dynamic",
            PaletteKind.OfPixelMath(new PixelMathProgram(
                "lerp(0.5*SII+0.5*Ha, SII, OIII)",
                "lerp(Ha, 0.5*Ha+0.5*OIII, OIII)",
                "OIII")),
            "Where OIII is strong the palette tends to SHO (blue/teal kept), where it is weak Hα dominates the warm tones."),
        new("dyn_hoo_boost", "HOO OIII-boost", "Dynamic",
            PaletteKind.OfPixelMath(new PixelMathProgram(
                "Ha",
                "OIII^0.8",
                "OIII^0.7")),
            "HOO with gamma-lifted OIII to bring out faint oxygen."),

        // --- Broadband ---
        new("rgb", "RGB", "Broadband", PaletteKind.OfMatrix(M(D((ChannelKind.Red, 1)), D((ChannelKind.Green, 1)), D((ChannelKind.Blue, 1)))),
            "Plain RGB combination."),
        new("lrgb", "LRGB", "Broadband", PaletteKind.OfMatrix(M(D((ChannelKind.Red, 1)), D((ChannelKind.Green, 1)), D((ChannelKind.Blue, 1)))),
            "RGB colour with the luminance channel injected.", ChannelKind.Luminance),
        new("hargb", "HαRGB", "Broadband", PaletteKind.OfMatrix(M(D((ChannelKind.Red, 0.5f), (ChannelKind.Ha, 0.5f)), D((ChannelKind.Green, 1)), D((ChannelKind.Blue, 1)))),
            "RGB with Hα blended into red."),
        new("ha_lrgb", "Hα as luminance + RGB", "Broadband", PaletteKind.OfMatrix(M(D((ChannelKind.Red, 1)), D((ChannelKind.Green, 1)), D((ChannelKind.Blue, 1)))),
            "RGB colour with Hα used as luminance.", ChannelKind.Ha),
        new("haoiiirgb", "Hα/OIII + RGB", "Broadband", PaletteKind.OfMatrix(M(D((ChannelKind.Red, 0.5f), (ChannelKind.Ha, 0.5f)), D((ChannelKind.Green, 0.5f), (ChannelKind.OIII, 0.5f)), D((ChannelKind.Blue, 0.5f), (ChannelKind.OIII, 0.5f)))),
            "Hα into red and OIII into green/blue, blended 50/50 with RGB."),

        // --- Mono ---
        new("monochrome", "Monochrome", "Mono", PaletteKind.Monochrome,
            "Weighted average of every loaded channel, each stretched on its own — a synthetic luminance. Balance it with the channel Weight sliders; press and hold a channel thumbnail to see that channel alone."),
    ];

    public static Palette? Palette(string id) => All.FirstOrDefault(p => p.Id == id);

    /// <summary>Palettes whose required channels are all available.</summary>
    public static List<Palette> Available(IReadOnlySet<ChannelKind> channels)
        => All.Where(p => (p.IsMonochrome && channels.Count > 0) || (p.RequiredChannels is var r && r.Count > 0 && r.IsSubsetOf(channels))).ToList();
}
