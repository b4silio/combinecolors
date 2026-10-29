namespace AstroCore;

public enum DualBandPreset { HaOiii, SiiHb, Custom }

public static class DualBandPresets
{
    public static readonly DualBandPreset[] All = [DualBandPreset.HaOiii, DualBandPreset.SiiHb, DualBandPreset.Custom];

    public static string DisplayName(this DualBandPreset p) => p switch
    {
        DualBandPreset.HaOiii => "Hα / OIII (L-eXtreme, L-eNhance, ALP-T…)",
        DualBandPreset.SiiHb => "SII / Hβ",
        _ => "Custom",
    };

    public static (ChannelKind first, ChannelKind second) Outputs(this DualBandPreset p) => p switch
    {
        DualBandPreset.SiiHb => (ChannelKind.SII, ChannelKind.Hb),
        _ => (ChannelKind.Ha, ChannelKind.OIII),
    };
}

/// <summary>Splits a one-shot-colour dual-band image (e.g. Hα/OIII filter) into two emission-line planes.</summary>
public sealed partial class DualBandSettings : IEquatable<DualBandSettings>
{
    public DualBandPreset Preset = DualBandPreset.HaOiii;
    /// <summary>Weights of the camera R, G, B channels into the first (red line) output.</summary>
    public float[] FirstWeights = [1, 0, 0];
    /// <summary>Weights into the second (blue/green line) output.</summary>
    public float[] SecondWeights = [0, 0.5f, 0.5f];
    /// <summary>Fraction of the first line subtracted from the second (filter cross-talk).</summary>
    public float CrosstalkSecond = 0;
    /// <summary>Fraction of the second line subtracted from the first.</summary>
    public float CrosstalkFirst = 0;
    /// <summary>Custom output kinds for the Custom preset.</summary>
    public ChannelKind CustomFirst = ChannelKind.Ha;
    public ChannelKind CustomSecond = ChannelKind.OIII;

    public (ChannelKind first, ChannelKind second) OutputKinds => Preset == DualBandPreset.Custom ? (CustomFirst, CustomSecond) : Preset.Outputs();

    public DualBandSettings Clone()
    {
        var c = (DualBandSettings)MemberwiseClone();
        c.FirstWeights = (float[])FirstWeights.Clone(); c.SecondWeights = (float[])SecondWeights.Clone();
        return c;
    }
    public bool Equals(DualBandSettings? o) => o != null && SettingsJson.SameJson(this, o);
    public override bool Equals(object? obj) => Equals(obj as DualBandSettings);
    public override int GetHashCode() => SettingsJson.JsonHash(this);
}

public static class DualBand
{
    /// <summary>Returns (first, second) planes.</summary>
    public static (PlanarImage first, PlanarImage second) Split(PlanarImage r, PlanarImage g, PlanarImage b, DualBandSettings s)
    {
        PlanarImage Mix(float[] w)
        {
            var out_ = PlanarImage.Uninitialized(r.Width, r.Height);
            Vec.Scale(r.Data, w[0], out_.Data);
            Vec.MulAdd(g.Data, w[1], out_.Data);
            Vec.MulAdd(b.Data, w[2], out_.Data);
            return out_;
        }
        var first = Mix(s.FirstWeights); var second = Mix(s.SecondWeights);
        if (s.CrosstalkSecond != 0) Vec.MulAdd(first.Data, -s.CrosstalkSecond, second.Data);
        if (s.CrosstalkFirst != 0) Vec.MulAdd(second.Data, -s.CrosstalkFirst, first.Data);
        first.Clamp(0, float.MaxValue);
        second.Clamp(0, float.MaxValue);
        return (first, second);
    }
}
