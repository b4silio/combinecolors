using System.Windows;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Tests;

/// <summary>Drives the app model headlessly: every asynchronous flow comes back through a pumped dispatcher.</summary>
public class ModelFixture : IDisposable
{
    public readonly string Dir;
    public readonly string PrefsPath;
    public readonly ManualDispatcher Ui = new();
    public readonly Preferences Prefs;

    public ModelFixture()
    {
        Dir = Path.Combine(Path.GetTempPath(), "CombineColorsModelTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        // Isolated preferences: the models under test must never touch the user's recent sets.
        PrefsPath = Path.Combine(Dir, "prefs.json");
        Prefs = new Preferences(PrefsPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch { }
    }

    public ProjectModel NewModel() => new(Ui, Prefs);

    public string MakeChannel(string name, ChannelKind kind, float scale, ulong seed, int width = 200, int height = 150)
    {
        var img = new PlanarImage(width, height);
        ulong state = seed;
        float Rnd() { state = unchecked(state * 6364136223846793005UL + 1442695040888963407UL); return ((state >> 33) & 0xFFFF) / 65535f; }
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            float dx = x - width / 2, dy = y - height / 2;
            img[x, y] = scale * (0.01f + 0.02f * MathF.Exp(-(dx * dx + dy * dy) / 800) + 0.001f * (Rnd() - 0.5f));
        }
        var path = Path.Combine(Dir, name);
        FitsWriter.Write([img], path, FitsWriter.Depth.Float32, [("FILTER", kind.RawValue(), null)]);
        return path;
    }

    /// <summary>Pumps the dispatcher until the condition holds or the timeout elapses.</summary>
    public bool Wait(Func<bool> condition, double timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            if (!Ui.Pump()) Thread.Sleep(5);
        }
        Ui.Pump();
        return condition();
    }

    public ProjectModel LoadedModel()
    {
        var m = NewModel();
        var paths = new[]
        {
            MakeChannel("x-Ha.fits", ChannelKind.Ha, 1, 1),
            MakeChannel("x-OIII.fits", ChannelKind.OIII, 0.4f, 2),
            MakeChannel("x-SII.fits", ChannelKind.SII, 0.3f, 3),
        };
        m.LoadFiles(paths);
        Assert.True(Wait(() => m.Sources.Count == 3 && !m.IsLoading && m.Context != null), "model did not load");
        return m;
    }

    public static readonly Viewport FullViewport = new(new Rect(0, 0, 200, 150), 1);
}

public class ProjectModelTests : IDisposable
{
    readonly ModelFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public void LoadDetectsChannelsAndRenders()
    {
        var m = f.LoadedModel();
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII }, m.Sources.Keys.ToHashSet());
        Assert.Equal(new Size(200, 150), m.ImageSize);
        Assert.Equal(3, m.SlotThumbnails.Count);
        m.Viewport = ModelFixture.FullViewport;
        Assert.True(f.Wait(() => m.Frame != null));
        Assert.Equal(0, m.Frame!.Level);
        Assert.Equal(200, m.Frame.Rgb.Width);
        Assert.Null(m.RenderError);
        Assert.True(f.Wait(() => m.Thumbnails.Count >= Palettes.Available(m.AvailableKinds).Count, 30));
        Assert.Equal(3, m.Histogram.Count);
    }

    [Fact]
    public void MismatchedSizeIsRejected()
    {
        var m = f.LoadedModel();
        var bad = f.MakeChannel("y-Hb.fits", ChannelKind.Hb, 1, 9, 100, 80);
        m.LoadFiles([bad]);
        Assert.True(f.Wait(() => m.ErrorMessage != null));
        Assert.Contains("does not match", m.ErrorMessage);
        Assert.Equal(3, m.Sources.Count);
    }

    [Fact]
    public void UnknownFileGoesToPending()
    {
        var m = f.LoadedModel();
        var img = new PlanarImage(200, 150, 0.02f);
        var path = Path.Combine(f.Dir, "mystery.fits");
        FitsWriter.Write([img], path);
        m.LoadFiles([path]);
        Assert.True(f.Wait(() => m.PendingFiles.Count > 0));
        Assert.Single(m.PendingFiles);
        m.AssignPending(m.PendingFiles[0], ChannelKind.Luminance);
        Assert.True(f.Wait(() => m.Sources.ContainsKey(ChannelKind.Luminance) && !m.IsLoading));
        Assert.Empty(m.PendingFiles);
    }

    [Fact]
    public void ColorFileAsRgb()
    {
        var m = f.NewModel();
        var p = new PlanarImage(50, 40, 0.3f);
        var path = Path.Combine(f.Dir, "stars.fits");
        FitsWriter.Write([p, p, p], path);
        m.LoadFiles([path]);
        Assert.True(f.Wait(() => m.PendingColorFile != null));
        m.AssignColorFileAsRGB(m.PendingColorFile!);
        Assert.True(f.Wait(() => m.Sources.Count == 3 && !m.IsLoading));
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.Red, ChannelKind.Green, ChannelKind.Blue }, m.Sources.Keys.ToHashSet());
    }

    [Fact]
    public void ReassignAndRemove()
    {
        var m = f.LoadedModel();
        m.Reassign(ChannelKind.SII, ChannelKind.Hb);
        Assert.False(m.Sources.ContainsKey(ChannelKind.SII)); Assert.Equal(ChannelKind.Hb, m.Sources[ChannelKind.Hb].Kind);
        m.Reassign(ChannelKind.Hb, ChannelKind.Ha);   // swap
        Assert.Equal("x-SII.fits", m.Sources[ChannelKind.Ha].FileName);
        Assert.Equal("x-Ha.fits", m.Sources[ChannelKind.Hb].FileName);
        m.RemoveChannel(ChannelKind.Hb);
        Assert.False(m.Sources.ContainsKey(ChannelKind.Hb));
        Assert.Equal(2, m.Sources.Count);
        m.RemoveAll();
        Assert.Empty(m.Sources);
        Assert.Null(m.Frame);
    }

    [Fact]
    public void PaletteFallbackWhenChannelsMissing()
    {
        var m = f.LoadedModel();
        m.Select(Palettes.Palette("sho")!);
        m.RemoveChannel(ChannelKind.SII);
        // SHO needs SII: the model must switch to a palette it can render.
        Assert.Empty(Renderer.MissingChannels(m.Settings, m.AvailableKinds));
        Assert.NotEqual("sho", m.Settings.PaletteID);
    }

    [Fact]
    public void CropFlow()
    {
        var m = f.LoadedModel();
        m.BeginCropEditing();
        Assert.True(m.CropEditing);
        Assert.NotNull(m.CropDraft);
        // Cancel without a crop keeps a draft (the toolbar fields may still be bound to it).
        m.CancelCropEditing();
        Assert.False(m.CropEditing); Assert.Null(m.Crop); Assert.NotNull(m.CropDraft);
        m.BeginCropEditing();
        m.CropDraft = new PixelRect(10, 20, 100, 80);
        m.CommitCropEditing();
        Assert.False(m.CropEditing);
        Assert.Equal(new PixelRect(10, 20, 100, 80), m.Crop);
        Assert.True(f.Wait(() => m.Sources[ChannelKind.Ha].Region == m.Crop && !m.IsLoading && m.Context != null));
        Assert.Equal(m.Crop, m.Sources[ChannelKind.Ha].Region);
        Assert.Same(m.BaseSources[ChannelKind.Ha].Pyramid, m.Sources[ChannelKind.Ha].Pyramid);
        // Slot thumbnails follow the crop (5:4 instead of 4:3).
        foreach (var k in new[] { ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII })
        {
            Assert.Equal(100, m.SlotThumbnails[k].PixelWidth); Assert.Equal(80, m.SlotThumbnails[k].PixelHeight);
        }
        // Full crop clears it.
        m.CropDraft = new PixelRect(0, 0, 200, 150);
        m.CropEditing = true;
        m.CommitCropEditing();
        Assert.Null(m.Crop);
        Assert.True(f.Wait(() => m.Sources[ChannelKind.Ha].Region == null && !m.IsLoading));
        Assert.Equal(200, m.SlotThumbnails[ChannelKind.Ha].PixelWidth);
        m.CancelCropEditing();
        m.ClearCrop();
        Assert.Null(m.Crop);
    }

    [Fact]
    public void AutoCropOnPaddedData()
    {
        var m = f.NewModel();
        var img = new PlanarImage(120, 100, 0.02f);
        for (int y = 0; y < 100; y++) for (int x = 0; x < 15; x++) img[x, y] = 0;
        var path = Path.Combine(f.Dir, "padded-Ha.fits");
        FitsWriter.Write([img], path);
        m.LoadFiles([path]);
        Assert.True(f.Wait(() => m.Sources.Count == 1 && !m.IsLoading));
        m.AutoCrop();
        Assert.True(f.Wait(() => m.Crop != null));
        Assert.Equal(new PixelRect(15, 0, 105, 100), m.Crop);
    }

    [Fact]
    public void ProjectRoundTrip()
    {
        var m = f.LoadedModel();
        m.Select(Palettes.Palette("foraxx_sho")!);
        m.UpdateSettings(s => s.Post.Saturation = 1.7f);
        m.Crop = new PixelRect(5, 5, 150, 100);
        var path = Path.Combine(f.Dir, "project.json");
        m.SaveProject(path);
        Assert.Equal(path, m.ProjectPath);
        var m2 = f.NewModel();
        m2.OpenProject(path);
        Assert.True(f.Wait(() => m2.Sources.Count == 3 && !m2.IsLoading && m2.Context != null));
        Assert.Equal(m.Settings, m2.Settings);
        Assert.Equal(m.Crop, m2.Crop);
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII, ChannelKind.SII }, m2.Sources.Keys.ToHashSet());
        Assert.Equal(m.Crop, m2.Sources[ChannelKind.Ha].Region);
    }

    [Fact]
    public void ExportWritesFile()
    {
        var m = f.LoadedModel();
        m.Crop = new PixelRect(0, 0, 100, 75);
        Assert.True(f.Wait(() => m.Sources[ChannelKind.Ha].Region != null && m.Context != null && !m.IsLoading));
        foreach (var (fmt, ext) in new[] { (RasterFormat.Tiff16, "tif"), (RasterFormat.Fits32, "fits"), (RasterFormat.Png8, "png") })
        {
            var path = Path.Combine(f.Dir, "out." + ext);
            bool done = false; Exception? err = null;
            m.Export(path, fmt, fmt == RasterFormat.Fits32, e => { err = e; done = true; });
            Assert.True(f.Wait(() => done, 30));
            Assert.Null(err);
            var back = ImageLoader.Load(path);
            Assert.Equal(100, back.Width); Assert.Equal(75, back.Height); Assert.Equal(3, back.Planes.Count);
        }
        Assert.Null(m.ExportProgress);
    }

    [Fact]
    public void RotateClockwise()
    {
        var m = f.LoadedModel();
        var haBefore = m.Sources[ChannelKind.Ha].Image;
        m.CropDraft = new PixelRect(10, 20, 100, 80); m.CropEditing = true; m.CommitCropEditing();
        Assert.True(f.Wait(() => m.Sources[ChannelKind.Ha].Region == m.Crop && !m.IsLoading && m.Context != null));
        m.RotateClockwise();
        Assert.True(f.Wait(() => m.ImageSize == new Size(150, 200) && !m.IsLoading && m.Context != null));
        Assert.Equal(1, m.Rotation);
        Assert.Equal(new Size(150, 200), m.ImageSize);
        // The crop follows the rotation: (x 10, y 20, 100×80) in 200×150 → (x 150−100, y 10, 80×100).
        Assert.Equal(new PixelRect(50, 10, 80, 100), m.Crop);
        Assert.Equal(m.Crop, m.Sources[ChannelKind.Ha].Region);
        // Pixels moved as a clockwise turn: source (x, y) → (H−1−y, x).
        var haAfter = m.Sources[ChannelKind.Ha].Image;
        Assert.Equal(haBefore[10, 20], haAfter[149 - 20, 10]);
        Assert.Equal(80, m.SlotThumbnails[ChannelKind.Ha].PixelWidth); Assert.Equal(100, m.SlotThumbnails[ChannelKind.Ha].PixelHeight);
        // Saved in the project; three more turns bring the original orientation back.
        Assert.Equal(1, m.ProjectDocument().Rotation);
        for (int i = 0; i < 3; i++)
        {
            m.RotateClockwise();
            Assert.True(f.Wait(() => !m.IsLoading && m.Context != null));
        }
        Assert.Equal(0, m.Rotation);
        Assert.Equal(new Size(200, 150), m.ImageSize);
        Assert.Equal(new PixelRect(10, 20, 100, 80), m.Crop);
        Assert.Equal(haBefore[10, 20], m.Sources[ChannelKind.Ha].Image[10, 20]);
        // A channel added while rotated is rotated the same way.
        m.RotateClockwise();
        Assert.True(f.Wait(() => m.Rotation == 1 && !m.IsLoading && m.Context != null));
        var path = f.MakeChannel("x-Hb.fits", ChannelKind.Hb, 0.2f, 4);
        m.LoadFiles([path]);
        Assert.True(f.Wait(() => m.Sources.ContainsKey(ChannelKind.Hb) && !m.IsLoading));
        Assert.Equal(150, m.Sources[ChannelKind.Hb].Width); Assert.Equal(200, m.Sources[ChannelKind.Hb].Height);
    }

    [Fact]
    public void Flips()
    {
        var m = f.LoadedModel();
        var haBefore = m.Sources[ChannelKind.Ha].Image;
        m.CropDraft = new PixelRect(10, 20, 100, 80); m.CropEditing = true; m.CommitCropEditing();
        Assert.True(f.Wait(() => m.Sources[ChannelKind.Ha].Region == m.Crop && !m.IsLoading && m.Context != null));
        m.FlipHorizontal();
        Assert.True(f.Wait(() => m.Orientation.Flipped && !m.IsLoading && m.Context != null));
        Assert.Equal(new Size(200, 150), m.ImageSize);
        Assert.Equal(new PixelRect(200 - 110, 20, 100, 80), m.Crop);
        Assert.Equal(haBefore[10, 20], m.Sources[ChannelKind.Ha].Image[199 - 10, 20]);
        m.FlipVertical();
        Assert.True(f.Wait(() => m.Orientation == new Orientation(2, false) && !m.IsLoading && m.Context != null));
        Assert.Equal(new PixelRect(90, 150 - 100, 100, 80), m.Crop);
        Assert.Equal(haBefore[10, 20], m.Sources[ChannelKind.Ha].Image[189, 129]);
        Assert.Equal(2, m.ProjectDocument().Rotation); Assert.False(m.ProjectDocument().Flipped);
        // A channel added now gets the same 180° turn.
        var path = f.MakeChannel("x-Hb.fits", ChannelKind.Hb, 0.2f, 4);
        m.LoadFiles([path]);
        Assert.True(f.Wait(() => m.Sources.ContainsKey(ChannelKind.Hb) && !m.IsLoading));
        var raw = ImageLoader.Load(path).Planes[0];
        Assert.Equal(raw[10, 20], m.Sources[ChannelKind.Hb].Image[189, 129]);
    }

    [Fact]
    public void PresetsAndUndo()
    {
        var m = f.LoadedModel();
        m.Viewport = ModelFixture.FullViewport;
        Assert.True(f.Wait(() => m.Frame != null));
        var original = m.Settings;
        var a = m.SavePreset();
        Assert.Equal("Preset 1", a.Name);
        Assert.NotNull(a.ThumbnailPng);
        Assert.NotNull(m.PresetThumbnail(a));
        Assert.Equal(a.Id, m.ActivePresetId);
        m.UpdateSettings(s => s.Post.HueShift = 30);
        Assert.True(m.CanUndo);
        Assert.Null(m.ActivePresetId);
        var b = m.SavePreset("Warm");
        Assert.Equal(2, m.Presets.Count);
        m.ApplyPreset(a);
        Assert.Equal(original, m.Settings);
        m.ApplyPreset(b);
        Assert.Equal(30, m.Settings.Post.HueShift);
        m.RenamePreset(b, "Warmer");
        Assert.Equal("Warmer", m.Presets[1].Name);
        m.UpdateSettings(s => s.Post.Saturation = 2);
        m.UpdatePreset(b);
        Assert.Equal(2, m.Presets[1].Settings.Post.Saturation);
        // Presets travel with the project.
        var path = Path.Combine(f.Dir, "presets.json");
        m.SaveProject(path);
        var m2 = f.NewModel();
        m2.OpenProject(path);
        Assert.True(f.Wait(() => m2.Presets.Count == 2));
        Assert.Equal(new[] { "Preset 1", "Warmer" }, m2.Presets.Select(p => p.Name).ToArray());
        Assert.NotNull(m2.PresetThumbnail(m2.Presets[0]));
        m.DeletePreset(a);
        Assert.Single(m.Presets);
        m.Undo();
        Assert.NotEqual(2, m.Settings.Post.Saturation);
        m.Redo();
        Assert.Equal(2, m.Settings.Post.Saturation);
    }

    [Fact]
    public void UndoCoalescesSliderDrags()
    {
        var m = f.LoadedModel();
        for (int i = 1; i <= 5; i++) m.UpdateSettings(s => s.Post.Saturation = 1 + 0.1f * i);
        m.Undo();   // one step covers the whole drag (within the coalescing window)
        Assert.Equal(1, m.Settings.Post.Saturation);
        Assert.False(m.CanUndo);
        Assert.True(m.CanRedo);
    }

    [Fact]
    public void HoverReadout()
    {
        var m = f.LoadedModel();
        m.Viewport = ModelFixture.FullViewport;
        Assert.True(f.Wait(() => m.Frame != null));
        m.UpdateHover(new Point(100, 75));
        Assert.Equal(3, m.Hover?.Values.Count);
        Assert.NotNull(m.Hover?.Rgb);
        m.UpdateHover(new Point(-1, 0));
        Assert.Null(m.Hover);
    }

    [Fact]
    public void DualBandAddsChannels()
    {
        var m = f.NewModel();
        var r = new PlanarImage(60, 40, 0.5f); var g = new PlanarImage(60, 40, 0.2f); var b = new PlanarImage(60, 40, 0.3f);
        var (ha, o3) = DualBand.Split(r, g, b, new DualBandSettings());
        m.AddSplitChannels((ChannelKind.Ha, ha), (ChannelKind.OIII, o3), "osc.fits");
        Assert.True(f.Wait(() => m.Sources.Count == 2 && !m.IsLoading));
        Assert.Equal(0.5f, m.Sources[ChannelKind.Ha].Stats.Median);
        Assert.Equal(0.25f, m.Sources[ChannelKind.OIII].Stats.Median, 1e-6f);
    }

    [Fact]
    public void DustSpotEditing()
    {
        var m = f.LoadedModel();
        var original = m.Sources[ChannelKind.Ha].Image;
        Assert.Same(original, m.OriginalImages[ChannelKind.Ha]);
        // Live preview while editing, dropped on cancel.
        m.BeginDustEditing(ChannelKind.Ha);
        Assert.Equal(ChannelKind.Ha, m.DustEditing); Assert.NotNull(m.DustDraft);
        Assert.Equal(CombineMode.Monochrome, m.DisplayedSettings.Mode);
        m.AddDustSpot(new Point(60, 70));
        Assert.Single(m.DustDraft!.SpotsFor(ChannelKind.Ha));
        Assert.NotNull(m.SelectedDustSpot);
        Assert.True(f.Wait(() => !ReferenceEquals(m.Sources[ChannelKind.Ha].Image, original)));
        m.CancelDustEditing();
        Assert.Null(m.DustEditing); Assert.Null(m.DustDraft); Assert.True(m.Dust.IsEmpty);
        Assert.True(f.Wait(() => ReferenceEquals(m.Sources[ChannelKind.Ha].Image, original) && !m.IsLoading));
        // Commit: the spot is healed on every channel it was copied to; pixels far away are untouched.
        m.BeginDustEditing(ChannelKind.Ha);
        m.AddDustSpot(new Point(60, 70));
        m.CopyDustSpotsToAllChannels();
        Assert.Single(m.DustDraft!.SpotsFor(ChannelKind.OIII));
        Assert.Single(m.DustDraft!.SpotsFor(ChannelKind.SII));
        var spot = m.DustDraft!.SpotsFor(ChannelKind.Ha)[0];
        m.CommitDustEditing();
        Assert.Null(m.DustEditing);
        Assert.True(f.Wait(() => !m.IsLoading && !ReferenceEquals(m.Sources[ChannelKind.Ha].Image, original) && !ReferenceEquals(m.Sources[ChannelKind.OIII].Image, m.OriginalImages[ChannelKind.OIII]) && m.Context != null));
        Assert.Equal(new[] { spot }, m.Dust.SpotsFor(ChannelKind.Ha));
        Assert.Equal(original[195, 145], m.Sources[ChannelKind.Ha].Image[195, 145]);
        Assert.Equal(m.OriginalImages[ChannelKind.OIII][195, 145], m.Sources[ChannelKind.OIII].Image[195, 145]);
        Assert.Equal(m.Dust, m.ProjectDocument().Dust);
        // Save / open keeps the spots and heals on load.
        var path = Path.Combine(f.Dir, "dust.json");
        m.SaveProject(path);
        var m2 = f.NewModel();
        m2.OpenProject(path);
        Assert.True(f.Wait(() => m2.Sources.Count == 3 && !m2.IsLoading && m2.Context != null));
        Assert.Equal(m.Dust, m2.Dust);
        Assert.Equal(0, m2.Sources[ChannelKind.Ha].Image.MaxAbsDifference(m.Sources[ChannelKind.Ha].Image));
        // Moving the channel takes its spots along; rotating maps them; removing drops them.
        m.Reassign(ChannelKind.Ha, ChannelKind.Luminance);
        Assert.Equal(new[] { spot }, m.Dust.SpotsFor(ChannelKind.Luminance)); Assert.Empty(m.Dust.SpotsFor(ChannelKind.Ha));
        Assert.True(m.OriginalImages.ContainsKey(ChannelKind.Luminance)); Assert.False(m.OriginalImages.ContainsKey(ChannelKind.Ha));
        m.RotateClockwise();
        Assert.True(f.Wait(() => !m.IsLoading && m.ImageSize?.Width == 150));
        var r = m.Dust.SpotsFor(ChannelKind.Luminance)[0];
        Assert.Equal(150 - spot.Y, r.X, 1e-9); Assert.Equal(spot.X, r.Y, 1e-9); Assert.Equal(spot.Id, r.Id);
        Assert.Equal(150, m.Sources[ChannelKind.Luminance].Image.Width);
        m.RemoveChannel(ChannelKind.Luminance);
        Assert.Empty(m.Dust.SpotsFor(ChannelKind.Luminance)); Assert.False(m.OriginalImages.ContainsKey(ChannelKind.Luminance));
    }

    /// <summary>
    /// A peek left behind must not override the edited channel, and switching channels re-previews the
    /// one shown (its draft may have changed through Copy to all while another channel was on screen).
    /// </summary>
    [Fact]
    public void DustEditorOverridesStuckPeekAndRepreviewsOnSwitch()
    {
        var m = f.LoadedModel();
        m.PeekChannel = ChannelKind.SII;
        m.BeginDustEditing(ChannelKind.Ha);
        Assert.Null(m.PeekChannel);
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.Ha }, m.DisplayedSettings.MonoChannels);
        m.AddDustSpot(new Point(60, 70));
        m.CopyDustSpotsToAllChannels();
        var oiii = m.Sources[ChannelKind.OIII].Image;
        m.PeekChannel = ChannelKind.SII;
        m.SwitchDustChannel(ChannelKind.OIII);
        Assert.Null(m.PeekChannel);
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.OIII }, m.DisplayedSettings.MonoChannels);
        Assert.True(f.Wait(() => !ReferenceEquals(m.Sources[ChannelKind.OIII].Image, oiii)), "the copied spot is previewed on the channel switched to");
        m.Viewport = ModelFixture.FullViewport;
        Assert.True(f.Wait(() => m.Frame != null));
        System.Windows.Media.Imaging.BitmapSource? copied = null;
        Assert.True(m.CopyPreviewToClipboard(b => copied = b));
        Assert.NotNull(copied);
        var whole = m.PreviewImageForCopy!;
        Assert.Equal(m.Frame!.Bitmap.PixelWidth, whole.PixelWidth);
        m.Crop = new PixelRect(20, 10, 100, 50);
        Assert.True(f.Wait(() => !m.IsLoading && m.Frame != null && m.Sources[ChannelKind.Ha].Region != null));
        var cropped = m.PreviewImageForCopy!;
        double scale = m.Frame!.Bitmap.PixelWidth / m.Frame.Rect.Width;
        Assert.Equal(100 * scale, cropped.PixelWidth, 1.5);
        Assert.Equal(50 * scale, cropped.PixelHeight, 1.5);
        m.CancelDustEditing();
        Assert.True(f.Wait(() => !m.IsLoading && ReferenceEquals(m.Sources[ChannelKind.OIII].Image, m.OriginalImages[ChannelKind.OIII])));
    }
}

public class RecentSetsTests : IDisposable
{
    readonly ModelFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public void RecordAndReopenSets()
    {
        var ha = f.MakeChannel("set-Ha.fits", ChannelKind.Ha, 1, 1);
        var o3 = f.MakeChannel("set-OIII.fits", ChannelKind.OIII, 0.5f, 2);
        var m = f.NewModel();
        Assert.Empty(m.RecentSets);
        m.LoadFiles([ha, o3]);
        Assert.True(f.Wait(() => m.Sources.Count == 2 && !m.IsLoading && m.RecentSets.Count > 0));
        Assert.Single(m.RecentSets);
        Assert.Equal(new[] { ChannelKind.Ha, ChannelKind.OIII }, m.RecentSets[0].Entries.Select(e => e.Kind).ToArray());
        Assert.Equal("set", m.RecentSets[0].Name);
        // Adding a channel updates the same entry (new path set → new entry at front, old one kept).
        var s2 = f.MakeChannel("set-SII.fits", ChannelKind.SII, 0.3f, 3);
        m.LoadFiles([s2]);
        Assert.True(f.Wait(() => m.Sources.Count == 3 && !m.IsLoading && m.RecentSets.Count == 2));
        Assert.Equal(3, m.RecentSets[0].Entries.Count);
        // Settings, crop and presets are auto-saved with the set (debounced) and restored on reopening.
        m.UpdateSettings(s => s.Post.Saturation = 1.7f);
        m.CropDraft = new PixelRect(10, 20, 100, 80); m.CropEditing = true; m.CommitCropEditing();
        m.SavePreset("look");
        Assert.True(f.Wait(() => m.RecentSets[0].State?.Settings.Post.Saturation == 1.7f && m.RecentSets[0].State?.Crop != null && m.RecentSets[0].State?.Presets.Count == 1, 10));
        Assert.Equal(new PixelRect(10, 20, 100, 80), m.RecentSets[0].State!.Crop);
        Assert.Empty(m.RecentSets[0].State!.Channels);
        // Reopening the first set replaces the loaded channels and brings back the state it was saved with.
        var first = m.RecentSets[1];
        m.OpenRecentSet(first);
        Assert.True(f.Wait(() => m.Sources.Count == 2 && !m.IsLoading));
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.Ha, ChannelKind.OIII }, m.Sources.Keys.ToHashSet());
        Assert.True(m.RecentSets[0].Paths.SetEquals(first.Paths), "reopened set moves to the front");
        Assert.Equal(2, m.RecentSets.Count);
        Assert.Equal(1, m.Settings.Post.Saturation);
        Assert.Null(m.Crop);
        // Now reopen the three-channel set: its state comes back.
        var three = m.RecentSets[1];
        Assert.Equal(1.7f, three.State?.Settings.Post.Saturation);
        m.OpenRecentSet(three);
        Assert.True(f.Wait(() => m.Sources.Count == 3 && !m.IsLoading && m.Crop != null));
        Assert.Equal(1.7f, m.Settings.Post.Saturation);
        Assert.Equal(new PixelRect(10, 20, 100, 80), m.Crop);
        Assert.Equal(new[] { "look" }, m.Presets.Select(p => p.Name).ToArray());
        Assert.Equal(three.Id, m.RecentSets[0].Id);
        // Persisted across model instances.
        f.Prefs.Flush();
        var m2 = new ProjectModel(f.Ui, new Preferences(f.PrefsPath));
        Assert.Equal(2, m2.RecentSets.Count);
        // Missing files are reported and the entry dropped.
        File.Delete(ha);
        m2.OpenRecentSet(m2.RecentSets[0]);
        Assert.NotNull(m2.ErrorMessage);
        Assert.Single(m2.RecentSets);
        m2.ClearRecentSets();
        Assert.Empty(m2.RecentSets);
        Assert.Equal("M31", RecentSet.MakeName([new RecentSet.Entry(ChannelKind.Ha, @"C:\a\b\M31_Ha.fits"), new RecentSet.Entry(ChannelKind.OIII, @"C:\a\b\M31_OIII.fits")]));
        Assert.Equal("Nebula", RecentSet.MakeName([new RecentSet.Entry(ChannelKind.Ha, @"C:\x\Nebula\h.fits"), new RecentSet.Entry(ChannelKind.OIII, @"C:\x\Nebula\o.fits")]));
    }
}

public class PaletteVisibilityTests : IDisposable
{
    readonly ModelFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public void HideAndFilterPersist()
    {
        var m = f.NewModel();
        m.LoadFiles([f.MakeChannel("v-Ha.fits", ChannelKind.Ha, 1, 1), f.MakeChannel("v-OIII.fits", ChannelKind.OIII, 0.5f, 2)]);
        Assert.True(f.Wait(() => m.Sources.Count == 2 && !m.IsLoading));
        var all = Palettes.Available(m.AvailableKinds);
        Assert.Equal(all.Count, m.VisiblePalettes.Count);
        m.SetPaletteHidden("hoo", true);
        m.SetPaletteHidden("hho", true);
        Assert.Equal(all.Count - 2, m.VisiblePalettes.Count);
        Assert.DoesNotContain(m.VisiblePalettes, p => p.Id == "hoo");
        m.PaletteFilter = PaletteFilter.All;
        Assert.Equal(all.Count, m.VisiblePalettes.Count);
        Assert.True(m.IsPaletteHidden("hoo"));
        m.SetPaletteHidden("hoo", false);
        Assert.False(m.IsPaletteHidden("hoo"));
        f.Prefs.Flush();
        var m2 = new ProjectModel(f.Ui, new Preferences(f.PrefsPath));
        Assert.Equal(new HashSet<string> { "hho" }, m2.HiddenPaletteIds);
        Assert.Equal(PaletteFilter.All, m2.PaletteFilter);
    }
}

public class PeekTests : IDisposable
{
    readonly ModelFixture f = new();
    public void Dispose() => f.Dispose();

    [Fact]
    public void PeekShowsSingleChannelThenRestores()
    {
        var m = f.LoadedModel();
        m.Select(Palettes.Palette("sho")!);
        m.Viewport = ModelFixture.FullViewport;
        Assert.True(f.Wait(() => m.Frame != null));
        m.PeekChannel = ChannelKind.OIII;
        Assert.Equal(CombineMode.Monochrome, m.DisplayedSettings.Mode);
        Assert.Equal(new HashSet<ChannelKind> { ChannelKind.OIII }, m.DisplayedSettings.MonoChannels);
        Assert.Equal(CombineMode.Matrix, m.Settings.Mode);
        Assert.True(f.Wait(() => m.Frame?.Settings.Mode == CombineMode.Monochrome));
        var fr = m.Frame!;
        Assert.Equal(fr.Rgb.R.ToArray(), fr.Rgb.B.ToArray());
        m.PeekChannel = null;
        Assert.True(f.Wait(() => m.Frame?.Settings.Mode == CombineMode.Matrix));
    }
}

static class TestExtensions
{
    public static float MaxAbsDifference(this PlanarImage a, PlanarImage other)
    {
        float m = 0;
        for (int i = 0; i < a.Count; i++) m = MathF.Max(m, MathF.Abs(a.Data[i] - other.Data[i]));
        return m;
    }
}
