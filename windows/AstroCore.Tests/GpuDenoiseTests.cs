using AstroCore;

namespace AstroCore.Tests;

/// <summary>The Direct3D compute paths must reproduce the CPU ports (which in turn mirror the Metal shaders).</summary>
[Collection("denoise-globals")]
public class GpuDenoiseTests : IDisposable
{
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

    public void Dispose() { Bm3dDenoise.TileSize = 1536; Bm3dDenoise.Enabled = true; GpuDenoise.Enabled = true; }

    [Fact]
    public void GpuNonLocalMeansMatchesTheCpu()
    {
        if (!GpuDenoise.IsAvailable) return;   // no GPU
        var (_, noisy) = Scene();
        float sigma = Denoise.NoiseSigma(noisy);
        var cpu = Denoise.NonLocalMeans(noisy, sigma, 1.2f * sigma, 5);
        var gpu = GpuDenoise.NonLocalMeans(noisy, sigma, 1.2f * sigma, 5)!;
        // Identical away from the borders (the CPU path extends edges slightly differently).
        var inner = gpu.Cropped(8, 8, gpu.Width - 16, gpu.Height - 16);
        Assert.True(inner.MaxAbsDifference(cpu.Cropped(8, 8, cpu.Width - 16, cpu.Height - 16)) < 2e-4f);
        Assert.True(gpu.MaxAbsDifference(cpu) < 0.01f);
        // Tiling with a tiny tile gives the same result as one tile.
        Bm3dDenoise.TileSize = 64;
        var tiled = GpuDenoise.NonLocalMeans(noisy, sigma, 1.2f * sigma, 5)!;
        float td = tiled.MaxAbsDifference(gpu);
        int worst = 0; for (int i = 0; i < gpu.Count; i++) if (MathF.Abs(tiled.Data[i] - gpu.Data[i]) == td) { worst = i; break; }
        Assert.True(td < 1e-5f, $"tiled vs whole differ by {td} at ({worst % gpu.Width}, {worst / gpu.Width})");
    }

    [Fact]
    public void GpuBm3dMatchesTheCpuAndTilesSeamlessly()
    {
        if (!GpuDenoise.IsAvailable) return;   // no GPU
        var (clean, noisy) = Scene();
        float sigma = Denoise.NoiseSigma(noisy);
        var cpu = Bm3dDenoise.Bm3d(noisy, sigma, 1, 16)!;
        var gpu = GpuDenoise.Bm3d(noisy, sigma, 1, 16)!;
        // Same algorithm, same groups; only the order of the float additions differs.
        Assert.True(gpu.MaxAbsDifference(cpu) < 2e-3f, $"GPU vs CPU BM3D differ by {gpu.MaxAbsDifference(cpu)}");
        Assert.True(Rms(gpu, clean, true) < Rms(noisy, clean, true) * 0.4f);
        A.Close(clean[50, 60], gpu[50, 60], 0.05f, "star peak kept");
        Bm3dDenoise.TileSize = 64;
        var tiled = GpuDenoise.Bm3d(noisy, sigma, 1, 16)!;
        Assert.True(tiled.MaxAbsDifference(gpu) < 1e-3f);
    }

    [Fact]
    public void ApplyPrefersTheGpuAndFallsBack()
    {
        var (_, noisy) = Scene();
        var d = new DenoiseSettings { Method = DenoiseMethod.Bm3d, Amount = 0.8f };
        var img = new RGBImage(noisy.Copy(), noisy.Copy(), noisy.Copy());
        Denoise.Apply(img, d);
        Assert.True(Denoise.NoiseSigma(img.Luma()) < Denoise.NoiseSigma(noisy) * 0.7f);
        GpuDenoise.Enabled = false;
        var img2 = new RGBImage(noisy.Copy(), noisy.Copy(), noisy.Copy());
        Denoise.Apply(img2, d);
        Assert.True(Denoise.NoiseSigma(img2.Luma()) < Denoise.NoiseSigma(noisy) * 0.7f);
        if (GpuDenoise.IsAvailable || true) Assert.True(img.Luma().MaxAbsDifference(img2.Luma()) < 5e-3f);
    }
}
