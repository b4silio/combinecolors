namespace AstroCore;

/// <summary>Colour post-processing on stretched [0,1] RGB data. Everything is point-wise.</summary>
public static class PostProcess
{
    public static void Apply(PostSettings s, RGBImage img)
    {
        if (s.HueShift != 0) HueShift(img, s.HueShift);
        if (s.Vibrance != 0) Vibrance(img, s.Vibrance);
        if (s.Saturation != 1) Saturation(img, s.Saturation);
        if (s.ScnrGreen > 0) Scnr(img, s.ScnrGreen);
        if (s.BlackPoint != 0 || s.WhitePoint != 1)
        {
            float a = 1 / MathF.Max(s.WhitePoint - s.BlackPoint, 1e-4f);
            foreach (var p in img.Planes) p.Affine(a, -s.BlackPoint * a);
        }
        img.Clamp01();
        if (s.Gamma != 1 && s.Gamma > 0)
        {
            float g = s.Gamma;
            foreach (var p in img.Planes)
                Par.Rows(p.Height, p.Width, (y0, y1) => { var seg = p.Data.AsSpan(y0 * p.Width, (y1 - y0) * p.Width); Vec.Pow(seg, g, seg); });
        }
    }

    /// <summary>Luma-preserving saturation: c' = Y + f (c − Y).</summary>
    public static void Saturation(RGBImage img, float factor)
    {
        var y = img.Luma();
        foreach (var p in img.Planes) Vec.Mix(p.Data, factor, y.Data, 1 - factor, p.Data);
    }

    /// <summary>Boosts saturation mostly where it is low.</summary>
    public static void Vibrance(RGBImage img, float amount)
    {
        var r = img.R.Data; var g = img.G.Data; var b = img.B.Data;
        Par.Rows(img.Height, img.Width * 4, (y0, y1) =>
        {
            for (int i = y0 * img.Width; i < y1 * img.Width; i++)
            {
                float mx = MathF.Max(r[i], MathF.Max(g[i], b[i])), mn = MathF.Min(r[i], MathF.Min(g[i], b[i]));
                float sat = mx > 1e-6f ? (mx - mn) / mx : 0;
                float f = 1 + amount * (1 - sat);
                float y = 0.2126f * r[i] + 0.7152f * g[i] + 0.0722f * b[i];
                r[i] = y + f * (r[i] - y); g[i] = y + f * (g[i] - y); b[i] = y + f * (b[i] - y);
            }
        });
    }

    /// <summary>SCNR "average neutral": G' = (1−a) G + a min(G, (R+B)/2).</summary>
    public static void Scnr(RGBImage img, float amount)
    {
        var tmp = PlanarImage.Uninitialized(img.Width, img.Height);
        Vec.Add(img.R.Data, img.B.Data, tmp.Data);
        Vec.Scale(tmp.Data, 0.5f, tmp.Data);
        Vec.Min(tmp.Data, img.G.Data, tmp.Data);
        Vec.Mix(tmp.Data, amount, img.G.Data, 1 - amount, img.G.Data);
    }

    /// <summary>Rotates hue in YIQ space.</summary>
    public static void HueShift(RGBImage img, float degrees)
    {
        float t = degrees * MathF.PI / 180;
        float c = MathF.Cos(t), s = MathF.Sin(t);
        float[] toYIQ = [0.299f, 0.587f, 0.114f, 0.596f, -0.274f, -0.322f, 0.211f, -0.523f, 0.312f];
        var fromYIQ = Invert3(toYIQ);
        float[] rot = [1, 0, 0, 0, c, -s, 0, s, c];
        var m = Mul3(fromYIQ, Mul3(rot, toYIQ));
        ApplyMatrix(img, m);
    }

    internal static float[] Invert3(float[] m)
    {
        var a = m.Select(v => (double)v).ToArray();
        double det = a[0] * (a[4] * a[8] - a[5] * a[7]) - a[1] * (a[3] * a[8] - a[5] * a[6]) + a[2] * (a[3] * a[7] - a[4] * a[6]);
        double[] inv =
        [
            (a[4] * a[8] - a[5] * a[7]), -(a[1] * a[8] - a[2] * a[7]), (a[1] * a[5] - a[2] * a[4]),
            -(a[3] * a[8] - a[5] * a[6]), (a[0] * a[8] - a[2] * a[6]), -(a[0] * a[5] - a[2] * a[3]),
            (a[3] * a[7] - a[4] * a[6]), -(a[0] * a[7] - a[1] * a[6]), (a[0] * a[4] - a[1] * a[3]),
        ];
        return inv.Select(v => (float)(v / det)).ToArray();
    }

    internal static float[] Mul3(float[] a, float[] b)
    {
        var o = new float[9];
        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) for (int k = 0; k < 3; k++) o[i * 3 + j] += a[i * 3 + k] * b[k * 3 + j];
        return o;
    }

    /// <summary>out = M · [r g b]ᵀ (row-major 3×3).</summary>
    public static void ApplyMatrix(RGBImage img, float[] m)
    {
        var src = img.Planes.Select(p => p.Copy()).ToArray();
        var planes = img.Planes;
        for (int o = 0; o < 3; o++)
        {
            var dst = planes[o].Data;
            Vec.Scale(src[0].Data, m[o * 3], dst);
            Vec.MulAdd(src[1].Data, m[o * 3 + 1], dst);
            Vec.MulAdd(src[2].Data, m[o * 3 + 2], dst);
        }
    }

    // Selective colour (HSL bands)

    /// <summary>
    /// Per-band hue/saturation/lightness. Band weights interpolate linearly between the band
    /// centres around the hue circle and fade out for nearly neutral pixels.
    /// </summary>
    public static void ColorBands(RGBImage img, ColorBandsSettings s)
    {
        if (s.IsNeutral) return;
        var bands = AstroCore.ColorBands.All;
        var centers = bands.Select(b => b.Hue()).ToArray();
        var adj = bands.Select(s.Adjust).ToArray();
        var r = img.R.Data; var g = img.G.Data; var b = img.B.Data;
        Par.Rows(img.Height, img.Width * 20, (y0, y1) =>
        {
            for (int i = y0 * img.Width; i < y1 * img.Width; i++)
            {
                var (h, sat, l) = RgbToHsl(r[i], g[i], b[i]);
                if (sat <= 1e-4f) continue;
                // Band weights.
                float dh = 0, ds = 0, dl = 0;
                for (int k = 0; k < bands.Length; k++)
                {
                    var a = adj[k];
                    if (a.IsNeutral) continue;
                    float w = BandWeight(h, k, centers);
                    if (w <= 0) continue;
                    dh += w * a.Hue; ds += w * a.Saturation; dl += w * a.Lightness;
                }
                if (dh == 0 && ds == 0 && dl == 0) continue;
                float t = MathF.Min(sat / 0.25f, 1);
                float f = t * t * (3 - 2 * t);          // fade for nearly grey pixels
                h = Mod(h + f * dh + 360, 360);
                sat = MathF.Min(MathF.Max(sat * (1 + f * ds), 0), 1);
                float dlf = f * dl;
                l = dlf >= 0 ? l + dlf * 0.5f * (1 - l) : l + dlf * 0.5f * l;
                (r[i], g[i], b[i]) = HslToRgb(h, sat, MathF.Min(MathF.Max(l, 0), 1));
            }
        });
    }

    static float Mod(float a, float m) => a % m;   // C fmodf semantics

    /// <summary>Piecewise-linear interpolation weight of band <paramref name="index"/> at <paramref name="hue"/> (degrees).</summary>
    internal static float BandWeight(float hue, int index, float[] centers)
    {
        float c = centers[index];
        float prev = centers[(index + centers.Length - 1) % centers.Length];
        float next = centers[(index + 1) % centers.Length];
        float toPrev = Mod(c - prev + 360, 360), toNext = Mod(next - c + 360, 360);
        float d = Mod(hue - c + 540, 360) - 180;      // signed distance in (−180, 180]
        if (d >= 0) return toNext > 0 ? MathF.Max(0, 1 - d / toNext) : 0;
        return toPrev > 0 ? MathF.Max(0, 1 + d / toPrev) : 0;
    }

    internal static (float h, float s, float l) RgbToHsl(float r, float g, float b)
    {
        float mx = MathF.Max(r, MathF.Max(g, b)), mn = MathF.Min(r, MathF.Min(g, b));
        float l = (mx + mn) / 2;
        float d = mx - mn;
        if (d <= 1e-6f) return (0, 0, l);
        float s = l > 0.5f ? d / (2 - mx - mn) : d / (mx + mn);
        float h;
        if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (mx == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, MathF.Min(s, 1), l);
    }

    internal static (float r, float g, float b) HslToRgb(float h, float s, float l)
    {
        if (s <= 0) return (l, l, l);
        float q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        float p = 2 * l - q;
        float F(float t0)
        {
            float t = t0;
            if (t < 0) t += 1; else if (t > 1) t -= 1;
            if (t < 1f / 6) return p + (q - p) * 6 * t;
            if (t < 1f / 2) return q;
            if (t < 2f / 3) return p + (q - p) * (2f / 3 - t) * 6;
            return p;
        }
        float hk = h / 360;
        return (F(hk + 1f / 3), F(hk), F(hk - 1f / 3));
    }

    // Local contrast (clarity / dehaze)

    /// <summary>
    /// Large-radius blur by box-pyramid down-sampling and bilinear up-sampling. <paramref name="levels"/>
    /// halves the size that many times, i.e. a radius of about 2^levels pixels.
    /// </summary>
    internal static PlanarImage PyramidBlur(PlanarImage p, int levels)
    {
        var cur = p;
        int n = 0;
        while (n < levels && cur.Width > 4 && cur.Height > 4) { cur = cur.Downsampled2x(); n++; }
        return n == 0 ? p.Copy() : cur.Resized(p.Width, p.Height);
    }

    /// <summary>Number of pyramid levels giving a radius of relativeRadius × width pixels.</summary>
    public static int BlurLevels(int width, float relativeRadius)
        => Math.Max(1, (int)MathF.Round(MathF.Log2(MathF.Max(width * relativeRadius, 1)), MidpointRounding.AwayFromZero));

    public const float TextureRadius = 0.006f;
    public const float ClarityRadius = 0.06f;
    public const float DehazeRadius = 0.25f;

    /// <summary>
    /// Luminance with the stars clipped off (99th percentile, at least 0.15): the blurs are taken
    /// from this copy so bright stars do not dig dark halos around themselves.
    /// </summary>
    static PlanarImage StarCapped(PlanarImage y)
    {
        var opts = new StatsOptions { MaxSamples = 200_000 };
        var sample = Statistics.Sample(y, opts).sample;
        Array.Sort(sample);
        float hi = MathF.Max(Statistics.Percentile(sample, 0.99), 0.15f);
        var out_ = PlanarImage.Uninitialized(y.Width, y.Height);
        Vec.Clip(y.Data, 0, hi, out_.Data);
        return out_;
    }

    /// <summary>
    /// Local contrast at one scale: t += amount × midtoneWeight × (t − blur), with the blur taken
    /// from the star-capped image and darkening limited to <paramref name="darkFactor"/> of the
    /// lightening (both measures against halos around stars).
    /// </summary>
    static void LocalContrast(float[] t, int n, int width, PlanarImage blur, float amount, float darkFactor)
    {
        var bp = blur.Data;
        Par.Rows(n / width, width * 4, (y0, y1) =>
        {
            for (int i = y0 * width; i < y1 * width; i++)
            {
                float l = t[i];
                float w = 4 * l * (1 - l);           // mid-tone weight
                float delta = amount * w * (l - bp[i]);
                if (delta < 0) delta *= darkFactor;
                t[i] = MathF.Min(MathF.Max(l + delta, 0), 1);
            }
        });
    }

    /// <summary>Separable Gaussian blur (σ in pixels, edges extended).</summary>
    public static PlanarImage GaussianBlur(PlanarImage p, float sigma)
    {
        int r = Math.Max(1, (int)MathF.Ceiling(3 * sigma));
        var k = new float[2 * r + 1];
        float sum = 0;
        for (int i = 0; i <= 2 * r; i++) { k[i] = MathF.Exp(-((i - r) * (i - r)) / (2 * sigma * sigma)); sum += k[i]; }
        for (int i = 0; i < k.Length; i++) k[i] /= sum;
        return Convolve.Separable(p, k, true);
    }

    /// <summary>
    /// Unsharp mask: t += amount × (t − blur) with the blur taken from the star-capped image and
    /// darkening halved, like the other scales. <paramref name="sigma"/> is in pixels of t.
    /// <paramref name="masking"/> (0…1) fades the mask out where the high-pass is below
    /// masking × 4σ of the high-pass noise (median absolute deviation), so flat noisy sky is left
    /// alone and edges are sharpened.
    /// </summary>
    static void Sharpen(float[] t, int n, int width, PlanarImage source, float sigma, float amount, float masking = 0)
    {
        var blur = GaussianBlur(StarCapped(source), sigma);
        var bp = blur.Data;
        float threshold = 0;
        if (masking > 0)
        {
            int step = Math.Max(1, n / 200_000);
            var sample = new List<float>(n / step + 1);
            for (int i = 0; i < n; i += step) sample.Add(MathF.Abs(t[i] - bp[i]));
            var arr = sample.ToArray();
            Array.Sort(arr);
            float noise = Statistics.Percentile(arr, 0.5) / 0.6745f;
            threshold = masking * 4 * noise;
        }
        Par.Rows(n / width, width * 4, (y0, y1) =>
        {
            for (int i = y0 * width; i < y1 * width; i++)
            {
                float l = t[i];
                float d = l - bp[i];
                float delta = amount * d;
                if (threshold > 0)
                {
                    // Smooth ramp from 0 at half the threshold to 1 at the threshold.
                    float w = MathF.Min(MathF.Max((MathF.Abs(d) - 0.5f * threshold) / (0.5f * threshold), 0), 1);
                    delta *= w * w * (3 - 2 * w);
                }
                if (delta < 0) delta *= 0.5f;
                t[i] = MathF.Min(MathF.Max(l + delta, 0), 1);
            }
        });
    }

    /// <summary>
    /// Texture (fine), clarity (mid) and dehaze (large-scale) local contrast plus sharpening,
    /// applied to the luminance and propagated to RGB by ratio. The local-contrast radii are
    /// relative to the image width so every pyramid level and the export agree; the sharpening
    /// radius is in full-resolution pixels and scaled by <paramref name="scale"/> (rendered px per full-res px).
    /// </summary>
    public static void Detail(RGBImage img, DetailSettings d, float scale = 1)
    {
        if (d.IsNeutral) return;
        int n = img.Width * img.Height, w = img.Width;
        var y = img.Luma();
        var target = y.Copy();
        var t = target.Data; var yp = y.Data;
        if (d.Dehaze != 0)
        {
            var haze = PyramidBlur(StarCapped(target), BlurLevels(img.Width, DehazeRadius));
            var hp = haze.Data;
            float k = d.Dehaze * 0.6f;
            float inv = 1 / MathF.Max(1 - k, 0.05f);
            Par.Rows(img.Height, w * 2, (y0, y1) =>
            {
                for (int i = y0 * w; i < y1 * w; i++)
                {
                    // Remove a fraction of the local haze level, renormalise so bright areas stay put.
                    float v = (t[i] - k * hp[i]) * inv;
                    t[i] = MathF.Min(MathF.Max(v, 0), 1);
                }
            });
        }
        if (d.Clarity != 0)
        {
            var blur = PyramidBlur(StarCapped(target), BlurLevels(img.Width, ClarityRadius));
            LocalContrast(t, n, w, blur, d.Clarity * 1.2f, 0.7f);
        }
        if (d.Texture != 0)
        {
            var blur = PyramidBlur(StarCapped(target), BlurLevels(img.Width, TextureRadius));
            LocalContrast(t, n, w, blur, d.Texture * 1.0f, 0.5f);
        }
        if (d.Sharpen > 0)
        {
            // Below ~0.25 px the blur is the identity and the mask vanishes (a down-sampled preview
            // of a 1 px sharpening legitimately shows nothing).
            float sigma = d.SharpenRadius * scale;
            if (sigma >= 0.25f) Sharpen(t, n, w, target, sigma, d.Sharpen, d.SharpenMasking);
        }
        var r = img.R.Data; var g = img.G.Data; var b = img.B.Data;
        Par.Rows(img.Height, w * 4, (y0, y1) =>
        {
            for (int i = y0 * w; i < y1 * w; i++)
            {
                float baseV = yp[i];
                if (baseV > 1e-5f)
                {
                    float ratio = MathF.Min(t[i] / baseV, 8);
                    r[i] *= ratio; g[i] *= ratio; b[i] *= ratio;
                }
                else { r[i] = t[i]; g[i] = t[i]; b[i] = t[i]; }
            }
        });
        if (d.Dehaze > 0) Saturation(img, 1 + 0.15f * d.Dehaze);
        img.Clamp01();
    }

    // Luminance injection

    /// <summary>Replaces the luminance of <paramref name="img"/> with <paramref name="lum"/> (both in [0,1]) by <paramref name="amount"/>.</summary>
    public static void InjectLuminance(RGBImage img, PlanarImage lum, float amount, LuminanceMethod method)
    {
        if (method == LuminanceMethod.Ratio) InjectLuminanceRatio(img, lum, amount);
        else InjectLuminanceLab(img, lum, amount);
    }

    static void InjectLuminanceRatio(RGBImage img, PlanarImage lum, float amount)
    {
        var y = img.Luma();
        int w = img.Width;
        var r = img.R.Data; var g = img.G.Data; var b = img.B.Data; var l = lum.Data; var yp = y.Data;
        Par.Rows(img.Height, w * 4, (y0, y1) =>
        {
            for (int i = y0 * w; i < y1 * w; i++)
            {
                float target = yp[i] + amount * (l[i] - yp[i]);
                float ratio = yp[i] > 1e-5f ? MathF.Min(target / yp[i], 16) : 0;
                if (yp[i] <= 1e-5f) { r[i] = target; g[i] = target; b[i] = target; }
                else { r[i] *= ratio; g[i] *= ratio; b[i] *= ratio; }
            }
        });
        img.Clamp01();
    }

    static void InjectLuminanceLab(RGBImage img, PlanarImage lum, float amount)
    {
        int w = img.Width;
        var r = img.R.Data; var g = img.G.Data; var b = img.B.Data; var l = lum.Data;
        static float Fwd(float t) => t > 0.008856f ? MathF.Cbrt(t) : (7.787f * t + 16f / 116f);
        static float Inv(float t) { float c = t * t * t; return c > 0.008856f ? c : (t - 16f / 116f) / 7.787f; }
        Par.Rows(img.Height, w * 20, (y0, y1) =>
        {
            for (int i = y0 * w; i < y1 * w; i++)
            {
                // Treat the stretched values as linear-light for the purpose of the L replacement
                // (the same convention PixInsight uses for LRGBCombination on stretched data).
                float R = r[i], G = g[i], B = b[i];
                float X = 0.4124f * R + 0.3576f * G + 0.1805f * B;
                float Y = 0.2126f * R + 0.7152f * G + 0.0722f * B;
                float Z = 0.0193f * R + 0.1192f * G + 0.9505f * B;
                float fx = Fwd(X / 0.95047f), fy = Fwd(Y), fz = Fwd(Z / 1.08883f);
                float Lstar = 116 * fy - 16;
                float a = 500 * (fx - fy), bb = 200 * (fy - fz);
                // Target L* from the luminance plane (treated the same way).
                float Lt = 116 * Fwd(MathF.Max(l[i], 0)) - 16;
                Lstar = Lstar + amount * (Lt - Lstar);
                float fy2 = (Lstar + 16) / 116;
                float fx2 = fy2 + a / 500, fz2 = fy2 - bb / 200;
                float X2 = 0.95047f * Inv(fx2), Y2 = Inv(fy2), Z2 = 1.08883f * Inv(fz2);
                r[i] = 3.2406f * X2 - 1.5372f * Y2 - 0.4986f * Z2;
                g[i] = -0.9689f * X2 + 1.8758f * Y2 + 0.0415f * Z2;
                b[i] = 0.0557f * X2 - 0.2040f * Y2 + 1.0570f * Z2;
            }
        });
        img.Clamp01();
    }

    // Star blending

    /// <summary>Blends a stretched RGB star image into <paramref name="img"/>.</summary>
    public static void BlendStars(RGBImage img, RGBImage stars, StarsBlendSettings s)
    {
        int n = img.Width * img.Height, w = img.Width;
        var st = stars.Copy();
        if (s.Saturation != 1) Saturation(st, s.Saturation);
        PlanarImage mask;
        if (s.UseMask)
        {
            mask = st.Luma();
            var m = mask.Data;
            float lo = s.Threshold, hi = s.Threshold + MathF.Max(s.Softness, 1e-3f);
            for (int i = 0; i < n; i++)
            {
                float t = MathF.Min(MathF.Max((m[i] - lo) / (hi - lo), 0), 1);
                m[i] = t * t * (3 - 2 * t);
            }
        }
        else mask = new PlanarImage(img.Width, img.Height, 1);
        var imgPlanes = img.Planes; var stPlanes = st.Planes;
        var md = mask.Data;
        for (int c = 0; c < 3; c++)
        {
            var a = imgPlanes[c].Data; var b = stPlanes[c].Data;
            var mode = s.Mode; float amount = s.Amount;
            Par.Rows(img.Height, w * 4, (y0, y1) =>
            {
                for (int i = y0 * w; i < y1 * w; i++)
                {
                    float blended = mode switch
                    {
                        StarsBlendMode.Screen => 1 - (1 - a[i]) * (1 - b[i]),
                        StarsBlendMode.Lighten => MathF.Max(a[i], b[i]),
                        _ => a[i] + b[i],
                    };
                    a[i] += amount * md[i] * (blended - a[i]);
                }
            });
        }
        img.Clamp01();
    }
}
