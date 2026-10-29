using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Orientation = System.Windows.Controls.Orientation;
using Stretch = System.Windows.Media.Stretch;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Views;

/// <summary>Palette previews (right pane): a header row of controls, then the thumbnails grouped by family.</summary>
public sealed class PaletteGridView : DockPanel
{
    readonly ProjectModel model;
    readonly StackPanel body = new() { Margin = new Thickness(8) };
    readonly Dictionary<string, Image> images = new();
    readonly Dictionary<string, Border> frames = new();
    readonly TextBlock hiddenCount = new() { FontSize = 10, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
    readonly TextBlock description = new() { FontSize = 10, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    readonly TextBlock modeText = new() { FontSize = 9, Foreground = Brushes.DarkGray, Margin = new Thickness(0, 2, 0, 0) };
    readonly Slider sizeSlider;
    readonly Action refreshFilter, refreshRegion;
    string structure = "";
    double ThumbSize => sizeSlider.Value;
    public event Action? MatrixRequested, PixelMathRequested;

    public PaletteGridView(ProjectModel model)
    {
        this.model = model;
        var header = new StackPanel { Margin = new Thickness(8, 6, 8, 6) };
        var row1 = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var from = new MenuPicker<ThumbnailRegion>("From", () => model.ThumbnailRegion, v => model.ThumbnailRegion = v, ThumbnailRegions.All, r => r.DisplayName());
        refreshRegion = from.Refresh;
        row1.Children.Add(from);
        var filter = Ui.Segmented(new[] { PaletteFilter.Favorites, PaletteFilter.All }, f => f == PaletteFilter.All ? "All" : "Favourites", () => model.PaletteFilter, v => model.PaletteFilter = v, out refreshFilter);
        filter.Margin = new Thickness(8, 0, 0, 0);
        row1.Children.Add(filter);
        sizeSlider = new Slider { Minimum = 70, Maximum = 220, Value = model.Preferences.GetDouble("paletteThumbSize") ?? 100, Width = 90, VerticalAlignment = VerticalAlignment.Center, ToolTip = "Preview size", HorizontalAlignment = HorizontalAlignment.Right };
        sizeSlider.ValueChanged += (_, _) => { model.Preferences.Set("paletteThumbSize", sizeSlider.Value); Rebuild(force: true); };
        var sizeRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        sizeRow.Children.Add(new TextBlock { Text = "▫", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        sizeRow.Children.Add(sizeSlider);
        sizeRow.Children.Add(new TextBlock { Text = "◻", Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center });
        row1.Children.Add(sizeRow);
        header.Children.Add(row1);
        var row2 = new DockPanel();
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Ui.SmallButton("Matrix…", () => MatrixRequested?.Invoke()));
        left.Children.Add(Ui.SmallButton("Pixel Math…", () => PixelMathRequested?.Invoke()));
        left.Children.Add(hiddenCount);
        row2.Children.Add(left);
        var reset = Ui.SmallButton("Reset all", () =>
        {
            var s = new CombineSettings();
            if (model.CurrentPalette is Palette pal) s.Select(pal);
            model.Settings = s;
        }, "Reset all settings to the defaults for the current palette (the crop and presets stay).");
        reset.HorizontalAlignment = HorizontalAlignment.Right; reset.Margin = new Thickness(0);
        row2.Children.Add(reset);
        header.Children.Add(row2);
        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);
        var sep = new Separator(); DockPanel.SetDock(sep, Dock.Top); Children.Add(sep);
        Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = body });
        model.Changed += OnChanged;
        Rebuild(force: true);
    }

    void OnChanged(string what)
    {
        switch (what)
        {
            case Change.Sources: case Change.PaletteVisibility: case Change.Settings: Rebuild(force: false); break;
            case Change.Thumbnails: UpdateThumbnails(); refreshRegion(); break;
        }
    }

    void UpdateThumbnails()
    {
        foreach (var (id, img) in images)
            if (model.Thumbnails.TryGetValue(id, out var bmp) && !ReferenceEquals(img.Source, bmp)) img.Source = bmp;
    }

    void Rebuild(bool force)
    {
        refreshFilter();
        var palettes = model.VisiblePalettes;
        var sig = string.Join(",", palettes.Select(p => p.Id)) + "|" + model.Settings.PaletteID + "|" + model.PaletteFilter + "|" + ThumbSize + "|" + string.Join(",", model.HiddenPaletteIds.OrderBy(s => s));
        if (!force && sig == structure) { UpdateSelection(); return; }
        structure = sig;
        body.Children.Clear(); images.Clear(); frames.Clear();
        int hidden = Palettes.Available(model.AvailableKinds).Count(p => model.IsPaletteHidden(p.Id));
        hiddenCount.Text = hidden == 0 ? "" : $"{hidden} hidden";
        if (palettes.Count == 0)
            body.Children.Add(Ui.Caption(model.Sources.Count == 0 ? "Load channels to see palettes." : "Everything is hidden — switch to All and click the eye icons.", 11));
        foreach (var group in Palettes.Groups)
        {
            var items = palettes.Where(p => p.Group == group).ToList();
            if (items.Count == 0) continue;
            body.Children.Add(new TextBlock { Text = group, FontSize = 11, Foreground = Brushes.Gray, Margin = new Thickness(0, 4, 0, 2) });
            var wrap = new WrapPanel();
            foreach (var p in items) wrap.Children.Add(BuildCell(p));
            body.Children.Add(wrap);
        }
        body.Children.Add(description);
        body.Children.Add(modeText);
        UpdateSelection();
        UpdateThumbnails();
    }

    void UpdateSelection()
    {
        foreach (var (id, frame) in frames)
        {
            bool selected = model.Settings.PaletteID == id;
            frame.BorderBrush = selected ? (Brush)Application.Current.Resources["AccentBrush"] : new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            frame.BorderThickness = new Thickness(selected ? 2 : 1);
            if (frame.Tag is TextBlock name) name.Foreground = selected ? (Brush)Application.Current.Resources["AccentBrush"] : (model.IsPaletteHidden(id) ? Brushes.Gray : (Brush)Application.Current.Resources["TextBrush"]);
        }
        description.Text = model.CurrentPalette?.Description ?? "";
        var mode = model.Settings.Mode;
        modeText.Text = mode == CombineMode.PixelMath ? "Combination: Pixel Math (editable)" : (mode == CombineMode.Monochrome ? "Combination: weighted average of all channels" : "Combination: matrix (editable)");
    }

    FrameworkElement BuildCell(Palette p)
    {
        double w = ThumbSize, h = ThumbSize * 0.64;
        bool hidden = model.IsPaletteHidden(p.Id);
        var cell = new StackPanel { Width = w, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = p.Description };
        var frame = new Border { Height = h, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), ClipToBounds = true };
        var grid = new Grid();
        var img = new Image { Stretch = Stretch.UniformToFill, Opacity = hidden ? 0.35 : 1, IsHitTestVisible = false };
        if (model.Thumbnails.TryGetValue(p.Id, out var bmp)) img.Source = bmp;
        images[p.Id] = img;
        grid.Children.Add(img);
        var eye = new Button
        {
            Content = new TextBlock { Text = hidden ? "⊘" : "◉", FontSize = 10, Foreground = hidden ? Brushes.Orange : Brushes.White }, Padding = new Thickness(3, 0, 3, 0),
            Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(3),
            ToolTip = hidden ? "Show in favourites" : "Hide from favourites", Focusable = false,
            Visibility = model.PaletteFilter == PaletteFilter.All ? Visibility.Visible : Visibility.Hidden,
        };
        eye.Click += (_, e) => { model.SetPaletteHidden(p.Id, !model.IsPaletteHidden(p.Id)); e.Handled = true; };
        grid.Children.Add(eye);
        if (p.IsPixelMath)
            grid.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), CornerRadius = new CornerRadius(3), Padding = new Thickness(3, 0, 3, 0), Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Child = new TextBlock { Text = "ƒ", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            });
        frame.Child = grid;
        var name = new TextBlock { Text = p.Name, FontSize = 10, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
        frame.Tag = name;
        frames[p.Id] = frame;
        cell.Children.Add(frame);
        cell.Children.Add(name);
        cell.MouseLeftButtonUp += (_, e) => { model.Select(p); e.Handled = true; };
        if (model.PaletteFilter != PaletteFilter.All)
        {
            cell.MouseEnter += (_, _) => eye.Visibility = Visibility.Visible;
            cell.MouseLeave += (_, _) => eye.Visibility = Visibility.Hidden;
        }
        return cell;
    }
}
