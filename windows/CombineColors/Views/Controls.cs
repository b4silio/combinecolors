using System.Globalization;
using System.IO;
using Path = System.IO.Path;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Orientation = System.Windows.Controls.Orientation;
using Stretch = System.Windows.Media.Stretch;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AstroCore;

namespace CombineColors.Views;

/// <summary>A row whose displayed value comes from the model and is pushed back through a setter.</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>Labelled slider with an editable numeric field; double-click the label (or right-click the slider) to reset.</summary>
public sealed class SliderRow : Grid, IRefreshable
{
    readonly Func<float> get;
    readonly Action<float> set;
    readonly float lo, hi, defaultValue;
    readonly bool logarithmic;
    readonly string format;
    readonly Slider slider;
    readonly TextBox field;
    bool updating;

    public SliderRow(string title, Func<float> get, Action<float> set, float lo, float hi, float defaultValue, string format = "0.00", bool logarithmic = false, string? help = null, string? note = null, double labelWidth = 70)
    {
        this.get = get; this.set = set; this.lo = lo; this.hi = hi; this.defaultValue = defaultValue; this.logarithmic = logarithmic; this.format = format;
        Margin = new Thickness(0, 1, 0, 1);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Inlines.Add(title);
        if (note != null) label.Inlines.Add(new System.Windows.Documents.Run($" ({note})") { FontSize = 9, Foreground = Brushes.Gray });
        label.ToolTip = help ?? "Double-click or right-click to reset";
        label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { set(defaultValue); Refresh(); } };
        SetColumn(label, 0); Children.Add(label);
        slider = new Slider { Minimum = 0, Maximum = 1, SmallChange = 0.01, LargeChange = 0.1, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0), IsMoveToPointEnabled = true };
        slider.ValueChanged += (_, _) => { if (updating) return; var v = FromPosition(slider.Value); set(v); updating = true; field.Text = Format(v); updating = false; };
        slider.MouseRightButtonUp += (_, e) => { set(defaultValue); Refresh(); e.Handled = true; };
        SetColumn(slider, 1); Children.Add(slider);
        field = new TextBox { FontSize = 10, FontFamily = new FontFamily("Consolas"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(1, 0, 1, 0), Margin = new Thickness(2, 0, 0, 0) };
        field.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); e.Handled = true; } };
        field.LostFocus += (_, _) => Commit();
        SetColumn(field, 2); Children.Add(field);
        Refresh();
    }

    double ToPosition(float v)
    {
        if (logarithmic) return (Math.Log(Math.Max(v, lo)) - Math.Log(lo)) / (Math.Log(hi) - Math.Log(lo));
        return (v - lo) / (hi - lo);
    }

    float FromPosition(double p)
    {
        double t = Math.Min(Math.Max(p, 0), 1);
        if (logarithmic) return (float)Math.Exp(Math.Log(lo) + t * (Math.Log(hi) - Math.Log(lo)));
        return (float)(lo + t * (hi - lo));
    }

    string Format(float v) => v.ToString(format, CultureInfo.InvariantCulture);

    void Commit()
    {
        if (updating) return;
        if (float.TryParse(field.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || float.TryParse(field.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out v))
        {
            if (v != get()) set(v);
        }
        Refresh();
    }

    public void Refresh()
    {
        updating = true;
        var v = get();
        slider.Value = ToPosition(v);
        field.Text = Format(v);
        updating = false;
    }
}

/// <summary>Integer slider in the same layout as <see cref="SliderRow"/>.</summary>
public sealed class IntSliderRow : Grid, IRefreshable
{
    readonly Func<int> get; readonly Action<int> set; readonly int defaultValue;
    readonly Slider slider; readonly TextBlock value;
    bool updating;

    public IntSliderRow(string title, Func<int> get, Action<int> set, int lo, int hi, int defaultValue, string? help = null, double labelWidth = 70)
    {
        this.get = get; this.set = set; this.defaultValue = defaultValue;
        Margin = new Thickness(0, 1, 0, 1);
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
        var label = new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, ToolTip = help ?? "Double-click or right-click to reset" };
        label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { set(defaultValue); Refresh(); } };
        SetColumn(label, 0); Children.Add(label);
        slider = new Slider { Minimum = lo, Maximum = hi, TickFrequency = 1, IsSnapToTickEnabled = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 2, 0), IsMoveToPointEnabled = true };
        slider.ValueChanged += (_, _) => { if (updating) return; int v = (int)Math.Round(slider.Value); set(v); value.Text = v.ToString(); };
        slider.MouseRightButtonUp += (_, e) => { set(defaultValue); Refresh(); e.Handled = true; };
        SetColumn(slider, 1); Children.Add(slider);
        value = new TextBlock { FontSize = 10, FontFamily = new FontFamily("Consolas"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        SetColumn(value, 2); Children.Add(value);
        Refresh();
    }

    public void Refresh() { updating = true; var v = get(); slider.Value = v; value.Text = v.ToString(); updating = false; }
}

/// <summary>Slider with a tiny value label on the right; double-click the label or right-click the slider to reset to 0.</summary>
public sealed class MiniSlider : Grid, IRefreshable
{
    readonly Func<float> get; readonly Action<float> set; readonly string format;
    readonly Slider slider; readonly TextBlock value;
    bool updating;

    public MiniSlider(Func<float> get, Action<float> set, float lo, float hi, string format)
    {
        this.get = get; this.set = set; this.format = format;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        slider = new Slider { Minimum = lo, Maximum = hi, VerticalAlignment = VerticalAlignment.Center, IsMoveToPointEnabled = true, Margin = new Thickness(2, 0, 2, 0) };
        slider.ValueChanged += (_, _) => { if (updating) return; set((float)slider.Value); value.Text = Fmt((float)slider.Value); };
        slider.MouseRightButtonUp += (_, e) => { set(0); Refresh(); e.Handled = true; };
        SetColumn(slider, 0); Children.Add(slider);
        value = new TextBlock { FontSize = 9, FontFamily = new FontFamily("Consolas"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) { set(0); Refresh(); } };
        SetColumn(value, 1); Children.Add(value);
        Refresh();
    }

    string Fmt(float v) => string.Format(CultureInfo.InvariantCulture, format, v);

    public void Refresh() { updating = true; var v = get(); slider.Value = v; value.Text = Fmt(v); value.Foreground = v == 0 ? (Brush)Application.Current.Resources["SubtleText"] : (Brush)Application.Current.Resources["TextBrush"]; updating = false; }
}

/// <summary>Labelled drop-down over a fixed list of options.</summary>
public sealed class MenuPicker<T> : StackPanel, IRefreshable where T : notnull
{
    readonly Func<T> get; readonly Action<T> set; readonly IReadOnlyList<T> options; readonly Func<T, string> label;
    readonly ComboBox combo;
    bool updating;

    public MenuPicker(string title, Func<T> get, Action<T> set, IReadOnlyList<T> options, Func<T, string> label, string? help = null)
    {
        this.get = get; this.set = set; this.options = options; this.label = label;
        Orientation = System.Windows.Controls.Orientation.Horizontal; Margin = new Thickness(0, 1, 0, 1);
        if (!string.IsNullOrEmpty(title)) Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Margin = new Thickness(0, 0, 6, 0) });
        combo = new ComboBox { FontSize = 11, MinWidth = 90, Padding = new Thickness(4, 1, 4, 1) };
        if (help != null) combo.ToolTip = help;
        foreach (var o in options) combo.Items.Add(label(o));
        combo.SelectionChanged += (_, _) => { if (updating || combo.SelectedIndex < 0) return; set(options[combo.SelectedIndex]); };
        Children.Add(combo);
        Refresh();
    }

    /// <summary>Re-labels the options (e.g. when availability changes).</summary>
    public void RefreshLabels()
    {
        updating = true;
        for (int i = 0; i < options.Count; i++) combo.Items[i] = label(options[i]);
        updating = false;
        Refresh();
    }

    public void Refresh()
    {
        updating = true;
        int i = -1;
        var v = get();
        for (int k = 0; k < options.Count; k++) if (Equals(options[k], v)) { i = k; break; }
        combo.SelectedIndex = i;
        updating = false;
    }
}

public sealed class CheckRow : CheckBox, IRefreshable
{
    readonly Func<bool> get; readonly Action<bool> set; bool updating;
    public CheckRow(string title, Func<bool> get, Action<bool> set, string? help = null)
    {
        this.get = get; this.set = set;
        Content = title; FontSize = 11; Margin = new Thickness(0, 2, 0, 2);
        if (help != null) ToolTip = help;
        Checked += (_, _) => { if (!updating) set(true); };
        Unchecked += (_, _) => { if (!updating) set(false); };
        Refresh();
    }
    public void Refresh() { updating = true; IsChecked = get(); updating = false; }
}

/// <summary>A note that re-reads its text.</summary>
public sealed class NoteRow : TextBlock, IRefreshable
{
    readonly Func<string?> get; readonly Func<Brush>? brush;
    public NoteRow(Func<string?> get, double fontSize = 10, Func<Brush>? brush = null, FontFamily? family = null)
    {
        this.get = get; this.brush = brush;
        FontSize = fontSize; TextWrapping = TextWrapping.Wrap; Foreground = Brushes.Gray; Margin = new Thickness(0, 2, 0, 2);
        if (family != null) FontFamily = family;
        Refresh();
    }
    public void Refresh()
    {
        var t = get();
        Text = t ?? "";
        Visibility = string.IsNullOrEmpty(t) ? Visibility.Collapsed : Visibility.Visible;
        if (brush != null) Foreground = brush();
    }
}

/// <summary>
/// Block of the horizontal controls strip: a vertical title bar on the left (click to collapse or
/// expand) and, when expanded, the parameters in a fixed-width column that scrolls if needed.
/// </summary>
public sealed class ControlBlock : Border
{
    readonly Button titleButton;
    readonly ScrollViewer scroller;
    public StackPanel Content { get; } = new StackPanel { Margin = new Thickness(6, 5, 6, 5) };
    bool expanded = true;
    public string Title { get; }
    public event Action<bool>? ExpandedChanged;

    public ControlBlock(string title, double width)
    {
        Title = title;
        CornerRadius = new CornerRadius(6);
        BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)); BorderThickness = new Thickness(1);
        Background = new SolidColorBrush(Color.FromArgb(14, 255, 255, 255));
        Margin = new Thickness(3, 0, 3, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock { Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.Bold, LayoutTransform = new RotateTransform(-90), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        titleButton = new Button { Content = text, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Cursor = Cursors.Hand, Focusable = false };
        titleButton.Click += (_, _) => { Expanded = !expanded; ExpandedChanged?.Invoke(expanded); };
        Grid.SetColumn(titleButton, 0); grid.Children.Add(titleButton);
        scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Width = width, Content = Content };
        Grid.SetColumn(scroller, 1); grid.Children.Add(scroller);
        Child = grid;
        UpdateLook();
    }

    public bool Expanded
    {
        get => expanded;
        set { expanded = value; UpdateLook(); }
    }

    void UpdateLook()
    {
        scroller.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        titleButton.Foreground = expanded ? (Brush)Application.Current.Resources["AccentBrush"] : Brushes.Gray;
        titleButton.Background = expanded ? new SolidColorBrush(Color.FromArgb(50, 10, 110, 209)) : new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        titleButton.ToolTip = expanded ? $"Collapse {Title}" : $"Expand {Title}";
    }

    public void RefreshAll()
    {
        foreach (var c in Content.Children) RefreshTree(c);
    }

    static void RefreshTree(object node)
    {
        if (node is IRefreshable r) r.Refresh();
        else if (node is Panel p) foreach (var c in p.Children) RefreshTree(c);
        else if (node is Decorator d && d.Child != null) RefreshTree(d.Child);
        else if (node is ContentControl cc && cc.Content is UIElement e) RefreshTree(e);
    }
}

public static class Ui
{
    public static TextBlock Caption(string text, double size = 10, bool wrap = true) => new() { Text = text, FontSize = size, Foreground = Brushes.Gray, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, Margin = new Thickness(0, 2, 0, 2) };

    public static Button SmallButton(string text, Action onClick, string? help = null)
    {
        var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(6, 1, 6, 1), Margin = new Thickness(0, 0, 4, 0), MinHeight = 20 };
        if (help != null) b.ToolTip = help;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Button IconButton(string glyph, Action onClick, string? help = null, double size = 12)
    {
        var b = new Button { Content = new TextBlock { Text = glyph, FontSize = size, FontFamily = new FontFamily("Segoe UI Symbol") }, Padding = new Thickness(5, 0, 5, 0), Margin = new Thickness(0, 0, 4, 0), MinHeight = 20, MinWidth = 24 };
        if (help != null) b.ToolTip = help;
        b.Click += (_, _) => onClick();
        return b;
    }

    public static Ellipse Dot(Brush brush, double size = 8) => new() { Width = size, Height = size, Fill = brush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };

    public static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    public static Brush Tint(ChannelKind k) => k switch
    {
        ChannelKind.Ha => Brushes.Red,
        ChannelKind.OIII => new SolidColorBrush(Color.FromRgb(48, 176, 199)),
        ChannelKind.SII => Brushes.Orange,
        ChannelKind.Hb => Brushes.RoyalBlue,
        ChannelKind.Red => new SolidColorBrush(Color.FromRgb(255, 77, 77)),
        ChannelKind.Green => new SolidColorBrush(Color.FromRgb(77, 230, 77)),
        ChannelKind.Blue => new SolidColorBrush(Color.FromRgb(102, 128, 255)),
        _ => Brushes.Gray,
    };

    public static string G4(float v) => Fmt.G4(v);

    /// <summary>Segmented control: one toggle per option.</summary>
    public static StackPanel Segmented<T>(IReadOnlyList<T> options, Func<T, string> label, Func<T> get, Action<T> set, out Action refresh, double fontSize = 11) where T : notnull
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<ToggleButton>();
        bool updating = false;
        for (int i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var tb = new ToggleButton { Content = label(o), FontSize = fontSize, Padding = new Thickness(7, 1, 7, 1), MinHeight = 20, Focusable = false };
            tb.Checked += (_, _) => { if (updating) return; set(o); };
            tb.Click += (_, _) => { if (tb.IsChecked != true) tb.IsChecked = true; };
            buttons.Add(tb);
            panel.Children.Add(tb);
        }
        void Refresh()
        {
            updating = true;
            var v = get();
            for (int i = 0; i < options.Count; i++) buttons[i].IsChecked = Equals(options[i], v);
            updating = false;
        }
        refresh = Refresh;
        Refresh();
        return panel;
    }
}

/// <summary>File dialogs.</summary>
public static class Panels
{
    public const string ImageFilter = "Images|*.fits;*.fit;*.fts;*.tif;*.tiff;*.png;*.jpg;*.jpeg|FITS|*.fits;*.fit;*.fts|TIFF|*.tif;*.tiff|All files|*.*";

    public static List<string> OpenImages(Window owner)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Filter = ImageFilter, Title = "Choose FITS or TIFF channel images" };
        return d.ShowDialog(owner) == true ? DropHelpers.ExpandDirectories(d.FileNames) : new List<string>();
    }

    public static string? OpenFolder(Window owner, string title, string? initial = null)
    {
        var d = new Microsoft.Win32.OpenFolderDialog { Title = title, InitialDirectory = initial ?? "" };
        return d.ShowDialog(owner) == true ? d.FolderName : null;
    }

    public static string? Save(Window owner, string name, string filter, string title, string? initialDir = null)
    {
        var d = new Microsoft.Win32.SaveFileDialog { FileName = name, Filter = filter, Title = title, AddExtension = true, OverwritePrompt = true };
        if (initialDir != null && Directory.Exists(initialDir)) d.InitialDirectory = initialDir;
        return d.ShowDialog(owner) == true ? d.FileName : null;
    }

    public static string? OpenProject(Window owner)
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "CombineColors project|*.json|All files|*.*", Title = "Choose a CombineColors project (.json)" };
        return d.ShowDialog(owner) == true ? d.FileName : null;
    }
}

public static class DropHelpers
{
    public static List<string> ExpandDirectories(IEnumerable<string> paths)
    {
        var out_ = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
                out_.AddRange(Directory.EnumerateFiles(p).Where(ImageLoader.IsSupported).OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase));
            else out_.Add(p);
        }
        return out_;
    }

    public static List<string> FromDrop(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return new List<string>();
        var files = (string[]?)e.Data.GetData(DataFormats.FileDrop) ?? [];
        return files.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToList();
    }
}
