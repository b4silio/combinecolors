using AstroCore;

namespace AstroCore.Tests;

[Collection("denoise-globals")]
public class DenoiseTests
{
    /// <summary>Flat sky with Gaussian noise, a soft nebula gradient and a star.</summary>
    static (RGBImage clean, RGBImage noisy) Scene(float noise, ulong seed = 1)
    {
        int w = 128, h = 96;
        var rng = new SeededRng(seed);
        var clean = new RGBImage(w, h); var noisy = new RGBImage(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float dx = x - 40, dy = y - 48;
            float v = 0.25f + 0.15f * x / w + 0.7f * MathF.Exp(-(dx * dx + dy * dy) / 6);
            float n = rng.Gaussian() * noise;
            clean.R[x, y] = v; clean.G[x, y] = v; clean.B[x, y] = v;
            noisy.R[x, y] = v + n; noisy.G[x, y] = v + n + rng.Gaussian() * noise * 0.5f; noisy.B[x, y] = v + n;
        }
        noisy.Clamp01();
        return (clean, noisy);
    }

    static float Rms(PlanarImage a, PlanarImage b, Func<int, int, bool> keep)
    {
        float sum = 0; int n = 0;
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (keep(x, y)) { float d = a[x, y] - b[x, y]; sum += d * d; n++; }
        return MathF.Sqrt(sum / Math.Max(n, 1));
    }

    [Fact]
    public void NoiseEstimate()
    {
        var (_, noisy) = Scene(0.02f);
        float s = Denoise.NoiseSigma(noisy.Luma());
        A.Close(0.02f, s, 0.006f);
    }

    [Fact]
    public void EveryMethodReducesNoiseAndKeepsTheStar()
    {
        var (clean, noisy) = Scene(0.02f);
        bool Background(int x, int y) => (x - 40) * (x - 40) + (y - 48) * (y - 48) > 100;
        float before = Rms(noisy.Luma(), clean.Luma(), Background);
        foreach (var m in DenoiseMethods.All)
        {
            var img = noisy.Copy();
            var d = new DenoiseSettings { Method = m, Amount = 0.8f, ProtectBright = 0.5f };
            Denoise.Apply(img, d);
            float after = Rms(img.Luma(), clean.Luma(), Background);
            Assert.True(after < before * 0.6f, $"{m} should cut the background noise ({after} vs {before})");
            A.Close(noisy.Luma()[40, 48], img.Luma()[40, 48], 0.03f, $"{m} must keep the star peak");
            A.Bounded(img, m.ToString());
        }
    }

    [Fact]
    public void ColourSmoothingKeepsLuminance()
    {
        var (_, noisy) = Scene(0.02f);
        var img = noisy.Copy();
        var d = new DenoiseSettings { Color = 1 };
        Denoise.Apply(img, d);
        Assert.True(img.Luma().MaxAbsDifference(noisy.Luma()) < 0.01f);
        // The green-only noise (chroma) is reduced.
        static float ChromaStd(RGBImage i)
        {
            float s = 0; for (int k = 0; k < i.R.Count; k++) { float d = i.G.Data[k] - i.R.Data[k]; s += d * d; }
            return MathF.Sqrt(s / i.R.Count);
        }
        Assert.True(ChromaStd(img) < ChromaStd(noisy) * 0.5f);
    }

    [Fact]
    public void NeutralAndDecoding()
    {
        Assert.True(new DenoiseSettings().IsNeutral);
        var d = new DenoiseSettings { Color = 0.3f }; Assert.False(d.IsNeutral);
        var decoded = DenoiseSettings.FromJson(SettingsJson.ParseObject("{\"amount\": 0.4, \"method\": \"guided\"}"));
        Assert.Equal(0.4f, decoded.Amount); Assert.Equal(DenoiseMethod.Guided, decoded.Method); Assert.Equal(3, decoded.Layers);
        var old = CombineSettings.Parse("{}");
        Assert.True(old.Denoise.IsNeutral);
    }

    [Fact]
    public void DenoiseStripsMatchWholeRender()
    {
        var sources = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha), [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 0.4f, 2) };
        var planes = sources.Images();
        var s = new CombineSettings(); s.Select(Palettes.Palette("hoo")!);
        s.Denoise.Amount = 0.7f; s.Denoise.Color = 0.5f;
        var ctx = RenderContext.Make(s.Normalization, sources);
        var samples = Renderer.AnalysisSamples(sources);
        var whole = Renderer.Render(new RenderRequest(planes, s, ctx, samples));
        var strips = Renderer.RenderFull(sources, s, ctx, whole.PostStretch, stripPixels: 256 * 40)!;
        for (int c = 0; c < 3; c++) Assert.True(whole.Image.Planes[c].MaxAbsDifference(strips.Planes[c]) < 1e-4f);
    }
}

[Collection("denoise-globals")]
public class Bm3dTests : IDisposable
{
    /// <summary>Noisy sky with a gradient, a faint nebula blob and a star (luminance plane).</summary>
    static (PlanarImage clean, PlanarImage noisy) Scene(int w = 160, int h = 120, float noise = 0.02f, ulong seed = 3)
    {
        var rng = new SeededRng(seed);
        var clean = new PlanarImage(w, h); var noisy = new PlanarImage(w, h);
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            float dx = x - 50, dy = y - 60, nx = x - 110, ny = y - 50;
            float v = 0.25f + 0.03f * x / w + 0.7f * MathF.Exp(-(dx * dx + dy * dy) / 6) + 0.1f * MathF.Exp(-(nx * nx + ny * ny) / 300);
            clean[x, y] = v;
            noisy[x, y] = MathF.Min(MathF.Max(v + rng.Gaussian() * noise, 0), 1);
        }
        return (clean, noisy);
    }

    static float Rms(PlanarImage a, PlanarImage b, bool background)
    {
        float s = 0; int n = 0;
        for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++)
        {
            if ((((x - 50) * (x - 50) + (y - 60) * (y - 60)) > 100) != background) continue;
            float d = a[x, y] - b[x, y]; s += d * d; n++;
        }
        return MathF.Sqrt(s / Math.Max(n, 1));
    }

    public void Dispose() { Bm3dDenoise.TileSize = 1536; Bm3dDenoise.Enabled = true; Bm3dDenoise.WienerStage = true; }

    [Fact]
    public void Dct8x8RoundTripsAndIsOrthonormal()
    {
        var rng = new SeededRng(5);
        var block = new float[64];
        for (int i = 0; i < 64; i++) block[i] = rng.Gaussian();
        var copy = (float[])block.Clone();
        Bm3dDenoise.Dct8x8(copy, false);
        float e1 = block.Sum(v => v * v), e2 = copy.Sum(v => v * v);
        A.Close(e1, e2, 1e-3f * e1);
        Bm3dDenoise.Dct8x8(copy, true);
        for (int i = 0; i < 64; i++) A.Close(block[i], copy[i], 1e-4f);
        // A constant block has only a DC term.
        var flat = Enumerable.Repeat(0.5f, 64).ToArray();
        Bm3dDenoise.Dct8x8(flat, false);
        A.Close(4, flat[0], 1e-5f);
        for (int i = 1; i < 64; i++) A.Close(0, flat[i], 1e-5f);
    }

    [Fact]
    public void Bm3dReducesNoiseKeepsStarAndTilesSeamlessly()
    {
        var (clean, noisy) = Scene();
        float sigma = Denoise.NoiseSigma(noisy);
        var out_ = Bm3dDenoise.Bm3d(noisy, sigma, 1, 16)!;
        Assert.True(Rms(out_, clean, true) < Rms(noisy, clean, true) * 0.4f, "BM3D should cut the background noise by more than half");
        A.Close(clean[50, 60], out_[50, 60], 0.05f, "star peak kept");
        A.Close(clean[110, 50], out_[110, 50], 0.02f, "faint nebula kept");
        var (lo, hi) = out_.MinMax(); Assert.True(lo > -0.01f); Assert.True(hi < 1.01f);
        Bm3dDenoise.TileSize = 64;
        var tiled = Bm3dDenoise.Bm3d(noisy, sigma, 1, 16)!;
        Assert.True(tiled.MaxAbsDifference(out_) < 1e-3f);
    }

    [Fact]
    public void Bm3dKeepsStructureBetterThanTheOtherMethods()
    {
        var (clean, noisy) = Scene(noise: 0.03f);
        float sigma = Denoise.NoiseSigma(noisy);
        var bm3d = Bm3dDenoise.Bm3d(noisy, sigma, 1, 16)!;
        var wav = Denoise.Wavelets(noisy, sigma, 1, 3);
        var nlm = Denoise.NonLocalMeans(noisy, sigma, 1.5f * sigma, 5);
        // Around the star BM3D is the most faithful; on the flat sky it is within reach of the wavelets.
        Assert.True(Rms(bm3d, clean, false) < Rms(nlm, clean, false));
        Assert.True(Rms(bm3d, clean, false) < Rms(wav, clean, false) * 0.5f);
        Assert.True(Rms(bm3d, clean, true) < Rms(wav, clean, true) * 1.4f);
        // The Wiener stage keeps structure at least as well as the hard-thresholding estimate and
        // does not undo its noise reduction (on white noise both are limited by the window size).
        Bm3dDenoise.WienerStage = false;
        var hard = Bm3dDenoise.Bm3d(noisy, sigma, 1, 16)!;
        Bm3dDenoise.WienerStage = true;
        Assert.True(Rms(bm3d, clean, false) <= Rms(hard, clean, false) * 1.02f);
        Assert.True(Rms(bm3d, clean, true) < Rms(hard, clean, true) * 1.15f);
    }

    [Fact]
    public void ApplyUsesBm3dAndFallsBackWhenDisabled()
    {
        var (_, noisy) = Scene();
        var img = new RGBImage(noisy.Copy(), noisy.Copy(), noisy.Copy());
        var d = new DenoiseSettings { Method = DenoiseMethod.Bm3d, Amount = 0.8f };
        Denoise.Apply(img, d);
        Assert.True(Denoise.NoiseSigma(img.Luma()) < Denoise.NoiseSigma(noisy) * 0.7f);
        Bm3dDenoise.Enabled = false;
        var img2 = new RGBImage(noisy.Copy(), noisy.Copy(), noisy.Copy());
        Denoise.Apply(img2, d);
        Assert.True(Denoise.NoiseSigma(img2.Luma()) < Denoise.NoiseSigma(noisy) * 0.7f);
    }
}

/// <summary>
/// Opt-in timing of the denoise methods on real data: set CC_BENCH=&lt;fits file&gt; (and CC_BENCH_TIMING=1).
/// With CC_BENCH_OUT=&lt;dir&gt; it also writes 8-bit PGM crops of every variant for a visual check.
/// </summary>
public class DenoiseBenchmark
{
    [Fact]
    public void Timings()
    {
        var path = Environment.GetEnvironmentVariable("CC_BENCH");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;   // skipped
        var f = ImageLoader.Load(path);
        var full = f.Planes[0];
        // Crude stretch so the noise level resembles the app's output.
        var (lo, _) = full.MinMax();
        for (int i = 0; i < full.Count; i++) full.Data[i] = MathF.Asinh((full.Data[i] - lo) * 300) / MathF.Asinh(300);
        if (Environment.GetEnvironmentVariable("CC_BENCH_OUT") is string dir && dir.Length > 0)
        {
            Directory.CreateDirectory(dir);
            var crop = full.Cropped(2900, 2300, 450, 350);
            float sigma = Denoise.NoiseSigma(crop);
            Console.WriteLine($"sigma {sigma} spectrum {string.Join(" ", Bm3dDenoise.CoefficientNoise(crop).Select(v => v.ToString("F4")))}");
            var sorted = crop.ToArray(); Array.Sort(sorted);
            float a = Statistics.Percentile(sorted, 0.02), b = Statistics.Percentile(sorted, 0.9);
            void Save(string name, PlanarImage p)
            {
                using var fs = File.Create(Path.Combine(dir, name + ".pgm"));
                var hdr = System.Text.Encoding.ASCII.GetBytes($"P5\n{p.Width} {p.Height}\n255\n");
                fs.Write(hdr);
                fs.Write(p.Data.Select(v => (byte)MathF.Min(MathF.Max((v - a) / (b - a) * 255, 0), 255)).ToArray());
            }
            Save("noisy", crop);
            Save("wavelets", Denoise.Wavelets(crop, sigma, 0.8f, 3));
            Save("nlm", Denoise.NonLocalMeans(crop, sigma, 1.2f * sigma, 5));
            foreach (var amt in new float[] { 0.8f, 1, 1.3f }) foreach (var srch in new[] { 8, 16 })
                Save($"bm3d_a{amt}_s{srch}", Bm3dDenoise.Bm3d(crop, sigma, amt, srch)!);
        }
        if (Environment.GetEnvironmentVariable("CC_BENCH_TIMING") == null) return;
        var sizes = new[] { ("preview 1900×1300", full.Cropped(2000, 1500, 1900, 1300)), ($"full {full.Width}×{full.Height}", full) };
        foreach (var (name, img) in sizes)
        {
            float sigma = Denoise.NoiseSigma(img);
            void Time(string label, Func<PlanarImage?> body)
            {
                var t = System.Diagnostics.Stopwatch.StartNew(); body(); Console.WriteLine($"{name} {label}: {t.Elapsed.TotalSeconds:F3} s");
            }
            Time("wavelets", () => Denoise.Wavelets(img, sigma, 0.8f, 3));
            Time("guided", () => Denoise.Guided(img, MathF.Pow(2.4f * sigma, 2), 3));
            if (name.StartsWith("preview")) Time("nlm", () => Denoise.NonLocalMeans(img, sigma, 1.2f * sigma, 5));
            Time("bm3d s8", () => Bm3dDenoise.Bm3d(img, sigma, 0.8f, 8));
            Time("bm3d s16", () => Bm3dDenoise.Bm3d(img, sigma, 0.8f, 16));
            Time("noise spectrum", () => { Bm3dDenoise.CoefficientNoise(img); return null; });
        }
    }
}
