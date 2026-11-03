import Foundation

/// Photoshop-style colour grading on stretched [0,1] RGB: Color Balance and Selective Color.
extension PostProcess {

    /// Color Balance then Selective Color (the panel order); nothing when both are neutral.
    public static func grade(_ img: RGBImage, settings s: ColorGradingSettings) {
        if !s.balanceIsNeutral { colorBalance(img, settings: s) }
        if !s.selectiveIsNeutral { selectiveColor(img, settings: s) }
    }

    /// Color Balance: each tone range adds its three opponent-axis amounts to R, G and B, weighted
    /// by the pixel's HSL lightness with the shadows / midtones / highlights masks of GIMP's
    /// implementation (the usual reading of Photoshop's), ×0.7 so ±100 stays moderate. With
    /// "preserve luminosity" the result keeps the original lightness (hue and saturation move).
    public static func colorBalance(_ img: RGBImage, settings s: ColorGradingSettings) {
        let sh = s.balance(.shadows), mid = s.balance(.midtones), hi = s.balance(.highlights)
        guard !(sh.isNeutral && mid.isNeutral && hi.isNeutral) else { return }
        let n = img.width * img.height
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer
        let a: Float = 0.25, c: Float = 0.333, scale: Float = 0.7
        @inline(__always) func clamp01(_ v: Float) -> Float { min(max(v, 0), 1) }
        for i in 0..<n {
            let (_, _, l) = rgbToHSL(r[i], g[i], b[i])
            let ws = clamp01((l - c) / -a + 0.5) * scale
            let wm = clamp01((l - c) / a + 0.5) * clamp01((l + c - 1) / -a + 0.5) * scale
            let wh = clamp01((l + c - 1) / a + 0.5) * scale
            let dr = ws * sh.cyanRed + wm * mid.cyanRed + wh * hi.cyanRed
            let dg = ws * sh.magentaGreen + wm * mid.magentaGreen + wh * hi.magentaGreen
            let db = ws * sh.yellowBlue + wm * mid.yellowBlue + wh * hi.yellowBlue
            if dr == 0 && dg == 0 && db == 0 { continue }
            var nr = clamp01(r[i] + dr), ng = clamp01(g[i] + dg), nb = clamp01(b[i] + db)
            if s.preserveLuminosity {
                let (h2, s2, _) = rgbToHSL(nr, ng, nb)
                (nr, ng, nb) = hslToRGB(h2, s2, l)
            }
            r[i] = nr; g[i] = ng; b[i] = nb
        }
    }

    /// Weights of the achromatic Selective Color ranges for a pixel: whites grow with the smallest
    /// channel above 0.5, blacks with the largest below 0.5, neutrals peak at mid grey and vanish
    /// for pure white, black or saturated colours.
    @inline(__always) static func selectiveWeights(_ r: Float, _ g: Float, _ b: Float) -> (whites: Float, neutrals: Float, blacks: Float) {
        let mx = max(r, g, b), mn = min(r, g, b)
        return (max(0, 2 * mn - 1), max(0, 1 - abs(mx - 0.5) - abs(mn - 0.5)), max(0, 1 - 2 * mx))
    }

    /// Selective Color on whites, neutrals and blacks. Each slider changes the amount of its ink
    /// (cyan = 1 − R, magenta = 1 − G, yellow = 1 − B; black adds to all three) in the pixels of the
    /// range, by its weight: *relative* changes the ink by that fraction of what is there (so pure
    /// white cannot be touched, as in Photoshop), *absolute* adds the amount outright.
    public static func selectiveColor(_ img: RGBImage, settings s: ColorGradingSettings) {
        let ranges = SelectiveRange.allCases.map { s.selective($0) }
        guard ranges.contains(where: { !$0.isNeutral }) else { return }
        let n = img.width * img.height
        let r = img.r.pointer, g = img.g.pointer, b = img.b.pointer
        let relative = s.relative
        for i in 0..<n {
            let w = selectiveWeights(r[i], g[i], b[i])
            let ws = [w.whites, w.neutrals, w.blacks]
            var dc: Float = 0, dm: Float = 0, dy: Float = 0, dk: Float = 0
            for k in 0..<3 where ws[k] > 0 && !ranges[k].isNeutral {
                dc += ws[k] * ranges[k].cyan; dm += ws[k] * ranges[k].magenta; dy += ws[k] * ranges[k].yellow; dk += ws[k] * ranges[k].black
            }
            if dc == 0 && dm == 0 && dy == 0 && dk == 0 { continue }
            @inline(__always) func ink(_ v: Float, _ d: Float) -> Float {
                let present = 1 - v
                let next = relative ? present * (1 + d) : present + d
                return 1 - min(max(next, 0), 1)
            }
            r[i] = ink(r[i], dc + dk); g[i] = ink(g[i], dm + dk); b[i] = ink(b[i], dy + dk)
        }
    }
}
