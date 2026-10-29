using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Orientation = System.Windows.Controls.Orientation;
using Stretch = System.Windows.Media.Stretch;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Views;

static class DialogBase
{
    public static Window Make(Window owner, string title, double width)
        => new() { Owner = owner, Title = title, Width = width, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false };

    public static Button Default(string text, Action onClick)
    {
        var b = new Button { Content = text, IsDefault = true, Padding = new Thickness(12, 3, 12, 3), MinWidth = 70, Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button Cancel(string text, Action onClick)
    {
        var b = new Button { Content = text, IsCancel = true, Padding = new Thickness(12, 3, 12, 3), MinWidth = 70, Margin = new Thickness(4, 0, 0, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }
}

/// <summary>Asks for a name.</summary>
public static class RenameWindow
{
    public static string? Ask(Window owner, string title, string current)
    {
        var w = DialogBase.Make(owner, title, 360);
        var panel = new StackPanel { Margin = new Thickness(16) };
        var tb = new TextBox { Text = current, Margin = new Thickness(0, 0, 0, 12) };
        tb.SelectAll();
        panel.Children.Add(tb);
        string? result = null;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(DialogBase.Cancel("Cancel", () => w.DialogResult = false));
        buttons.Children.Add(DialogBase.Default("Rename", () => { result = tb.Text.Trim(); w.DialogResult = true; }));
        panel.Children.Add(buttons);
        w.Content = panel;
        tb.Focus();
        return w.ShowDialog() == true && !string.IsNullOrEmpty(result) ? result : null;
    }
}

/// <summary>Which channel is an unrecognised file?</summary>
public static class AssignChannelWindow
{
    public static void Show(Window owner, ProjectModel model, PendingFile pending)
    {
        var w = DialogBase.Make(owner, "Assign channel", 440);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"Which channel is {pending.File.FileName}?", FontWeight = FontWeights.Bold, FontSize = 13, Margin = new Thickness(0, 0, 0, 4) });
        panel.Children.Add(new TextBlock { Text = $"The channel could not be detected from the file name or metadata. {pending.File.Width} × {pending.File.Height} · {pending.File.FormatDescription}", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var grid = new UniformGrid { Columns = 4 };
        foreach (var k in ChannelKinds.All)
        {
            var b = new Button { Content = Ui.Row(Ui.Dot(Ui.Tint(k)), new TextBlock { Text = k.DisplayName() }), Margin = new Thickness(2), Padding = new Thickness(6, 4, 6, 4) };
            var kind = k;
            b.Click += (_, _) => { model.AssignPending(pending, kind); w.DialogResult = true; };
            grid.Children.Add(b);
        }
        panel.Children.Add(grid);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(DialogBase.Cancel("Skip", () => { model.SkipPending(pending); w.DialogResult = false; }));
        panel.Children.Add(buttons);
        w.Content = panel;
        w.Closed += (_, _) => { if (model.PendingFiles.Any(p => p.Id == pending.Id)) model.SkipPending(pending); };
        w.ShowDialog();
    }
}

/// <summary>Shown when a colour image is dropped: use it as R/G/B or split it into two emission lines.</summary>
public sealed class ColorFileWindow
{
    readonly ProjectModel model;
    readonly PendingFile pending;
    readonly Window w;
    DualBandSettings settings = new();
    readonly Image preview1 = new() { Width = 160, Height = 120, Stretch = Stretch.Uniform };
    readonly Image preview2 = new() { Width = 160, Height = 120, Stretch = Stretch.Uniform };
    readonly TextBlock title1 = new() { FontSize = 11, TextAlignment = TextAlignment.Center };
    readonly TextBlock title2 = new() { FontSize = 11, TextAlignment = TextAlignment.Center };
    readonly TextBlock message = new() { FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel dualPanel = new();
    readonly StackPanel advanced = new() { Visibility = Visibility.Collapsed };
    readonly Button addButton;
    readonly Button saveButton;
    bool dualBand;
    int previewGeneration;
    readonly List<IRefreshable> rows = new();

    public static void Show(Window owner, ProjectModel model, PendingFile pending) => new ColorFileWindow(owner, model, pending).w.ShowDialog();

    ColorFileWindow(Window owner, ProjectModel model, PendingFile pending)
    {
        this.model = model; this.pending = pending;
        dualBand = model.Preferences.GetString("colorFileMode") == "dualBand";
        w = DialogBase.Make(owner, "Colour image", 740);
        var f = pending.File;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = $"{f.FileName} is a colour image", FontWeight = FontWeights.Bold, FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = $"{f.Width} × {f.Height} · {f.FormatDescription}", FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 0, 0, 8) });
        TextBlock rgbText = new(); StackPanel customRow = new(); TextBlock first1 = new(), second1 = new();
        var seg = Ui.Segmented(new[] { false, true }, b => b ? "Dual-band" : "RGB", () => dualBand, v => { dualBand = v; model.Preferences.Set("colorFileMode", v ? "dualBand" : "rgb"); UpdateMode(); }, out _);
        panel.Children.Add(seg);
        rgbText = new TextBlock { Text = "The red, green and blue planes become the R, G and B channels (broadband colour or an RGB stars image).", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(rgbText);
        dualPanel.Children.Add(new TextBlock { Text = "One-shot-colour images taken through a dual-band filter hold one emission line in the red channel and the other in green/blue. They are extracted into two monochrome channels.", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) });
        var columns = new DockPanel();
        var left = new StackPanel { Width = 340 };
        var preset = new MenuPicker<DualBandPreset>("Filter", () => settings.Preset, v => { settings.Preset = v; Changed(); }, DualBandPresets.All, p => p.DisplayName());
        rows.Add(preset); left.Children.Add(preset);
        customRow = new StackPanel();
        var first = new MenuPicker<ChannelKind>("First (red)", () => settings.CustomFirst, v => { settings.CustomFirst = v; Changed(); }, ChannelKinds.All, k => k.DisplayName());
        var second = new MenuPicker<ChannelKind>("Second (green/blue)", () => settings.CustomSecond, v => { settings.CustomSecond = v; Changed(); }, ChannelKinds.All, k => k.DisplayName());
        rows.Add(first); rows.Add(second);
        customRow.Children.Add(first); customRow.Children.Add(second);
        left.Children.Add(customRow);
        var advToggle = Ui.SmallButton("Advanced ▾", () => advanced.Visibility = advanced.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible);
        advToggle.HorizontalAlignment = HorizontalAlignment.Left; advToggle.Margin = new Thickness(0, 4, 0, 2);
        left.Children.Add(advToggle);
        first1 = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold }; second1 = new TextBlock { FontSize = 11, FontWeight = FontWeights.Bold };
        advanced.Children.Add(first1);
        for (int i = 0; i < 3; i++) { int ci = i; var r = new SliderRow("RGB"[i].ToString(), () => settings.FirstWeights[ci], v => { settings.FirstWeights[ci] = v; Changed(); }, 0, 1, 0); rows.Add(r); advanced.Children.Add(r); }
        advanced.Children.Add(second1);
        for (int i = 0; i < 3; i++) { int ci = i; var r = new SliderRow("RGB"[i].ToString(), () => settings.SecondWeights[ci], v => { settings.SecondWeights[ci] = v; Changed(); }, 0, 1, 0); rows.Add(r); advanced.Children.Add(r); }
        var ct2 = new SliderRow("Cross-talk →2", () => settings.CrosstalkSecond, v => { settings.CrosstalkSecond = v; Changed(); }, 0, 1, 0, help: "Fraction of the first line subtracted from the second.", labelWidth: 90);
        var ct1 = new SliderRow("Cross-talk →1", () => settings.CrosstalkFirst, v => { settings.CrosstalkFirst = v; Changed(); }, 0, 1, 0, help: "Fraction of the second line subtracted from the first.", labelWidth: 90);
        rows.Add(ct2); rows.Add(ct1); advanced.Children.Add(ct2); advanced.Children.Add(ct1);
        left.Children.Add(advanced);
        columns.Children.Add(left);
        var previews = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        previews.Children.Add(PreviewBox(preview1, title1)); previews.Children.Add(PreviewBox(preview2, title2));
        columns.Children.Add(previews);
        dualPanel.Children.Add(columns);
        panel.Children.Add(dualPanel);
        panel.Children.Add(message);
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(DialogBase.Cancel("Cancel", () => { model.PendingColorFile = null; w.DialogResult = false; }));
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        saveButton = Ui.SmallButton("Save split channels as FITS…", SaveFits);
        right.Children.Add(saveButton);
        addButton = DialogBase.Default("Use as RGB", Add);
        right.Children.Add(addButton);
        buttons.Children.Add(right);
        panel.Children.Add(buttons);
        w.Content = panel;
        void UpdateMode()
        {
            rgbText.Visibility = dualBand ? Visibility.Collapsed : Visibility.Visible;
            dualPanel.Visibility = dualBand ? Visibility.Visible : Visibility.Collapsed;
            saveButton.Visibility = dualBand ? Visibility.Visible : Visibility.Collapsed;
            customRow.Visibility = settings.Preset == DualBandPreset.Custom ? Visibility.Visible : Visibility.Collapsed;
            var (a, b) = settings.OutputKinds;
            addButton.Content = dualBand ? $"Add {a.ShortName()} + {b.ShortName()}" : "Use as RGB";
            title1.Text = a.DisplayName(); title2.Text = b.DisplayName();
            first1.Text = $"{a.DisplayName()} from camera R/G/B"; second1.Text = $"{b.DisplayName()} from camera R/G/B";
            if (dualBand && preview1.Source == null) RefreshPreview();
        }
        modeUpdater = UpdateMode;
        UpdateMode();
        w.Closed += (_, _) => { if (model.PendingColorFile?.Id == pending.Id) model.PendingColorFile = null; };
    }

    Action? modeUpdater;

    static UIElement PreviewBox(Image img, TextBlock title)
    {
        var p = new StackPanel { Margin = new Thickness(6, 0, 0, 0) };
        p.Children.Add(new Border { Width = 160, Height = 120, Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), CornerRadius = new CornerRadius(4), Child = img });
        p.Children.Add(title);
        return p;
    }

    void Changed()
    {
        foreach (var r in rows) r.Refresh();
        modeUpdater?.Invoke();
        RefreshPreview();
    }

    void RefreshPreview()
    {
        var f = pending.File; var s = settings.Clone();
        int gen = ++previewGeneration;
        Task.Run(() =>
        {
            // Work on a small version for the preview.
            var r = f.Planes[0]; var g = f.Planes[1]; var b = f.Planes[2];
            while (r.Count > 200_000) { r = r.Downsampled2x(); g = g.Downsampled2x(); b = b.Downsampled2x(); }
            var (a, c) = DualBand.Split(r, g, b, s);
            foreach (var p in new[] { a, c }) AstroCore.Stretch.Resolve(new StretchSettings(), Statistics.Compute(p)).Apply(p);
            var imgs = (RasterWriter.MakeDisplayImage(a), RasterWriter.MakeDisplayImage(c));
            w.Dispatcher.BeginInvoke(() => { if (gen != previewGeneration) return; preview1.Source = imgs.Item1; preview2.Source = imgs.Item2; });
        });
    }

    (PlanarImage, PlanarImage) Split() => DualBand.Split(pending.File.Planes[0], pending.File.Planes[1], pending.File.Planes[2], settings);

    void Add()
    {
        if (!dualBand) model.AssignColorFileAsRGB(pending);
        else
        {
            var (a, b) = Split();
            var kinds = settings.OutputKinds;
            model.PendingColorFile = null;
            model.AddSplitChannels((kinds.first, a), (kinds.second, b), pending.File.FileName);
        }
        w.DialogResult = true;
    }

    void SaveFits()
    {
        var (a, b) = Split();
        var kinds = settings.OutputKinds;
        var baseName = Path.GetFileNameWithoutExtension(pending.File.Path);
        var dir = Panels.OpenFolder(w, $"Choose the folder for {baseName}-{kinds.first.RawValue()}.fits and -{kinds.second.RawValue()}.fits", Path.GetDirectoryName(pending.File.Path));
        if (dir == null) return;
        try
        {
            var u1 = Path.Combine(dir, $"{baseName}-{kinds.first.RawValue()}.fits");
            var u2 = Path.Combine(dir, $"{baseName}-{kinds.second.RawValue()}.fits");
            FitsWriter.Write([a], u1, FitsWriter.Depth.Float32, [("FILTER", kinds.first.RawValue(), "extracted line")]);
            FitsWriter.Write([b], u2, FitsWriter.Depth.Float32, [("FILTER", kinds.second.RawValue(), "extracted line")]);
            message.Text = $"Saved {Path.GetFileName(u1)} and {Path.GetFileName(u2)}";
        }
        catch (Exception e) { message.Text = e.Message; }
    }
}

/// <summary>Combine &amp; export.</summary>
public static class ExportWindow
{
    public static void Show(Window owner, ProjectModel model)
    {
        var w = DialogBase.Make(owner, "Combine & Export", 460);
        var panel = new StackPanel { Margin = new Thickness(16) };
        var format = RasterFormats.FromRawValue(model.Preferences.GetString("exportFormat")) ?? RasterFormat.Tiff16;
        var picker = new MenuPicker<RasterFormat>("Format", () => format, v => { format = v; model.Preferences.Set("exportFormat", v.RawValue()); }, RasterFormats.All, f => f.RawValue());
        panel.Children.Add(picker);
        bool linear = false;
        var linearBox = new CheckRow("Linear output (skip stretch and colour post-processing)", () => linear, v => linear = v, "Writes the normalised, weighted, combined channels without any stretch — for further processing in PixInsight or Siril.");
        panel.Children.Add(linearBox);
        if (model.ImageSize is Size size)
        {
            var c = model.Crop;
            panel.Children.Add(new TextBlock { Text = $"Output: {c?.Width ?? (int)size.Width} × {c?.Height ?? (int)size.Height} px{(c != null ? " (cropped)" : "")}, full resolution, rendered with the exact preview parameters.", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        var buttons = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(DialogBase.Cancel("Cancel", () => w.DialogResult = false));
        var export = DialogBase.Default("Export…", () =>
        {
            var baseName = model.Sources.Values.FirstOrDefault() is ChannelSource s ? Path.GetFileNameWithoutExtension(s.FileName) : "combined";
            var name = $"{baseName}-{model.Settings.PaletteID}{(linear ? "-linear" : "")}.{format.FileExtension()}";
            var path = Panels.Save(owner, name, format.DialogFilter(), "Save the combined image");
            if (path == null) return;
            w.DialogResult = true;
            model.Export(path, format, linear, err => { if (err != null) model.ErrorMessage = err.Message; });
        });
        export.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(export);
        panel.Children.Add(buttons);
        w.Content = panel;
        w.ShowDialog();
    }
}

/// <summary>Combination matrix editor (applies live).</summary>
public static class MatrixEditorWindow
{
    public static void Show(Window owner, ProjectModel model)
    {
        var w = DialogBase.Make(owner, "Combination matrix", 440);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = "Combination matrix", FontWeight = FontWeights.Bold, FontSize = 13 });
        panel.Children.Add(new TextBlock { Text = "Each output channel is a weighted sum of the (stretched or linear) input channels.", FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var kinds = ChannelKinds.All.Where(k => model.Sources.ContainsKey(k) || model.Settings.Matrix.Weights.ContainsKey(k)).ToList();
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int o = 0; o < 3; o++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        grid.RowDefinitions.Add(new RowDefinition());
        string[] heads = ["→ Red", "→ Green", "→ Blue"]; Brush[] cols = [Brushes.Red, Brushes.Green, Brushes.Blue];
        for (int o = 0; o < 3; o++) { var t = new TextBlock { Text = heads[o], FontWeight = FontWeights.Bold, Foreground = cols[o], TextAlignment = TextAlignment.Right, Margin = new Thickness(4) }; Grid.SetColumn(t, o + 1); Grid.SetRow(t, 0); grid.Children.Add(t); }
        var fields = new Dictionary<(ChannelKind, int), TextBox>();
        bool updating = false;
        void Refresh() { updating = true; foreach (var ((k, o), tb) in fields) tb.Text = model.Settings.Matrix.Weight(k, o).ToString("0.###", CultureInfo.InvariantCulture); updating = false; }
        int row = 1;
        foreach (var k in kinds)
        {
            grid.RowDefinitions.Add(new RowDefinition());
            var label = Ui.Row(Ui.Dot(Ui.Tint(k)), new TextBlock { Text = k.DisplayName(), VerticalAlignment = VerticalAlignment.Center });
            label.Margin = new Thickness(4); Grid.SetColumn(label, 0); Grid.SetRow(label, row); grid.Children.Add(label);
            for (int o = 0; o < 3; o++)
            {
                var tb = new TextBox { TextAlignment = TextAlignment.Right, Margin = new Thickness(4, 2, 4, 2), Width = 70 };
                var kk = k; int oo = o;
                tb.TextChanged += (_, _) =>
                {
                    if (updating) return;
                    if (float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || float.TryParse(tb.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
                        model.UpdateSettings(s => { s.Mode = CombineMode.Matrix; s.Matrix.Set(kk, oo, v); });
                };
                fields[(k, o)] = tb;
                Grid.SetColumn(tb, o + 1); Grid.SetRow(tb, row); grid.Children.Add(tb);
            }
            row++;
        }
        panel.Children.Add(grid);
        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Ui.SmallButton("Normalise rows", () =>
        {
            model.UpdateSettings(s =>
            {
                for (int o = 0; o < 3; o++)
                {
                    float sum = kinds.Sum(k => s.Matrix.Weight(k, o));
                    if (sum > 0) foreach (var k in kinds) s.Matrix.Set(k, o, s.Matrix.Weight(k, o) / sum);
                }
            });
            Refresh();
        }, "Scales the weights so every output column sums to 1."));
        left.Children.Add(Ui.SmallButton("Clear", () => { model.UpdateSettings(s => s.Matrix = PaletteMatrix.Empty); Refresh(); }));
        buttons.Children.Add(left);
        var done = DialogBase.Default("Done", () => w.DialogResult = true); done.HorizontalAlignment = HorizontalAlignment.Right;
        buttons.Children.Add(done);
        panel.Children.Add(buttons);
        w.Content = panel;
        Refresh();
        w.ShowDialog();
    }
}

/// <summary>Pixel Math editor (applies live while the expressions compile).</summary>
public static class PixelMathEditorWindow
{
    const string PresetsKey = "pixelMathPresets";

    static Dictionary<string, PixelMathProgram> UserPresets(Preferences prefs)
    {
        var out_ = new Dictionary<string, PixelMathProgram>();
        if (prefs.GetNode(PresetsKey) is JsonObject o)
            foreach (var kv in o) if (kv.Value is JsonObject p) out_[kv.Key] = PixelMathProgram.FromJson(p);
        return out_;
    }

    static void SaveUserPresets(Preferences prefs, Dictionary<string, PixelMathProgram> presets)
    {
        var o = new JsonObject();
        foreach (var (k, v) in presets) o[k] = v.ToJson();
        prefs.SetNode(PresetsKey, o);
    }

    public static void Show(Window owner, ProjectModel model)
    {
        var w = DialogBase.Make(owner, "Pixel Math", 800);
        var root = new DockPanel { Margin = new Thickness(16) };
        var left = new StackPanel { Width = 450 };
        var right = new StackPanel { Width = 290, Margin = new Thickness(16, 0, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        var program = model.Settings.PixelMath.Clone();
        var fields = new Dictionary<PixelMathProgram.Slot, TextBox>();
        var status = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        bool updating = false;
        void Validate()
        {
            try
            {
                var applied = model.Settings.Clone(); applied.Mode = CombineMode.PixelMath; applied.PixelMath = program.Clone();
                program.Compile();
                var missing = Renderer.MissingChannels(applied, model.AvailableKinds);
                if (missing.Count == 0) { status.Text = "✓ Expressions compile. Applied live."; status.Foreground = Brushes.Green; }
                else { status.Text = "Missing channels: " + string.Join(", ", missing.Select(k => k.DisplayName())); status.Foreground = Brushes.DarkOrange; }
                if (!model.Settings.PixelMath.Equals(program) || model.Settings.Mode != CombineMode.PixelMath)
                    model.UpdateSettings(s => { s.Mode = CombineMode.PixelMath; s.PixelMath = program.Clone(); s.PaletteID = "custom"; });
            }
            catch (PixelMathException e) { status.Text = "⚠ " + e.Detail; status.Foreground = Brushes.Red; }
        }
        void SetProgram(PixelMathProgram p)
        {
            program = p.Clone();
            updating = true;
            fields[PixelMathProgram.Slot.Red].Text = program.Red; fields[PixelMathProgram.Slot.Green].Text = program.Green; fields[PixelMathProgram.Slot.Blue].Text = program.Blue; fields[PixelMathProgram.Slot.Luminance].Text = program.Luminance;
            updating = false;
            Validate();
        }
        var head = new DockPanel();
        head.Children.Add(new TextBlock { Text = "Pixel Math", FontWeight = FontWeights.Bold, FontSize = 13 });
        var presetsButton = new Button { Content = "Presets ▾", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Right };
        presetsButton.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = presetsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var p in Palettes.All.Where(p => p.IsPixelMath))
            {
                var item = new MenuItem { Header = p.Name };
                var prog = ((PaletteKind.PixelMathKind)p.Kind).Program;
                item.Click += (_, _) => SetProgram(prog);
                menu.Items.Add(item);
            }
            var perm = new MenuItem { Header = "Permutation of current matrix" };
            perm.Click += (_, _) => SetProgram(MatrixAsProgram(model.Settings.Matrix));
            menu.Items.Add(perm);
            var mine = UserPresets(model.Preferences);
            if (mine.Count > 0)
            {
                menu.Items.Add(new Separator());
                foreach (var (name, prog) in mine.OrderBy(kv => kv.Key, StringComparer.Ordinal)) { var item = new MenuItem { Header = name }; item.Click += (_, _) => SetProgram(prog); menu.Items.Add(item); }
            }
            menu.IsOpen = true;
        };
        head.Children.Add(presetsButton);
        left.Children.Add(head);
        void Field(string label, PixelMathProgram.Slot slot, Brush color, Func<string> get, Action<string> set)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            row.Children.Add(new TextBlock { Text = label, Width = 80, TextAlignment = TextAlignment.Right, FontWeight = FontWeights.Bold, Foreground = color, Margin = new Thickness(0, 4, 8, 0) });
            var tb = new TextBox { Text = get(), FontFamily = new FontFamily("Consolas"), AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 44, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            tb.TextChanged += (_, _) => { if (updating) return; set(tb.Text); Validate(); };
            fields[slot] = tb;
            row.Children.Add(tb);
            left.Children.Add(row);
        }
        Field("R", PixelMathProgram.Slot.Red, Brushes.Red, () => program.Red, v => program.Red = v);
        Field("G", PixelMathProgram.Slot.Green, Brushes.Green, () => program.Green, v => program.Green = v);
        Field("B", PixelMathProgram.Slot.Blue, Brushes.Blue, () => program.Blue, v => program.Blue = v);
        Field("L (optional)", PixelMathProgram.Slot.Luminance, Brushes.Gray, () => program.Luminance, v => program.Luminance = v);
        left.Children.Add(status);
        var presetRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var nameBox = new TextBox { Width = 150, Margin = new Thickness(0, 0, 6, 0) };
        presetRow.Children.Add(nameBox);
        var saveBtn = Ui.SmallButton("Save preset", () =>
        {
            var p = UserPresets(model.Preferences); p[nameBox.Text.Trim()] = program.Clone(); SaveUserPresets(model.Preferences, p);
        });
        var deleteBtn = Ui.SmallButton("Delete", () => { var p = UserPresets(model.Preferences); p.Remove(nameBox.Text.Trim()); SaveUserPresets(model.Preferences, p); });
        nameBox.TextChanged += (_, _) => { saveBtn.IsEnabled = nameBox.Text.Trim().Length > 0; deleteBtn.Visibility = UserPresets(model.Preferences).ContainsKey(nameBox.Text.Trim()) ? Visibility.Visible : Visibility.Collapsed; };
        saveBtn.IsEnabled = false; deleteBtn.Visibility = Visibility.Collapsed;
        presetRow.Children.Add(saveBtn); presetRow.Children.Add(deleteBtn);
        var done = DialogBase.Default("Done", () => w.DialogResult = true); done.HorizontalAlignment = HorizontalAlignment.Right;
        presetRow.Children.Add(done);
        left.Children.Add(presetRow);
        right.Children.Add(new TextBlock { Text = "Reference", FontWeight = FontWeights.Bold, FontSize = 12 });
        right.Children.Add(new TextBlock { Text = "Available: " + string.Join(", ", ChannelKinds.All.Where(k => model.Sources.ContainsKey(k)).Select(k => k.PixelMathName())), FontSize = 11, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        foreach (var (title, text) in PixelMath.Help)
        {
            right.Children.Add(new TextBlock { Text = title, FontSize = 11, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 4, 0, 0) });
            right.Children.Add(new TextBlock { Text = text, FontSize = 10, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
        }
        right.Children.Add(new TextBlock { Text = "Channels are normalised and (for the pre-combine stage) stretched to [0,1] before the expressions run. Results are clamped to [0,1].", FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        root.Children.Add(right);
        root.Children.Add(left);
        w.Content = root;
        Validate();
        w.ShowDialog();
    }

    static PixelMathProgram MatrixAsProgram(PaletteMatrix m)
    {
        string Expr(int o)
        {
            var terms = m.OrderedChannels.Select(k => (k, w: m.Weight(k, o))).Where(t => t.w != 0).Select(t => t.w == 1 ? t.k.PixelMathName() : $"{Fmt.G3(t.w)}*{t.k.PixelMathName()}").ToList();
            return terms.Count == 0 ? "0" : string.Join(" + ", terms);
        }
        return new PixelMathProgram(Expr(0), Expr(1), Expr(2));
    }
}

public static class AboutWindow
{
    public static void Show(Window owner)
    {
        var version = typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "?";
        MessageBox.Show(owner, $"CombineColors {version}\n\nCombine monochrome astrophotography channels into colour images.\nWindows build; the processing matches the macOS app.", "About CombineColors", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
