using System.IO;
using Avalonia.Media.Imaging;
using AstroCore;

namespace CombineColors.Model;

/// <summary>
/// The two sets of channels of a project: the background (starless) image and its stars, each
/// with its own settings; the Combine stars block of the Background tab blends the Stars tab in.
/// </summary>
public enum ImageLayer { Background, Stars }

public static class ImageLayers
{
    public static readonly ImageLayer[] All = [ImageLayer.Background, ImageLayer.Stars];
    public static string DisplayName(this ImageLayer l) => l == ImageLayer.Background ? "Background" : "Stars";
}

/// <summary>Everything that belongs to one tab (the shown tab keeps it in the model's live properties).</summary>
public sealed class LayerState
{
    public Dictionary<ChannelKind, ChannelSource> BaseSources = new();
    public Dictionary<ChannelKind, ChannelSource> Sources = new();
    public Dictionary<ChannelKind, Bitmap> SlotThumbnails = new();
    public Dictionary<ChannelKind, PlanarImage> OriginalImages = new();
    public CombineSettings Settings = new();
    public DustSettings Dust = new();
    public GradientSettings Gradient = new();
    public CorrectionSettings Corrections = new();
    public Dictionary<ChannelKind, CorrectionMap> CorrectionMaps = new();
    public List<SettingsPreset> Presets = new();
    public Dictionary<Guid, Bitmap> PresetThumbnailCache = new();
    public RenderContext? Context;
    public Dictionary<ChannelKind, PlanarImage> AnalysisSamples = new();
    public Dictionary<string, List<ResolvedStretch>> PostStretchCache = new();
    public Dictionary<string, Bitmap> Thumbnails = new();
    public PreviewFrame? Frame;
    public List<int[]> Histogram = new();
    public bool StretchHintShown;
    /// <summary>The crop the sources' statistics regions were built with (the crop is shared and may change meanwhile).</summary>
    public PixelRect? Crop;

    public bool IsEmpty => BaseSources.Count == 0;

    /// <summary>A copy whose collections can be changed without touching this one (the images are shared).</summary>
    public LayerState ShallowCopy()
    {
        var c = (LayerState)MemberwiseClone();
        c.BaseSources = new(BaseSources); c.Sources = new(Sources); c.SlotThumbnails = new(SlotThumbnails); c.OriginalImages = new(OriginalImages);
        c.CorrectionMaps = new(CorrectionMaps); c.Presets = new(Presets); c.PresetThumbnailCache = new(PresetThumbnailCache);
        c.AnalysisSamples = new(AnalysisSamples); c.PostStretchCache = new(PostStretchCache); c.Thumbnails = new(Thumbnails); c.Histogram = new(Histogram);
        return c;
    }
}

/// <summary>What a rendered tab depends on.</summary>
public sealed record StarsCompositeKey(string SourceIds, CombineSettings Settings, PixelRect? Crop, Orientation Orientation);

public sealed partial class ProjectModel
{
    public const string EmptyStarsText = "Stars tab: drop the star channels here, or use Remove Stars… or Load RGB stars… on the Background tab.";

    /// <summary>A tab's state, whichever is shown.</summary>
    public LayerState LayerStateOf(ImageLayer l) => l == ActiveLayer ? CaptureActiveLayer() : inactiveLayer;
    public IReadOnlyDictionary<ChannelKind, ChannelSource> LayerSources(ImageLayer l) => l == ActiveLayer ? sources : inactiveLayer.Sources;
    public IReadOnlyDictionary<ChannelKind, ChannelSource> BackgroundSources => LayerSources(ImageLayer.Background);

    /// <summary>The stars the shown tab blends: the rendered Stars tab, on the Background tab only.</summary>
    public StarsLayer? BlendStars => ActiveLayer == ImageLayer.Background && HasStars ? starsLayer : null;

    public bool CanSwitchLayer => EditingTool == null && !IsLoading && !CropEditing && !IsRemovingStars;

    static string SourceIds(IReadOnlyDictionary<ChannelKind, ChannelSource> s) => string.Join(",", s.Values.Select(v => v.Id.ToString()).OrderBy(x => x, StringComparer.Ordinal));

    CombineSettings ThumbnailSettings()
    {
        var s = settings.Clone();
        if (BlendStars == null) s.Stars.Enabled = false;
        return s;
    }

    public string LoadedStatusText()
    {
        if (sources.Values.FirstOrDefault() is not ChannelSource first) return ActiveLayer == ImageLayer.Stars ? EmptyStarsText : "Drop FITS or TIFF images to begin.";
        int n = sources.Count;
        string tab = ActiveLayer == ImageLayer.Stars ? "Stars tab · " : (HasStars ? "Background tab · " : "");
        return tab + $"{n} channel{(n == 1 ? "" : "s")} loaded · {first.Width} × {first.Height}" + (statusHint != null ? " · " + statusHint : "");
    }

    LayerState CaptureActiveLayer() => new()
    {
        BaseSources = new(baseSources), Sources = new(sources), SlotThumbnails = new(slotThumbnails), OriginalImages = new(originalImages),
        Settings = settings, Dust = Dust, Gradient = Gradient, Corrections = Corrections, CorrectionMaps = new(correctionMaps),
        Presets = Presets, PresetThumbnailCache = new(presetThumbnailCache),
        Context = Context, AnalysisSamples = AnalysisSamples, PostStretchCache = new(postStretchCache), Thumbnails = new(thumbnails),
        Frame = Frame, Histogram = Histogram, StretchHintShown = stretchHintShown, Crop = crop,
    };

    static void Refill<K, V>(Dictionary<K, V> target, Dictionary<K, V> from) where K : notnull
    {
        target.Clear();
        foreach (var (k, v) in from) target[k] = v;
    }

    /// <summary>Puts a tab's state into the live properties (no undo, no per-property side effects).</summary>
    void RestoreLayer(LayerState st)
    {
        restoringLayer = true;
        Refill(baseSources, st.BaseSources); Refill(sources, st.Sources); Refill(slotThumbnails, st.SlotThumbnails); Refill(originalImages, st.OriginalImages);
        settings = st.Settings; Dust = st.Dust; Gradient = st.Gradient; Corrections = st.Corrections; Refill(correctionMaps, st.CorrectionMaps);
        Presets = st.Presets; Refill(presetThumbnailCache, st.PresetThumbnailCache);
        Context = st.Context; AnalysisSamples = st.AnalysisSamples; Refill(postStretchCache, st.PostStretchCache); Refill(thumbnails, st.Thumbnails);
        Frame = st.Frame; Histogram = st.Histogram; stretchHintShown = st.StretchHintShown;
        restoringLayer = false;
    }

    /// <summary>Shows the other tab. Not while an editor, the crop, a load or a star removal is in progress.</summary>
    public void SwitchLayer(ImageLayer layer)
    {
        if (layer == ActiveLayer || !CanSwitchLayer) return;
        var leaving = CaptureActiveLayer();
        var target = inactiveLayer;
        peekChannel = null;
        Hover = null;
        undoStack.Clear(); redoStack.Clear(); undoTimer?.Dispose(); undoPending = false;
        renderGeneration++;
        minFrameGeneration = renderGeneration;
        contextGeneration++;
        thumbnailGeneration = -1;
        RestoreLayer(target);
        inactiveLayer = leaving;
        ActiveLayer = layer;
        IsRendering = false;
        Notify(Change.Layer); Notify(Change.Settings); Notify(Change.Sources); Notify(Change.SlotThumbnails); Notify(Change.Thumbnails);
        Notify(Change.Presets); Notify(Change.Dust); Notify(Change.Gradient); Notify(Change.Corrections); Notify(Change.Frame); Notify(Change.Context); Notify(Change.Peek);
        if (sources.Count == 0)
        {
            Frame = null; Histogram = new();
            StatusText = LoadedStatusText();
            Notify(Change.Frame);
        }
        else if (target.Crop != crop) CropChanged();      // statistics regions follow the shared crop, then the context
        else if (Context == null) RebuildContext();
        else
        {
            ScheduleRender();
            if (thumbnails.Count == 0) ScheduleThumbnails(TimeSpan.FromSeconds(0.05));
            StatusText = LoadedStatusText();
        }
        // Zoom and pan stay where they were (both tabs have the same geometry).
        UpdateComposites();
    }

    /// <summary>
    /// Loads channels into a tab without showing it (the Stars tab from Remove Stars, Load RGB stars
    /// or a project). <paramref name="stretchedStars"/> says whether star images are already stretched (null: guessed).
    /// </summary>
    public void LoadIntoLayer(ImageLayer layer, IReadOnlyList<ChannelItem> items, bool? stretchedStars = null, bool applyDefaults = true, Action? completion = null)
    {
        if (items.Count == 0) { completion?.Invoke(); return; }
        if (layer == ActiveLayer)
        {
            if (completion != null) afterChannelsLoaded.Add(completion);
            AddChannels(items);
            return;
        }
        var st = inactiveLayer; var cropNow = crop; var orient = Orientation;
        var dust = st.Dust.Clone(); var gradient = st.Gradient.Clone(); var corr = st.Corrections.Clone();
        IsLoading = true;
        LoadingMessage = $"Loading the {layer.DisplayName().ToLowerInvariant()} channels…";
        Task.Run(() =>
        {
            var built = BuildChannels(items, cropNow, orient, dust, gradient, corr);
            ui.Post(() =>
            {
                IsLoading = false;
                StoreInactive(built, layer, stretchedStars, applyDefaults);
                completion?.Invoke();
            });
        });
    }

    /// <summary>Stores built channels on the tab that is not shown.</summary>
    void StoreInactive(BuiltChannels built, ImageLayer layer, bool? stretchedStars, bool applyDefaults)
    {
        if (layer == ActiveLayer || built.Base.Count == 0) return;
        var first = built.Base[0];
        if (sources.Values.FirstOrDefault() is ChannelSource other && (other.Width != first.Width || other.Height != first.Height))
        {
            ErrorMessage = $"{first.FileName}: {first.Width}×{first.Height} does not match the loaded channels ({other.Width}×{other.Height}).";
            return;
        }
        bool wasEmpty = inactiveLayer.IsEmpty;
        unrotatedPlanes.AddRange(built.Unrotated);
        var l = inactiveLayer;
        for (int i = 0; i < built.Base.Count; i++)
        {
            var b = built.Base[i];
            l.BaseSources[b.Kind] = b; l.Sources[b.Kind] = built.Cropped[i]; l.OriginalImages[b.Kind] = built.Originals[i];
            if (built.Maps.TryGetValue(b.Kind, out var m)) l.CorrectionMaps[b.Kind] = m; else l.CorrectionMaps.Remove(b.Kind);
        }
        foreach (var (k, t) in built.Thumbs) l.SlotThumbnails[k] = t;
        l.Context = null; l.AnalysisSamples = new(); l.PostStretchCache.Clear();
        l.Thumbnails.Clear(); l.Frame = null; l.Histogram = new();
        l.Crop = crop;
        // A palette the tab can render (the shown tab gets the same fix in SourcesChanged).
        var kinds = l.Sources.Keys.ToHashSet();
        if (Renderer.MissingChannels(l.Settings, kinds).Count > 0 && Palettes.Available(kinds).FirstOrDefault() is Palette fix)
        {
            var fixedSettings = l.Settings.Clone(); fixedSettings.Select(fix); l.Settings = fixedSettings;
        }
        if (wasEmpty && applyDefaults && layer == ImageLayer.Stars)
        {
            l.Settings = StarsLayerDefaults(settings, l.Sources.Keys.ToHashSet(), stretchedStars ?? LooksLikeStretchedStars(l.Sources.Values));
            if (settings.Stars.UseMask) UpdateSettings(s => s.Stars.UseMask = false);
        }
        StatusText = LoadedStatusText() + $" · {layer.DisplayName()} tab: {l.Sources.Count} channel{(l.Sources.Count == 1 ? "" : "s")}";
        Notify(Change.Layer); Notify(Change.Stars);
        if (layer == ImageLayer.Stars) UpdateStarsComposite();
        ScheduleRecentStateSave();
    }

    /// <summary>A project's or recent set's Stars tab (or its legacy RGB stars file), loaded once the Background channels are in.</summary>
    void RestoreStarsAfterLoading(ProjectDocument doc)
    {
        if (doc.StarsLayer is LayerDocument l && l.Channels.Count > 0)
        {
            afterChannelsLoaded.Add(() =>
            {
                inactiveLayer.Settings = l.Settings.Clone(); inactiveLayer.Dust = l.Dust.Clone(); inactiveLayer.Gradient = l.Gradient.Clone();
                inactiveLayer.Corrections = l.Corrections.Clone(); inactiveLayer.Presets = l.Presets.Select(p => p.Clone()).ToList();
                var entries = l.Channels.Select(c => (c.Kind, c.Path)).ToList();
                Task.Run(() =>
                {
                    var (items, errs) = LoadEntries(entries);
                    ui.Post(() =>
                    {
                        if (errs.Count > 0) ErrorMessage = "Stars tab: " + string.Join("\n", errs);
                        LoadIntoLayer(ImageLayer.Stars, items, applyDefaults: false);
                    });
                });
            });
        }
        else if (doc.StarsFile is string p)
        {
            // Projects from before the tabs kept one RGB stars image: it becomes the Stars tab.
            afterChannelsLoaded.Add(() => LoadStarsLayer(p));
        }
    }

    // ---- Combination -----------------------------------------------------------------------------

    /// <summary>The keys of the composite renders in flight (a second request for the same key waits for it).</summary>
    StarsCompositeKey? starsCompositePending, backgroundCompositePending;

    StarsCompositeKey? CurrentStarsCompositeKey()
    {
        var st = LayerStateOf(ImageLayer.Stars);
        if (st.IsEmpty) return null;
        var s = st.Settings.Clone();
        s.Stars.Enabled = false;
        return new StarsCompositeKey(SourceIds(st.BaseSources), s, crop, Orientation);
    }

    StarsCompositeKey? CurrentBackgroundCompositeKey()
    {
        var bg = LayerStateOf(ImageLayer.Background);
        if (bg.IsEmpty) return null;
        var s = bg.Settings.Clone();
        s.Stars.Enabled = false;
        return new StarsCompositeKey(SourceIds(bg.BaseSources), s, crop, Orientation);
    }

    /// <summary>
    /// The settings of the Combine stars block (the Background tab's), from either tab. Set from the
    /// Stars tab, the change re-blends that tab's preview over the background.
    /// </summary>
    public StarsBlendSettings CombineStars => ActiveLayer == ImageLayer.Background ? settings.Stars : inactiveLayer.Settings.Stars;

    public void UpdateCombineStars(Action<StarsBlendSettings> edit)
    {
        if (ActiveLayer == ImageLayer.Background) { UpdateSettings(s => edit(s.Stars)); return; }
        var n = inactiveLayer.Settings.Stars.Clone();
        edit(n);
        if (n.Equals(inactiveLayer.Settings.Stars)) return;
        var s2 = inactiveLayer.Settings.Clone();
        s2.Stars = n;
        inactiveLayer.Settings = s2;
        Notify(Change.Stars);
        UpdateBackgroundComposite();
        ScheduleRender();
        ScheduleRecentStateSave();
    }

    /// <summary>Both renders the combination needs for the tab shown: the Stars tab under the Background tab, or the Background tab under the Stars tab.</summary>
    void UpdateComposites()
    {
        UpdateStarsComposite();
        UpdateBackgroundComposite();
    }

    /// <summary>Renders the Background tab (stars off) for the Stars tab's preview when the combination is on.</summary>
    void UpdateBackgroundComposite()
    {
        if (ActiveLayer != ImageLayer.Stars || !inactiveLayer.Settings.Stars.Enabled || CurrentBackgroundCompositeKey() is not StarsCompositeKey key || Equals(key, backgroundCompositeKey)) return;
        if (BackgroundCompositeBusy && Equals(key, backgroundCompositePending)) return;
        backgroundCompositePending = key;
        int gen = ++backgroundCompositeGeneration;
        BackgroundCompositeBusy = true;
        Notify(Change.Stars);
        var bg = inactiveLayer; var cropNow = crop;
        Task.Run(() =>
        {
            var image = RenderStars(bg, key.Settings, cropNow);
            ui.Post(() =>
            {
                if (gen != backgroundCompositeGeneration) return;
                BackgroundCompositeBusy = false;
                backgroundImage = image;
                backgroundCompositeKey = image == null ? null : key;
                Notify(Change.Stars);
                if (ActiveLayer == ImageLayer.Stars) ScheduleRender();
            });
        });
    }

    /// <summary>Renders the Stars tab for the Combine stars block when it changed (Background tab, combination on).</summary>
    void UpdateStarsComposite()
    {
        if (ActiveLayer != ImageLayer.Background) return;
        if (CurrentStarsCompositeKey() is not StarsCompositeKey key)
        {
            if (starsLayer != null) { starsLayer = null; starsCompositeKey = null; ScheduleRender(); ScheduleThumbnails(TimeSpan.FromSeconds(0.1)); }
            return;
        }
        if (!settings.Stars.Enabled || Equals(key, starsCompositeKey)) return;
        if (StarsCompositeBusy && Equals(key, starsCompositePending)) return;
        starsCompositePending = key;
        int gen = ++starsCompositeGeneration;
        StarsCompositeBusy = true;
        Notify(Change.Stars);
        var st = inactiveLayer; var cropNow = crop;
        Task.Run(() =>
        {
            var layer = RenderStars(st, key.Settings, cropNow);
            ui.Post(() =>
            {
                if (gen != starsCompositeGeneration) return;
                StarsCompositeBusy = false;
                starsLayer = layer;
                starsCompositeKey = layer == null ? null : key;
                Notify(Change.Stars);
                if (ActiveLayer == ImageLayer.Background) { ScheduleRender(); ScheduleThumbnails(TimeSpan.FromSeconds(0.1)); }
            });
        });
    }

    /// <summary>A tab at full resolution, as its preview shows it (<paramref name="s"/> has the stars blend off).</summary>
    static StarsLayer? RenderStars(LayerState st, CombineSettings s, PixelRect? cropNow)
    {
        if (st.IsEmpty) return null;
        var srcs = st.Crop == cropNow ? new Dictionary<ChannelKind, ChannelSource>(st.Sources)
            : st.BaseSources.ToDictionary(kv => kv.Key, kv => cropNow == null ? kv.Value : new ChannelSource(kv.Value, cropNow));
        var ctx = (st.Crop == cropNow && st.Settings.Normalization.Equals(s.Normalization) ? st.Context : null) ?? RenderContext.Make(s.Normalization, srcs);
        st.PostStretchCache.TryGetValue(PostStretchKey(s, cropNow, srcs.Keys), out var fixedStretch);
        try
        {
            var img = Renderer.RenderFull(srcs, s, ctx, fixedStretch, null);
            if (img == null) return null;
            return new StarsLayer(img.Planes, "Stars tab", null, "rendered Stars tab", processed: true);
        }
        catch { return null; }
    }

    /// <summary>A tab turned with the other one: spots, models, maps and channels rebuilt (off the UI thread).</summary>
    static LayerState Reoriented(LayerState st, OrientationOp op, int w, int h, PixelRect? newCrop,
                                 List<(PlanarImage Rotated, PlanarImage Unrotated)> pairs, List<(PlanarImage, PlanarImage)> newPairs)
    {
        var o = st.ShallowCopy();
        var dust = st.Dust.Clone();
        dust.Spots = st.Dust.Spots.ToDictionary(kv => kv.Key, kv => kv.Value.Select(s => s.Applying(op, w, h)).ToList());
        o.Dust = dust;
        o.Gradient = st.Gradient.Applying(op, w, h);
        o.CorrectionMaps = st.CorrectionMaps.ToDictionary(kv => kv.Key, kv => kv.Value.Applying(op));
        o.BaseSources = new(); o.Sources = new(); o.OriginalImages = new(); o.SlotThumbnails = new();
        foreach (var (k, old) in st.BaseSources)
        {
            var before = st.OriginalImages.TryGetValue(k, out var oi) ? oi : old.Image;
            var orig = before.Applying(op);
            if (Unrotated(before, pairs) is PlanarImage un) { var pr = (orig, un.Applying(op)); lock (newPairs) newPairs.Add(pr); }
            var healed = Process(orig, k, o.Dust, o.Gradient, o.CorrectionMaps.TryGetValue(k, out var cm) ? (cm, (float)o.Corrections.StrengthFor(k)) : null);
            var b = new ChannelSource(k, healed, old.FileName, old.Path, old.FormatDescription);
            var c = newCrop == null ? b : new ChannelSource(b, newCrop);
            o.BaseSources[k] = b; o.Sources[k] = c; o.OriginalImages[k] = orig;
            o.SlotThumbnails[k] = SlotThumbnail(c);
        }
        o.Context = null; o.AnalysisSamples = new(); o.PostStretchCache = new(); o.Thumbnails = new(); o.Frame = null; o.Histogram = new();
        o.Crop = newCrop;
        return o;
    }

    public const float StarsTargetBackground = 0.02f;
    public const float StarsShadowsClipping = -1.5f;

    /// <summary>
    /// Settings for a fresh Stars tab: the Background palette when the channels allow it (RGB for
    /// R, G, B stars), no normalisation (a star image has no sky to match), no luminance injection,
    /// and either no stretch (stretched star images) or a dark-sky Auto STF.
    /// </summary>
    public static CombineSettings StarsLayerDefaults(CombineSettings bg, HashSet<ChannelKind> kinds, bool stretched)
    {
        var available = Palettes.Available(kinds).Select(p => p.Id).ToList();
        string pid;
        if (kinds.Contains(ChannelKind.Red) && kinds.Contains(ChannelKind.Green) && kinds.Contains(ChannelKind.Blue) && available.Contains("rgb")) pid = "rgb";
        else if (available.Contains(bg.PaletteID)) pid = bg.PaletteID;
        else pid = available.FirstOrDefault() ?? bg.PaletteID;
        var s = CombineSettings.DefaultWith(pid);
        s.Normalization.Method = NormalizationMethod.None;
        s.Luminance.Enabled = false;
        s.Stretch.Linked = true;
        if (stretched) s.Stretch.Method = StretchMethod.None;
        else
        {
            s.Stretch.Method = StretchMethod.AutoSTF;
            s.Stretch.TargetBackground = StarsTargetBackground;
            s.Stretch.ShadowsClipping = StarsShadowsClipping;
        }
        return s;
    }

    /// <summary>Stretched star images reach high values on their stars (linear star layers stay low).</summary>
    public static bool LooksLikeStretchedStars(IEnumerable<ChannelSource> srcs) => srcs.Any(s => s.Stats.Median > 0.08f || s.Stats.Percentile(0.999) > 0.15f);

    // ---- Close, defaults, reset ------------------------------------------------------------------

    /// <summary>File ▸ Close Image: both tabs, crop, rotation, settings, presets and the project go; the app is back at its start screen.</summary>
    public void CloseImage()
    {
        if (IsRemovingStars) return;
        if (DustEditing != null) CancelDustEditing();
        if (GradientEditing != null) CancelGradientEditing();
        if (CorrectionEditing != null) CancelCorrectionEditing();
        if (CropEditing) CancelCropEditing();
        RemoveAll();
        PendingFiles.Clear();
        pendingColorFile = null;
        restoringLayer = true;
        settings = new CombineSettings();
        restoringLayer = false;
        Presets = new();
        presetThumbnailCache.Clear();
        ProjectPath = null;
        statusHint = null;
        stretchHintShown = false;
        Histogram = new();
        Hover = null;
        undoStack.Clear(); redoStack.Clear(); undoTimer?.Dispose(); undoPending = false;
        StatusText = "Drop FITS or TIFF images to begin.";
        Notify(Change.Settings); Notify(Change.Presets); Notify(Change.Project); Notify(Change.Pending); Notify(Change.Layer);
    }

    /// <summary>The tab's default settings for the current palette (what a fresh load gives): the Default button of the preset bar. Crop, presets and the stars combination stay.</summary>
    public void ApplyDefaultSettings() => Settings = DefaultSettings();

    public CombineSettings DefaultSettings()
    {
        CombineSettings s;
        if (ActiveLayer == ImageLayer.Stars)
            s = StarsLayerDefaults(inactiveLayer.Settings, AvailableKinds, LooksLikeStretchedStars(sources.Values));
        else
        {
            s = CombineSettings.DefaultWith(settings.PaletteID);
            if (sources.Count > 0 && sources.Values.All(x => x.Stats.Median > 0.08f)) s.Stretch.Method = StretchMethod.None;
        }
        if (CurrentPalette is Palette p) s.Select(p);
        s.Stars = settings.Stars.Clone();
        return s;
    }

    /// <summary>Reset all: no processing at all — no stretch, normalisation, luminance injection, colour, tone, grading, denoise or detail; the palette, the channels and the stars combination stay.</summary>
    public void ResetAllProcessing()
    {
        var s = new CombineSettings();
        if (CurrentPalette is Palette p) s.Select(p);
        s.Stretch.Method = StretchMethod.None;
        s.Normalization.Method = NormalizationMethod.None;
        s.Luminance.Enabled = false;
        s.Stars = settings.Stars.Clone();
        Settings = s;
    }
}
