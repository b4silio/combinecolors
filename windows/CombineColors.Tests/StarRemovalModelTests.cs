using AstroCore;
using CombineColors.Model;

namespace CombineColors.Tests;

/// <summary>
/// Remove Stars end to end in the app model with the real StarNet2: runs only when the
/// environment variable COMBINECOLORS_STARNET points at a starnet2 executable (skipped otherwise).
/// </summary>
public class StarRemovalModelTests : IDisposable
{
    readonly ModelFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public void RemoveStarsFillsBothTabsAndSwitchesTheCombinationOn()
    {
        if (Environment.GetEnvironmentVariable("COMBINECOLORS_STARNET") is not string exe || !File.Exists(exe)) return;
        var m = f.NewModel();
        var setup = new StarNetSetupModel(f.Ui, new StarNetSetup(StarNetPreferences.InMemory(), Path.Combine(f.Dir, "sn")));
        m.StarNet = setup;
        setup.Choose(exe);
        Assert.True(f.Wait(() => setup.State == StarNetSetupModel.Phase.NeedsLicense || setup.State == StarNetSetupModel.Phase.Ready, 60));
        setup.AcceptLicense();
        Assert.Equal(StarNetSetupModel.Phase.Ready, setup.State);

        m.LoadFiles([f.MakeChannel("r-Ha.fits", ChannelKind.Ha, 1, 1, 640, 520), f.MakeChannel("r-OIII.fits", ChannelKind.OIII, 0.4f, 2, 640, 520)]);
        Assert.True(f.Wait(() => m.Sources.Count == 2 && !m.IsLoading && m.Context != null));
        var items = m.StarRemovalItems();
        Assert.Equal(2, items.Count);
        m.RemoveStars(items, new StarRemovalOptions(StarRemovalMethod.StarnetLinear, m.Settings.Stretch.Clone()));
        Assert.True(m.IsRemovingStars);
        Assert.False(m.CanSwitchLayer);
        Assert.True(f.Wait(() => !m.IsRemovingStars && m.LayerSources(ImageLayer.Stars).Count == 2 && !m.IsLoading && m.StarsLayer != null && !m.StarsCompositeBusy, 150),
            $"removing={m.IsRemovingStars} progress={m.StarRemovalProgress} stars={m.LayerSources(ImageLayer.Stars).Count} loading={m.IsLoading} layer={m.StarsLayer != null} busy={m.StarsCompositeBusy} err={m.ErrorMessage} status={m.StatusText}");
        Assert.All(m.Sources.Values, s => Assert.EndsWith("_bg.fits", s.FileName));
        Assert.All(m.LayerSources(ImageLayer.Stars).Values, s => Assert.EndsWith("_stars.fits", s.FileName));
        Assert.Equal(ImageLayer.Background, m.ActiveLayer);
        Assert.True(m.Settings.Stars.Enabled);
        Assert.False(m.Settings.Stars.UseMask);
        Assert.True(File.Exists(Path.Combine(f.Dir, "r-Ha_bg.fits")));
    }
}
