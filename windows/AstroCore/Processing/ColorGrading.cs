namespace AstroCore;

/// <summary>Photoshop-style colour grading on stretched [0,1] RGB: Color Balance and Selective Color.</summary>
public static partial class PostProcess
{
    /// <summary>Color Balance then Selective Color (the panel order); nothing when both are neutral.</summary>
    public static void Grade(RGBImage img, ColorGradingSettings s)
    {
        if (!s.BalanceIsNeutral) ColorBalance(img, s);
        if (!s.SelectiveIsNeutral) SelectiveColor(img, s);
    }

    /// <summary>
    /// Color Balance: each tone range adds its three opponent-axis amounts to R, G and B, weighted
    /// by the pixel's HSL lightness with the shadows / midtones / highlights masks of GIMP's
    /// implementation (the usual reading of Photoshop's), ×0.7 so ±100 stays moderate. With
    /// "preserve luminosity" the result keeps the original lightness (hue and saturation move).
    /// </summary>
    public static void ColorBalance(RGBImage img, ColorGradingSettings s)
    {
        var sh = s.GetBalance(ToneRange.Shadows); var mid = s.GetBalance(ToneRange.Midtones); var hi = s.GetBalance(ToneRange.Highlights);
        if (sh.IsNeutral && mid.IsNeutral && hi.IsNeutral) return;
        var r = img.R.Data; var g = img.G.Data; var b = img.B.Data;
        const float a = 0.25f, c = 0.333f, scale = 0.7f;
        bool preserve = s.PreserveLuminosity;
        Par.Rows(img.Height, img.Width * 20, (y0, y1) =>
        {
            for (int i = y0 * img.Width; i < y1 * img.Width; i++)
            {
                var (_, _, l) = RgbToHsl(r[i], g[i], b[i]);
                float ws = Clamp01((l - c) / -a + 0.5f) * scale;
                float wm = Clamp01((l - c) / a + 0.5f) * Clamp01((l + c - 1) / -a + 0.5f) * scale;
                float wh = Clamp01((l + c - 1) / a + 0.5f) * scale;
                float dr = ws * sh.CyanRed + wm * mid.CyanRed + wh * hi.CyanRed;
                float dg = ws * sh.MagentaGreen + wm * mid.MagentaGreen + wh * hi.MagentaGreen;
                float db = ws * sh.YellowBlue + wm * mid.YellowBlue + wh * hi.YellowBlue;
                if (dr == 0 && dg == 0 && db == 0) continue;
                float nr = Clamp01(r[i] + dr), ng = Clamp01(g[i] + dg), nb = Clamp01(b[i] + db);
                if (preserve)
                {
                    var (h2, s2, _) = RgbToHsl(nr, ng, nb);
                    (nr, ng, nb) = HslToRgb(h2, s2, l);
                }
                r[i] = nr; g[i] = ng; b[i] = nb;
            }
        });
    }

    static float Clamp01(float v) => MathF.Min(MathF.Max(v, 0), 1);

    /// <summary>
    /// Weights of the achromatic Selective Color ranges for a pixel: whites grow with the smallest
    /// channel above 0.5, blacks with the largest below 0.5, neutrals peak at mid grey and vanish
    /// for pure white, black or saturated colours.
    /// </summary>
    internal static (float whites, float neutrals, float blacks) SelectiveWeights(float r, float g, float b)
    {
        float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
        return (MathF.Max(0, 2 * mn - 1), MathF.Max(0, 1 - MathF.Abs(mx - 0.5f) - MathF.Abs(mn - 0.5f)), MathF.Max(0, 1 - 2 * mx));
    }

    /// <summary>
    /// Selective Color on whites, neutrals and blacks. Each slider changes the amount of its ink
    /// (cyan = 1 − R, magenta = 1 − G, yellow = 1 − B; black adds to all three) in the pixels of the
    /// range, by its weight: <i>relative</i> changes the ink by that fraction of what is there (so pure
    /// white cannot be touched, as in Photoshop), <i>absolute</i> adds the amount outright.
    /// </summary>
    public static void SelectiveColor(RGBImage img, ColorGradingSettings s)
    {
        var ranges = SelectiveRanges.All.Select(s.GetSelective).ToArray();
        if (!ranges.Any(a => !a.IsNeutral)) return;
        var r = img.R.Data; var g = img.G.Data; var b = img.B.Data;
        bool relative = s.Relative;
        float Ink(float v, float d)
        {
            float present = 1 - v;
            float next = relative ? present * (1 + d) : present + d;
            return 1 - MathF.Min(MathF.Max(next, 0), 1);
        }
        Par.Rows(img.Height, img.Width * 20, (y0, y1) =>
        {
            var ws = new float[3];
            for (int i = y0 * img.Width; i < y1 * img.Width; i++)
            {
                var w = SelectiveWeights(r[i], g[i], b[i]);
                ws[0] = w.whites; ws[1] = w.neutrals; ws[2] = w.blacks;
                float dc = 0, dm = 0, dy = 0, dk = 0;
                for (int k = 0; k < 3; k++)
                {
                    if (!(ws[k] > 0) || ranges[k].IsNeutral) continue;
                    dc += ws[k] * ranges[k].Cyan; dm += ws[k] * ranges[k].Magenta; dy += ws[k] * ranges[k].Yellow; dk += ws[k] * ranges[k].Black;
                }
                if (dc == 0 && dm == 0 && dy == 0 && dk == 0) continue;
                r[i] = Ink(r[i], dc + dk); g[i] = Ink(g[i], dm + dk); b[i] = Ink(b[i], dy + dk);
            }
        });
    }
}
