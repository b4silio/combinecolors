namespace AstroCore;

/// <summary>Guesses which channel slot a file belongs to from FITS metadata and/or the file name.</summary>
public static class ChannelDetection
{
    /// <summary>Try the FITS FILTER keyword first, then the file name.</summary>
    public static ChannelKind? Detect(string fileName, string? filterKeyword)
    {
        if (filterKeyword != null && DetectFilterName(filterKeyword) is ChannelKind k) return k;
        return DetectFileName(fileName);
    }

    /// <summary>Match a filter name such as "Hydrogen-alpha", "OIII", "S2", "Red", "L".</summary>
    public static ChannelKind? DetectFilterName(string filterName) => Match(Tokenize(filterName), strict: false);

    /// <summary>Match a file name such as "Soul_Core-Oxygen_III-session_1-lpc.fits".</summary>
    public static ChannelKind? DetectFileName(string fileName)
    {
        var name = Path.GetFileName(fileName);
        int dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[..dot];
        return Match(Tokenize(name), strict: true);
    }

    internal static List<string> Tokenize(string s)
    {
        var lowered = s.ToLowerInvariant().Replace("α", "alpha").Replace("β", "beta");
        var tokens = new List<string>();
        var cur = new System.Text.StringBuilder();
        foreach (var ch in lowered)
        {
            if (char.IsLetter(ch) || char.IsDigit(ch)) cur.Append(ch);
            else if (cur.Length > 0) { tokens.Add(cur.ToString()); cur.Clear(); }
        }
        if (cur.Length > 0) tokens.Add(cur.ToString());
        return tokens;
    }

    static readonly (HashSet<string> names, ChannelKind kind)[] Strong =
    [
        (new HashSet<string> { "ha", "halpha", "hydrogen", "h_alpha", "hα", "656", "656nm", "h1" }, ChannelKind.Ha),
        (new HashSet<string> { "oiii", "o3", "oxygen", "o_iii", "501", "500", "oxy" }, ChannelKind.OIII),
        (new HashSet<string> { "sii", "s2", "sulphur", "sulfur", "s_ii", "672", "sul" }, ChannelKind.SII),
        (new HashSet<string> { "hb", "hbeta", "486" }, ChannelKind.Hb),
        (new HashSet<string> { "red" }, ChannelKind.Red), (new HashSet<string> { "green" }, ChannelKind.Green), (new HashSet<string> { "blue" }, ChannelKind.Blue),
        (new HashSet<string> { "lum", "luminance", "clear", "luminosity" }, ChannelKind.Luminance),
    ];

    static readonly Dictionary<string, ChannelKind> Weak = new()
    {
        ["r"] = ChannelKind.Red, ["g"] = ChannelKind.Green, ["b"] = ChannelKind.Blue, ["l"] = ChannelKind.Luminance,
        ["h"] = ChannelKind.Ha, ["o"] = ChannelKind.OIII, ["s"] = ChannelKind.SII,
    };

    static ChannelKind? Match(List<string> tokens, bool strict)
    {
        var joined = string.Join(" ", tokens);
        // Multi-token names first.
        if (joined.Contains("hydrogen alpha") || joined.Contains("h alpha") || joined.Contains("h a ") || joined.EndsWith("h a")) return ChannelKind.Ha;
        if (joined.Contains("hydrogen beta") || joined.Contains("h beta")) return ChannelKind.Hb;
        if (joined.Contains("oxygen iii") || joined.Contains("oxygen 3") || joined.Contains("o iii") || joined.Contains("o 3")) return ChannelKind.OIII;
        if (joined.Contains("sulphur ii") || joined.Contains("sulfur ii") || joined.Contains("sulphur 2") || joined.Contains("sulfur 2") || joined.Contains("s ii") || joined.Contains("s 2")) return ChannelKind.SII;

        foreach (var t in tokens)
            foreach (var (names, kind) in Strong) if (names.Contains(t)) return kind;
        // Single-letter tokens are only trusted when they stand alone (e.g. "M31_R.fits").
        for (int i = tokens.Count - 1; i >= 0; i--)   // suffix tokens are the most informative
            if (Weak.TryGetValue(tokens[i], out var k)) return k;
        if (!strict)
            foreach (var t in tokens) if (Weak.TryGetValue(t, out var k)) return k;
        return null;
    }
}
