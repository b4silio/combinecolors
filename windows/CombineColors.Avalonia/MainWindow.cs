using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Orientation = Avalonia.Layout.Orientation;
using Avalonia.Media;
using Stretch = Avalonia.Media.Stretch;
using Avalonia.Platform;
using Avalonia.Threading;
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
    MenuItem? recentMenu, undoItem, redoItem, hqItem, limitsMenu;
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

        var menu = BuildMenu();
        DockPanel.SetDock(menu, Dock.Top); Root.Children.Add(menu);
        DockPanel.SetDock(statusBar, Dock.Bottom); Root.Children.Add(statusBar);
        var controlsSep = new Separator { Margin = new Thickness(0) }; DockPanel.SetDock(controlsSep, Dock.Bottom);
        DockPanel.SetDock(controls, Dock.Bottom); Root.Children.Add(controls); Root.Children.Add(controlsSep);

        var main = new Grid();
        main.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        main.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 440 });
        main.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        main.ColumnDefinitions.Add(new ColumnDefinition(460, GridUnitType.Pixel) { MinWidth = 380, MaxWidth = 760 });
        Grid.SetColumn(channels, 0); main.Children.Add(channels);
        BuildPreviewArea();
        Grid.SetColumn(previewArea, 1); main.Children.Add(previewArea);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Stretch, Background = AppTheme.PanelBorder, ResizeDirection = GridResizeDirection.Columns };
        Grid.SetColumn(splitter, 2); main.Children.Add(splitter);
        var right = new DockPanel();
        var presets = new PresetBarView(model, this);
        DockPanel.SetDock(presets, Dock.Bottom); right.Children.Add(presets);
        var presetSep = new Separator { Margin = new Thickness(0) }; DockPanel.SetDock(presetSep, Dock.Bottom); right.Children.Add(presetSep);
        right.Children.Add(palettes);
        Grid.SetColumn(right, 3); main.Children.Add(right);
        Root.Children.Add(main);

        WirePreview();
        model.Changed += OnChanged;
        model.FitRequested += () => Dispatcher.UIThread.Post(() => preview.Perform(ZoomAction.Fit));
        DropHelpers.Accept(this, OpenPaths);
        Closing += (_, _) => { model.SaveRecentState(); model.Preferences.Flush(); };
        UpdateTitle();
        RefreshToolbars();
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
        file.Items.Add(Item("Remove All Channels", model.RemoveAll));
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
        var view = new MenuItem { Header = "_View" };
        view.Items.Add(Item("Fit to Window", () => preview.Perform(ZoomAction.Fit), Key.D0, Cmd));
        view.Items.Add(Item("Actual Size", () => preview.Perform(ZoomAction.ActualSize), Key.D1, Cmd));
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
        view.Items.Add(Item("Rotate Clockwise", model.RotateClockwise, Key.R, Cmd));
        view.Items.Add(Item("Flip Horizontal", model.FlipHorizontal, Key.H, Cmd | KeyModifiers.Shift));
        view.Items.Add(Item("Flip Vertical", model.FlipVertical, Key.V, Cmd | KeyModifiers.Shift));
        view.Items.Add(Item("Auto Trim Borders", model.AutoCrop));
        menu.Items.Add(view);
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
        preview.CropDraftChanged += r => model.CropDraft = r;
        preview.DustSpotsChanged += spots => model.UpdateDustSpots(spots);
        preview.DustAdd += p => model.AddDustSpot(p);
        preview.DustSelect += id => model.SelectedDustSpot = id;
        preview.DustDelete += model.RemoveSelectedDustSpot;
        preview.DoubleClick += () => preview.Perform(preview.IsActualSize ? ZoomAction.Fit : ZoomAction.ActualSize);
        preview.KeyDown += (_, e) =>
        {
            if (model.DustEditing != null)
            {
                if (e.Key is Key.Return or Key.Enter) { model.CommitDustEditing(); e.Handled = true; }
                else if (e.Key == Key.Escape) { model.CancelDustEditing(); e.Handled = true; }
                else if (e.Key == Key.B) { model.DustBypass = !model.DustBypass; e.Handled = true; }
            }
            else if (model.CropEditing)
            {
                if (e.Key is Key.Return or Key.Enter) { model.CommitCropEditing(); e.Handled = true; }
                else if (e.Key == Key.Escape) { model.CancelCropEditing(); e.Handled = true; }
            }
        };
        SyncPreview();
    }

    void SyncPreview()
    {
        preview.ImageSize = model.ImageSize ?? new Size(0, 0);
        preview.Frame = model.Frame;
        preview.Crop = model.Crop;
        preview.CropEditing = model.CropEditing;
        preview.CropDraft = model.CropDraft;
        preview.CropOverlay = model.CropOverlay;
        preview.DustEditing = model.DustEditing != null;
        preview.DustSpots = model.DustEditing is ChannelKind k ? model.DustSpots(k) : new List<DustSpot>();
        preview.SelectedDustSpot = model.SelectedDustSpot;
    }

    void OnChanged(string what)
    {
        switch (what)
        {
            case Change.Frame: preview.Frame = model.Frame; break;
            case Change.Sources: SyncPreview(); RefreshToolbars(); break;
            case Change.Crop: case Change.Dust: SyncPreview(); RefreshToolbars(); break;
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
        zoomToolbar.IsVisible = hasSources;
        topToolbar.IsVisible = hasSources;
        RefreshEmptyState();
        if (!hasSources) return;
        var panel = new StackPanel();
        if (model.DustEditing != null) panel.Children.Add(DustToolbar());
        else if (model.CropEditing)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            row.Children.Add(Ui.SmallButton("Apply", model.CommitCropEditing, "Return"));
            row.Children.Add(Ui.SmallButton("Cancel", model.CancelCropEditing, "Esc"));
            panel.Children.Add(row);
            panel.Children.Add(CropFields());
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

    Control CropFields()
    {
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
        panel.Children.Add(new TextBlock { Text = "Click a shadow to add a fitted spot · drag to move · drag the handle to resize · drag empty sky to pan", FontSize = 9, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 0), HorizontalAlignment = HorizontalAlignment.Right });
        return panel;
    }

    void RefreshEmptyState()
    {
        if (model.Sources.Count > 0) { emptyState.IsVisible = false; return; }
        emptyState.IsVisible = true;
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
}

sealed class RelayCommand : ICommand
{
    readonly Action action;
    public RelayCommand(Action a) { action = a; }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => action();
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
