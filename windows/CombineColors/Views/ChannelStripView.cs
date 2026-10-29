using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Orientation = System.Windows.Controls.Orientation;
using Stretch = System.Windows.Media.Stretch;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Views;

/// <summary>
/// Column of channel slots on the left of the preview, with the preview histogram underneath.
/// Press and hold a slot to see that channel full size; keep the button down and move over other
/// slots to switch between them.
/// </summary>
public sealed class ChannelStripView : DockPanel
{
    public const double SlotWidth = 120, SlotHeight = 80, SlotSpacing = 6, Pad = 6;
    readonly ProjectModel model;
    readonly Window owner;
    readonly StackPanel slots = new() { Margin = new Thickness(Pad) };
    readonly HistogramView histogram = new() { Width = SlotWidth, Height = 64, Margin = new Thickness(Pad) };
    readonly Dictionary<ChannelKind, Border> slotViews = new();
    bool peeking;

    public ChannelStripView(ProjectModel model, Window owner)
    {
        this.model = model; this.owner = owner;
        Width = SlotWidth + 2 * Pad;
        Background = (Brush)Application.Current.Resources["PanelBackground"];
        DockPanel.SetDock(histogram, Dock.Bottom);
        Children.Add(histogram);
        var sep = new Separator(); DockPanel.SetDock(sep, Dock.Bottom); Children.Add(sep);
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = slots };
        Children.Add(scroller);
        // One gesture for the whole column so that a held press can move between slots.
        slots.PreviewMouseLeftButtonDown += OnPress;
        slots.PreviewMouseMove += OnMove;
        slots.PreviewMouseLeftButtonUp += (_, _) => EndPeek();
        slots.LostMouseCapture += (_, _) => EndPeek();
        model.Changed += OnChanged;
        Rebuild();
    }

    void OnChanged(string what)
    {
        switch (what)
        {
            case Change.Sources: case Change.SlotThumbnails: case Change.Dust: case Change.Peek: case Change.Crop: Rebuild(); break;
            case Change.Frame: histogram.Histogram = model.Histogram; break;
        }
    }

    IEnumerable<ChannelKind> Order() => ChannelKinds.All.Where(k => model.Sources.ContainsKey(k)).Concat(ChannelKinds.All.Where(k => !model.Sources.ContainsKey(k)));

    void Rebuild()
    {
        slots.Children.Clear();
        slotViews.Clear();
        foreach (var kind in Order())
        {
            var slot = BuildSlot(kind);
            slotViews[kind] = slot;
            slots.Children.Add(slot);
        }
    }

    Border BuildSlot(ChannelKind kind)
    {
        model.Sources.TryGetValue(kind, out var source);
        bool peek = model.PeekChannel == kind;
        var tint = Ui.Tint(kind);
        var border = new Border
        {
            Width = SlotWidth, Height = SlotHeight, Margin = new Thickness(0, 0, 0, SlotSpacing), CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(source == null ? (byte)30 : (byte)153, 0, 0, 0)),
            BorderBrush = tint, BorderThickness = new Thickness(source == null ? 1 : (peek ? 3 : 2)), AllowDrop = true, Tag = kind,
        };
        if (source == null) border.Opacity = 0.75;
        var grid = new Grid();
        if (source != null && model.SlotThumbnails.TryGetValue(kind, out var thumb))
            grid.Children.Add(new Image { Source = thumb, Stretch = Stretch.Uniform, Margin = new Thickness(2), IsHitTestVisible = false });
        else
            grid.Children.Add(new TextBlock { Text = $"+  Drop {kind.ShortName()}", FontSize = 10, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (source != null)
        {
            // The channel is identified by the frame colour and a small badge.
            grid.Children.Add(new Border
            {
                Background = tint, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false,
                Child = new TextBlock { Text = kind.ShortName(), FontSize = 9, FontWeight = FontWeights.Bold, Foreground = Brushes.White },
            });
            int n = model.DustSpots(kind).Count;
            if (n > 0)
                grid.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
                    Child = new TextBlock { Text = $"◌ {n}", FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White },
                    ToolTip = $"{n} dust spot{(n == 1 ? "" : "s")} healed on this channel",
                });
            var menuButton = new Button
            {
                Content = new TextBlock { Text = "⋯", FontSize = 12, FontWeight = FontWeights.Bold, Foreground = Brushes.White, Margin = new Thickness(0, -3, 0, 0) },
                Width = 18, Height = 18, Padding = new Thickness(0), Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), BorderThickness = new Thickness(0), Focusable = false, Tag = "menu",
            };
            menuButton.Click += (_, _) => { EndPeek(); ShowMenu(kind, source, menuButton); };
            grid.Children.Add(menuButton);
            var s = source;
            border.ToolTip = $"{kind.DisplayName()} — {s.FileName}\n{s.FormatDescription}\nmedian {Ui.G4(s.Stats.Median)}, background {Ui.G4(s.MaskedStats.Background)}, noise {Ui.G4(s.Stats.Noise)}, signal {Ui.G4(s.MaskedStats.SignalStrength)}σ\nPress and hold to see this channel full size; move over other slots to switch";
        }
        else border.ToolTip = $"Drop a {kind.DisplayName()} image here";
        border.Child = grid;
        border.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        border.Drop += (_, e) =>
        {
            var files = DropHelpers.ExpandDirectories(DropHelpers.FromDrop(e));
            if (files.Count == 0) return;
            model.LoadFile(files[0], kind);
            if (files.Count > 1) model.LoadFiles(files.Skip(1).ToList());
            e.Handled = true;
        };
        return border;
    }

    void ShowMenu(ChannelKind kind, ChannelSource s, Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var move = new MenuItem { Header = "Move to" };
        foreach (var k in ChannelKinds.All.Where(k => k != kind))
        {
            var item = new MenuItem { Header = k.DisplayName() + (model.Sources.ContainsKey(k) ? " (swap)" : "") };
            item.Click += (_, _) => model.Reassign(kind, k);
            move.Items.Add(item);
        }
        menu.Items.Add(move);
        menu.Items.Add(new Separator());
        var heal = new MenuItem { Header = "Heal Dust Spots…" }; heal.Click += (_, _) => model.BeginDustEditing(kind); menu.Items.Add(heal);
        var save = new MenuItem { Header = "Save channel as FITS…" };
        save.Click += (_, _) =>
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(s.FileName) + $"-{kind.RawValue()}.fits";
            var path = Panels.Save(owner, name, "FITS image|*.fits", "Save the channel (cropped if a crop is set).");
            if (path != null) { try { model.ExportChannel(kind, path, RasterFormat.Fits32); } catch (Exception ex) { model.ErrorMessage = ex.Message; } }
        };
        menu.Items.Add(save);
        var remove = new MenuItem { Header = "Remove" }; remove.Click += (_, _) => model.RemoveChannel(kind); menu.Items.Add(remove);
        menu.IsOpen = true;
    }

    /// <summary>Loaded channel under a point of the column's content (null over empty slots, gaps and the slot's menu button).</summary>
    ChannelKind? SlotAt(Point p, DependencyObject? hit)
    {
        for (var d = hit; d != null; d = VisualTreeHelper.GetParent(d))
        {
            if (d is Button b && Equals(b.Tag, "menu")) return null;
            if (d is Border bd && bd.Tag is ChannelKind k) return model.Sources.ContainsKey(k) ? k : null;
            if (d == slots) break;
        }
        return null;
    }

    void OnPress(object sender, MouseButtonEventArgs e)
    {
        var k = SlotAt(e.GetPosition(slots), e.OriginalSource as DependencyObject);
        if (k == null) return;
        peeking = true;
        model.PeekChannel = k;
        slots.CaptureMouse();
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (!peeking) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndPeek(); return; }
        var p = e.GetPosition(slots);
        var hit = slots.InputHitTest(p) as DependencyObject;
        var k = SlotAt(p, hit);
        if (model.PeekChannel != k) model.PeekChannel = k;
    }

    void EndPeek()
    {
        if (!peeking) return;
        peeking = false;
        if (slots.IsMouseCaptured) slots.ReleaseMouseCapture();
        if (model.PeekChannel != null) model.PeekChannel = null;
    }
}

/// <summary>Logarithmic RGB histogram of the preview.</summary>
public sealed class HistogramView : FrameworkElement
{
    List<int[]> histogram = new();
    public List<int[]> Histogram { get => histogram; set { histogram = value; InvalidateVisual(); } }
    static readonly Brush[] Colors = [new SolidColorBrush(Color.FromArgb(90, 255, 0, 0)), new SolidColorBrush(Color.FromArgb(90, 0, 255, 0)), new SolidColorBrush(Color.FromArgb(90, 0, 0, 255))];

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(100, 0, 0, 0)), null, new Rect(0, 0, ActualWidth, ActualHeight), 4, 4);
        if (histogram.Count == 0) return;
        double maxV = Math.Max(1, histogram.SelectMany(h => h).DefaultIfEmpty(1).Max());
        for (int c = 0; c < Math.Min(3, histogram.Count); c++)
        {
            var bins = histogram[c];
            if (bins.Length < 2) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, ActualHeight), true, true);
                for (int i = 0; i < bins.Length; i++)
                {
                    double x = ActualWidth * i / (bins.Length - 1);
                    double y = ActualHeight - ActualHeight * Math.Log(1 + bins[i]) / Math.Log(1 + maxV);
                    g.LineTo(new Point(x, y), true, false);
                }
                g.LineTo(new Point(ActualWidth, ActualHeight), true, false);
            }
            geo.Freeze();
            dc.DrawGeometry(Colors[c], null, geo);
        }
    }
}
