namespace AstroCore;

/// <summary>The channel slots the application understands (declared in the canonical order).</summary>
public enum ChannelKind
{
    Luminance,
    Red,
    Green,
    Blue,
    Ha,
    OIII,
    SII,
    Hb,
}

public static class ChannelKinds
{
    /// <summary>Every kind in canonical order (L, R, G, B, Hα, OIII, SII, Hβ).</summary>
    public static readonly ChannelKind[] All =
        [ChannelKind.Luminance, ChannelKind.Red, ChannelKind.Green, ChannelKind.Blue, ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII, ChannelKind.Hb];

    public static readonly ChannelKind[] Narrowband = [ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII, ChannelKind.Hb];
    public static readonly ChannelKind[] Broadband = [ChannelKind.Luminance, ChannelKind.Red, ChannelKind.Green, ChannelKind.Blue];

    /// <summary>The identifier used in project files ("L", "R", "G", "B", "Ha", "OIII", "SII", "Hb").</summary>
    public static string RawValue(this ChannelKind k) => k switch
    {
        ChannelKind.Luminance => "L",
        ChannelKind.Red => "R",
        ChannelKind.Green => "G",
        ChannelKind.Blue => "B",
        ChannelKind.Ha => "Ha",
        ChannelKind.OIII => "OIII",
        ChannelKind.SII => "SII",
        ChannelKind.Hb => "Hb",
        _ => throw new ArgumentOutOfRangeException(nameof(k)),
    };

    public static ChannelKind? FromRawValue(string? s)
    {
        if (s == null) return null;
        foreach (var k in All) if (k.RawValue() == s) return k;
        return null;
    }

    public static string DisplayName(this ChannelKind k) => k switch
    {
        ChannelKind.Luminance => "Luminance",
        ChannelKind.Red => "Red",
        ChannelKind.Green => "Green",
        ChannelKind.Blue => "Blue",
        ChannelKind.Ha => "Hα",
        ChannelKind.OIII => "OIII",
        ChannelKind.SII => "SII",
        ChannelKind.Hb => "Hβ",
        _ => k.ToString(),
    };

    public static string ShortName(this ChannelKind k) => k switch
    {
        ChannelKind.Ha => "Hα",
        ChannelKind.OIII => "OIII",
        ChannelKind.SII => "SII",
        ChannelKind.Hb => "Hβ",
        _ => k.RawValue(),
    };

    /// <summary>Identifiers accepted by Pixel Math for this channel (lower-case).</summary>
    public static string[] PixelMathIdentifiers(this ChannelKind k) => k switch
    {
        ChannelKind.Luminance => ["l", "lum", "luminance"],
        ChannelKind.Red => ["r", "red"],
        ChannelKind.Green => ["g", "green"],
        ChannelKind.Blue => ["b", "blue"],
        ChannelKind.Ha => ["ha", "h", "halpha", "h_alpha", "hydrogen"],
        ChannelKind.OIII => ["oiii", "o3", "o", "oxygen"],
        ChannelKind.SII => ["sii", "s2", "s", "sulphur", "sulfur"],
        ChannelKind.Hb => ["hb", "hbeta", "h_beta"],
        _ => [],
    };

    /// <summary>The canonical Pixel Math identifier.</summary>
    public static string PixelMathName(this ChannelKind k) => k.RawValue();

    public static bool IsNarrowband(this ChannelKind k) => Array.IndexOf(Narrowband, k) >= 0;

    /// <summary>Approximate emission wavelength in nm (used for "natural" tinting).</summary>
    public static double? WavelengthNm(this ChannelKind k) => k switch
    {
        ChannelKind.Ha => 656.3,
        ChannelKind.OIII => 500.7,
        ChannelKind.SII => 672.4,
        ChannelKind.Hb => 486.1,
        _ => null,
    };

    public static ChannelKind? FromPixelMathIdentifier(string s)
    {
        var l = s.ToLowerInvariant();
        foreach (var k in All) if (Array.IndexOf(k.PixelMathIdentifiers(), l) >= 0) return k;
        return null;
    }

    /// <summary>Ordering by the raw identifier (ordinal), as the macOS build sorts channels.</summary>
    public static int CompareRaw(ChannelKind a, ChannelKind b) => string.CompareOrdinal(a.RawValue(), b.RawValue());

    public static IEnumerable<ChannelKind> SortedByRaw(this IEnumerable<ChannelKind> kinds) => kinds.OrderBy(k => k.RawValue(), StringComparer.Ordinal);
}
