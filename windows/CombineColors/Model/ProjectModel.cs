using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using AstroCore;

namespace CombineColors.Model;

public sealed record HoverInfo(int X, int Y, List<(ChannelKind kind, float value)> Values, (float r, float g, float b)? Rgb);

public readonly record struct Viewport(Rect VisibleRect, double Scale);

public sealed class PendingFile
{
    public Guid Id { get; } = Guid.NewGuid();
    public LoadedFile File { get; }
    public PendingFile(LoadedFile file) { File = file; }
}

public enum ThumbnailRegion { WholeImage, CurrentView, Crop }

public static class ThumbnailRegions
{
    public static readonly ThumbnailRegion[] All = [ThumbnailRegion.WholeImage, ThumbnailRegion.CurrentView, ThumbnailRegion.Crop];
    public static string DisplayName(this ThumbnailRegion r) => r switch { ThumbnailRegion.WholeImage => "Whole image", ThumbnailRegion.CurrentView => "Current view", _ => "Crop" };
}

/// <summary>How the crop is shown on the preview when not editing it.</summary>
public enum CropOverlayMode
{
    /// <summary>Nothing drawn.</summary>
    Hidden,
    /// <summary>Dashed frame with the outside dimmed.</summary>
    Frame,
    /// <summary>Only the cropped part is visible.</summary>
    CropOnly,
}

public static class CropOverlayModes
{
    public static readonly CropOverlayMode[] All = [CropOverlayMode.Hidden, CropOverlayMode.Frame, CropOverlayMode.CropOnly];
    public static string DisplayName(this CropOverlayMode m) => m switch { CropOverlayMode.Hidden => "Hide Crop Limits", CropOverlayMode.Frame => "Show Crop Limits", _ => "Show Only the Cropped Part" };
}

public enum PaletteFilter { Favorites, All }

/// <summary>Names passed to <see cref="ProjectModel.Changed"/>.</summary>
public static class Change
{
    public const string Sources = "Sources", Settings = "Settings", Frame = "Frame", Thumbnails = "Thumbnails", SlotThumbnails = "SlotThumbnails",
        Status = "Status", Crop = "Crop", Dust = "Dust", Presets = "Presets", RecentSets = "RecentSets", PaletteVisibility = "PaletteVisibility",
        Pending = "Pending", Hover = "Hover", Context = "Context", Peek = "Peek", Error = "Error", Project = "Project";
}

/// <summary>
/// The application state: loaded channels, settings, crop / orientation / dust spots, preview frames,
/// thumbnails, presets and recent sets. Every member runs on the UI thread; background work comes
/// back through the <see cref="IUiDispatcher"/>.
/// </summary>
public sealed class ProjectModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Coarse change notification (see <see cref="Change"/>).</summary>
    public event Action<string>? Changed;
    /// <summary>The preview should fit the (cropped) image.</summary>
    public event Action? FitRequested;

    void Notify(string name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        Changed?.Invoke(name);
    }

    readonly IUiDispatcher ui;
    readonly Preferences prefs;

    // Data
    readonly Dictionary<ChannelKind, ChannelSource> baseSources = new();
    readonly Dictionary<ChannelKind, ChannelSource> sources = new();
    public IReadOnlyDictionary<ChannelKind, ChannelSource> BaseSources => baseSources;
    public IReadOnlyDictionary<ChannelKind, ChannelSource> Sources => sources;
    public Size? ImageSize => sources.Values.FirstOrDefault() is ChannelSource s ? new Size(s.Width, s.Height) : null;
    /// <summary>Size a new file must have (the loaded size before the orientation is applied).</summary>
    public Size? RequiredFileSize => ImageSize is Size sz ? (Orientation.SwapsAxes ? new Size(sz.Height, sz.Width) : sz) : null;
    public HashSet<ChannelKind> AvailableKinds => sources.Keys.ToHashSet();
    readonly Dictionary<ChannelKind, BitmapSource> slotThumbnails = new();
    public IReadOnlyDictionary<ChannelKind, BitmapSource> SlotThumbnails => slotThumbnails;
    public List<PendingFile> PendingFiles { get; } = new();
    PendingFile? pendingColorFile;
    public PendingFile? PendingColorFile { get => pendingColorFile; set { pendingColorFile = value; Notify(Change.Pending); } }

    // Settings
    CombineSettings settings = new();
    public CombineSettings Settings
    {
        get => settings;
        set
        {
            var old = settings;
            settings = value;
            SettingsChanged(old);
        }
    }

    /// <summary>Edits a copy of the settings and assigns it (one undo step).</summary>
    public void UpdateSettings(Action<CombineSettings> edit)
    {
        var s = settings.Clone();
        edit(s);
        Settings = s;
    }

    PixelRect? crop;
    public PixelRect? Crop
    {
        get => crop;
        set
        {
            if (crop == value) return;
            crop = value;
            Notify(Change.Crop);
            CropChanged();
            ScheduleRecentStateSave();
        }
    }
    bool cropEditing;
    public bool CropEditing { get => cropEditing; set { if (cropEditing == value) return; cropEditing = value; Notify(Change.Crop); } }
    PixelRect? cropDraft;
    public PixelRect? CropDraft { get => cropDraft; set { if (cropDraft == value) return; cropDraft = value; Notify(Change.Crop); } }
    CropOverlayMode cropOverlay = CropOverlayMode.Frame;
    public CropOverlayMode CropOverlay { get => cropOverlay; set { if (cropOverlay == value) return; cropOverlay = value; Notify(Change.Crop); } }
    /// <summary>Rotation / mirroring applied to every channel. Stored in the project; new channels are oriented on load.</summary>
    public Orientation Orientation { get; private set; } = Orientation.Identity;
    public int Rotation => Orientation.Rotation;

    // Dust spots
    readonly Dictionary<ChannelKind, PlanarImage> originalImages = new();
    /// <summary>Loaded (oriented) planes before dust healing; the same instance as the channel image when a channel has no spots.</summary>
    public IReadOnlyDictionary<ChannelKind, PlanarImage> OriginalImages => originalImages;
    /// <summary>Spots applied to the channels (stored in the project).</summary>
    public DustSettings Dust { get; private set; } = new();
    DustSettings? dustDraft;
    /// <summary>Working copy while the dust editor is open; changes are previewed on the edited channel.</summary>
    public DustSettings? DustDraft
    {
        get => dustDraft;
        set
        {
            bool changed = !Equals(dustDraft, value);
            dustDraft = value;
            if (changed) { Notify(Change.Dust); if (DustEditing != null) ScheduleDustPreview(); }
        }
    }
    /// <summary>Channel shown in the dust editor (null = editor closed).</summary>
    public ChannelKind? DustEditing { get; private set; }
    Guid? selectedDustSpot;
    public Guid? SelectedDustSpot { get => selectedDustSpot; set { if (selectedDustSpot == value) return; selectedDustSpot = value; Notify(Change.Dust); } }
    bool dustBypass;
    /// <summary>Shows the edited channel without any spot (before / after comparison).</summary>
    public bool DustBypass { get => dustBypass; set { if (dustBypass == value) return; dustBypass = value; Notify(Change.Dust); if (DustEditing != null) ScheduleDustPreview(); } }
    IDisposable? dustPreviewTimer;
    int dustPreviewGeneration;
    /// <summary>Channels whose live source currently shows draft spots (restored on cancel).</summary>
    readonly HashSet<ChannelKind> dustPreviewed = new();

    // Derived state
    public RenderContext? Context { get; private set; }
    public Dictionary<ChannelKind, PlanarImage> AnalysisSamples { get; private set; } = new();
    readonly Dictionary<string, List<ResolvedStretch>> postStretchCache = new();
    int contextGeneration;
    bool stretchHintShown;
    string? statusHint;

    // Preview
    public PreviewFrame? Frame { get; private set; }
    public List<int[]> Histogram { get; private set; } = new();
    Viewport viewport = new(Rect.Empty, 1);
    public Viewport Viewport { get => viewport; set { if (viewport == value) return; viewport = value; ViewportChanged(); } }
    bool highQualityPreview;
    public bool HighQualityPreview { get => highQualityPreview; set { if (highQualityPreview == value) return; highQualityPreview = value; Notify(Change.Status); ScheduleRender(); } }
    public bool IsRendering { get; private set; }
    public string? RenderError { get; private set; }
    public double LastRenderMillis { get; private set; }
    public int LastRenderLevel { get; private set; }
    HoverInfo? hover;
    public HoverInfo? Hover { get => hover; private set { if (Equals(hover, value)) return; hover = value; Notify(Change.Hover); } }
    public int FitRequest { get; private set; }
    /// <summary>Saved presets (settings + thumbnail), shown in the bar under the preview.</summary>
    public List<SettingsPreset> Presets { get; private set; } = new();
    readonly Dictionary<Guid, BitmapSource> presetThumbnailCache = new();
    ChannelKind? peekChannel;
    /// <summary>Channel shown full-size while its slot thumbnail is pressed.</summary>
    public ChannelKind? PeekChannel { get => peekChannel; set { if (peekChannel == value) return; peekChannel = value; Notify(Change.Peek); ScheduleRender(); } }

    /// <summary>Settings currently displayed (a single channel while peeking).</summary>
    public CombineSettings DisplayedSettings
    {
        get
        {
            var k = PeekChannel ?? DustEditing;
            if (k is ChannelKind kind && sources.ContainsKey(kind)) return PeekSettings(kind, settings);
            return settings;
        }
    }

    // Thumbnails
    readonly Dictionary<string, BitmapSource> thumbnails = new();
    public IReadOnlyDictionary<string, BitmapSource> Thumbnails => thumbnails;
    ThumbnailRegion thumbnailRegion = ThumbnailRegion.WholeImage;
    public ThumbnailRegion ThumbnailRegion { get => thumbnailRegion; set { if (thumbnailRegion == value) return; thumbnailRegion = value; Notify(Change.Thumbnails); ScheduleThumbnails(TimeSpan.Zero); } }
    int thumbnailGeneration;
    IDisposable? thumbnailTimer;

    // Status
    bool isLoading;
    public bool IsLoading { get => isLoading; private set { isLoading = value; Notify(Change.Status); } }
    public string LoadingMessage { get; private set; } = "";
    string? errorMessage;
    public string? ErrorMessage { get => errorMessage; set { errorMessage = value; Notify(Change.Error); } }
    public double? ExportProgress { get; private set; }
    public string? ProjectPath { get; private set; }
    string statusText = "Drop FITS or TIFF images to begin.";
    public string StatusText { get => statusText; set { statusText = value; Notify(Change.Status); } }

    // Infrastructure
    readonly RenderCoordinator renderer = new();
    readonly ThumbnailCoordinator thumbnailer = new();
    int renderGeneration;

    // Undo
    readonly Stack<CombineSettings> undoStack = new();
    readonly Stack<CombineSettings> redoStack = new();
    bool undoPending;
    IDisposable? undoTimer;
    bool restoring;
    public bool CanUndo => undoStack.Count > 0;
    public bool CanRedo => redoStack.Count > 0;

    // Palette visibility
    public HashSet<string> HiddenPaletteIds { get; private set; } = new();
    PaletteFilter paletteFilter = PaletteFilter.Favorites;
    public PaletteFilter PaletteFilter { get => paletteFilter; set { paletteFilter = value; prefs.Set("paletteFilter", value == PaletteFilter.All ? "all" : "favorites"); Notify(Change.PaletteVisibility); } }

    public bool IsPaletteHidden(string id) => HiddenPaletteIds.Contains(id);

    public void SetPaletteHidden(string id, bool hidden)
    {
        if (hidden) HiddenPaletteIds.Add(id); else HiddenPaletteIds.Remove(id);
        prefs.SetStrings("hiddenPalettes", HiddenPaletteIds.OrderBy(s => s, StringComparer.Ordinal));
        Notify(Change.PaletteVisibility);
    }

    /// <summary>Palettes to show for the loaded channels under the current filter.</summary>
    public List<Palette> VisiblePalettes
    {
        get
        {
            var avail = Palettes.Available(AvailableKinds);
            return PaletteFilter == PaletteFilter.All ? avail : avail.Where(p => !HiddenPaletteIds.Contains(p.Id)).ToList();
        }
    }

    // Recent sets
    readonly RecentSetsStore recentStore;
    public List<RecentSet> RecentSets { get; private set; } = new();
    /// <summary>Suppresses recording while a set/project is being (re)loaded.</summary>
    bool suppressRecent;

    public Preferences Preferences => prefs;

    public ProjectModel(IUiDispatcher dispatcher, Preferences preferences)
    {
        ui = dispatcher;
        prefs = preferences;
        recentStore = new RecentSetsStore(prefs);
        RecentSets = recentStore.Load();
        HiddenPaletteIds = prefs.GetStrings("hiddenPalettes").ToHashSet();
        paletteFilter = prefs.GetString("paletteFilter") == "all" ? PaletteFilter.All : PaletteFilter.Favorites;
        renderer.OnFrame = (frame, gen) => ui.Post(() =>
        {
            Frame = frame;
            IsRendering = gen != renderGeneration;
            RenderError = null;
            LastRenderMillis = frame.Millis;
            LastRenderLevel = frame.Level;
            Histogram = frame.Rgb.Planes.Select(p => Statistics.Histogram(p, 128, maxSamples: 300_000)).ToList();
            if (frame.PostStretch != null) postStretchCache[PostStretchKey(frame.Settings)] = frame.PostStretch;
            Notify(Change.Frame);
        });
        renderer.OnError = (error, gen) => ui.Post(() =>
        {
            IsRendering = gen != renderGeneration;
            RenderError = error.Message;
            Notify(Change.Frame);
        });
        thumbnailer.OnThumbnail = (id, image, gen) => ui.Post(() =>
        {
            if (gen != thumbnailGeneration) return;
            thumbnails[id] = image;
            Notify(Change.Thumbnails);
        });
    }

    // ---- Presets ---------------------------------------------------------------------------------

    /// <summary>Stores the current settings with a thumbnail of the current preview.</summary>
    public SettingsPreset SavePreset(string? name = null)
    {
        var n = name ?? $"Preset {Presets.Count + 1}";
        byte[]? png = null;
        if (Frame != null) { try { png = RasterWriter.PngData(Frame.Rgb, 192); } catch { } }
        var p = new SettingsPreset(n, settings.Clone(), png);
        Presets.Add(p);
        Notify(Change.Presets);
        ScheduleRecentStateSave();
        return p;
    }

    public void ApplyPreset(SettingsPreset p) => Settings = p.Settings.Clone();

    public void DeletePreset(SettingsPreset p)
    {
        Presets.RemoveAll(x => x.Id == p.Id);
        presetThumbnailCache.Remove(p.Id);
        Notify(Change.Presets);
        ScheduleRecentStateSave();
    }

    public void RenamePreset(SettingsPreset p, string name)
    {
        int i = Presets.FindIndex(x => x.Id == p.Id);
        if (i < 0 || string.IsNullOrEmpty(name)) return;
        Presets[i].Name = name;
        Notify(Change.Presets);
        ScheduleRecentStateSave();
    }

    /// <summary>Replaces the preset's settings and thumbnail with the current state.</summary>
    public void UpdatePreset(SettingsPreset p)
    {
        int i = Presets.FindIndex(x => x.Id == p.Id);
        if (i < 0) return;
        Presets[i].Settings = settings.Clone();
        Presets[i].ThumbnailPng = null;
        if (Frame != null) { try { Presets[i].ThumbnailPng = RasterWriter.PngData(Frame.Rgb, 192); } catch { } }
        Presets[i].Date = DateTime.UtcNow;
        presetThumbnailCache.Remove(p.Id);
        Notify(Change.Presets);
        ScheduleRecentStateSave();
    }

    public BitmapSource? PresetThumbnail(SettingsPreset p)
    {
        if (presetThumbnailCache.TryGetValue(p.Id, out var c)) return c;
        if (p.ThumbnailPng == null) return null;
        var img = RasterWriter.BitmapFromPng(p.ThumbnailPng);
        if (img != null) presetThumbnailCache[p.Id] = img;
        return img;
    }

    /// <summary>The preset whose settings equal the current ones (highlighted in the bar).</summary>
    public Guid? ActivePresetId => Presets.FirstOrDefault(p => p.Settings.Equals(settings))?.Id;

    /// <summary>Grayscale view of one channel with the current stretch, no colour processing.</summary>
    public static CombineSettings PeekSettings(ChannelKind kind, CombineSettings baseSettings)
    {
        var s = baseSettings.Clone();
        s.Mode = CombineMode.Monochrome;
        s.MonoChannels = [kind];
        s.PaletteID = "monochrome";
        var adj = s.Adjust(kind).Clone(); adj.Enabled = true; adj.Weight = 1; s.SetAdjust(kind, adj);
        s.Luminance.Enabled = false;
        s.Post = new PostSettings();
        s.Stars.Enabled = false;
        if (s.Stretch.Method == StretchMethod.AutoSTF) s.Stretch.Linked = false;
        return s;
    }

    // ---- Loading ---------------------------------------------------------------------------------

    public void LoadFiles(IReadOnlyList<string> paths)
    {
        var supported = paths.Where(ImageLoader.IsSupported).ToList();
        if (supported.Count == 0) { ErrorMessage = "No supported images (FITS, TIFF, PNG, JPEG)."; return; }
        IsLoading = true;
        LoadingMessage = $"Loading {supported.Count} file{(supported.Count == 1 ? "" : "s")}…";
        Task.Run(() =>
        {
            var results = new (LoadedFile? file, Exception? error)[supported.Count];
            Parallel.For(0, supported.Count, i =>
            {
                try { results[i] = (ImageLoader.Load(supported[i]), null); }
                catch (Exception e) { results[i] = (null, e); }
            });
            ui.Post(() => FilesLoaded(results));
        });
    }

    void FilesLoaded((LoadedFile? file, Exception? error)[] results)
    {
        var errors = new List<string>();
        var toAssign = new List<(ChannelKind, LoadedFile)>();
        foreach (var (file, error) in results)
        {
            if (error != null) { errors.Add(error.Message); continue; }
            if (file == null) continue;
            Size? size = RequiredFileSize ?? (toAssign.Count > 0 ? new Size(toAssign[0].Item2.Width, toAssign[0].Item2.Height) : null);
            if (size is Size sz && ((int)sz.Width != file.Width || (int)sz.Height != file.Height))
            {
                errors.Add($"{file.FileName}: {file.Width}×{file.Height} does not match the loaded channels ({(int)sz.Width}×{(int)sz.Height}).");
                continue;
            }
            if (file.IsColor) PendingColorFile = new PendingFile(file);
            else if (file.DetectedKind is ChannelKind k) toAssign.Add((k, file));
            else { PendingFiles.Add(new PendingFile(file)); Notify(Change.Pending); }
        }
        if (errors.Count > 0) ErrorMessage = string.Join("\n", errors);
        if (toAssign.Count == 0) IsLoading = false;
        else AddChannels(toAssign.Select(t => new ChannelItem(t.Item1, t.Item2.Planes[0], t.Item2.FileName, t.Item2.Path, t.Item2.FormatDescription)).ToList());
    }

    /// <summary>Assigns a pending (unrecognised) file to a slot.</summary>
    public void AssignPending(PendingFile pending, ChannelKind kind)
    {
        PendingFiles.RemoveAll(p => p.Id == pending.Id);
        Notify(Change.Pending);
        var f = pending.File;
        AddChannels([new ChannelItem(kind, f.Planes[0], f.FileName, f.Path, f.FormatDescription)]);
    }

    /// <summary>Drops a pending file without loading it.</summary>
    public void SkipPending(PendingFile pending)
    {
        PendingFiles.RemoveAll(p => p.Id == pending.Id);
        Notify(Change.Pending);
    }

    /// <summary>Assigns the planes of a colour file to R, G, B.</summary>
    public void AssignColorFileAsRGB(PendingFile pending)
    {
        PendingColorFile = null;
        var f = pending.File;
        var kinds = new[] { ChannelKind.Red, ChannelKind.Green, ChannelKind.Blue };
        AddChannels(kinds.Select((k, i) => new ChannelItem(k, f.Planes[i], f.FileName, f.Path, f.FormatDescription)).ToList());
    }

    public sealed record ChannelItem(ChannelKind Kind, PlanarImage Plane, string FileName, string? Path, string Format);

    public void AddChannels(IReadOnlyList<ChannelItem> items)
    {
        if (items.Count == 0) return;
        IsLoading = true;
        LoadingMessage = $"Analysing {string.Join(", ", items.Select(i => i.Kind.ShortName()))}…";
        var cropNow = crop; var orient = Orientation; var dust = Dust.Clone();
        Task.Run(() =>
        {
            var built = new ChannelSource[items.Count];
            var originals = new PlanarImage[items.Count];
            Parallel.For(0, items.Count, i =>
            {
                var it = items[i];
                var oriented = orient.Apply(it.Plane);
                var healed = DustRemoval.Heal(oriented, dust.SpotsFor(it.Kind));
                built[i] = new ChannelSource(it.Kind, healed, it.FileName, it.Path, it.Format);
                originals[i] = oriented;
            });
            var cropped = built.Select(b => cropNow == null ? b : new ChannelSource(b, cropNow)).ToArray();
            var thumbs = cropped.Select(c => (c.Kind, SlotThumbnail(c))).ToArray();
            ui.Post(() =>
            {
                for (int i = 0; i < built.Length; i++) { baseSources[built[i].Kind] = built[i]; sources[cropped[i].Kind] = cropped[i]; originalImages[built[i].Kind] = originals[i]; }
                foreach (var (k, t) in thumbs) slotThumbnails[k] = t;
                IsLoading = false;
                Notify(Change.SlotThumbnails);
                SourcesChanged();
            });
        });
    }

    /// <summary>Grayscale strip thumbnail of a source (cropped to its region when a crop is set).</summary>
    public static BitmapSource SlotThumbnail(ChannelSource s) => RasterWriter.MakeDisplayImage(s.DisplayPlane(80_000));

    public void RemoveChannel(ChannelKind kind)
    {
        if (PeekChannel == kind) PeekChannel = null;
        baseSources.Remove(kind);
        sources.Remove(kind);
        slotThumbnails.Remove(kind);
        originalImages.Remove(kind);
        Dust.Spots.Remove(kind);
        dustDraft?.Spots.Remove(kind);
        dustPreviewed.Remove(kind);
        if (DustEditing == kind) { DustEditing = null; dustDraft = null; Notify(Change.Dust); }
        if (sources.Count == 0) { Frame = null; crop = null; cropDraft = null; cropEditing = false; Notify(Change.Crop); Notify(Change.Frame); }
        Notify(Change.SlotThumbnails);
        SourcesChanged();
    }

    public void RemoveAll()
    {
        baseSources.Clear(); sources.Clear(); slotThumbnails.Clear(); Frame = null; thumbnails.Clear();
        crop = null; cropDraft = null; cropEditing = false; Orientation = Orientation.Identity;
        originalImages.Clear(); Dust = new DustSettings(); dustDraft = null; DustEditing = null; dustPreviewed.Clear();
        Notify(Change.Crop); Notify(Change.Dust); Notify(Change.Frame); Notify(Change.SlotThumbnails); Notify(Change.Thumbnails);
        SourcesChanged();
    }

    // ---- Rotation / flips --------------------------------------------------------------------------

    public void RotateClockwise() => Transform(OrientationOp.RotateClockwise);
    public void FlipHorizontal() => Transform(OrientationOp.FlipHorizontal);
    public void FlipVertical() => Transform(OrientationOp.FlipVertical);

    /// <summary>Rotates or mirrors every channel (and the crop); statistics and star masks are rebuilt.</summary>
    public void Transform(OrientationOp op)
    {
        if (baseSources.Count == 0 || IsLoading) return;
        var baseCopy = new Dictionary<ChannelKind, ChannelSource>(baseSources);
        var first = baseCopy.Values.First();
        int w = first.Width, h = first.Height;
        Orientation = Orientation.Applying(op);
        var newCrop = crop?.Applying(op, w, h);
        cropDraft = cropDraft?.Applying(op, w, h);
        DustSettings MapSpots(DustSettings d)
        {
            var m = d.Clone();
            m.Spots = d.Spots.ToDictionary(kv => kv.Key, kv => kv.Value.Select(s => s.Applying(op, w, h)).ToList());
            return m;
        }
        Dust = MapSpots(Dust);
        if (dustDraft != null) dustDraft = MapSpots(dustDraft);
        var dust = Dust; var draft = dustDraft; var editing = DustEditing;
        var originals = new Dictionary<ChannelKind, PlanarImage>(originalImages);
        IsLoading = true;
        LoadingMessage = op == OrientationOp.RotateClockwise ? "Rotating…" : "Flipping…";
        Frame = null;
        Notify(Change.Frame); Notify(Change.Dust);
        Task.Run(() =>
        {
            var kinds = baseCopy.Keys.ToArray();
            var newBase = new ChannelSource[kinds.Length]; var newCur = new ChannelSource[kinds.Length]; var newOrig = new PlanarImage[kinds.Length];
            Parallel.For(0, kinds.Length, i =>
            {
                var k = kinds[i]; var old = baseCopy[k];
                var orig = (originals.TryGetValue(k, out var o) ? o : old.Image).Applying(op);
                // The edited channel keeps showing its draft spots.
                var d = (k == editing ? draft : null) ?? dust;
                var healed = DustRemoval.Heal(orig, d.SpotsFor(k));
                var b = new ChannelSource(k, healed, old.FileName, old.Path, old.FormatDescription);
                newBase[i] = b; newCur[i] = newCrop == null ? b : new ChannelSource(b, newCrop); newOrig[i] = orig;
            });
            var thumbs = newCur.Select(c => (c.Kind, SlotThumbnail(c))).ToArray();
            ui.Post(() =>
            {
                baseSources.Clear(); sources.Clear(); originalImages.Clear();
                for (int i = 0; i < kinds.Length; i++) { baseSources[kinds[i]] = newBase[i]; sources[kinds[i]] = newCur[i]; originalImages[kinds[i]] = newOrig[i]; }
                foreach (var (k, t) in thumbs) slotThumbnails[k] = t;
                IsLoading = false;
                postStretchCache.Clear();
                suppressCropRebuild = true;
                crop = newCrop;
                suppressCropRebuild = false;
                Notify(Change.Crop); Notify(Change.SlotThumbnails); Notify(Change.Sources);
                RebuildContext();
                RequestFit();
                ScheduleRecentStateSave();
            });
        });
    }
    bool suppressCropRebuild;

    void RequestFit() { FitRequest++; FitRequested?.Invoke(); }

    /// <summary>Moves a channel to another slot (swapping if occupied).</summary>
    public void Reassign(ChannelKind kind, ChannelKind newKind)
    {
        if (kind == newKind || !baseSources.TryGetValue(kind, out var src) || !sources.TryGetValue(kind, out var cur)) return;
        PeekChannel = null;
        baseSources.TryGetValue(newKind, out var otherBase); sources.TryGetValue(newKind, out var otherCur); slotThumbnails.TryGetValue(newKind, out var otherThumb);
        originalImages.TryGetValue(newKind, out var otherOrig); Dust.Spots.TryGetValue(newKind, out var otherSpots); List<DustSpot>? otherDraft = null; dustDraft?.Spots.TryGetValue(newKind, out otherDraft);
        baseSources[newKind] = new ChannelSource(src, newKind);
        sources[newKind] = new ChannelSource(cur, newKind);
        if (slotThumbnails.TryGetValue(kind, out var th)) slotThumbnails[newKind] = th;
        if (originalImages.TryGetValue(kind, out var oi)) originalImages[newKind] = oi;
        if (Dust.Spots.TryGetValue(kind, out var sp)) Dust.Spots[newKind] = sp; else Dust.Spots.Remove(newKind);
        if (dustDraft != null) { if (dustDraft.Spots.TryGetValue(kind, out var dsp)) dustDraft.Spots[newKind] = dsp; else dustDraft.Spots.Remove(newKind); }
        if (otherBase != null && otherCur != null)
        {
            baseSources[kind] = new ChannelSource(otherBase, kind); sources[kind] = new ChannelSource(otherCur, kind);
            if (otherThumb != null) slotThumbnails[kind] = otherThumb; else slotThumbnails.Remove(kind);
            if (otherOrig != null) originalImages[kind] = otherOrig; else originalImages.Remove(kind);
            if (otherSpots != null) Dust.Spots[kind] = otherSpots; else Dust.Spots.Remove(kind);
            if (dustDraft != null) { if (otherDraft != null) dustDraft.Spots[kind] = otherDraft; else dustDraft.Spots.Remove(kind); }
        }
        else
        {
            baseSources.Remove(kind); sources.Remove(kind); slotThumbnails.Remove(kind);
            originalImages.Remove(kind); Dust.Spots.Remove(kind); dustDraft?.Spots.Remove(kind);
        }
        if (dustPreviewed.Remove(kind)) dustPreviewed.Add(newKind);
        if (DustEditing == kind) DustEditing = newKind;
        Notify(Change.SlotThumbnails); Notify(Change.Dust);
        SourcesChanged();
    }

    void SourcesChanged()
    {
        postStretchCache.Clear();
        RecordRecentSet();
        Notify(Change.Sources);
        if (sources.Count == 0)
        {
            Context = null; AnalysisSamples = new(); thumbnails.Clear();
            StatusText = "Drop FITS or TIFF images to begin.";
            Notify(Change.Thumbnails); Notify(Change.Context);
            return;
        }
        // Pick a sensible palette if the current one cannot be rendered.
        if (Renderer.MissingChannels(settings, AvailableKinds).Count > 0)
        {
            if (Palettes.Available(AvailableKinds).FirstOrDefault() is Palette p) UpdateSettings(s => s.Select(p));
        }
        // Already-stretched data (medians far above a linear sky level): default to no stretch.
        if (!stretchHintShown && sources.Values.All(s => s.Stats.Median > 0.08f) && settings.Stretch.Method == StretchMethod.AutoSTF && settings.Equals(CombineSettings.DefaultWith(settings.PaletteID)))
        {
            UpdateSettings(s => s.Stretch.Method = StretchMethod.None);
            stretchHintShown = true;
            statusHint = "stretch set to none because the data look already stretched";
            RebuildContext();
            return;
        }
        RebuildContext();
    }

    // ---- Recent sets -----------------------------------------------------------------------------

    void RecordRecentSet()
    {
        if (suppressRecent || IsLoading || sources.Count == 0) return;
        var entries = sources.Values.Where(s => s.Path != null).Select(s => new RecentSet.Entry(s.Kind, s.Path!)).ToList();
        // Only complete sets (every channel backed by a file) are remembered.
        if (entries.Count != sources.Count) return;
        RecentSets = recentStore.Record(entries);
        Notify(Change.RecentSets);
        SaveRecentState();
    }

    IDisposable? stateSaveTimer;

    /// <summary>Auto-saves the current settings/crop/orientation/presets with the recent set (debounced).</summary>
    void ScheduleRecentStateSave()
    {
        stateSaveTimer?.Dispose();
        stateSaveTimer = ui.Delay(TimeSpan.FromSeconds(0.7), SaveRecentState);
    }

    public void SaveRecentState()
    {
        System.Diagnostics.Debug.WriteLine("");
        stateSaveTimer?.Dispose(); stateSaveTimer = null;
        if (suppressRecent || IsLoading || sources.Count == 0) return;
        var paths = sources.Values.Where(s => s.Path != null).Select(s => s.Path!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (paths.Count != sources.Count) return;
        var doc = ProjectDocument();
        doc.Channels = new();
        RecentSets = recentStore.UpdateState(paths, doc);
        Notify(Change.RecentSets);
    }

    public void OpenRecentSet(RecentSet set)
    {
        var missing = set.Entries.Where(e => !File.Exists(e.Path)).ToList();
        if (missing.Count > 0)
        {
            ErrorMessage = "Files no longer exist:\n" + string.Join("\n", missing.Select(e => Path.GetFileName(e.Path)));
            RecentSets = recentStore.Remove(set.Id);
            Notify(Change.RecentSets);
            return;
        }
        RemoveAll();
        IsLoading = true;
        LoadingMessage = $"Loading {set.Name}…";
        var entries = set.Entries.ToList();
        Task.Run(() =>
        {
            var items = new List<ChannelItem>();
            var errors = new List<string>();
            Parallel.ForEach(entries, e =>
            {
                try
                {
                    var f = ImageLoader.Load(e.Path);
                    lock (items) items.Add(new ChannelItem(e.Kind, f.Planes[0], f.FileName, e.Path, f.FormatDescription));
                }
                catch (Exception ex) { lock (errors) errors.Add($"{Path.GetFileName(e.Path)}: {ex.Message}"); }
            });
            ui.Post(() =>
            {
                if (errors.Count > 0) ErrorMessage = string.Join("\n", errors);
                if (set.State is ProjectDocument st)
                {
                    Orientation = st.Orientation;
                    Settings = st.Settings.Clone();
                    Crop = st.Crop;
                    Dust = st.Dust.Clone();
                    Presets = st.Presets.Select(p => p.Clone()).ToList();
                    presetThumbnailCache.Clear();
                    Notify(Change.Presets); Notify(Change.Dust);
                }
                AddChannels(items);
            });
        });
    }

    public void RemoveRecentSet(RecentSet set) { RecentSets = recentStore.Remove(set.Id); Notify(Change.RecentSets); }
    public void ClearRecentSets() { recentStore.Clear(); RecentSets = new(); Notify(Change.RecentSets); }

    // ---- Crop ------------------------------------------------------------------------------------

    void CropChanged()
    {
        if (baseSources.Count == 0 || suppressCropRebuild) return;
        var cropNow = crop;
        var baseCopy = new Dictionary<ChannelKind, ChannelSource>(baseSources);
        IsLoading = true;
        LoadingMessage = cropNow == null ? "Removing crop…" : "Applying crop…";
        Task.Run(() =>
        {
            var kinds = baseCopy.Keys.ToArray();
            var outSources = new ChannelSource[kinds.Length];
            Parallel.For(0, kinds.Length, i => outSources[i] = cropNow == null ? baseCopy[kinds[i]] : new ChannelSource(baseCopy[kinds[i]], cropNow));
            var thumbs = outSources.Select(c => (c.Kind, SlotThumbnail(c))).ToArray();
            ui.Post(() =>
            {
                sources.Clear();
                foreach (var s in outSources) sources[s.Kind] = s;
                foreach (var (k, t) in thumbs) slotThumbnails[k] = t;
                IsLoading = false;
                postStretchCache.Clear();
                Notify(Change.SlotThumbnails); Notify(Change.Sources);
                RebuildContext();
                RequestFit();
            });
        });
    }

    public void AutoCrop()
    {
        if (sources.Count == 0) return;
        IsLoading = true; LoadingMessage = "Detecting empty borders…";
        var images = sources.Values.Select(s => s.Image).ToList();
        Task.Run(() =>
        {
            var r = AstroCore.AutoCrop.Detect(images);
            ui.Post(() =>
            {
                IsLoading = false;
                if (r is PixelRect rr && rr != PixelRect.Full(images[0].Width, images[0].Height))
                {
                    CropDraft = rr;
                    if (!CropEditing) Crop = rr;
                }
                else StatusText = "No empty borders found.";
            });
        });
    }

    public void BeginCropEditing()
    {
        if (DustEditing != null) CancelDustEditing();
        CropDraft = crop ?? (ImageSize is Size s ? new PixelRect((int)(s.Width * 0.1), (int)(s.Height * 0.1), (int)(s.Width * 0.8), (int)(s.Height * 0.8)) : null);
        CropEditing = true;
    }

    public void CommitCropEditing()
    {
        CropEditing = false;
        if (cropDraft is PixelRect d && ImageSize is Size size && d.Clamped((int)size.Width, (int)size.Height) is PixelRect c)
            Crop = c == PixelRect.Full((int)size.Width, (int)size.Height) ? null : c;
    }

    /// <summary>Keeps a draft around (the fields are hidden anyway) so bindings created while editing stay valid.</summary>
    public void CancelCropEditing() { CropEditing = false; CropDraft = crop ?? cropDraft; }

    public void ClearCrop() { Crop = null; CropDraft = null; CropEditing = false; }

    // ---- Context / rendering ---------------------------------------------------------------------

    void RebuildContext()
    {
        if (sources.Count == 0) return;
        int gen = ++contextGeneration;
        var srcs = new Dictionary<ChannelKind, ChannelSource>(sources);
        var norm = settings.Normalization.Clone();
        Task.Run(() =>
        {
            var ctx = RenderContext.Make(norm, srcs);
            var samples = Renderer.AnalysisSamples(srcs);
            ui.Post(() =>
            {
                if (gen != contextGeneration) return;
                Context = ctx;
                AnalysisSamples = samples;
                postStretchCache.Clear();
                Notify(Change.Context);
                ScheduleRender();
                ScheduleThumbnails(TimeSpan.FromSeconds(0.05));
                var any = srcs.Values.First();
                StatusText = $"{srcs.Count} channel{(srcs.Count == 1 ? "" : "s")} loaded · {any.Width} × {any.Height}" + (statusHint != null ? " · " + statusHint : "");
            });
        });
    }

    void SettingsChanged(CombineSettings old)
    {
        if (settings.Equals(old)) return;
        RegisterUndo(old);
        Notify(Change.Settings);
        if (!settings.Normalization.Equals(old.Normalization))
        {
            RebuildContext();
            ScheduleRecentStateSave();
            return;
        }
        ScheduleRender();
        bool paletteOnly = CombineSettings.DifferOnlyInPalette(settings, old);
        if (!paletteOnly) ScheduleThumbnails(TimeSpan.FromSeconds(0.6));
        ScheduleRecentStateSave();
    }

    void ViewportChanged()
    {
        if (Frame is not PreviewFrame f) { ScheduleRender(); return; }
        // Re-render when the visible area leaves the rendered region or the level should change.
        var visible = viewport.VisibleRect;
        int needsLevel = sources.Values.FirstOrDefault()?.Pyramid.LevelIndexForScale(viewport.Scale) ?? f.Level;
        var inter = Rect.Intersect(visible, FullRect);
        if (!(inter.IsEmpty || f.Rect.Contains(inter)) || needsLevel < f.Level || needsLevel > f.Level + 1) ScheduleRender();
        else if (ThumbnailRegion == ThumbnailRegion.CurrentView) ScheduleThumbnails(TimeSpan.FromSeconds(0.8));
    }

    public Rect FullRect => ImageSize is Size s ? new Rect(0, 0, s.Width, s.Height) : Rect.Empty;

    string PostStretchKey(CombineSettings s)
    {
        var k = new CombineSettings
        {
            Normalization = s.Normalization, Channels = s.Channels, Mode = s.Mode, Matrix = s.Matrix, PixelMath = s.PixelMath, Stretch = s.Stretch, StretchStage = s.StretchStage,
        };
        return SettingsJson.Canonical(k) + "|" + (crop?.ToString() ?? "-") + "|" + string.Join(",", AvailableKinds.SortedByRaw().Select(x => x.RawValue()));
    }

    public void ScheduleRender()
    {
        if (Context is not RenderContext ctx || sources.Count == 0) return;
        renderGeneration++;
        IsRendering = true;
        var shown = DisplayedSettings;
        var srcs = new Dictionary<ChannelKind, ChannelSource>(sources);
        Rect? visible = viewport.VisibleRect.IsEmpty || viewport.VisibleRect.Width <= 0 ? null : viewport.VisibleRect;
        int budget = HighQualityPreview ? 12_000_000 : 3_000_000;
        if (!shown.Normalization.Equals(settings.Normalization))
        {
            // Different normalisation than the live context: render with a context of its own.
            var snapshotCtx = RenderContext.Make(shown.Normalization, srcs);
            renderer.Submit(new PreviewRequest { Sources = srcs, Settings = shown, Context = snapshotCtx, AnalysisSamples = AnalysisSamples, FixedPostStretch = null, VisibleRect = visible, Scale = viewport.Scale, PixelBudget = budget, Generation = renderGeneration });
            return;
        }
        List<ResolvedStretch>? fixedStretch = shown.StretchStage == StretchStage.PostCombine && postStretchCache.TryGetValue(PostStretchKey(shown), out var ps) ? ps : null;
        renderer.Submit(new PreviewRequest { Sources = srcs, Settings = shown, Context = ctx, AnalysisSamples = AnalysisSamples, FixedPostStretch = fixedStretch, VisibleRect = visible, Scale = viewport.Scale, PixelBudget = budget, Generation = renderGeneration });
    }

    public void ScheduleThumbnails(TimeSpan delay)
    {
        thumbnailTimer?.Dispose();
        thumbnailTimer = ui.Delay(delay, RenderThumbnails);
    }

    void RenderThumbnails()
    {
        if (Context is not RenderContext ctx || sources.Count == 0) return;
        Rect? cropRect = crop is PixelRect c ? new Rect(c.X, c.Y, c.Width, c.Height) : null;
        Rect? region = ThumbnailRegion switch
        {
            ThumbnailRegion.CurrentView => viewport.VisibleRect.IsEmpty || viewport.VisibleRect.Width <= 0 ? cropRect : viewport.VisibleRect,
            _ => cropRect,
        };
        thumbnailGeneration = thumbnailer.Submit(new ThumbnailCoordinator.Job
        {
            Palettes = Palettes.Available(AvailableKinds), Sources = new Dictionary<ChannelKind, ChannelSource>(sources), Settings = settings.Clone(), Context = ctx,
            AnalysisSamples = AnalysisSamples, Region = region, MaxPixels = 90_000,
        });
    }

    // ---- Hover read-out --------------------------------------------------------------------------

    public void UpdateHover(Point? imagePoint)
    {
        if (imagePoint is not Point p || ImageSize is not Size size || p.X < 0 || p.Y < 0 || p.X >= size.Width || p.Y >= size.Height) { Hover = null; return; }
        int x = (int)p.X, y = (int)p.Y;
        var values = new List<(ChannelKind, float)>();
        foreach (var k in ChannelKinds.All) if (sources.TryGetValue(k, out var s)) values.Add((k, s.Image[x, y]));
        (float, float, float)? rgb = null;
        if (Frame is PreviewFrame f && f.Rect.Contains(p))
        {
            double sc = Math.Pow(2.0, -f.Level);
            int fx = Math.Min(f.Rgb.Width - 1, Math.Max(0, (int)((p.X - f.Rect.X) * sc))), fy = Math.Min(f.Rgb.Height - 1, Math.Max(0, (int)((p.Y - f.Rect.Y) * sc)));
            rgb = (f.Rgb.R[fx, fy], f.Rgb.G[fx, fy], f.Rgb.B[fx, fy]);
        }
        Hover = new HoverInfo(x, y, values, rgb);
    }

    // ---- Palette selection helpers ---------------------------------------------------------------

    public void Select(Palette palette) => UpdateSettings(s => s.Select(palette));

    public Palette? CurrentPalette => Palettes.Palette(settings.PaletteID);

    // ---- Dual band -------------------------------------------------------------------------------

    public void AddSplitChannels((ChannelKind kind, PlanarImage plane) first, (ChannelKind kind, PlanarImage plane) second, string fileName)
        => AddChannels([new ChannelItem(first.kind, first.plane, $"{fileName} [{first.kind.ShortName()}]", null, "split"), new ChannelItem(second.kind, second.plane, $"{fileName} [{second.kind.ShortName()}]", null, "split")]);

    // ---- Export ----------------------------------------------------------------------------------

    /// <summary>
    /// The preview as it is on screen (rendered region at the preview resolution), limited to the
    /// crop when one is set; null when nothing is rendered yet.
    /// </summary>
    public BitmapSource? PreviewImageForCopy
    {
        get
        {
            if (Frame is not PreviewFrame f) return null;
            if (crop is not PixelRect c) return f.Bitmap;
            var r = Rect.Intersect(new Rect(c.X, c.Y, c.Width, c.Height), f.Rect);
            if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return null;
            double s = f.Bitmap.PixelWidth / f.Rect.Width;
            int px0 = (int)Math.Round((r.X - f.Rect.X) * s), py0 = (int)Math.Round((r.Y - f.Rect.Y) * s);
            int pw = (int)Math.Round(r.Width * s), ph = (int)Math.Round(r.Height * s);
            px0 = Math.Max(0, Math.Min(px0, f.Bitmap.PixelWidth - 1)); py0 = Math.Max(0, Math.Min(py0, f.Bitmap.PixelHeight - 1));
            pw = Math.Max(1, Math.Min(pw, f.Bitmap.PixelWidth - px0)); ph = Math.Max(1, Math.Min(ph, f.Bitmap.PixelHeight - py0));
            var cropped = new CroppedBitmap(f.Bitmap, new Int32Rect(px0, py0, pw, ph));
            cropped.Freeze();
            return cropped;
        }
    }

    /// <summary>
    /// Puts the preview as it is on screen (cropped part only when a crop is set) on the clipboard as
    /// an image; returns false when nothing is rendered yet. <paramref name="sink"/> replaces the
    /// clipboard (tests).
    /// </summary>
    public bool CopyPreviewToClipboard(Action<BitmapSource>? sink = null)
    {
        if (PreviewImageForCopy is not BitmapSource bmp) return false;
        try { (sink ?? (b => Clipboard.SetImage(b)))(bmp); }
        catch (Exception e) { ErrorMessage = e.Message; return false; }
        StatusText = $"Preview copied ({bmp.PixelWidth} × {bmp.PixelHeight} px).";
        return true;
    }

    public void Export(string path, RasterFormat format, bool linear, Action<Exception?> completion)
    {
        if (Context is not RenderContext ctx || sources.Count == 0) { completion(RenderException.NoOutput()); return; }
        var s = settings.Clone();
        if (linear)
        {
            s.Stretch.Method = StretchMethod.None;
            s.Post = new PostSettings();
            s.ColorBands = new ColorBandsSettings();
            s.Detail = new DetailSettings();
            s.Luminance.Enabled = false;
            s.Stars.Enabled = false;
        }
        postStretchCache.TryGetValue(PostStretchKey(s), out var fixedStretch);
        var srcs = new Dictionary<ChannelKind, ChannelSource>(sources); var cropNow = crop;
        ExportProgress = 0; Notify(Change.Status);
        Task.Run(() =>
        {
            try
            {
                var img = Renderer.RenderFull(srcs, s, ctx, fixedStretch, cropNow, progress: p => ui.Post(() => { ExportProgress = p * 0.9; Notify(Change.Status); }));
                if (img == null) { ui.Post(() => { ExportProgress = null; Notify(Change.Status); completion(null); }); return; }
                RasterWriter.Write(img.Planes, path, format);
                ui.Post(() => { ExportProgress = null; StatusText = $"Exported {Path.GetFileName(path)}"; completion(null); });
            }
            catch (Exception e)
            {
                ui.Post(() => { ExportProgress = null; Notify(Change.Status); completion(e); });
            }
        });
    }

    /// <summary>Writes single channels (e.g. split dual-band planes) as FITS.</summary>
    public void ExportChannel(ChannelKind kind, string path, RasterFormat format)
    {
        if (!sources.TryGetValue(kind, out var s)) return;
        var plane = crop is PixelRect c ? s.Image.Cropped(c) : s.Image;
        RasterWriter.Write([plane], path, format);
    }

    // ---- Project persistence ---------------------------------------------------------------------

    public ProjectDocument ProjectDocument()
    {
        var doc = new ProjectDocument { Settings = settings.Clone(), Crop = crop, Orientation = Orientation, Dust = Dust.Clone(), Presets = Presets.Select(p => p.Clone()).ToList() };
        doc.Channels = sources.Values.OrderBy(s => s.Kind.RawValue(), StringComparer.Ordinal).Where(s => s.Path != null).Select(s => new ProjectDocument.ChannelRef(s.Kind, s.Path!)).ToList();
        return doc;
    }

    public void SaveProject(string path)
    {
        ProjectDocument().Save(path);
        ProjectPath = path;
        StatusText = $"Saved {Path.GetFileName(path)}";
        Notify(Change.Project);
    }

    public void OpenProject(string path)
    {
        ProjectDocument doc;
        try { doc = AstroCore.ProjectDocument.Load(path); }
        catch (Exception e) { ErrorMessage = $"Could not open project: {e.Message}"; return; }
        ProjectPath = path;
        Notify(Change.Project);
        RemoveAll();
        Presets = doc.Presets.Select(p => p.Clone()).ToList();
        presetThumbnailCache.Clear();
        Notify(Change.Presets);
        var refs = doc.Channels;
        Orientation = doc.Orientation;
        Dust = doc.Dust.Clone();
        Notify(Change.Dust);
        if (refs.Count == 0) { Settings = doc.Settings.Clone(); Crop = doc.Crop; return; }
        IsLoading = true;
        LoadingMessage = "Loading project…";
        Task.Run(() =>
        {
            var items = new List<ChannelItem>();
            var errors = new List<string>();
            Parallel.ForEach(refs, r =>
            {
                try
                {
                    var f = ImageLoader.Load(r.Path);
                    lock (items) items.Add(new ChannelItem(r.Kind, f.Planes[0], f.FileName, r.Path, f.FormatDescription));
                }
                catch (Exception ex) { lock (errors) errors.Add($"{Path.GetFileName(r.Path)}: {ex.Message}"); }
            });
            ui.Post(() =>
            {
                if (errors.Count > 0) ErrorMessage = string.Join("\n", errors);
                Settings = doc.Settings.Clone();
                Crop = doc.Crop;
                AddChannels(items);
            });
        });
    }

    // ---- Undo ------------------------------------------------------------------------------------

    void RegisterUndo(CombineSettings old)
    {
        if (restoring) return;
        if (!undoPending)
        {
            undoPending = true;
            undoStack.Push(old);
            redoStack.Clear();
        }
        undoTimer?.Dispose();
        undoTimer = ui.Delay(TimeSpan.FromSeconds(0.8), () => undoPending = false);
    }

    public void Undo()
    {
        if (undoStack.Count == 0) return;
        undoTimer?.Dispose(); undoPending = false;
        var target = undoStack.Pop();
        redoStack.Push(settings);
        restoring = true;
        try { Settings = target; } finally { restoring = false; }
    }

    public void Redo()
    {
        if (redoStack.Count == 0) return;
        undoTimer?.Dispose(); undoPending = false;
        var target = redoStack.Pop();
        undoStack.Push(settings);
        restoring = true;
        try { Settings = target; } finally { restoring = false; }
    }

    // ---- Loading one file into a slot ------------------------------------------------------------

    /// <summary>Loads one file straight into a given slot, ignoring automatic detection.</summary>
    public void LoadFile(string path, ChannelKind kind)
    {
        if (!ImageLoader.IsSupported(path)) { ErrorMessage = $"Unsupported file: {Path.GetFileName(path)}"; return; }
        IsLoading = true;
        LoadingMessage = $"Loading {Path.GetFileName(path)}…";
        Task.Run(() =>
        {
            try
            {
                var f = ImageLoader.Load(path);
                ui.Post(() =>
                {
                    if (RequiredFileSize is Size size && ((int)size.Width != f.Width || (int)size.Height != f.Height))
                    {
                        IsLoading = false;
                        ErrorMessage = $"{f.FileName}: {f.Width}×{f.Height} does not match the loaded channels ({(int)size.Width}×{(int)size.Height}).";
                        return;
                    }
                    if (f.IsColor) { IsLoading = false; PendingColorFile = new PendingFile(f); }
                    else AddChannels([new ChannelItem(kind, f.Planes[0], f.FileName, f.Path, f.FormatDescription)]);
                });
            }
            catch (Exception e) { ui.Post(() => { IsLoading = false; ErrorMessage = e.Message; }); }
        });
    }

    // ---- Dust spots ------------------------------------------------------------------------------

    /// <summary>Spots the editor currently shows (the draft while editing, else the applied ones).</summary>
    public List<DustSpot> DustSpots(ChannelKind kind) => (dustDraft ?? Dust).SpotsFor(kind);

    /// <summary>
    /// Radius given to a new spot when nothing is found around the click (small: the fit grows it to
    /// the shadow, and a stray click must not cost a huge window).
    /// </summary>
    public double DefaultDustRadius => ImageSize is Size s ? Math.Max(12, Math.Min(s.Width, s.Height) / 150) : 20;

    /// <summary>Opens the dust editor on a channel (the preview shows that channel alone, with the draft spots).</summary>
    public void BeginDustEditing(ChannelKind kind)
    {
        if (!sources.ContainsKey(kind)) return;
        if (CropEditing) CancelCropEditing();
        // A press-and-hold peek left behind by the slot menu would otherwise override the edited channel.
        PeekChannel = null;
        if (DustEditing == null) { dustDraft = Dust.Clone(); dustBypass = false; }
        DustEditing = kind;
        selectedDustSpot = null;
        Notify(Change.Dust);
        ScheduleDustPreview();
    }

    /// <summary>
    /// Switches the editor to another loaded channel, keeping every draft. The channel is always
    /// re-previewed: its draft may have changed while another one was shown (Copy to all) and the
    /// Original toggle applies to the channel on screen only.
    /// </summary>
    public void SwitchDustChannel(ChannelKind kind)
    {
        if (DustEditing == null || DustEditing == kind || !sources.ContainsKey(kind)) return;
        PeekChannel = null;
        DustEditing = kind;
        selectedDustSpot = null;
        Notify(Change.Dust);
        ScheduleDustPreview();
    }

    /// <summary>Adds a spot at an image point, fitted to the shadow found there (plain spot of the default radius when nothing is found).</summary>
    public void AddDustSpot(Point p)
    {
        if (DustEditing is not ChannelKind k || dustDraft is not DustSettings d) return;
        var orig = originalImages.TryGetValue(k, out var o) ? o : baseSources.GetValueOrDefault(k)?.Image;
        if (orig == null) return;
        var spot = d.NewSpot(p.X, p.Y, DefaultDustRadius);
        if (DustRemoval.Fit(orig, p.X, p.Y, DefaultDustRadius, d.Smoothness) is DustSpot f) { spot.X = f.X; spot.Y = f.Y; spot.Radius = f.Radius; }
        var nd = d.Clone();
        if (!nd.Spots.TryGetValue(k, out var list)) nd.Spots[k] = list = new List<DustSpot>();
        list.Add(spot);
        selectedDustSpot = spot.Id;
        DustDraft = nd;
    }

    /// <summary>Replaces the spots of the edited channel (moves / resizes from the preview).</summary>
    public void UpdateDustSpots(List<DustSpot> spots)
    {
        if (DustEditing is not ChannelKind k || dustDraft == null) return;
        var nd = dustDraft.Clone();
        nd.Spots[k] = spots.Select(s => s.Clone()).ToList();
        DustDraft = nd;
    }

    public void RemoveSelectedDustSpot()
    {
        if (DustEditing is not ChannelKind k || selectedDustSpot is not Guid id || dustDraft == null) return;
        var nd = dustDraft.Clone();
        if (nd.Spots.TryGetValue(k, out var list)) list.RemoveAll(s => s.Id == id);
        selectedDustSpot = null;
        DustDraft = nd;
    }

    /// <summary>Re-runs the fit from the centre of the selected spot (after moving it by hand, say).</summary>
    public void RefitSelectedDustSpot()
    {
        if (DustEditing is not ChannelKind k || selectedDustSpot is not Guid id || dustDraft == null) return;
        var orig = originalImages.TryGetValue(k, out var o) ? o : baseSources.GetValueOrDefault(k)?.Image;
        if (orig == null || !dustDraft.Spots.TryGetValue(k, out var list)) return;
        int i = list.FindIndex(s => s.Id == id);
        if (i < 0) return;
        var s = list[i];
        if (DustRemoval.Fit(orig, s.X, s.Y, s.Radius / 2, s.Smoothness) is not DustSpot f) { StatusText = "No shadow found around the spot."; return; }
        var nd = dustDraft.Clone();
        var ns = nd.Spots[k][i]; ns.X = f.X; ns.Y = f.Y; ns.Radius = f.Radius;
        DustDraft = nd;
    }

    /// <summary>The selected spot of the edited channel (null when none).</summary>
    public DustSpot? SelectedDustSpotValue
    {
        get
        {
            if (DustEditing is not ChannelKind k || selectedDustSpot is not Guid id) return null;
            return dustDraft?.SpotsFor(k).FirstOrDefault(s => s.Id == id);
        }
    }

    /// <summary>Replaces the selected spot (parameter edits from the toolbar).</summary>
    public void UpdateSelectedDustSpot(DustSpot spot)
    {
        if (DustEditing is not ChannelKind k || dustDraft == null || !dustDraft.Spots.TryGetValue(k, out var list)) return;
        int i = list.FindIndex(s => s.Id == spot.Id);
        if (i < 0) return;
        var nd = dustDraft.Clone();
        nd.Spots[k][i] = spot.Clone();
        DustDraft = nd;
    }

    /// <summary>Changes the parameters given to new spots.</summary>
    public void UpdateDustDefaults(Action<DustSettings> edit)
    {
        if (dustDraft == null) return;
        var nd = dustDraft.Clone();
        edit(nd);
        DustDraft = nd;
    }

    public void ClearDustSpots()
    {
        if (DustEditing is not ChannelKind k || dustDraft == null) return;
        var nd = dustDraft.Clone();
        nd.Spots[k] = new List<DustSpot>();
        selectedDustSpot = null;
        DustDraft = nd;
    }

    /// <summary>Copies the spots of the edited channel to every other loaded channel (dust on the sensor window shadows every filter at the same place).</summary>
    public void CopyDustSpotsToAllChannels()
    {
        if (DustEditing is not ChannelKind k || dustDraft == null) return;
        var nd = dustDraft.Clone();
        var spots = nd.SpotsFor(k);
        foreach (var other in sources.Keys) if (other != k) nd.Spots[other] = spots.Select(s => { var c = s.Clone(); c.Id = Guid.NewGuid(); return c; }).ToList();
        DustDraft = nd;
    }

    /// <summary>
    /// Applies the draft: channels whose spots (or parameters) changed are healed from the original and
    /// fully re-analysed; everything downstream is recomputed once.
    /// </summary>
    public void CommitDustEditing()
    {
        if (dustDraft is not DustSettings draft) { DustEditing = null; Notify(Change.Dust); return; }
        dustPreviewTimer?.Dispose();
        dustPreviewGeneration++;
        var changed = sources.Keys.Where(k => !draft.SpotsFor(k).SequenceEqual(Dust.SpotsFor(k))).ToHashSet();
        Dust = draft;
        dustDraft = null;
        DustEditing = null;
        selectedDustSpot = null;
        dustBypass = false;
        Notify(Change.Dust);
        changed.UnionWith(dustPreviewed);
        dustPreviewed.Clear();
        RebuildHealed(changed);
        ScheduleRecentStateSave();
    }

    /// <summary>Drops the draft and puts the previewed channels back to their applied state.</summary>
    public void CancelDustEditing()
    {
        dustPreviewTimer?.Dispose();
        dustPreviewGeneration++;
        dustDraft = null;
        DustEditing = null;
        selectedDustSpot = null;
        dustBypass = false;
        Notify(Change.Dust);
        var previewed = dustPreviewed.ToHashSet();
        dustPreviewed.Clear();
        if (previewed.Count == 0) ScheduleRender(); else RebuildHealed(previewed);
    }

    /// <summary>
    /// Live preview of the draft on the edited channel: heals from the original and swaps the source in
    /// with the statistics it already has (statistics barely move with a few local spots), so only a
    /// render follows. Debounced and latest-wins.
    /// </summary>
    void ScheduleDustPreview()
    {
        dustPreviewTimer?.Dispose();
        dustPreviewTimer = ui.Delay(TimeSpan.FromSeconds(0.08), DustPreview);
    }

    void DustPreview()
    {
        if (DustEditing is not ChannelKind k || dustDraft is not DustSettings d || !originalImages.TryGetValue(k, out var orig) || !baseSources.TryGetValue(k, out var baseSrc)) return;
        int gen = ++dustPreviewGeneration;
        var spots = dustBypass ? new List<DustSpot>() : d.SpotsFor(k); var cropNow = crop;
        dustPreviewed.Add(k);
        Task.Run(() =>
        {
            var healed = DustRemoval.Heal(orig, spots);
            var b = new ChannelSource(baseSrc, healed);
            var c = cropNow == null ? b : new ChannelSource(b, cropNow);
            ui.Post(() =>
            {
                if (gen != dustPreviewGeneration || DustEditing != k) return;
                baseSources[k] = b;
                sources[k] = c;
                Notify(Change.Sources);
                ScheduleRender();
            });
        });
    }

    /// <summary>Heals <paramref name="kinds"/> from their originals with the applied spots and rebuilds their sources (statistics, star mask, thumbnails), then the render context.</summary>
    void RebuildHealed(HashSet<ChannelKind> kinds)
    {
        var todo = kinds.Where(k => baseSources.ContainsKey(k) && originalImages.ContainsKey(k)).ToArray();
        if (todo.Length == 0) { ScheduleRender(); return; }
        var baseCopy = new Dictionary<ChannelKind, ChannelSource>(baseSources); var originals = new Dictionary<ChannelKind, PlanarImage>(originalImages); var dust = Dust.Clone(); var cropNow = crop;
        IsLoading = true;
        LoadingMessage = "Healing dust spots…";
        Task.Run(() =>
        {
            var outB = new ChannelSource[todo.Length]; var outC = new ChannelSource[todo.Length];
            Parallel.For(0, todo.Length, i =>
            {
                var k = todo[i]; var old = baseCopy[k];
                var healed = DustRemoval.Heal(originals[k], dust.SpotsFor(k));
                var b = new ChannelSource(k, healed, old.FileName, old.Path, old.FormatDescription);
                outB[i] = b; outC[i] = cropNow == null ? b : new ChannelSource(b, cropNow);
            });
            var thumbs = outC.Select(c => (c.Kind, SlotThumbnail(c))).ToArray();
            ui.Post(() =>
            {
                for (int i = 0; i < todo.Length; i++) { baseSources[todo[i]] = outB[i]; sources[todo[i]] = outC[i]; }
                foreach (var (k, t) in thumbs) slotThumbnails[k] = t;
                IsLoading = false;
                Notify(Change.SlotThumbnails);
                SourcesChanged();
            });
        });
    }
}
