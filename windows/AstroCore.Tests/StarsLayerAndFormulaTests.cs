using AstroCore;

namespace AstroCore.Tests;

public class StarsLayerAndFormulaTests
{
    /// <summary>A black frame with three stars of known peak, as an RGB stars-only layer.</summary>
    static List<PlanarImage> StarsPlanes(int w = 128, int h = 96, float peak = 0.6f)
    {
        var planes = new List<PlanarImage> { new(w, h), new(w, h), new(w, h) };
        foreach (var (sx, sy) in new[] { (20, 20), (64, 48), (100, 70) })
            for (int y = sy - 4; y <= sy + 4; y++) for (int x = sx - 4; x <= sx + 4; x++)
            {
                float d2 = (x - sx) * (x - sx) + (y - sy) * (y - sy);
                float v = peak * MathF.Exp(-d2 / 3);
                planes[0][x, y] = v; planes[1][x, y] = v * 0.9f; planes[2][x, y] = v * 0.8f;
            }
        return planes;
    }

    [Fact]
    public void StarsLayerBlendsWithoutRgbChannelsAndNormalisesToWhite()
    {
        var sources = new Dictionary<ChannelKind, ChannelSource> { [ChannelKind.Ha] = Synth.Source(ChannelKind.Ha, width: 128, height: 96), [ChannelKind.OIII] = Synth.Source(ChannelKind.OIII, 0.4f, 2, 128, 96) };
        var s = new CombineSettings(); s.Select(Palettes.Palette("hoo")!);
        var ctx = RenderContext.Make(s.Normalization, sources);
        var planes = sources.ToDictionary(kv => kv.Key, kv => kv.Value.Image);
        var plain = Renderer.Render(new RenderRequest(planes, s, ctx));
        var layer = new StarsLayer(StarsPlanes(), "stars.tif");
        s.Stars.Enabled = true; s.Stars.UseMask = false; s.Stars.Stretch = StarsStretchMode.None; s.Stars.Saturation = 1; s.Stars.Mode = StarsBlendMode.Screen;
        // No R, G, B channels: the stars come from the layer alone.
        var req = new RenderRequest(planes, s, ctx) { Stars = layer.Input(null) };
        var blended = Renderer.Render(req);
        Assert.True(blended.Image.R[64, 48] > plain.Image.R[64, 48] + 0.3f, "the star shows in the result");
        A.Close(plain.Image.R[5, 5], blended.Image.R[5, 5], 1e-4f, "the black sky of the layer leaves the result alone (screen)");
        // Brightest stars to white: the star peak (0.6 in the layer) reaches 1.
        s.Stars.Normalize = true;
        var white = Renderer.Render(new RenderRequest(planes, s, ctx) { Stars = layer.Input(null) });
        A.Close(1, white.Image.R[64, 48], 0.02f);
        // Brightness −1 EV halves the layer; contrast +1 darkens the faint skirt and keeps the core.
        s.Stars.Normalize = false; s.Stars.Brightness = -1;
        var dim = Renderer.Render(new RenderRequest(planes, s, ctx) { Stars = layer.Input(null) });
        Assert.True(dim.Image.R[64, 48] < blended.Image.R[64, 48]);
        s.Stars.Brightness = 0; s.Stars.Contrast = 1;
        var crisp = Renderer.Render(new RenderRequest(planes, s, ctx) { Stars = layer.Input(null) });
        Assert.True(crisp.Image.R[67, 48] - plain.Image.R[67, 48] < blended.Image.R[67, 48] - plain.Image.R[67, 48], "the skirt is darker");
        Assert.True(crisp.Image.R[64, 48] >= blended.Image.R[64, 48] - 0.05f, "the core is kept");
        foreach (var mode in StarsBlendModes.All)
        {
            s.Stars.Contrast = 0; s.Stars.Mode = mode;
            var r = Renderer.Render(new RenderRequest(planes, s, ctx) { Stars = layer.Input(null) });
            Assert.True(r.Image.R[64, 48] > plain.Image.R[64, 48]);
        }
        // The strip renderer blends the layer region by region like the whole render.
        s.Stars.Mode = StarsBlendMode.Screen;
        var whole = Renderer.Render(new RenderRequest(planes, s, ctx) { Stars = layer.Input(null) });
        var strips = Renderer.RenderFull(sources, s, ctx, whole.PostStretch, stripPixels: 128 * 20, stars: layer)!;
        for (int c = 0; c < 3; c++) Assert.True(whole.Image.Planes[c].MaxAbsDifference(strips.Planes[c]) < 1e-4f);
        // Rotation keeps the layer with the channels.
        var rotated = layer.Applying(OrientationOp.RotateClockwise);
        Assert.Equal(96, rotated.Width); Assert.Equal(128, rotated.Height);
        // Decoding defaults for pre-1.8 settings.
        var old = StarsBlendSettings.FromJson(System.Text.Json.Nodes.JsonNode.Parse("{\"enabled\":true}")!.AsObject());
        Assert.Equal(0, old.Brightness); Assert.Equal(0, old.Contrast); Assert.False(old.Normalize); Assert.Equal(StarsBlendMode.Screen, old.Mode);
    }

    [Fact]
    public void FormulaLinesReadLikePixelMath()
    {
        var s = new CombineSettings(); s.Select(Palettes.Palette("sho")!);
        var available = new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII };
        var lines = s.FormulaLines(available);
        Assert.Equal("R: SII", lines[0]); Assert.Equal("G: Ha", lines[1]); Assert.Equal("B: OIII", lines[2]);
        s.Matrix.Set(ChannelKind.Ha, 0, 0.5f); s.Matrix.Set(ChannelKind.SII, 0, 0.5f);
        Assert.Equal("R: 0.5*Ha + 0.5*SII", s.FormulaLines(available)[0]);
        s.Mode = CombineMode.PixelMath; s.PixelMath = new PixelMathProgram("Ha*0.7 + SII*0.3", "OIII", "OIII");
        Assert.Equal("R: Ha*0.7 + SII*0.3", s.FormulaLines(available)[0]);
        s.Mode = CombineMode.Monochrome; s.MonoChannels = [ChannelKind.Ha, ChannelKind.OIII];
        Assert.StartsWith("Mono: (", s.FormulaLines(available)[0]);
        Assert.Equal("0.25", CombineSettings.FormatWeight(0.25f)); Assert.Equal("1", CombineSettings.FormatWeight(1));
    }
}
