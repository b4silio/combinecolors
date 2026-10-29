using AstroCore;

namespace AstroCore.Tests;

public class PixelMathTests
{
    static float Eval(string s, Dictionary<string, float>? vars = null)
        => PixelMath.EvaluateScalar(PixelMath.Parse(s), vars ?? new Dictionary<string, float>());

    [Fact]
    public void Precedence()
    {
        Assert.Equal(7, Eval("1 + 2 * 3"));
        Assert.Equal(9, Eval("(1 + 2) * 3"));
        Assert.Equal(512, Eval("2 ^ 3 ^ 2"));
        Assert.Equal(-4, Eval("-2 ^ 2"));
        Assert.Equal(0.5f, Eval("2 ^ -1"));
        Assert.Equal(0.75f, Eval("~0.25"));
        Assert.Equal(0.75f, Eval("~0.5 ^ 2"));     // ~(0.5^2)
        A.Close(MathF.Pow(0.5f, 0.25f), Eval("0.5 ^ ~0.75"), 1e-6f); // 0.5^0.25
        Assert.Equal(1, Eval("7 % 3"));
        Assert.Equal(1, Eval("1 < 2"));
        Assert.Equal(1, Eval("1 > 2 || 3 >= 3"));
        Assert.Equal(0, Eval("1 == 1 && 0"));
        Assert.Equal(1, Eval("!0"));
        Assert.Equal(5, Eval("1 ? 5 : 6"));
        Assert.Equal(6, Eval("0 ? 5 : 6"));
        Assert.Equal(1, Eval("1e-2 * 100"));
        Assert.Equal(1, Eval(".5 + .5"));
        Assert.Equal(MathF.PI, Eval("pi"));
    }

    [Fact]
    public void Functions()
    {
        Assert.Equal(1, Eval("min(3, 1, 2)"));
        Assert.Equal(3, Eval("max(3, 1, 2)"));
        Assert.Equal(2, Eval("mean(1, 2, 3)"));
        Assert.Equal(3, Eval("med(5, 1, 3)"));
        Assert.Equal(1, Eval("clamp(2)"));
        Assert.Equal(-1, Eval("clamp(-2, -1, 1)"));
        Assert.Equal(10, Eval("iif(1 > 0, 10, 20)"));
        A.Close(3, Eval("lerp(0, 10, 0.3)"), 1e-6f);
        A.Close(0.3f, Eval("mtf(0.5, 0.3)"), 1e-6f);
        A.Close(0.5f, Eval("mtf(0.25, 0.25)"), 1e-6f);
        Assert.Equal(4, Eval("sqrt(16)"));
        Assert.Equal(1024, Eval("pow(2, 10)"));
        Assert.Equal(3, Eval("abs(-3)"));
        Assert.Equal(1, Eval("step(0.5, 0.7)"));
        A.Close(0.5f, Eval("smoothstep(0, 1, 0.5)"), 1e-6f);
        A.Close(5, Eval("exp(ln(5))"), 1e-5f);
        Assert.Equal(0, Eval("asinh(0)"));
        Assert.Equal(8, Eval("floor(2.7) + ceil(2.1) + round(2.5)"));
    }

    [Fact]
    public void VariablesAndAliases()
    {
        var vars = new Dictionary<string, float> { ["ha"] = 0.5f, ["oiii"] = 0.25f, ["sii"] = 0.125f, ["r"] = 1 };
        Assert.Equal(1.875f, Eval("Ha + OIII + SII + R", vars));
        Assert.Equal(0.875f, Eval("H + O + S", vars));
        Assert.Equal(0.015625f, Eval("ha*o3*s2", vars));
        Assert.Equal(0.5f, Eval("Halpha", vars));
        Assert.Equal(0.5f, Eval("Hydrogen", vars));
    }

    [Fact]
    public void Errors()
    {
        Assert.Throws<PixelMathException>(() => Eval("1 +"));
        Assert.Throws<PixelMathException>(() => Eval("(1 + 2"));
        Assert.Throws<PixelMathException>(() => Eval("foo(1)"));
        Assert.Throws<PixelMathException>(() => Eval("min()"));
        Assert.Throws<PixelMathException>(() => Eval("clamp(1, 2)"));
        Assert.Throws<PixelMathException>(() => Eval("1 = 2"));
        Assert.Throws<PixelMathException>(() => Eval(""));
        Assert.Throws<PixelMathException>(() => Eval("Ha + 1"));   // unknown variable in this scope
        Assert.Throws<PixelMathException>(() => Eval("1 # 2"));
        var e = Assert.Throws<PixelMathException>(() => Eval("1 + (2 * 3"));
        Assert.Equal("Expected ')'", e.Detail); Assert.Equal(10, e.Position);
    }

    [Fact]
    public void VectorEvaluationMatchesScalar()
    {
        int w = 64, h = 48;
        var ha = Synth.Channel(w, h, seed: 1); var o3 = Synth.Channel(w, h, seed: 2); var s2 = Synth.Channel(w, h, seed: 3);
        foreach (var p in new[] { ha, o3, s2 }) { var r = Stretch.Resolve(new StretchSettings(), Statistics.Compute(p)); r.C1 = 1; r.Apply(p); }
        var env = new PixelMathEnvironment(w, h);
        env.Set(ChannelKind.Ha, ha, Statistics.Compute(ha)); env.Set(ChannelKind.OIII, o3, Statistics.Compute(o3)); env.Set(ChannelKind.SII, s2);
        string[] formulas =
        [
            "(OIII^~OIII)*SII + ~(OIII^~OIII)*Ha",
            "((OIII*Ha)^~(OIII*Ha))*Ha + ~((OIII*Ha)^~(OIII*Ha))*OIII",
            "max(SII,Ha,OIII) * 3*SII/(SII+Ha+OIII+0.001)",
            "iif(Ha > OIII, Ha - OIII, 0) + (SII < 0.5 ? SII : 0.5)",
            "lerp(Ha, OIII, SII) + mtf(0.3, Ha) + clamp(Ha * 2) + min(Ha, 0.2) + med(Ha, OIII, SII)",
            "Ha - cmed(Ha) + cbg(OIII) * 0 + cpct(Ha, 0.5) - cmed(Ha)",
            "sqrt(Ha) + log(1 + OIII) + exp(-SII) + asinh(Ha * 10) + abs(Ha - OIII) + Ha % 0.1 + 2 ^ Ha + Ha ^ OIII",
            "(Ha >= OIII) * 0.5 + (Ha != OIII && SII <= 1) + !(Ha == 2)",
        ];
        var stats = new Dictionary<string, ChannelStats> { ["ha"] = env.Stats["ha"], ["oiii"] = env.Stats["oiii"] };
        foreach (var f in formulas)
        {
            var expr = PixelMath.Parse(f);
            var out_ = PixelMath.EvaluateToPlane(expr, env);
            for (int i = 0; i < w * h; i += 97)
            {
                var vars = new Dictionary<string, float> { ["ha"] = ha.Data[i], ["oiii"] = o3.Data[i], ["sii"] = s2.Data[i] };
                float reference = PixelMath.EvaluateScalar(expr, vars, stats);
                A.Close(reference, out_.Data[i], MathF.Max(1e-4f, MathF.Abs(reference) * 1e-4f), $"formula {f} at {i}");
            }
        }
    }

    [Fact]
    public void ProgramCompileAndRequiredChannels()
    {
        var p = new PixelMathProgram("SII", "Ha", "OIII", "L");
        var c = p.Compile();
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.SII, ChannelKind.Ha, ChannelKind.OIII, ChannelKind.Luminance }, c.RequiredChannels);
        Assert.Empty(c.UnknownIdentifiers);
        var bad = new PixelMathProgram("SII +", "Ha", "OIII");
        var e = Assert.Throws<PixelMathException>(() => bad.Compile());
        Assert.StartsWith("Red:", e.Detail);
        var unknown = new PixelMathProgram("foo", "Ha", "OIII").Compile();
        Assert.Equal(new[] { "foo" }, unknown.UnknownIdentifiers);
    }

    [Fact]
    public void AllPresetPalettesCompile()
    {
        foreach (var p in Palettes.All)
        {
            if (p.Kind is PaletteKind.PixelMathKind pm) pm.Program.Compile();
            Assert.True(p.IsMonochrome || p.RequiredChannels.Count > 0, p.Id);
        }
        Assert.Equal(Palettes.All.Count, Palettes.All.Select(p => p.Id).Distinct().Count());
    }
}
