using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Orientation = Avalonia.Layout.Orientation;
using Avalonia.Media;
using Stretch = Avalonia.Media.Stretch;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AstroCore;
using PixelRect = AstroCore.PixelRect;
using CombineColors.Model;
using CombineColors.Views;

namespace CombineColors;

/// <summary>Window layout: channel column | preview | palettes + presets, with the processing strip underneath.</summary>
public sealed class MainWindow : Window
{
    readonly ProjectModel model;
    readonly DockPanel Root = new() { Background = AppTheme.PreviewBackground };
    readonly PreviewControl preview = new();
    readonly StatusBarView statusBar;
    readonly ChannelStripView channels;
    readonly PaletteGridView palettes;
    readonly ControlsStripView controls;
    readonly Grid previewArea = new();
    readonly Border topToolbar = new();
    readonly Border zoomToolbar = new();
    readonly Border emptyState = new();
    readonly Grid main = new();
    readonly DockPanel right = new();
    readonly GridSplitter splitter;
    GridLength rightWidth = new(460, GridUnitType.Pixel);
    MenuItem? panesItem;
    MenuItem? recentMenu, undoItem, redoItem, hqItem, limitsMenu, closeItem, rotateItem, flipHItem, flipVItem, trimItem;
    bool dialogPending;
    /// <summary>Command key: ⌘ on macOS, Ctrl elsewhere.</summary>
    static readonly KeyModifiers Cmd = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    public MainWindow(ProjectModel model)
    {
        this.model = model;
        Title = "CombineColors"; Width = 1500; Height = 950; MinWidth = 1100; MinHeight = 700;
        Background = AppTheme.PreviewBackground;
        Content = Root;
        try { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://CombineColors/Resources/AppIcon-256.png"))); } catch { }
        statusBar = new StatusBarView(model);
        channels = new ChannelStripView(model, this);
        palettes = new PaletteGridView(model);
        controls = new ControlsStripView(model);
        palettes.MatrixRequested += async () => await MatrixEditorWindow.Show(this, model);
        palettes.PixelMathRequested += async () => await PixelMathEditorWindow.Show(this, model);
        channels.RemoveStarsRequested += ShowStarRemoval;

        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top); Root.Children.Add(menu);
        DockPanel.SetDock(statusBar, Dock.Bottom); Root.Children.Add(statusBar);
        var controlsSep = new Separator { Margin = new Thickness(0) }; DockPanel.SetDock(controlsSep, Dock.Bottom);
        DockPanel.SetDock(controls, Dock.Bottom); Root.Children.Add(controls); Root.Children.Add(controlsSep);

        main.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        main.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 440 });
        main.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        main.ColumnDefinitions.Add(new ColumnDefinition(460, GridUnitType.Pixel) { MinWidth = 380, MaxWidth = 760 });
        Grid.SetColumn(channels, 0); main.Children.Add(channels);
        BuildPreviewArea();
        Grid.SetColumn(previewArea, 1); main.Children.Add(previewArea);
        splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = AppTheme.PanelBorder, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 2); main.Children.Add(splitter);
        var presets = new PresetBarView(model, this);
        DockPanel.SetDock(presets, Dock.Bottom); right.Children.Add(presets);
        var presetSep = new Separator { Margin = new Thickness(0) }; DockPanel.SetDock(presetSep, Dock.Bottom); right.Children.Add(presetSep);
        // Channel mix between the palettes and the presets: weights and palette make the combination together.
        var mix = new ChannelMixView(model); DockPanel.SetDock(mix, Dock.Bottom); right.Children.Add(mix);
        var mixSep = new Separator { Margin = new Thickness(0) }; DockPanel.SetDock(mixSep, Dock.Bottom); right.Children.Add(mixSep);
        right.Children.Add(palettes);
        Grid.SetColumn(right, 3); main.Children.Add(right);
        Root.Children.Add(main);

        WirePreview();
        WireSpacePan();
        // Tab (no modifiers, not while typing) hides / shows the side panes and the crop / zoom toolbars.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None || IsTextInputFocused()) return;
            PanesHidden = !PanesHidden; e.Handled = true;
        }, RoutingStrategies.Tunnel);
        model.Changed += OnChanged;
        model.FitRequested += () => Dispatcher.UIThread.Post(() => preview.Perform(ZoomAction.Fit));
        DropHelpers.Accept(this, OpenPaths);
        Closing += (_, _) => { model.SaveRecentState(); model.Preferences.Flush(); };
        UpdateTitle();
        RefreshToolbars();
    }

    /// <summary>
    /// The side panes (channel column, palettes + channel mix + presets) and the crop / zoom toolbars are
    /// hidden, leaving the preview and the processing strip (Tab, View menu). Editor toolbars and the crop's
    /// Apply / Cancel stay while open. Never saved: every launch starts with them shown.
    /// </summary>
    public bool PanesHidden
    {
        get => panesHidden;
        set
        {
            if (panesHidden == value) return;
            panesHidden = value;
            var col = main.ColumnDefinitions[3];
            if (value) { rightWidth = col.Width; col.MinWidth = 0; col.Width = new GridLength(0); }
            else { col.Width = rightWidth; col.MinWidth = 380; }
            channels.IsVisible = !value; splitter.IsVisible = !value; right.IsVisible = !value;
            if (panesItem != null) panesItem.Header = value ? "Show Side Panels" : "Hide Side Panels";
            RefreshToolbars();
        }
    }
    bool panesHidden;

    /// <summary>
    /// View ▸ Interface Size: the UI scale (fonts, panes, controls), saved and applied when the app starts (Avalonia takes
    /// the scale before the window exists), so a change offers to restart. Automatic = the screen's own density.
    /// </summary>
    MenuItem InterfaceSizeMenu()
    {
        double saved = model.Preferences.GetDouble(LinuxScaling.PreferenceKey) ?? 0;
        var menu = new MenuItem { Header = $"Interface Size (now {RenderScaling * 100:0} %)" };
        ToolTip.SetTip(menu, "Scales the fonts, panes and controls. Automatic follows the screen (now from " + LinuxScaling.Source + ").");
        void Add(string header, double value)
        {
            var it = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = value == saved || (value == 0 && !LinuxScaling.Choices.Contains(saved)) };
            it.Click += async (_, _) =>
            {
                model.Preferences.Set(LinuxScaling.PreferenceKey, value);
                model.Preferences.Flush();
                if (await DialogBase.Confirm(this, "Interface size", "The new interface size applies when CombineColors starts again. Restart now? (The current set reopens from Recent sets.)", "Restart now", "Later"))
                    Restart();
            };
            menu.Items.Add(it);
        }
        Add("Automatic", 0);
        menu.Items.Add(new Separator());
        foreach (var c in LinuxScaling.Choices) Add($"{c * 100:0} %", c);
        return menu;
    }

    /// <summary>Starts a new instance with the same command line and closes this one.</summary>
    void Restart()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var exe = Environment.ProcessPath ?? args[0];
            var psi = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
            // Run through `dotnet CombineColors.dll`: the dll is the first argument.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) psi.ArgumentList.Add(args[0]);
            foreach (var a in args.Skip(1)) psi.ArgumentList.Add(a);
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception e) { model.ErrorMessage = "Could not restart: " + e.Message; return; }
        Close();
    }

    /// <summary>Files passed on the command line, dropped or chosen: projects open, images load.</summary>
    public void OpenPaths(IReadOnlyList<string> paths)
    {
        var projects = paths.Where(p => Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase)).ToList();
        if (projects.Count > 0) model.OpenProject(projects[0]);
        var images = DropHelpers.ExpandDirectories(paths.Where(p => !Path.GetExtension(p).Equals(".json", StringComparison.OrdinalIgnoreCase)));
        if (images.Count > 0) model.LoadFiles(images);
    }

    // ---- Menu ------------------------------------------------------------------------------------

    Menu BuildMenu()
    {
        var menu = new Menu { Background = AppTheme.PanelBackground };
        MenuItem Item(string header, Action action, Key key = Key.None, KeyModifiers mods = KeyModifiers.None)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => action();
            if (key != Key.None)
            {
                var gesture = new KeyGesture(key, mods);
                m.InputGesture = gesture;
                KeyBindings.Add(new KeyBinding { Gesture = gesture, Command = new RelayCommand(action) });
            }
            return m;
        }
        var file = new MenuItem { Header = "_File" };
        file.Items.Add(Item("Add Images…", async () => OpenPaths(await Panels.OpenImages(this)), Key.O, Cmd));
        file.Items.Add(Item("Open Project…", async () => { if (await Panels.OpenProject(this) is string p) model.OpenProject(p); }, Key.O, Cmd | KeyModifiers.Shift));
        recentMenu = new MenuItem { Header = "Open Recent Set" };
        file.Items.Add(recentMenu);
        file.Items.Add(new Separator());
        file.Items.Add(Item("Save Project", SaveProject, Key.S, Cmd));
        file.Items.Add(Item("Save Project As…", SaveProjectAs, Key.S, Cmd | KeyModifiers.Shift));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Export Combined Image…", async () => { if (model.Sources.Count > 0) await ExportWindow.Show(this, model); }, Key.E, Cmd));
        file.Items.Add(new Separator());
        closeItem = Item("Close Image", model.CloseImage, Key.W, Cmd);
        file.Items.Add(closeItem);
        file.Items.Add(new Separator());
        file.Items.Add(Item(OperatingSystem.IsMacOS() ? "Quit" : "Exit", Close, Key.Q, Cmd));
        menu.Items.Add(file);
        var edit = new MenuItem { Header = "_Edit" };
        undoItem = Item("Undo", model.Undo, Key.Z, Cmd);
        redoItem = Item("Redo", model.Redo, Key.Y, Cmd);
        edit.Items.Add(undoItem); edit.Items.Add(redoItem);
        edit.Items.Add(new Separator());
        edit.Items.Add(Item("Copy Preview Image", () => model.CopyPreviewToClipboard(), Key.C, Cmd | KeyModifiers.Shift));
        menu.Items.Add(edit);
        var tools = new MenuItem { Header = "_Tools" };
        tools.Items.Add(Item("Remove Stars with StarNet2…", () =>
        {
            // Star removal works on the Background tab's channels.
            if (model.ActiveLayer == ImageLayer.Stars) model.SwitchLayer(ImageLayer.Background);
            if (model.ActiveLayer == ImageLayer.Background) ShowStarRemoval();
        }));
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("Background Tab", () => model.SwitchLayer(ImageLayer.Background), Key.D1, Cmd));
        view.Items.Add(Item("Stars Tab", () => model.SwitchLayer(ImageLayer.Stars), Key.D2, Cmd));
        view.Items.Add(new Separator());
        panesItem = Item("Hide Side Panels", () => PanesHidden = !PanesHidden);
        panesItem.InputGesture = new KeyGesture(Key.Tab);   // shown only: the window handles Tab itself
        view.Items.Add(panesItem);
        if (OperatingSystem.IsLinux()) view.Items.Add(InterfaceSizeMenu());
        view.Items.Add(new Separator());
        view.Items.Add(Item("Fit to Window", () => preview.Perform(ZoomAction.Fit), Key.D0, Cmd));
        // Plain "1" is handled by the preview (shown here, not bound: Cmd+1 is the Background tab).
        var actual = Item("Actual Size", () => preview.Perform(ZoomAction.ActualSize));
        actual.InputGesture = new KeyGesture(Key.D1);
        view.Items.Add(actual);
        hqItem = new MenuItem { Header = "High Quality Preview", ToggleType = MenuItemToggleType.CheckBox };
        hqItem.Click += (_, _) => model.HighQualityPreview = hqItem.IsChecked;
        view.Items.Add(hqItem);
        limitsMenu = new MenuItem { Header = "Crop Limits" };
        foreach (var m in CropOverlayModes.All)
        {
            var mode = m;
            var it = new MenuItem { Header = m.DisplayName(), ToggleType = MenuItemToggleType.Radio };
            it.Click += (_, _) => model.CropOverlay = mode;
            limitsMenu.Items.Add(it);
        }
        view.Items.Add(limitsMenu);
        view.Items.Add(new Separator());
        rotateItem = Item("Rotate Clockwise", model.RotateClockwise, Key.R, Cmd); view.Items.Add(rotateItem);
        flipHItem = Item("Flip Horizontal", model.FlipHorizontal, Key.H, Cmd | KeyModifiers.Shift); view.Items.Add(flipHItem);
        flipVItem = Item("Flip Vertical", model.FlipVertical, Key.V, Cmd | KeyModifiers.Shift); view.Items.Add(flipVItem);
        trimItem = Item("Auto Trim Borders", model.AutoCrop); view.Items.Add(trimItem);
        menu.Items.Add(view);
        menu.Items.Add(tools);
        file.SubmenuOpened += (_, _) => closeItem!.IsEnabled = !((model.Sources.Count == 0 && !model.HasStars && model.ProjectPath == null) || model.IsRemovingStars);
        view.SubmenuOpened += (_, _) =>
        {
            bool can = model.Sources.Count > 0 && !model.IsLoading && model.EditingTool == null;
            rotateItem!.IsEnabled = can; flipHItem!.IsEnabled = can; flipVItem!.IsEnabled = can;
            trimItem!.IsEnabled = model.Sources.Count > 0 && model.EditingTool == null;
        };
        var help = new MenuItem { Header = "_Help" };
        help.Items.Add(Item("About CombineColors", async () => await AboutWindow.Show(this)));
        menu.Items.Add(help);
        RefreshRecentMenu();
        return menu;
    }

    void RefreshRecentMenu()
    {
        if (recentMenu == null) return;
        recentMenu.Items.Clear();
        foreach (var set in model.RecentSets)
        {
            var s = set;
            var it = new MenuItem { Header = $"{set.Name}  ({set.Summary})" };
            it.Click += (_, _) => model.OpenRecentSet(s);
            recentMenu.Items.Add(it);
        }
        if (model.RecentSets.Count == 0) recentMenu.Items.Add(new MenuItem { Header = "No recent sets", IsEnabled = false });
        recentMenu.Items.Add(new Separator());
        var clear = new MenuItem { Header = "Clear Menu", IsEnabled = model.RecentSets.Count > 0 };
        clear.Click += (_, _) => model.ClearRecentSets();
        recentMenu.Items.Add(clear);
    }

    void SaveProject()
    {
        if (model.Sources.Count == 0) return;
        if (model.ProjectPath is string p) { try { model.SaveProject(p); } catch (Exception e) { model.ErrorMessage = e.Message; } }
        else SaveProjectAs();
    }

    async void SaveProjectAs()
    {
        if (model.Sources.Count == 0) return;
        var path = await Panels.Save(this, "combination.json", Panels.Project, "Save the project (channel paths, crop and all settings)");
        if (path == null) return;
        try { model.SaveProject(path); } catch (Exception e) { model.ErrorMessage = e.Message; }
    }

    /// <summary>Opens the star removal (StarNet2) dialog on the Background channels.</summary>
    void ShowStarRemoval()
    {
        if (model.Sources.Count == 0 || model.ActiveLayer != ImageLayer.Background) return;
        _ = StarRemovalWindow.Show(this, model);
    }

    void UpdateTitle() => Title = model.ProjectPath is string p ? $"{Path.GetFileNameWithoutExtension(p)} — CombineColors" : "CombineColors";

    // ---- Preview area ----------------------------------------------------------------------------

    void BuildPreviewArea()
    {
        previewArea.Children.Add(preview);
        emptyState.HorizontalAlignment = HorizontalAlignment.Center; emptyState.VerticalAlignment = VerticalAlignment.Center;
        previewArea.Children.Add(emptyState);
        topToolbar.HorizontalAlignment = HorizontalAlignment.Right; topToolbar.VerticalAlignment = VerticalAlignment.Top; topToolbar.Margin = new Thickness(8);
        topToolbar.Background = AppTheme.OverlayBackground; topToolbar.CornerRadius = new CornerRadius(6); topToolbar.Padding = new Thickness(6);
        previewArea.Children.Add(topToolbar);
        zoomToolbar.HorizontalAlignment = HorizontalAlignment.Right; zoomToolbar.VerticalAlignment = VerticalAlignment.Bottom; zoomToolbar.Margin = new Thickness(8);
        zoomToolbar.Background = AppTheme.OverlayBackground; zoomToolbar.CornerRadius = new CornerRadius(6); zoomToolbar.Padding = new Thickness(6);
        previewArea.Children.Add(zoomToolbar);
        BuildZoomToolbar();
    }

    void BuildZoomToolbar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var hq = new CheckRow("HQ", () => model.HighQualityPreview, v => model.HighQualityPreview = v, "High quality preview (more pixels)");
        hq.Margin = new Thickness(0, 0, 6, 0); hq.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(hq);
        row.Children.Add(Ui.IconButton("−", () => preview.Perform(ZoomAction.ZoomOut), "Zoom out"));
        row.Children.Add(Ui.IconButton("+", () => preview.Perform(ZoomAction.ZoomIn), "Zoom in"));
        row.Children.Add(Ui.SmallButton("Fit", () => preview.Perform(ZoomAction.Fit)));
        row.Children.Add(Ui.SmallButton("1:1", () => preview.Perform(ZoomAction.ActualSize)));
        var copy = Ui.SmallButton("Copy", () => model.CopyPreviewToClipboard(), "Copy the preview as shown to the clipboard");
        copy.Margin = new Thickness(0);
        row.Children.Add(copy);
        zoomToolbar.Child = row;
    }

    void WirePreview()
    {
        preview.ViewportChanged += (rect, scale) => { model.Viewport = new Viewport(rect, scale); statusBar.ZoomPercent = scale * 100; };
        preview.Hover += p => model.UpdateHover(p);
        preview.DustMaxRadiusChanged += r => model.DustBrightMaxRadius = r;
        preview.CropAngleChange += a => model.SetCropAngle(a);
        preview.CropDraftChanged += r => model.CropDraft = r;
        preview.DustSpotsChanged += spots => model.UpdateDustSpots(spots);
        preview.DustAdd += p => model.AddDustSpot(p);
        preview.DustSelect += id => model.SelectedDustSpot = id;
        preview.DustDelete += model.RemoveSelectedDustSpot;
        preview.GradientGestureChanged += g => model.SetGradientGesture(g);
        preview.GradientRegionsChanged += r => model.UpdateGradientRegions(r);
        preview.GradientRegionAdd += p => model.AddGradientRegion(p);
        preview.GradientRegionSelect += id => model.SelectedGradientRegion = id;
        preview.GradientDelete += model.DeleteGradientSelection;
        preview.GradientSampleAdd += p => model.AddGradientSample(p);
        preview.GradientSamplesChanged += s => model.UpdateGradientSamples(s);
        preview.GradientSampleSelect += id => model.SelectedGradientSample = id;
        preview.StrokeBegin += model.BeginCorrectionStroke;
        preview.Paint += (a, b, invert) => model.PaintCorrection(a, b, invert);
        preview.BrushRadiusChanged += r => model.CorrectionBrushRadius = r;
        // Fit first; from the fitted view, 1:1.
        preview.DoubleClick += () => preview.Perform(preview.IsFitted ? ZoomAction.ActualSize : ZoomAction.Fit);
        preview.KeyDown += (_, e) =>
        {
            if (model.DustEditing != null)
            {
                if (e.Key is Key.Return or Key.Enter) { model.CommitDustEditing(); e.Handled = true; }
                else if (e.Key == Key.Escape) { model.CancelDustEditing(); e.Handled = true; }
                else if (e.Key == Key.B) { model.DustBypass = !model.DustBypass; e.Handled = true; }
            }
            else if (model.CorrectionEditing != null)
            {
                bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
                if (e.Key is Key.Return or Key.Enter) { model.CommitCorrectionEditing(); e.Handled = true; }
                else if (e.Key == Key.Escape) { model.CancelCorrectionEditing(); e.Handled = true; }
                else if (e.Key == Key.B) { model.CorrectionBypass = !model.CorrectionBypass; e.Handled = true; }
                else if (e.Key == Key.M) { model.CorrectionShowMap = !model.CorrectionShowMap; e.Handled = true; }
                else if (e.Key == Key.D) { model.CorrectionDarken = !model.CorrectionDarken; e.Handled = true; }
                else if (e.Key == Key.Z && ctrl) { model.UndoCorrection(); e.Handled = true; }
            }
            else if (model.GradientEditing != null)
            {
                if (e.Key is Key.Return or Key.Enter) { model.CommitGradientEditing(); e.Handled = true; }
                else if (e.Key == Key.Escape) { model.CancelGradientEditing(); e.Handled = true; }
                else if (e.Key == Key.B) { model.GradientView = model.GradientView == GradientView.Original ? GradientView.Corrected : GradientView.Original; e.Handled = true; }
                else if (e.Key == Key.M) { model.GradientView = model.GradientView == GradientView.Background ? GradientView.Corrected : GradientView.Background; e.Handled = true; }
                else if (e.Key == Key.P) { model.GradientProtectMode = !model.GradientProtectMode; e.Handled = true; }
            }
            else if (model.CropEditing)
            {
                if (e.Key is Key.Return or Key.Enter) { model.CommitCropEditing(); e.Handled = true; }
                else if (e.Key == Key.Escape) { model.CancelCropEditing(); e.Handled = true; }
            }
        };
        SyncPreview();
    }

    /// <summary>
    /// Space bar held = temporary pan mode on the preview, whichever toolbar control has focus
    /// (tunnelling handlers at the window level, before the focused control sees the key). Not when
    /// a text box is being edited, so typing spaces still works; key repeats are ignored.
    /// </summary>
    bool spacePan;
    void WireSpacePan()
    {
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Space) return;
            if (spacePan) { e.Handled = true; return; }
            if (IsTextInputFocused()) return;
            spacePan = true; preview.SpaceHeld = true; e.Handled = true;
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key != Key.Space || !spacePan) return;
            spacePan = false; preview.SpaceHeld = false; e.Handled = true;
        }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => { spacePan = false; preview.SpaceHeld = false; };
    }

    bool IsTextInputFocused()
    {
        for (var v = FocusManager?.GetFocusedElement() as Visual; v != null; v = v.GetVisualParent())
            if (v is TextBox or AutoCompleteBox or ComboBox { IsEditable: true }) return true;
        return false;
    }

    void SyncPreview()
    {
        preview.ImageSize = model.ImageSize ?? new Size(0, 0);
        preview.Frame = model.Frame;
        preview.Crop = model.Crop;
        preview.CropEditing = model.CropEditing;
        preview.CropDraft = model.CropDraft;
        preview.FineAngle = model.Orientation.Angle;
        preview.CropAngle = model.CropEditing ? model.CropAngleDraft : model.Orientation.Angle;
        var eb = model.CropEditBounds;
        preview.CropBounds = model.CropEditing ? new Rect(eb.X, eb.Y, eb.Width, eb.Height) : default;
        preview.CropOverlay = model.CropOverlay;
        preview.DustEditing = model.DustEditing != null;
        preview.DustSpots = model.DustEditing is ChannelKind k ? model.DustSpots(k) : new List<DustSpot>();
        preview.SelectedDustSpot = model.SelectedDustSpot;
        preview.DustBrightMode = model.DustEditing != null && model.DustDraft?.Polarity == SpotPolarity.Bright;
        preview.DustMaxRadius = model.DustBrightMaxRadius;
        bool gradientEditing = model.GradientEditing != null;
        preview.GradientEditing = gradientEditing;
        preview.GradientGesture = model.EditedGradientSpec?.Gesture;
        preview.GradientRefined = model.GradientFit is GradientFitInfo gf && gf.Kind == model.GradientEditing ? gf.Gesture : null;
        preview.GradientShape = model.GradientShape;
        preview.GradientRegions = gradientEditing ? model.GradientRegions : new List<GradientRegion>();
        preview.SelectedGradientRegion = model.SelectedGradientRegion;
        preview.GradientProtectMode = model.GradientProtectMode;
        preview.GradientSamples = gradientEditing ? model.GradientSamples.ToList() : new List<BackgroundSample>();
        preview.GradientSampleRadius = model.GradientSampleRadius;
        var sampleResults = new Dictionary<Guid, SampleResult>();
        if (model.GradientFit is GradientFitInfo sf && sf.Kind == model.GradientEditing) foreach (var r in sf.SampleResultsOrEmpty) sampleResults[r.Id] = r;
        preview.GradientSampleResults = sampleResults;
        preview.SelectedGradientSample = model.SelectedGradientSample;
        preview.CorrectionEditing = model.CorrectionEditing != null;
        preview.CorrectionMap = model.CorrectionDraft;
        preview.CorrectionVersion = model.CorrectionVersion;
        preview.CorrectionShowMap = model.CorrectionShowMap;
        preview.BrushRadius = model.CorrectionBrushRadius;
        preview.BrushDarken = model.CorrectionDarken;
    }

    void OnChanged(string what)
    {
        switch (what)
        {
            case Change.Frame: preview.Frame = model.Frame; break;
            case Change.Sources: case Change.Layer: SyncPreview(); RefreshToolbars(); break;
            case Change.Crop: case Change.Dust: case Change.Gradient: case Change.Corrections: SyncPreview(); RefreshToolbars(); break;
            case Change.Status: if (hqItem != null) hqItem.IsChecked = model.HighQualityPreview; break;
            case Change.Settings: UpdateUndo(); break;
            case Change.RecentSets: RefreshRecentMenu(); RefreshEmptyState(); break;
            case Change.Project: UpdateTitle(); break;
            case Change.Error: ShowError(); break;
            case Change.Pending: ShowPendingDialogs(); break;
        }
        if (limitsMenu != null)
            for (int i = 0; i < limitsMenu.Items.Count; i++) if (limitsMenu.Items[i] is MenuItem mi) mi.IsChecked = CropOverlayModes.All[i] == model.CropOverlay;
    }

    void UpdateUndo()
    {
        if (undoItem != null) undoItem.IsEnabled = model.CanUndo;
        if (redoItem != null) redoItem.IsEnabled = model.CanRedo;
    }

    void ShowError()
    {
        if (model.ErrorMessage is not string msg) return;
        Dispatcher.UIThread.Post(async () =>
        {
            if (model.ErrorMessage != msg) return;
            await DialogBase.Message(this, "CombineColors", msg);
            model.ErrorMessage = null;
        });
    }

    void ShowPendingDialogs()
    {
        if (dialogPending) return;
        dialogPending = true;
        Dispatcher.UIThread.Post(async () =>
        {
            dialogPending = false;
            if (model.PendingColorFile is PendingFile color) await ColorFileWindow.Show(this, model, color);
            else if (model.PendingFiles.FirstOrDefault() is PendingFile pending) await AssignChannelWindow.Show(this, model, pending);
            if (model.PendingColorFile != null || model.PendingFiles.Count > 0) ShowPendingDialogs();
        });
    }

    // ---- Toolbars --------------------------------------------------------------------------------

    void RefreshToolbars()
    {
        bool hasSources = model.Sources.Count > 0;
        bool editing = model.DustEditing != null || model.GradientEditing != null || model.CorrectionEditing != null || model.CropEditing;
        zoomToolbar.IsVisible = hasSources && !panesHidden;
        topToolbar.IsVisible = hasSources && (!panesHidden || editing);
        RefreshEmptyState();
        if (!hasSources) return;
        var panel = new StackPanel();
        if (model.DustEditing != null) panel.Children.Add(DustToolbar());
        else if (model.GradientEditing != null) panel.Children.Add(GradientToolbar());
        else if (model.CorrectionEditing != null) panel.Children.Add(CorrectionToolbar());
        else if (model.CropEditing)
        {
            // Kept while editing (only refreshed), so dragging the Angle slider is not interrupted by a rebuild.
            if (cropToolbar != null && topToolbar.Child == cropToolbar) { RefreshCropToolbar(); return; }
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(Ui.SmallButton("Apply", model.CommitCropEditing, "Return"));
            row.Children.Add(Ui.SmallButton("Cancel", model.CancelCropEditing, "Esc"));
            panel.Children.Add(row);
            panel.Children.Add(CropFields());
            panel.Children.Add(CropAngleControls());
            cropToolbar = panel;
            topToolbar.Child = panel;
            RefreshCropToolbar();
            return;
        }
        else
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(Ui.SmallButton("Crop", model.BeginCropEditing, "Draw a crop rectangle shared by every channel (Return applies, Esc cancels)"));
            row.Children.Add(Ui.SmallButton("Auto", model.AutoCrop, "Crop away the empty borders left by registration"));
            if (model.Crop != null)
            {
                row.Children.Add(Ui.SmallButton("Clear", model.ClearCrop, "Remove the crop"));
                var limits = new CheckBox { Content = "Limits", IsThreeState = true, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0), MinHeight = 20 };
                ToolTip.SetTip(limits, "Crop limits: unchecked hides them, partial shows the dashed frame over the whole image, checked shows only the cropped part");
                limits.IsChecked = model.CropOverlay switch { CropOverlayMode.Hidden => false, CropOverlayMode.Frame => null, _ => true };
                limits.Click += (_, _) => model.CropOverlay = limits.IsChecked switch { false => CropOverlayMode.Hidden, null => CropOverlayMode.Frame, _ => CropOverlayMode.CropOnly };
                row.Children.Add(limits);
            }
            var rotate = Ui.SmallButton("Rotate ↻", model.RotateClockwise, "Rotate every channel 90° clockwise"); rotate.IsEnabled = !model.IsLoading; row.Children.Add(rotate);
            var fh = Ui.IconButton("⇔", model.FlipHorizontal, "Flip horizontally"); fh.IsEnabled = !model.IsLoading; row.Children.Add(fh);
            var fv = Ui.IconButton("⇕", model.FlipVertical, "Flip vertically"); fv.IsEnabled = !model.IsLoading; fv.Margin = new Thickness(0); row.Children.Add(fv);
            panel.Children.Add(row);
        }
        topToolbar.Child = panel;
    }

    Control? cropToolbar;
    readonly List<(TextBox Box, int Index)> cropFieldBoxes = new();
    SliderRow? angleRow; CheckBox? constrainBox; Button? levelButton;

    void RefreshCropToolbar()
    {
        var d = model.CropDraft ?? new PixelRect(0, 0, 0, 0);
        foreach (var (tb, i) in cropFieldBoxes)
            if (!tb.IsFocused) tb.Text = (i switch { 0 => d.X, 1 => d.Y, 2 => d.Width, _ => d.Height }).ToString(CultureInfo.InvariantCulture);
        angleRow?.Refresh();
        if (constrainBox != null) constrainBox.IsChecked = model.CropConstrain;
        if (levelButton != null) levelButton.IsEnabled = model.CropAngleDraft != 0;
    }

    /// <summary>Straightening in the crop editor: the picture turns under the upright crop (applied with the crop).</summary>
    Control CropAngleControls()
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Width = 230 };
        panel.Children.Add(new Border { Height = 1, Background = AppTheme.PanelBorder, Margin = new Thickness(0, 0, 0, 4) });
        angleRow = new SliderRow("Angle", () => (float)model.CropAngleDraft, v => model.SetCropAngle(v), -180, 180, 0, "0.00", false,
            "Turns the picture under the crop, clockwise for positive angles, all the way round (double-click to reset). Dragging in the band around the crop (up to 160 px from it) turns it too (Shift: whole degrees; quarter turns catch the pointer). On Apply whole quarter turns are exact and only the rest (±45°) is resampled.",
            "°", 52);
        panel.Children.Add(angleRow);
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
        row.Children.Add(Ui.IconButton("↺", () => model.SetCropAngle(model.CropAngleDraft - 90), "Turn a quarter counter-clockwise"));
        row.Children.Add(Ui.IconButton("↻", () => model.SetCropAngle(model.CropAngleDraft + 90), "Turn a quarter clockwise"));
        constrainBox = new CheckBox { Content = "Constrain to image", FontSize = 11, MinHeight = 20, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) };
        ToolTip.SetTip(constrainBox, "While turning, the crop shrinks (keeping its shape and centre) so no empty corner gets inside it");
        constrainBox.Click += (_, _) => model.CropConstrain = constrainBox.IsChecked == true;
        row.Children.Add(constrainBox);
        levelButton = Ui.SmallButton("Level", () => model.SetCropAngle(0), "Back to 0: no fine turn (quarter turns and flips stay)");
        levelButton.Margin = new Thickness(0);
        row.Children.Add(levelButton);
        panel.Children.Add(row);
        panel.Children.Add(new TextBlock { Text = "Drag around the crop to turn", FontSize = 9, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) });
        return panel;
    }

    Control CropFields()
    {
        cropFieldBoxes.Clear();
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto)); grid.ColumnDefinitions.Add(new ColumnDefinition(60, GridUnitType.Pixel));
        string[] names = ["X", "Y", "W", "H"];
        for (int i = 0; i < 4; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = new TextBlock { Text = names[i], FontSize = 11, Margin = new Thickness(0, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
            Grid.SetRow(label, i); Grid.SetColumn(label, 0); grid.Children.Add(label);
            var d = model.CropDraft ?? new PixelRect(0, 0, 0, 0);
            var tb = Ui.CompactTextBox(11);
            tb.Text = (i switch { 0 => d.X, 1 => d.Y, 2 => d.Width, _ => d.Height }).ToString(CultureInfo.InvariantCulture); tb.Margin = new Thickness(0, 1, 0, 1); tb.TextAlignment = TextAlignment.Right;
            int idx = i;
            void Commit()
            {
                if (!int.TryParse(tb.Text, out var v)) return;
                var c = model.CropDraft ?? new PixelRect(0, 0, 1, 1);
                model.CropDraft = idx switch { 0 => c with { X = v }, 1 => c with { Y = v }, 2 => c with { Width = Math.Max(1, v) }, _ => c with { Height = Math.Max(1, v) } };
            }
            tb.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
            tb.LostFocus += (_, _) => Commit();
            Grid.SetRow(tb, i); Grid.SetColumn(tb, 1); grid.Children.Add(tb);
            cropFieldBoxes.Add((tb, i));
        }
        return grid;
    }

    /// <summary>Dust-spot editor: channel switch, model and parameters (of the selected spot, else of new spots), spot actions, before / after toggle.</summary>
    Control DustToolbar()
    {
        var kind = model.DustEditing ?? ChannelKind.Luminance;
        var loaded = ChannelKinds.All.Where(k => model.Sources.ContainsKey(k)).ToList();
        var spots = model.DustSpots(kind);
        var selected = model.SelectedDustSpotValue;
        var draft = model.DustDraft ?? new DustSettings();
        var panel = new StackPanel();
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row1.Children.Add(new TextBlock { Text = "◌ Dust spots", FontWeight = FontWeight.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var seg = Ui.Segmented(loaded, k => k.ShortName(), () => kind, k => model.SwitchDustChannel(k), out _);
        ToolTip.SetTip(seg, "Channel shown; the spots of every channel are kept until Apply"); seg.Margin = new Thickness(0, 0, 6, 0);
        row1.Children.Add(seg);
        var original = new ToggleButton { Content = "Original", FontSize = 11, Padding = new Thickness(7, 1, 7, 1), MinHeight = 20, IsChecked = model.DustBypass, Margin = new Thickness(0, 0, 6, 0) };
        ToolTip.SetTip(original, "Show the channel without any spot, to compare before and after (B)");
        original.Click += (_, _) => model.DustBypass = original.IsChecked == true;
        row1.Children.Add(original);
        row1.Children.Add(Ui.SmallButton("Apply", model.CommitDustEditing, "Return"));
        var cancel = Ui.SmallButton("Cancel", model.CancelDustEditing, "Esc"); cancel.Margin = new Thickness(0); row1.Children.Add(cancel);
        panel.Children.Add(row1);
        var row2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        row2.Children.Add(new TextBlock { Text = selected == null ? "New spots" : "Selected", FontSize = 11, Foreground = Brushes.Gray, Width = 60, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var modelSeg = Ui.Segmented(DustModels.All, m => m.DisplayName(), () => selected?.Model ?? draft.Model,
            m => { if (model.SelectedDustSpotValue is DustSpot sp) { var c = sp.Clone(); c.Model = m; model.UpdateSelectedDustSpot(c); } else model.UpdateDustDefaults(d => d.Model = m); }, out _);
        ToolTip.SetTip(modelSeg, "Local flat: free-form shadow (star-masked, multi-scale estimate of the ratio to the surrounding sky, noise left untouched), follows irregular motes and fringes.\nRadial profile: one transmission per distance to the centre, robust to nebulosity, for round motes and donuts.\nEdits the selected spot, or the parameters given to new spots.");
        row2.Children.Add(modelSeg);
        var smooth = new SliderRow("Smooth", () => (float)(model.SelectedDustSpotValue?.Smoothness ?? model.DustDraft?.Smoothness ?? 3),
            v => { if (model.SelectedDustSpotValue is DustSpot sp) { var c = sp.Clone(); c.Smoothness = v; model.UpdateSelectedDustSpot(c); } else model.UpdateDustDefaults(d => d.Smoothness = v); },
            1, 12, 3, "0.0", help: "Finest scale of the shadow estimate in pixels (σ of the local flat, window of the radial profile)", labelWidth: 46) { Width = 170, Margin = new Thickness(6, 0, 0, 0) };
        row2.Children.Add(smooth);
        var feather = new SliderRow("Feather", () => (float)(model.SelectedDustSpotValue?.Feather ?? model.DustDraft?.Feather ?? 0.2),
            v => { if (model.SelectedDustSpotValue is DustSpot sp) { var c = sp.Clone(); c.Feather = v; model.UpdateSelectedDustSpot(c); } else model.UpdateDustDefaults(d => d.Feather = v); },
            0, 0.5f, 0.2f, "0.00", help: "Fraction of the radius over which the correction fades out beyond the shadow", labelWidth: 46) { Width = 170, Margin = new Thickness(6, 0, 0, 0) };
        row2.Children.Add(feather);
        panel.Children.Add(row2);
        // Dark spots (dust shadows) or bright spots (star halos left by star removal).
        var polarity = selected?.Polarity ?? draft.Polarity;
        var rowP = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var polSeg = Ui.Segmented(SpotPolarities.All, p => p.DisplayName(), () => model.SelectedDustSpotValue?.Polarity ?? model.DustDraft?.Polarity ?? SpotPolarity.Dark,
            p => { if (model.SelectedDustSpotValue is DustSpot sp) { var c = sp.Clone(); c.Polarity = p; model.UpdateSelectedDustSpot(c); } else model.UpdateDustDefaults(d => d.Polarity = p); }, out _);
        ToolTip.SetTip(polSeg, "Dark spots: dust shadows, divided out like a flat (white dashed circles).\nBright spots: star halos left behind by star removal, the glow above the surrounding sky subtracted (orange dotted circles).\nEdits the selected spot, or the kind of new spots.");
        rowP.Children.Add(polSeg);
        if (polarity == SpotPolarity.Bright)
        {
            float longSide = model.ImageSize is Size isz ? (float)Math.Max(isz.Width, isz.Height) : 4000;
            var maxR = new SliderRow("Max radius", () => (float)model.DustBrightMaxRadius, v => model.DustBrightMaxRadius = v, 4, Math.Max(longSide * 0.1f, 40), Math.Max(24, longSide * 0.01f), "0", true,
                "Largest radius a click gives a new bright spot (the fit stays inside it; halos are local). Right-drag on the preview changes it too; the dotted orange circle at the pointer shows it.", "px", 78) { Width = 250, Margin = new Thickness(6, 0, 0, 0) };
            rowP.Children.Add(maxR);
        }
        panel.Children.Add(rowP);
        var row3 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        row3.Children.Add(new TextBlock { Text = $"{spots.Count} spot{(spots.Count == 1 ? "" : "s")}", FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var fit = Ui.SmallButton("Fit", model.RefitSelectedDustSpot, "Re-centre and resize the selected spot on the shadow found around it"); fit.IsEnabled = selected != null; row3.Children.Add(fit);
        var del = Ui.SmallButton("Delete", model.RemoveSelectedDustSpot, "Remove the selected spot (Del)"); del.IsEnabled = selected != null; row3.Children.Add(del);
        var clear = Ui.SmallButton("Clear", model.ClearDustSpots, "Remove every spot of this channel"); clear.IsEnabled = spots.Count > 0; row3.Children.Add(clear);
        var copy = Ui.SmallButton("Copy to all", model.CopyDustSpotsToAllChannels, "Give every other loaded channel the same spots (dust on the sensor window shadows every filter alike)"); copy.IsEnabled = spots.Count > 0 && loaded.Count > 1; copy.Margin = new Thickness(0); row3.Children.Add(copy);
        panel.Children.Add(row3);
        if (selected is DustSpot sel)
        {
            var row4 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            void Field(string name, double value, Action<DustSpot, double> apply, double width)
            {
                row4.Children.Add(new TextBlock { Text = name, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) });
                var tb = Ui.CompactTextBox(11);
                tb.Text = value.ToString("0.0", CultureInfo.InvariantCulture); tb.Width = width; tb.TextAlignment = TextAlignment.Right;
                void Commit() { if (double.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && model.SelectedDustSpotValue is DustSpot sp) { var c = sp.Clone(); apply(c, v); model.UpdateSelectedDustSpot(c); } }
                tb.KeyDown += (_, e) => { if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; } };
                tb.LostFocus += (_, _) => Commit();
                row4.Children.Add(tb);
            }
            Field("X", sel.X, (s, v) => s.X = v, 64); Field("Y", sel.Y, (s, v) => s.Y = v, 64); Field("R", sel.Radius, (s, v) => s.Radius = Math.Max(3, v), 56);
            panel.Children.Add(row4);
        }
        panel.Children.Add(new TextBlock { Text = "Click a shadow (or, with Bright spots, a halo) to add a fitted spot · drag to move · drag the handle to resize · drag empty sky to pan", FontSize = 9, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right });
        return panel;
    }

    static T Tip<T>(T c, string tip) where T : Control { ToolTip.SetTip(c, tip); return c; }

    /// <summary>Gradient editor: channel switch, guide shape and model parameters, protected regions, view switch, fit read-out.</summary>
    /// <summary>Corrections editor: channel switch, darken / brighten, brush, strength, map overlay, undo.</summary>
    Control CorrectionToolbar()
    {
        var kind = model.CorrectionEditing ?? ChannelKind.Luminance;
        var loaded = ChannelKinds.All.Where(k => model.Sources.ContainsKey(k)).ToList();
        double maxRadius = model.ImageSize is Size isz ? Math.Max(isz.Width, isz.Height) * 0.2 : 500;
        var panel = new StackPanel();
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row1.Children.Add(new TextBlock { Text = "✎ Corrections", FontWeight = FontWeight.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var seg = Ui.Segmented(loaded, k => k.ShortName(), () => kind, k => model.SwitchCorrectionChannel(k), out _);
        Tip(seg, "Channel painted (switching applies the current map)"); seg.Margin = new Thickness(0, 0, 6, 0);
        row1.Children.Add(seg);
        var sense = Ui.Segmented(new[] { true, false }, d => d ? "Darken" : "Brighten", () => model.CorrectionDarken, d => model.CorrectionDarken = d, out _);
        Tip(sense, "What the brush does; hold Alt while painting for the opposite (D toggles)"); sense.Margin = new Thickness(0, 0, 6, 0);
        row1.Children.Add(sense);
        var original = new ToggleButton { Content = "Original", FontSize = 11, Padding = new Thickness(7, 1, 7, 1), IsChecked = model.CorrectionBypass, Margin = new Thickness(0, 0, 6, 0) };
        Tip(original, "Show the channel without the corrections, to compare (B)");
        original.Click += (_, _) => model.CorrectionBypass = original.IsChecked == true;
        row1.Children.Add(original);
        var map = new ToggleButton { Content = "Map", FontSize = 11, Padding = new Thickness(7, 1, 7, 1), IsChecked = model.CorrectionShowMap, Margin = new Thickness(0, 0, 6, 0) };
        Tip(map, "Show the painted map as a tint: red darkens, blue brightens (M)");
        map.Click += (_, _) => model.CorrectionShowMap = map.IsChecked == true;
        row1.Children.Add(map);
        row1.Children.Add(Ui.SmallButton("Apply", () => model.CommitCorrectionEditing(), "Return"));
        var cancel = Ui.SmallButton("Cancel", model.CancelCorrectionEditing, "Esc"); cancel.Margin = new Thickness(0); row1.Children.Add(cancel);
        panel.Children.Add(row1);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        row2.Children.Add(new SliderRow("Radius", () => (float)model.CorrectionBrushRadius, v => model.CorrectionBrushRadius = v, 4, (float)maxRadius, (float)(maxRadius / 5), "0", true,
            "Brush radius in image pixels (right-drag on the preview: left shrinks, right grows; up to 20 % of the image)", "px", 58) { Width = 170 });
        row2.Children.Add(new SliderRow("Flow", () => (float)(model.CorrectionBrushFlow * 100), v => model.CorrectionBrushFlow = v / 100, 0.5f, 10, 2, "0.0", false,
            "Amount added by every dab, as a percentage of the full map range: low for gentle, build-up strokes", "%", 52) { Width = 150, Margin = new Thickness(6, 0, 0, 0) });
        row2.Children.Add(new SliderRow("Feather", () => (float)model.CorrectionBrushFeather, v => model.CorrectionBrushFeather = v, 0, 1, 0.7f, "0.00", false,
            "Fraction of the radius over which a dab fades out (0 = hard edge)", null, 48) { Width = 150, Margin = new Thickness(6, 0, 0, 0) });
        row2.Children.Add(new SliderRow("Strength", () => (float)model.CorrectionStrength, v => model.CorrectionStrength = v, 0, 1.5f, 0.5f, "0.00", false,
            "Exposure range of the map in stops: a fully painted area is darkened or brightened by this many stops", "EV", 66) { Width = 170, Margin = new Thickness(6, 0, 0, 0) });
        panel.Children.Add(row2);

        var row3 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var undo = Ui.SmallButton("Undo stroke", model.UndoCorrection, "Undo the last stroke (Ctrl+Z)"); undo.IsEnabled = model.CanUndoCorrection; row3.Children.Add(undo);
        var clear = Ui.SmallButton("Clear", model.ClearCorrection, "Erase the whole map"); clear.IsEnabled = !(model.CorrectionDraft?.IsEmpty ?? true); row3.Children.Add(clear);
        row3.Children.Add(new TextBlock { Text = "Paint on the channel to darken or brighten it · right-drag to set the radius · Alt paints the opposite · the map is kept next to the image (.ccmask.png)", FontSize = 9, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(row3);
        return panel;
    }

    Control GradientToolbar()
    {
        var kind = model.GradientEditing ?? ChannelKind.Luminance;
        var loaded = ChannelKinds.All.Where(k => model.Sources.ContainsKey(k)).ToList();
        var spec = model.EditedGradientSpec;
        var defaults = model.GradientDraft ?? new GradientSettings();
        var shape = model.GradientShape;
        var fit = model.GradientFit is GradientFitInfo fi && fi.Kind == kind ? fi : null;
        var regions = model.GradientRegions;
        bool isSamples = shape == GradientShape.Samples;
        double maxRadius = model.ImageSize is Size isz ? Math.Max(isz.Width, isz.Height) / 15 : 300;
        var panel = new StackPanel();
        var row1 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        row1.Children.Add(new TextBlock { Text = "◐ Gradient", FontWeight = FontWeight.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        var seg = Ui.Segmented(loaded, k => k.ShortName(), () => kind, k => model.SwitchGradientChannel(k), out _);
        Tip(seg, "Channel shown; the models of every channel are kept until Apply"); seg.Margin = new Thickness(0, 0, 6, 0);
        row1.Children.Add(seg);
        var view = Ui.Segmented(GradientViews.All, v => v.DisplayName(), () => model.GradientView, v => model.GradientView = v, out _);
        Tip(view, "Corrected: the channel with the model taken out.\nOriginal: the channel untouched (B toggles).\nBackground: the model alone — what is taken away — auto-stretched on its own (M toggles).");
        view.Margin = new Thickness(0, 0, 6, 0);
        row1.Children.Add(view);
        row1.Children.Add(Ui.SmallButton("Apply", model.CommitGradientEditing, "Return"));
        var cancel = Ui.SmallButton("Cancel", model.CancelGradientEditing, "Esc"); cancel.Margin = new Thickness(0); row1.Children.Add(cancel);
        panel.Children.Add(row1);

        var row2 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var shapeSeg = Ui.Segmented(GradientShapes.All, s => s.DisplayName(), () => model.GradientShape, s => model.SetGradientShape(s), out _);
        Tip(shapeSeg, "Line: the background varies along the line and is constant across it (light pollution from one side).\nEllipse: the background is a function of the distance to the centre, which may lie outside the frame (vignetting, a glow from a bright source).\nPolynomial: no guide, a plain 2-D polynomial of the chosen degree.\nSamples: discs placed on sky (a generated grid plus your own) with a smoothing spline or kriging surface through them.");
        shapeSeg.Margin = new Thickness(0, 0, 6, 0); row2.Children.Add(shapeSeg);
        var modeSeg = Ui.Segmented(GradientModes.All, m => m.DisplayName(), () => model.EditedGradientSpec?.Mode ?? defaults.Mode, m => model.EditGradient(sp => sp.Mode = m, d => d.Mode = m), out _);
        Tip(modeSeg, "Subtract: the background is taken off, the overall level kept (sky glow, stretched data).\nDivide: the pixels are divided by the background normalised to its brightest level (vignetting / missing flats on linear data: stars at the edge get their flux back).");
        row2.Children.Add(modeSeg);
        if (isSamples)
        {
            row2.Children.Add(new MenuPicker<SampleSurface>("Surface", () => model.EditedGradientSpec?.Surface ?? defaults.Surface, v => model.EditGradient(sp => sp.Surface = v, d => d.Surface = v), SampleSurfaces.All, v => v.DisplayName(),
                "Spline: thin-plate spline through the samples, relaxed by the smoothing (DBE-like; bends smoothly across the gaps).\nKriging: Gaussian-process mean around a constant, the smoothing sets its reach and nugget (stays near the overall level in the gaps).") { Margin = new Thickness(6, 0, 0, 0) });
            bool autoOn = spec?.AutoSmoothing ?? defaults.AutoSmoothing;
            var auto = new CheckBox { Content = "Auto", FontSize = 11, IsChecked = autoOn, VerticalAlignment = VerticalAlignment.Center, MinHeight = 20, Margin = new Thickness(6, 0, 0, 0) };
            auto.Click += (_, _) => { bool on = auto.IsChecked == true; model.EditGradient(sp => sp.AutoSmoothing = on, d => d.AutoSmoothing = on); };
            Tip(auto, "Pick the smoothing by cross-validation: the surface that best predicts every sample from the others");
            row2.Children.Add(auto);
            row2.Children.Add(new SliderRow("Smooth", () => (float)(spec?.SampleSmoothing ?? defaults.SampleSmoothing), v => model.EditGradient(sp => sp.SampleSmoothing = v, d => d.SampleSmoothing = v),
                0, 1, 0.5f, "0.00", help: "Smoothing of the surface: 0 follows the samples closely, 1 is a very stiff surface", labelWidth: 46) { Width = 160, Margin = new Thickness(6, 0, 0, 0), IsEnabled = !autoOn });
            row2.Children.Add(new SliderRow("Radius", () => (float)(spec?.SampleRadius ?? defaults.SampleRadius), v => model.EditGradient(sp => sp.SampleRadius = v, d => d.SampleRadius = v),
                0, (float)maxRadius, 0, "0", help: "Radius of the sample discs in pixels (0 = automatic: ≈ 1/80 of the long side)", note: "px", labelWidth: 46) { Width = 160, Margin = new Thickness(6, 0, 0, 0) });
        }
        else
            row2.Children.Add(new MenuPicker<GradientResidual>("Residual", () => model.EditedGradientSpec?.Residual ?? defaults.Residual, r => model.EditGradient(sp => sp.Residual = r, d => d.Residual = r), GradientResiduals.All, r => r.DisplayName(),
                "Low-order 2-D term fitted after the guided profile, for what the profile cannot explain (a sky gradient on top of the vignetting)") { Margin = new Thickness(6, 0, 0, 0) });
        if (shape == GradientShape.Ellipse)
            row2.Children.Add(new MenuPicker<int>("Sectors", () => model.EditedGradientSpec?.Sectors ?? defaults.Sectors, v => model.EditGradient(sp => sp.Sectors = v, d => d.Sectors = v), new[] { 1, 4, 8, 12 }, v => v == 1 ? "1 (round)" : v.ToString(),
                "Angular sectors of the profile: 1 = the same fall-off in every direction; 4–12 let it differ with the direction (tilted sensor, asymmetric vignetting)") { Margin = new Thickness(6, 0, 0, 0) });
        if (shape == GradientShape.Polynomial)
            row2.Children.Add(new MenuPicker<int>("Degree", () => model.EditedGradientSpec?.Degree ?? defaults.Degree, v => model.EditGradient(sp => sp.Degree = v, d => d.Degree = v), new[] { 1, 2, 3, 4, 5, 6 }, v => v.ToString()) { Margin = new Thickness(6, 0, 0, 0) });
        if (!isSamples)
            row2.Children.Add(new SliderRow("Smooth", () => (float)(model.EditedGradientSpec?.Smoothness ?? model.GradientDraft?.Smoothness ?? 1.5), v => model.EditGradient(sp => sp.Smoothness = v, d => d.Smoothness = v),
                0, 6, 1.5f, "0.0", help: "Smoothing of the profile along the guide, in bins (120 bins across the frame)", labelWidth: 46) { Width = 160, Margin = new Thickness(6, 0, 0, 0) });
        panel.Children.Add(row2);

        if (isSamples)
        {
            var rowG = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
            rowG.Children.Add(new TextBlock { Text = "Grid", FontSize = 11, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
            rowG.Children.Add(new MenuPicker<int>("Per row", () => spec?.GridPerRow ?? defaults.GridPerRow, v => model.EditGradient(sp => sp.GridPerRow = v, d => d.GridPerRow = v), new[] { 6, 8, 10, 12, 14, 16, 20, 24, 30 }, v => v.ToString(),
                "Samples per row of the generated grid"));
            rowG.Children.Add(new SliderRow("Tolerance", () => (float)(spec?.GridTolerance ?? defaults.GridTolerance), v => model.EditGradient(sp => sp.GridTolerance = v, d => d.GridTolerance = v),
                0.5f, 10, 2.5f, "0.0", help: "How far above the planar sky model (in sample noises) a grid point may lie and still count as sky; raise it to keep a glow the plane cannot follow, lower it to leave out faint nebulosity", labelWidth: 60) { Width = 190, Margin = new Thickness(6, 0, 6, 0) });
            rowG.Children.Add(Ui.SmallButton("Generate", model.RegenerateGradientGrid, "Place the grid again with these parameters (your own samples are dropped)"));
            var clearSamples = Ui.SmallButton("Clear samples", model.ClearGradientSamples, "Remove every sample; click the sky to place them by hand"); clearSamples.IsEnabled = model.GradientSamples.Count > 0; rowG.Children.Add(clearSamples);
            var delSample = Ui.SmallButton("Delete", model.RemoveSelectedGradientSample, "Remove the selected sample (Del)"); delSample.IsEnabled = model.SelectedGradientSample != null; delSample.Margin = new Thickness(0); rowG.Children.Add(delSample);
            panel.Children.Add(rowG);
        }

        var row3 = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        if (shape != GradientShape.Polynomial && !isSamples)
        {
            var refine = new CheckBox { Content = "Refine", FontSize = 11, IsChecked = spec?.Refine ?? defaults.Refine, VerticalAlignment = VerticalAlignment.Center, MinHeight = 20, Margin = new Thickness(0, 0, 6, 0) };
            refine.Click += (_, _) => { bool on = refine.IsChecked == true; model.EditGradient(sp => sp.Refine = on, d => d.Refine = on); };
            Tip(refine, "Optimise the drawing from the data (line direction; ellipse centre, axis ratio and angle). The guide used is shown dashed in orange when it differs from the drawing.");
            row3.Children.Add(refine);
            var adopt = Ui.SmallButton("Adopt", model.AdoptRefinedGradient, "Replace the drawing by the refined guide");
            adopt.IsEnabled = fit != null && !Equals(fit.Gesture, spec?.Gesture);
            row3.Children.Add(adopt);
        }
        var protect = new ToggleButton { Content = "Protect", FontSize = 11, Padding = new Thickness(7, 1, 7, 1), MinHeight = 20, IsChecked = model.GradientProtectMode, Margin = new Thickness(0, 0, 6, 0) };
        protect.Click += (_, _) => model.GradientProtectMode = protect.IsChecked == true;
        Tip(protect, "Click the preview to add a protected region (nebula, galaxy) left out of the background estimate, for every channel; drag to move, drag the handles to resize, Del removes the selected one (P)");
        row3.Children.Add(protect);
        var del = Ui.SmallButton("Delete", model.RemoveSelectedGradientRegion, "Remove the selected protected region (Del)"); del.IsEnabled = model.SelectedGradientRegion != null; row3.Children.Add(del);
        var clearRegions = Ui.SmallButton("Clear regions", model.ClearGradientRegions, "Remove every protected region"); clearRegions.IsEnabled = regions.Count > 0; row3.Children.Add(clearRegions);
        var remove = Ui.SmallButton("Remove model", model.ClearGradient, "Drop this channel's model (the channel is shown untouched)"); remove.IsEnabled = spec != null; row3.Children.Add(remove);
        var copy = Ui.SmallButton("Copy to all", model.CopyGradientToAllChannels, "Give every other loaded channel the same model (vignetting is the same for every filter)"); copy.IsEnabled = spec != null && loaded.Count > 1; copy.Margin = new Thickness(0); row3.Children.Add(copy);
        panel.Children.Add(row3);

        string status;
        if (fit != null)
        {
            var g = fit.Gesture;
            string geo = g.Shape switch
            {
                GradientShape.Line => FormattableString.Invariant($"line {g.X:0},{g.Y:0} → {g.X2:0},{g.Y2:0}"),
                GradientShape.Ellipse => FormattableString.Invariant($"ellipse centre {g.X:0},{g.Y:0} · axes {g.A:0} × {g.B:0} · {g.Angle * 180 / Math.PI:0}°"),
                GradientShape.Samples => $"{(spec?.Surface ?? SampleSurface.Spline).DisplayName().ToLowerInvariant()} through {fit.SampleResultsOrEmpty.Count(r => !r.IsRejected)} of {fit.SampleResultsOrEmpty.Count} samples",
                _ => $"polynomial, degree {spec?.Degree ?? 0}",
            };
            status = FormattableString.Invariant($"Fit: {geo} · residual {fit.ResidualSigma:0.00}σ · {(int)fit.Millis} ms");
        }
        else if (spec == null)
            status = shape == GradientShape.Line ? "Drag along the gradient, from the dark side towards the bright side" : isSamples ? "Click the sky to place a sample" : "Drag from the centre of the gradient outwards (the centre may be dragged outside the frame afterwards)";
        else status = "";
        if (status.Length > 0) panel.Children.Add(new TextBlock { Text = status, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right });
        panel.Children.Add(new TextBlock { Text = isSamples ? "Click the sky to add a sample · drag to move · Del removes the selected one · green = used, red = rejected · regions: Protect then click · B original, M background"
            : "Drag the handles to adjust the guide · drag empty sky to pan · regions: Protect then click · B original, M background", FontSize = 9, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right });
        return panel;
    }

    void RefreshEmptyState()
    {
        if (model.Sources.Count > 0) { emptyState.IsVisible = false; return; }
        emptyState.IsVisible = true;
        if (model.ActiveLayer == ImageLayer.Stars) { emptyState.Child = EmptyStarsState(); return; }
        var panel = new StackPanel { Width = 540 };
        panel.Children.Add(new TextBlock { Text = "🖼", FontSize = 44, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Drop stacked FITS or TIFF channels here", FontSize = 20, Foreground = Brushes.WhiteSmoke, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "Hα, OIII, SII, Hβ, L, R, G, B are detected from the FITS FILTER keyword or the file name.\nA colour image can be used as RGB or split into its dual-band lines.", FontSize = 12, Foreground = Brushes.Silver, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var open = new Button { Content = "Open images…", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        open.Click += async (_, _) => OpenPaths(await Panels.OpenImages(this));
        panel.Children.Add(open);
        if (model.RecentSets.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Recent sets", FontSize = 13, FontWeight = FontWeight.Bold, Foreground = Brushes.WhiteSmoke, Margin = new Thickness(0, 16, 0, 4) });
            foreach (var set in model.RecentSets.Take(8))
            {
                var s = set;
                var b = new Button { HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 2, 0, 2), Padding = new Thickness(8, 4, 8, 4), Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(0), Foreground = Brushes.WhiteSmoke, Cursor = new Cursor(StandardCursorType.Hand) };
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = set.Name, FontWeight = FontWeight.Bold });
                content.Children.Add(new TextBlock { Text = set.Summary + "  ·  " + set.Folder, FontSize = 11, Foreground = Brushes.Silver, TextTrimming = TextTrimming.CharacterEllipsis });
                b.Content = content;
                b.Click += (_, _) => model.OpenRecentSet(s);
                var menu = new ContextMenu(); var rm = new MenuItem { Header = "Remove from recent" }; rm.Click += (_, _) => model.RemoveRecentSet(s); menu.Items.Add(rm);
                b.ContextMenu = menu;
                panel.Children.Add(b);
            }
        }
        emptyState.Child = panel;
    }

    Control EmptyStarsState()
    {
        var panel = new StackPanel { Width = 560 };
        panel.Children.Add(new TextBlock { Text = "✦", FontSize = 44, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock { Text = "Stars tab", FontSize = 20, Foreground = Brushes.WhiteSmoke, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 4) });
        panel.Children.Add(new TextBlock { Text = "Drop the star channels here (stars-only images, one per filter, or an RGB stars image).\nThey get settings of their own; the Combine stars block of the Background tab blends them in.\nRemove Stars… and Load RGB stars… on the Background tab fill this tab too.", FontSize = 12, Foreground = Brushes.Silver, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        var open = new Button { Content = "Open star images…", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
        open.Click += async (_, _) => { var p = await Panels.OpenImages(this); if (p.Count > 0) model.LoadFiles(p); };
        var back = new Button { Content = "Back to the Background tab", Padding = new Thickness(12, 4, 12, 4) };
        back.Click += (_, _) => model.SwitchLayer(ImageLayer.Background);
        row.Children.Add(open); row.Children.Add(back);
        panel.Children.Add(row);
        return panel;
    }
}

sealed class RelayCommand : ICommand
{
    readonly Action action;
    public RelayCommand(Action a) { action = a; }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
