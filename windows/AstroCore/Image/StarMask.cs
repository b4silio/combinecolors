namespace AstroCore;

/// <summary>
/// Builds a binary exclusion mask (1 = star / invalid, 0 = usable sky or nebula) so that
/// statistics describe the background and the extended emission rather than stars.
/// </summary>
public static class StarMask
{
    public sealed class Options
    {
        /// <summary>Detection threshold in units of the high-pass noise sigma.</summary>
        public float ThresholdSigma = 4;
        /// <summary>Half-size of the dilation applied to detected pixels (in mask pixels).</summary>
        public int Dilation = 2;
        /// <summary>Pyramid levels used to estimate the local background (2^n blur radius).</summary>
        public int BackgroundLevels = 3;
        /// <summary>Upper bound on the pixels sampled for the noise estimate.</summary>
        public int MaxSamples = 300_000;
    }

    /// <summary>Builds the mask for <paramref name="image"/> (normally a pyramid level with a few megapixels).</summary>
    public static PlanarImage Build(PlanarImage image, Options? options = null)
    {
        options ??= new Options();
        int w = image.Width, h = image.Height, n = w * h;
        // Local background: heavy box-pyramid blur then bilinear up-scaling.
        var bg = image;
        for (int i = 0; i < options.BackgroundLevels && bg.Width > 8 && bg.Height > 8; i++) bg = bg.Downsampled2x();
        var background = ReferenceEquals(bg, image) ? image.Copy() : bg.Resized(w, h);
        // High-pass residual.
        var hp = PlanarImage.Uninitialized(w, h);
        Vec.Sub(image.Data, background.Data, hp.Data);
        // Robust sigma of the residual on a sample excluding zero padding.
        var sampleHP = new List<float>(Math.Min(n, options.MaxSamples));
        int step = Math.Max(1, n / options.MaxSamples);
        var src = image.Data; var hpd = hp.Data;
        for (int i = 0; i < n; i += step) if (src[i] != 0) sampleHP.Add(hpd[i]);
        float sigma = 0;
        if (sampleHP.Count > 0)
        {
            var arr = sampleHP.ToArray();
            Array.Sort(arr);
            float med = Statistics.Percentile(arr, 0.5);
            sigma = 1.4826f * Statistics.MedianAbsoluteDeviation(arr, med);
        }
        float thr = MathF.Max(sigma * options.ThresholdSigma, 1e-12f);
        // Threshold: 1 where hp > thr.
        var mask = PlanarImage.Uninitialized(w, h);
        var md = mask.Data;
        for (int i = 0; i < n; i++) md[i] = hpd[i] > thr ? 1 : 0;
        // Dilate.
        if (options.Dilation > 0)
        {
            int k = 2 * options.Dilation + 1;
            mask = Convolve.DilateMax(mask, k);
            md = mask.Data;
        }
        // Invalid (zero) pixels are excluded as well.
        for (int j = 0; j < n; j++) if (src[j] == 0) md[j] = 1;
        return mask;
    }

    /// <summary>Fraction of masked pixels.</summary>
    public static float Coverage(PlanarImage mask) => Vec.Sum(mask.Data) / mask.Count;
}
