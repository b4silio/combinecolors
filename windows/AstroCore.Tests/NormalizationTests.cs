using AstroCore;

namespace AstroCore.Tests;

public class NormalizationTests
{
    [Fact]
    public void RobustLinearFitRecoversParametersWithOutliers()
    {
        var rng = new Synth.LCG(11);
        var xs = new List<float>(); var ys = new List<float>();
        for (int i = 0; i < 5000; i++)
        {
            float x = i / 5000f;
            float y = 2.5f * x + 0.1f + 0.001f * rng.Gaussian();
            if (i % 50 == 0) y += 1;   // outliers
            xs.Add(x); ys.Add(y);
        }
        var fit = Normalizer.RobustLinearFit(xs.ToArray(), ys.ToArray());
        A.Close(2.5f, fit.a, 0.01f);
        A.Close(0.1f, fit.b, 0.002f);
    }

    [Fact]
    public void UniformLutApplyMatchesEvaluate()
    {
        var table = Enumerable.Range(0, 1024).Select(i => i / 1023f * 2).ToArray();
        var lut = new UniformLUT(0.2f, 0.8f, table, 0.5f, 3);
        float[] xs = [-1, 0, 0.2f, 0.3f, 0.5f, 0.8f, 1.0f, 5];
        var img = new PlanarImage(xs.Length, 1, xs);
        lut.Apply(img);
        var got = img.ToArray();
        for (int i = 0; i < xs.Length; i++) A.Close(lut.Evaluate(xs[i]), got[i], 1e-4f, $"x={xs[i]}");
        A.Close(1, lut.Evaluate(0.5f), 1e-3f);
        A.Close(2 + 3 * 0.2f, lut.Evaluate(1.0f), 1e-5f);
        A.Close(-0.1f, lut.Evaluate(0), 1e-5f);
    }

    static List<NormalizationInput> MakeInputs()
    {
        var ha = Synth.Source(ChannelKind.Ha, 1, 1);
        var o3 = Synth.Source(ChannelKind.OIII, 0.4f, 2);
        var s2 = Synth.Source(ChannelKind.SII, 0.25f, 3);
        return [ha.NormalizationInput, o3.NormalizationInput, s2.NormalizationInput];
    }

    [Fact]
    public void AllMethodsProduceSensibleTransforms()
    {
        var inputs = MakeInputs();
        var reference = inputs[0];
        foreach (var method in NormalizationMethods.All)
        {
            var s = new NormalizationSettings { Method = method };
            var t = Normalizer.Transforms(s, inputs);
            Assert.True(t[ChannelKind.Ha].IsIdentity, method.ToString());
            var o3 = t[ChannelKind.OIII];
            var st = o3.Transform(inputs[1].MaskedStats);
            switch (method)
            {
                case NormalizationMethod.None:
                    Assert.True(o3.IsIdentity);
                    break;
                case NormalizationMethod.BackgroundEqualize:
                    A.Close(reference.MaskedStats.Background, st.Background, 1e-4f);
                    Assert.Equal(1, Assert.IsType<ChannelTransform.AffineT>(o3).A);
                    break;
                case NormalizationMethod.BackgroundDispersion:
                    A.Close(reference.MaskedStats.Background, st.Background, 1e-4f);
                    A.Close(reference.MaskedStats.BackgroundSigma, st.BackgroundSigma, 1e-4f);
                    break;
                case NormalizationMethod.BackgroundSignal:
                    A.Close(reference.MaskedStats.Background, st.Background, 1e-4f);
                    A.Close(reference.MaskedStats.Percentile(0.995) - reference.MaskedStats.Background, st.Percentile(0.995) - st.Background, 1e-3f);
                    break;
                case NormalizationMethod.NoiseWeighted:
                    A.Close(reference.MaskedStats.Noise, st.Noise, 1e-4f);
                    break;
                case NormalizationMethod.LinearFit:
                    A.Close(2.5f, Assert.IsType<ChannelTransform.AffineT>(o3).A, 0.3f);
                    break;
                case NormalizationMethod.HistogramMatch:
                    foreach (var q in new[] { 0.1, 0.5, 0.9 }) A.Close(reference.MaskedStats.Percentile(q), st.Percentile(q), 0.002f, $"q={q}");
                    break;
            }
        }
    }

    [Fact]
    public void ReferenceSelection()
    {
        var inputs = MakeInputs();
        var s = new NormalizationSettings();
        Assert.Equal(ChannelKind.Ha, Normalizer.ReferenceChannel(s, inputs));
        s.Reference = ChannelKind.SII;
        Assert.Equal(ChannelKind.SII, Normalizer.ReferenceChannel(s, inputs));
        s.Reference = ChannelKind.Luminance;   // not available → automatic
        Assert.Equal(ChannelKind.Ha, Normalizer.ReferenceChannel(s, inputs));
        Assert.Equal(ChannelKind.OIII, Normalizer.ReferenceChannel(s, inputs.Skip(1).ToList()));
    }

    [Fact]
    public void RenderContextTransformsStats()
    {
        var ha = Synth.Source(ChannelKind.Ha); var o3 = Synth.Source(ChannelKind.OIII, 0.5f, 5);
        var s = new NormalizationSettings { Method = NormalizationMethod.BackgroundDispersion };
        var ctx = RenderContext.Make(s, new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = ha, [ChannelKind.OIII] = o3 });
        Assert.Equal(ChannelKind.Ha, ctx.Reference);
        A.Close(ha.MaskedStats.Background, ctx.NormalizedMaskedStats[ChannelKind.OIII].Background, 1e-4f);
        Assert.True(ctx.ReferenceBackgroundSigma > 0);
    }
}
