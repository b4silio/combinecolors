using System.Windows;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Tests;

/// <summary>
/// Background / Stars tabs: two sets of channels with their own settings, the Stars tab rendered
/// and blended by the Combine stars block of the Background tab.
/// </summary>
public class LayerTests : IDisposable
{
    readonly ModelFixture f = new();
    public void Dispose() => f.Dispose();

    string[] StarChannels() =>
    [
        f.MakeChannel("s-Ha.fits", ChannelKind.Ha, 0.5f, 11),
        f.MakeChannel("s-OIII.fits", ChannelKind.OIII, 0.3f, 12),
        f.MakeChannel("s-SII.fits", ChannelKind.SII, 0.2f, 13),
    ];

    ProjectModel TwoTabModel()
    {
        var m = f.LoadedModel();
        m.UpdateSettings(s => s.Stretch.TargetBackground = 0.3f);
        m.SwitchLayer(ImageLayer.Stars);
        Assert.Equal(ImageLayer.Stars, m.ActiveLayer);
        Assert.Empty(m.Sources);
        m.LoadFiles(StarChannels());
        Assert.True(f.Wait(() => m.Sources.Count == 3 && !m.IsLoading && m.Context != null), "stars did not load");
        return m;
    }

    static HashSet<string> Names(IReadOnlyDictionary<ChannelKind, ChannelSource> s) => s.Values.Select(v => v.FileName).ToHashSet();

    [Fact]
    public void StarsTabKeepsItsOwnChannelsAndSettings()
    {
        var m = TwoTabModel();
        Assert.Equal(new HashSet<string> { "s-Ha.fits", "s-OIII.fits", "s-SII.fits" }, Names(m.Sources));
        Assert.Equal(NormalizationMethod.None, m.Settings.Normalization.Method);
        Assert.Equal(ProjectModel.StarsTargetBackground, m.Settings.Stretch.TargetBackground);
        Assert.False(m.Settings.Luminance.Enabled);
        m.UpdateSettings(s => s.Post.Saturation = 1.7f);
        m.Viewport = ModelFixture.FullViewport;
        Assert.True(f.Wait(() => m.Frame != null));

        m.SwitchLayer(ImageLayer.Background);
        Assert.True(f.Wait(() => m.Context != null && m.Frame != null));
        Assert.Equal(new HashSet<string> { "x-Ha.fits", "x-OIII.fits", "x-SII.fits" }, Names(m.Sources));
        Assert.Equal(0.3f, m.Settings.Stretch.TargetBackground);
        Assert.Equal(1f, m.Settings.Post.Saturation);
        Assert.Equal(3, m.LayerSources(ImageLayer.Stars).Count);
        Assert.False(m.Settings.Stars.UseMask);

        m.SwitchLayer(ImageLayer.Stars);
        Assert.Equal(1.7f, m.Settings.Post.Saturation);
        // Not while an editor is open.
        m.BeginDustEditing(ChannelKind.Ha);
        Assert.False(m.CanSwitchLayer);
        m.SwitchLayer(ImageLayer.Background);
        Assert.Equal(ImageLayer.Stars, m.ActiveLayer);
        m.CancelDustEditing();
    }

    [Fact]
    public void CombineBlendsTheRenderedStarsTab()
    {
        var m = f.LoadedModel();
        Assert.False(m.HasStars);
        m.UpdateSettings(s => s.Stars.Enabled = true);
        m.Viewport = ModelFixture.FullViewport;
        Assert.True(f.Wait(() => m.Frame != null && !m.IsRendering));
        Assert.Null(m.BlendStars);
        Assert.False(m.Frame!.Settings.Stars.Enabled, "no R, G, B fallback");

        m.SwitchLayer(ImageLayer.Stars);
        m.LoadFiles(StarChannels());
        Assert.True(f.Wait(() => m.Sources.Count == 3 && !m.IsLoading && m.Context != null));
        m.SwitchLayer(ImageLayer.Background);
        Assert.True(f.Wait(() => m.StarsLayer != null && !m.StarsCompositeBusy));
        var layer = m.StarsLayer!;
        Assert.True(layer.Processed);
        Assert.Equal(200, layer.Width);
        Assert.True(f.Wait(() => m.Frame?.Settings.Stars.Enabled == true));

        // A change on the Stars tab is picked up when coming back.
        m.SwitchLayer(ImageLayer.Stars);
        m.UpdateSettings(s => s.Stretch.TargetBackground = 0.3f);
        m.SwitchLayer(ImageLayer.Background);
        Assert.True(f.Wait(() => !ReferenceEquals(m.StarsLayer, layer) && m.StarsLayer != null && !m.StarsCompositeBusy));

        // Emptying the Stars tab turns the combination off.
        m.RemoveStarsLayer();
        Assert.False(m.HasStars);
        Assert.False(m.Settings.Stars.Enabled);
        Assert.Null(m.StarsLayer);
    }

    [Fact]
    public void ProjectKeepsTheStarsTab()
    {
        var m = TwoTabModel();
        m.UpdateSettings(s => s.Post.Saturation = 1.4f);
        m.SwitchLayer(ImageLayer.Background);
        m.UpdateSettings(s => s.Stars.Enabled = true);
        var path = Path.Combine(f.Dir, "p.json");
        m.SaveProject(path);
        var doc = ProjectDocument.Load(path);
        Assert.Equal(3, doc.Channels.Count);
        Assert.Equal(3, doc.StarsLayer?.Channels.Count);
        Assert.Equal(1.4f, doc.StarsLayer!.Settings.Post.Saturation);
        Assert.Null(doc.StarsFile);

        var m2 = f.NewModel();
        m2.OpenProject(path);
        Assert.True(f.Wait(() => m2.Sources.Count == 3 && m2.LayerSources(ImageLayer.Stars).Count == 3 && !m2.IsLoading && m2.StarsLayer != null, 30));
        Assert.Equal(ImageLayer.Background, m2.ActiveLayer);
        Assert.Equal(new HashSet<string> { "s-Ha.fits", "s-OIII.fits", "s-SII.fits" }, Names(m2.LayerSources(ImageLayer.Stars)));
        m2.SwitchLayer(ImageLayer.Stars);
        Assert.Equal(1.4f, m2.Settings.Post.Saturation);
        m2.RemoveAll();
        Assert.Equal(ImageLayer.Background, m2.ActiveLayer);
        Assert.Empty(m2.LayerSources(ImageLayer.Stars));
    }

    [Fact]
    public void OldProjectStarsFileBecomesTheStarsTab()
    {
        var ha = f.MakeChannel("x-Ha.fits", ChannelKind.Ha, 1, 1);
        var stars = Path.Combine(f.Dir, "rgb-stars.fits");
        FitsWriter.Write([new PlanarImage(200, 150, 0.2f), new PlanarImage(200, 150, 0.3f), new PlanarImage(200, 150, 0.4f)], stars);
        var doc = new ProjectDocument();
        doc.Channels.Add(new ProjectDocument.ChannelRef(ChannelKind.Ha, ha));
        doc.StarsFile = stars;
        doc.Settings.Stars.Enabled = true;
        var path = Path.Combine(f.Dir, "old.json");
        doc.Save(path);
        var m = f.NewModel();
        m.OpenProject(path);
        Assert.True(f.Wait(() => m.LayerSources(ImageLayer.Stars).Count == 3 && !m.IsLoading && m.StarsLayer != null, 30));
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.Red, ChannelKind.Green, ChannelKind.Blue }, m.LayerSources(ImageLayer.Stars).Keys.ToHashSet());
        Assert.Equal(StretchMethod.None, m.LayerStateOf(ImageLayer.Stars).Settings.Stretch.Method);
        Assert.Equal(ImageLayer.Background, m.ActiveLayer);
    }

    [Fact]
    public void RotationTurnsBothTabs()
    {
        var m = TwoTabModel();
        m.SwitchLayer(ImageLayer.Background);
        m.RotateClockwise();
        Assert.True(f.Wait(() => !m.IsLoading && m.ImageSize == new Size(150, 200)));
        Assert.Equal(150, m.LayerSources(ImageLayer.Stars).Values.First().Width);
        Assert.Equal(200, m.LayerSources(ImageLayer.Stars).Values.First().Height);
        m.SwitchLayer(ImageLayer.Stars);
        Assert.True(f.Wait(() => m.Context != null && !m.IsLoading));
        Assert.Equal(new Size(150, 200), m.ImageSize);
    }

    [Fact]
    public void StarsTabRefusesAnotherSize()
    {
        var m = f.LoadedModel();
        m.SwitchLayer(ImageLayer.Stars);
        m.LoadFiles([f.MakeChannel("small-Ha.fits", ChannelKind.Ha, 1, 5, 100, 80)]);
        Assert.True(f.Wait(() => m.ErrorMessage != null && !m.IsLoading));
        Assert.Empty(m.Sources);
    }

    [Fact]
    public void StarsTabShowsTheCombinationAndKeepsTheView()
    {
        var m = TwoTabModel();
        var view = new Viewport(new Rect(40, 30, 80, 60), 2);
        m.Viewport = view;
        Assert.True(f.Wait(() => m.Frame != null && !m.IsRendering));
        static float Mean(PreviewFrame fr) => fr.Rgb.Planes.Sum(p => p.Mean()) / 3;
        float alone = Mean(m.Frame!);
        int fit = m.FitRequest;
        Assert.True(m.HasStars);
        m.UpdateCombineStars(s => s.Enabled = true);
        Assert.True(m.LayerStateOf(ImageLayer.Background).Settings.Stars.Enabled, "the setting belongs to the Background tab");
        Assert.True(f.Wait(() => m.Frame != null && Mean(m.Frame) > alone + 0.02f, 30), "the background is under the stars");
        m.UpdateCombineStars(s => s.Enabled = false);
        Assert.True(f.Wait(() => m.Frame != null && Math.Abs(Mean(m.Frame) - alone) < 1e-4f));
        // Switching tabs keeps zoom and pan.
        m.SwitchLayer(ImageLayer.Background);
        Assert.Equal(fit, m.FitRequest);
        Assert.Equal(view, m.Viewport);
    }

    [Fact]
    public void CloseImageResetAllAndDefault()
    {
        var m = TwoTabModel();
        m.SwitchLayer(ImageLayer.Background);
        m.UpdateSettings(s => { s.Post.Saturation = 1.8f; s.Tone.Contrast = 0.4f; });
        var palette = m.Settings.PaletteID;
        m.SavePreset("p");

        m.ResetAllProcessing();
        Assert.Equal(StretchMethod.None, m.Settings.Stretch.Method);
        Assert.Equal(NormalizationMethod.None, m.Settings.Normalization.Method);
        Assert.False(m.Settings.Luminance.Enabled);
        Assert.True(m.Settings.Tone.IsNeutral);
        Assert.Equal(1f, m.Settings.Post.Saturation);
        Assert.Equal(palette, m.Settings.PaletteID);
        Assert.Single(m.Presets);

        m.ApplyDefaultSettings();
        Assert.Equal(StretchMethod.AutoSTF, m.Settings.Stretch.Method);
        Assert.Equal(m.DefaultSettings(), m.Settings);
        m.SwitchLayer(ImageLayer.Stars);
        m.ApplyDefaultSettings();
        Assert.Equal(NormalizationMethod.None, m.Settings.Normalization.Method);
        m.SwitchLayer(ImageLayer.Background);

        // Crop, rotation and flips are locked while an editor is open.
        m.BeginDustEditing(ChannelKind.Ha);
        m.BeginCropEditing();
        Assert.False(m.CropEditing);
        m.RotateClockwise();
        Assert.False(m.IsLoading);
        Assert.Equal(new Size(200, 150), m.ImageSize);
        m.CancelDustEditing();

        m.SaveProject(Path.Combine(f.Dir, "x.json"));
        m.CloseImage();
        Assert.Empty(m.Sources);
        Assert.False(m.HasStars);
        Assert.Equal(ImageLayer.Background, m.ActiveLayer);
        Assert.Empty(m.Presets);
        Assert.Null(m.ProjectPath);
        Assert.Equal(new CombineSettings(), m.Settings);
        Assert.Null(m.Frame);
    }
}
