using AstroCore;

namespace AstroCore.Tests;

public static class Synth
{
    /// <summary>Deterministic pseudo-random generator.</summary>
    public struct LCG
    {
        public ulong State;
        public LCG(ulong seed) { State = seed; }
        public float Next()
        {
            State = unchecked(State * 6364136223846793005UL + 1442695040888963407UL);
            return ((State >> 33) & 0xFFFFFF) / (float)0xFFFFFF;
        }
        /// <summary>Approximately Gaussian via sum of 12 uniforms.</summary>
        public float Gaussian()
        {
            float s = 0;
            for (int i = 0; i < 12; i++) s += Next();
            return s - 6;
        }
    }

    /// <summary>Background + gradient + gaussian blobs (nebula) + stars + noise.</summary>
    public static PlanarImage Channel(int width = 256, int height = 192, float background = 0.01f, float nebula = 0.02f, float stars = 0.5f, float noise = 0.001f, ulong seed = 1, float gradient = 0)
    {
        var img = new PlanarImage(width, height);
        var rng = new LCG(seed);
        (float, float, float, float)[] blobs = [(0.5f, 0.5f, 40, 1), (0.3f, 0.6f, 25, 0.7f), (0.7f, 0.3f, 30, 0.5f)];
        var starList = new List<(float, float, float)>();
        for (int i = 0; i < 40; i++) starList.Add((rng.Next() * width, rng.Next() * height, 0.2f + rng.Next()));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                float v = background + gradient * x / width;
                foreach (var (cx, cy, s, a) in blobs)
                {
                    float dx = x - cx * width, dy = y - cy * height;
                    v += nebula * a * MathF.Exp(-(dx * dx + dy * dy) / (2 * s * s));
                }
                foreach (var (sx, sy, a) in starList)
                {
                    float dx = x - sx, dy = y - sy;
                    float d2 = dx * dx + dy * dy;
                    if (d2 < 36) v += stars * a * MathF.Exp(-d2 / 2.5f);
                }
                v += noise * rng.Gaussian();
                img[x, y] = MathF.Max(v, 1e-6f);
            }
        }
        return img;
    }

    public static ChannelSource Source(ChannelKind kind, float scale = 1, ulong seed = 1, int width = 256, int height = 192, float gradient = 0)
    {
        var img = Channel(width, height, 0.01f * scale, 0.02f * scale, 0.5f * scale, 0.001f * scale, seed, gradient);
        return new ChannelSource(kind, img, $"{kind.RawValue()}.fits");
    }

    public static string TempPath(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "CombineColorsTests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, name);
    }

    /// <summary>Folder with the user's sample stacks (tests that need them skip when it is absent).</summary>
    public const string SampleDir = @"C:\_Astrophotography";
}

/// <summary>Deterministic generator for the synthetic scenes (xorshift).</summary>
public struct SeededRng
{
    ulong state;
    public SeededRng(ulong seed) { state = unchecked(seed * 0x9E3779B97F4A7C15UL) | 1; }
    public ulong Next()
    {
        state ^= state << 13; state ^= state >> 7; state ^= state << 17;
        return state;
    }
    public float Gaussian()
    {
        float u1 = MathF.Max((Next() % 1_000_000) / 1_000_000f, 1e-6f), u2 = (Next() % 1_000_000) / 1_000_000f;
        return MathF.Sqrt(-2 * MathF.Log(u1)) * MathF.Cos(2 * MathF.PI * u2);
    }
    /// <summary>Uniform in [lo, hi].</summary>
    public float Uniform(float lo, float hi) => lo + (hi - lo) * ((Next() % 1_000_000) / 1_000_000f);
}

public static class TestExtensions
{
    public static float MaxAbsDifference(this PlanarImage a, PlanarImage other)
    {
        float m = 0;
        for (int i = 0; i < a.Count; i++) m = MathF.Max(m, MathF.Abs(a.Data[i] - other.Data[i]));
        return m;
    }

    public static Dictionary<ChannelKind, PlanarImage> Images(this IReadOnlyDictionary<ChannelKind, ChannelSource> sources)
        => sources.ToDictionary(kv => kv.Key, kv => kv.Value.Image);
}

public static class A
{
    public static void Close(double expected, double actual, double tol, string? msg = null)
        => Assert.True(Math.Abs(expected - actual) <= tol, $"{msg}expected {expected} ± {tol}, got {actual}".Replace("\0", ""));

    public static void Close(float expected, float actual, float tol, string? msg = null) => Close((double)expected, actual, tol, msg == null ? null : msg + ": ");

    public static void Bounded(RGBImage img, string? msg = null)
    {
        foreach (var p in img.Planes)
        {
            var (lo, hi) = p.MinMax();
            Assert.True(lo >= 0, $"{msg} min {lo} < 0"); Assert.True(hi <= 1, $"{msg} max {hi} > 1");
            Assert.DoesNotContain(p.Data, v => !float.IsFinite(v));
        }
    }
}
