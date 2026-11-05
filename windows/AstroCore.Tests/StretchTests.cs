using AstroCore;

namespace AstroCore.Tests;

public class StretchTests
{
    [Fact]
    public void MtfProperties()
    {
        foreach (var x in new float[] { 0, 0.1f, 0.5f, 0.9f, 1 }) A.Close(x, Stretch.Mtf(0.5f, x), 1e-6f);
        foreach (var m in new float[] { 0.01f, 0.25f, 0.5f, 0.75f }) A.Close(0.5f, Stretch.Mtf(m, m), 1e-5f);
        Assert.Equal(0, Stretch.Mtf(0.25f, 0));
        Assert.Equal(1, Stretch.Mtf(0.25f, 1));
        // MTF(t, v) is the midtones parameter that maps v to t.
        float mm = Stretch.Mtf(0.25f, 0.002f);
        A.Close(0.25f, Stretch.Mtf(mm, 0.002f), 1e-5f);
    }

    [Fact]
    public void AutoSTFMapsMedianToTarget()
    {
        var img = Synth.Channel(300, 200, 0.006f, 0.001f, 0.3f, 0.00005f);
        var stats = Statistics.Compute(img);
        var s = new StretchSettings { TargetBackground = 0.25f };
        var r = Stretch.Resolve(s, stats);
        A.Close(stats.Median - 2.8f * stats.Madn, r.C0, 1e-7f);
        A.Close(0.25f, r.Evaluate(stats.Median), 1e-4f);
        Assert.Equal(0, r.Evaluate(0));
        Assert.Equal(1, r.Evaluate(1));
        // Vectorised application matches the scalar reference.
        var copy = img.Copy();
        r.Apply(copy);
        float maxErr = 0;
        for (int i = 0; i < img.Count; i++) maxErr = MathF.Max(maxErr, MathF.Abs(copy.Data[i] - r.Evaluate(img.Data[i])));
        Assert.True(maxErr < 1e-4f);
    }

    [Fact]
    public void LinkedUsesAveragedStats()
    {
        var a = Statistics.FromSample([0.1f, 0.2f, 0.3f]); var b = Statistics.FromSample([0.5f, 0.6f, 0.7f]);
        var s = new StretchSettings();
        var r = Stretch.ResolveLinked(s, [a, b]);
        A.Close(s.TargetBackground, r.Evaluate(0.4f), 1e-4f);
    }

    /// <summary>Linear stacks of very different depth: the manual strengths are relative to the noise, so
    /// the default of every method puts the sky at a similar, sensible level on both.</summary>
    [Fact]
    public void ManualStrengthsAreRelativeToTheNoise()
    {
        static ChannelStats Stack(float sky, float noise)
        {
            // Gaussian-ish sky (deterministic) plus a sparse bright tail.
            var v = new List<float>();
            for (int i = 0; i < 20000; i++)
            {
                double u = (i + 0.5) / 20000;
                float z = (float)(Math.Sqrt(2) * ErfInv(2 * u - 1));
                v.Add(MathF.Max(sky + noise * z, 0));
            }
            for (int i = 0; i < 200; i++) v.Add(MathF.Min(sky + i * 0.004f, 1));
            return Statistics.FromSample(v.ToArray());
        }
        ChannelStats deep = Stack(0.006f, 0.0003f), noisy = Stack(0.05f, 0.01f);
        foreach (var method in new[] { StretchMethod.Mtf, StretchMethod.Arcsinh, StretchMethod.Ghs, StretchMethod.Log })
        {
            var s = new StretchSettings { Method = method };
            foreach (var st in new[] { deep, noisy })
            {
                float sky = Stretch.Resolve(s, st).Evaluate(st.Median);
                Assert.True(sky > 0.12f, $"{method} sky too dark: {sky}");
                Assert.True(sky < 0.38f, $"{method} sky too bright: {sky}");
            }
            // Brighter with more strength (lower midtones for the MTF).
            var stronger = s.Clone();
            switch (method)
            {
                case StretchMethod.Mtf: stronger.MtfMidtonesSigma = 3; break;
                case StretchMethod.Arcsinh: stronger.ArcsinhStrength = 10; break;
                case StretchMethod.Ghs: stronger.GhsStrength = 1; break;
                default: stronger.LogStrength = 10; break;
            }
            Assert.True(Stretch.Resolve(stronger, deep).Evaluate(deep.Median) > Stretch.Resolve(s, deep).Evaluate(deep.Median), method.ToString());
        }
    }

    [Fact]
    public void GhsPointsArePercentilesOfTheImage()
    {
        var st = Statistics.FromSample(Enumerable.Range(0, 1001).Select(i => i / 1000f * 0.02f).ToArray());
        var s = new StretchSettings { Method = StretchMethod.Ghs, ShadowsClipping = -6, GhsSPPercentile = 50, GhsHPPercentile = 100 };
        var r = Stretch.Resolve(s, st);
        float expected = (st.Percentile(0.5) - r.C0) / (r.C1 - r.C0);
        A.Close(expected, r.GhsSP, 1e-5f);
        Assert.Equal(1f, r.GhsHP);
        s.GhsLPPercentile = 80;   // above SP: held at SP
        A.Close(r.GhsSP, Stretch.Resolve(s, st).GhsLP, 1e-6f);
    }

    /// <summary>Inverse error function (Giles 2010 approximation), for synthetic Gaussian samples.</summary>
    static double ErfInv(double x)
    {
        double w = -Math.Log((1 - x) * (1 + x)), p;
        if (w < 5)
        {
            w -= 2.5;
            p = 2.81022636e-08; p = 3.43273939e-07 + p * w; p = -3.5233877e-06 + p * w; p = -4.39150654e-06 + p * w;
            p = 0.00021858087 + p * w; p = -0.00125372503 + p * w; p = -0.00417768164 + p * w; p = 0.246640727 + p * w; p = 1.50140941 + p * w;
        }
        else
        {
            w = Math.Sqrt(w) - 3;
            p = -0.000200214257; p = 0.000100950558 + p * w; p = 0.00134934322 + p * w; p = -0.00367342844 + p * w;
            p = 0.00573950773 + p * w; p = -0.0076224613 + p * w; p = 0.00943887047 + p * w; p = 1.00167406 + p * w; p = 2.83297682 + p * w;
        }
        return p * x;
    }

    [Fact]
    public void OtherMethodsMonotonicAndBounded()
    {
        var stats = Statistics.FromSample(Enumerable.Range(0, 1000).Select(i => i / 1000f).ToArray());
        foreach (var method in new[] { StretchMethod.Arcsinh, StretchMethod.Ghs, StretchMethod.Log, StretchMethod.Mtf })
        {
            var s = new StretchSettings { Method = method, GhsStrength = 0.5f, GhsB = 1.5f, GhsSPPercentile = 40, GhsLPPercentile = 20, GhsHPPercentile = 90 };
            var r = Stretch.Resolve(s, stats);
            float prev = -1;
            for (int i = 0; i <= 200; i++)
            {
                float x = i / 200f;
                float y = r.Evaluate(x);
                Assert.True(y >= prev - 1e-5f, $"{method} not monotonic at {x}");
                Assert.True(y >= -1e-6f); Assert.True(y <= 1 + 1e-5f);
                prev = y;
            }
            A.Close(0, r.Evaluate(0), 1e-5f);
            A.Close(1, r.Evaluate(1), 1e-5f);
            // Vectorised vs scalar.
            var img = new PlanarImage(201, 1, Enumerable.Range(0, 201).Select(i => i / 200f).ToArray());
            var reference = img.ToArray().Select(r.Evaluate).ToArray();
            r.Apply(img);
            var got = img.ToArray();
            for (int i = 0; i < got.Length; i++) A.Close(reference[i], got[i], 2e-4f, method.ToString());
        }
    }

    [Fact]
    public void GhsDerivativeContinuityAtProtectionPoints()
    {
        float D = 50, b = 1, SP = 0.2f, LP = 0.1f, HP = 0.8f;
        float F(float x) => Stretch.Ghs(x, D, b, SP, LP, HP);
        float h = 1e-3f;
        A.Close((F(LP) - F(LP - h)) / h, (F(LP + h) - F(LP)) / h, 0.5f);
        A.Close((F(HP) - F(HP - h)) / h, (F(HP + h) - F(HP)) / h, 0.5f);
        A.Close(0, F(0), 1e-6f); A.Close(1, F(1), 1e-6f);
    }

    [Fact]
    public void GammaAndNone()
    {
        var s = new StretchSettings { Method = StretchMethod.None, Gamma = 0.5f };
        var r = Stretch.Resolve(s, new ChannelStats());
        A.Close(0.5f, r.Evaluate(0.25f), 1e-6f);
        var img = new PlanarImage(2, 1, [0.25f, 1]);
        r.Apply(img);
        A.Close(0.5f, img[0, 0], 1e-6f);
    }
}
