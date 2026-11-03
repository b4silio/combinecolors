using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Orientation = Avalonia.Layout.Orientation;
using Avalonia.Media;
using Stretch = Avalonia.Media.Stretch;
using Avalonia.VisualTree;
using AstroCore;
using PixelRect = AstroCore.PixelRect;
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
    readonly StackPanel slots = new() { Margin = new Thickness(Pad), Background = Brushes.Transparent };
    readonly HistogramView histogram = new() { Width = SlotWidth, Height = 64, Margin = new Thickness(Pad) };
    readonly Dictionary<ChannelKind, Border> slotViews = new();
    bool peeking;
    IPointer? captured;

    public ChannelStripView(ProjectModel model, Window owner)
    {
        this.model = model; this.owner = owner;
        Width = SlotWidth + 2 * Pad;
        Background = AppTheme.PanelBackground;
        DockPanel.SetDock(histogram, Dock.Bottom);
        Children.Add(histogram);
        var sep = new Separator(); DockPanel.SetDock(sep, Dock.Bottom); Children.Add(sep);
        var scroller = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = slots };
        Children.Add(scroller);
        // One gesture for the whole column so that a held press can move between slots.
        slots.AddHandler(PointerPressedEvent, OnPress, RoutingStrategies.Tunnel);
        slots.AddHandler(PointerMovedEvent, OnMove, RoutingStrategies.Tunnel);
        slots.AddHandler(PointerReleasedEvent, (_, _) => EndPeek(), RoutingStrategies.Tunnel);
        slots.PointerCaptureLost += (_, _) => EndPeek();
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
            Background = source == null ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(153, 0, 0, 0)),
            BorderBrush = tint, BorderThickness = new Thickness(source == null ? 1 : (peek ? 3 : 2)), Tag = kind,
        };
        if (source == null) border.Opacity = 0.75;
        var grid = new Grid();
        if (source != null && model.SlotThumbnails.TryGetValue(kind, out var thumb))
            grid.Children.Add(new Image { Source = thumb, Stretch = Stretch.Uniform, Margin = new Thickness(2), IsHitTestVisible = false });
        else
            grid.Children.Add(new TextBlock { Text = $"+  Drop {kind.ShortName()}", FontSize = 10, Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        if (source != null)
        {
            // The channel is identified by the frame colour and a small badge; the corrections badge sits
            // directly underneath it (same left edge). Dust count bottom-left, gradient bottom-right, menu top-right.
            var tags = new StackPanel { Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            tags.Children.Add(new Border
            {
                Background = tint, CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock { Text = kind.ShortName(), FontSize = 9, FontWeight = FontWeight.Bold, Foreground = Brushes.White },
            });
            if (model.HasCorrection(kind))
            {
                var cbadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = "✎", FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                };
                ToolTip.SetTip(cbadge, "Hand-painted corrections applied to this channel");
                tags.Children.Add(cbadge);
            }
            grid.Children.Add(tags);
            int n = model.DustSpots(kind).Count;
            if (n > 0)
            {
                var badge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
                    Child = new TextBlock { Text = $"◌ {n}", FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                };
                ToolTip.SetTip(badge, $"{n} dust spot{(n == 1 ? "" : "s")} healed on this channel");
                grid.Children.Add(badge);
            }
            if (model.GradientSpecFor(kind) is GradientSpec gspec)
            {
                var gbadge = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 0, 4, 1), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
                    Child = new TextBlock { Text = "◐", FontSize = 9, FontWeight = FontWeight.SemiBold, Foreground = Brushes.White },
                };
                ToolTip.SetTip(gbadge, $"Gradient removed on this channel ({gspec.Gesture.Shape.DisplayName().ToLowerInvariant()} guide, {gspec.Mode.DisplayName().ToLowerInvariant()})");
                grid.Children.Add(gbadge);
            }
            var menuButton = new Button
            {
                Content = new TextBlock { Text = "⋯", FontSize = 12, FontWeight = FontWeight.Bold, Foreground = Brushes.White, Margin = new Thickness(0, -3, 0, 0) },
                Width = 18, Height = 18, MinHeight = 18, MinWidth = 18, Padding = new Thickness(0), Margin = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), BorderThickness = new Thickness(0), Focusable = false, Tag = "menu",
            };
            menuButton.Click += (_, _) => { EndPeek(); ShowMenu(kind, source, menuButton); };
            grid.Children.Add(menuButton);
            var s = source;
            ToolTip.SetTip(border, $"{kind.DisplayName()} — {s.FileName}\n{s.FormatDescription}\nmedian {Ui.G4(s.Stats.Median)}, background {Ui.G4(s.MaskedStats.Background)}, noise {Ui.G4(s.Stats.Noise)}, signal {Ui.G4(s.MaskedStats.SignalStrength)}σ\nPress and hold to see this channel full size; move over other slots to switch");
        }
        else ToolTip.SetTip(border, $"Drop a {kind.DisplayName()} image here");
        border.Child = grid;
        DropHelpers.Accept(border, files =>
        {
            files = DropHelpers.ExpandDirectories(files);
            if (files.Count == 0) return;
            model.LoadFile(files[0], kind);
            if (files.Count > 1) model.LoadFiles(files.Skip(1).ToList());
        });
        return border;
    }

    void ShowMenu(ChannelKind kind, ChannelSource s, Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
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
        var gradient = new MenuItem { Header = "Remove Gradient…" }; gradient.Click += (_, _) => model.BeginGradientEditing(kind); menu.Items.Add(gradient);
        var corrections = new MenuItem { Header = "Manual Corrections…" }; corrections.Click += (_, _) => model.BeginCorrectionEditing(kind); menu.Items.Add(corrections);
        if (model.HasCorrection(kind)) { var rc = new MenuItem { Header = "Remove Corrections" }; rc.Click += (_, _) => model.RemoveCorrection(kind); menu.Items.Add(rc); }
        var save = new MenuItem { Header = "Save channel as FITS…" };
        save.Click += async (_, _) =>
        {
            var name = System.IO.Path.GetFileNameWithoutExtension(s.FileName) + $"-{kind.RawValue()}.fits";
            var path = await Panels.Save(owner, name, Panels.Fits, "Save the channel (cropped if a crop is set).");
            if (path != null) { try { model.ExportChannel(kind, path, RasterFormat.Fits32); } catch (Exception ex) { model.ErrorMessage = ex.Message; } }
        };
        menu.Items.Add(save);
        var remove = new MenuItem { Header = "Remove" }; remove.Click += (_, _) => model.RemoveChannel(kind); menu.Items.Add(remove);
        menu.Open(anchor);
    }

    /// <summary>Loaded channel under a visual of the column's content (null over empty slots, gaps and the slot's menu button).</summary>
    ChannelKind? SlotAt(Visual? hit)
    {
        for (var d = hit; d != null; d = d.GetVisualParent())
        {
            if (d is Button b && Equals(b.Tag, "menu")) return null;
            if (d is Border bd && bd.Tag is ChannelKind k) return model.Sources.ContainsKey(k) ? k : null;
            if (d == slots) break;
        }
        return null;
    }

    void OnPress(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(slots).Properties.IsLeftButtonPressed) return;
        var k = SlotAt(e.Source as Visual);
        if (k == null) return;
        peeking = true;
        model.PeekChannel = k;
        captured = e.Pointer;
        e.Pointer.Capture(slots);
    }

    void OnMove(object? sender, PointerEventArgs e)
    {
        if (!peeking) return;
        if (!e.GetCurrentPoint(slots).Properties.IsLeftButtonPressed) { EndPeek(); return; }
        var p = e.GetPosition(slots);
        var hit = slots.InputHitTest(p) as Visual;
        var k = SlotAt(hit);
        if (model.PeekChannel != k) model.PeekChannel = k;
    }

    void EndPeek()
    {
        if (!peeking) return;
        peeking = false;
        if (captured != null) { captured.Capture(null); captured = null; }
        if (model.PeekChannel != null) model.PeekChannel = null;
    }
}

/// <summary>Logarithmic RGB histogram of the preview.</summary>
public sealed class HistogramView : Control
{
    List<int[]> histogram = new();
    public List<int[]> Histogram { get => histogram; set { histogram = value; InvalidateVisual(); } }
    static readonly IBrush[] Colors = [new SolidColorBrush(Color.FromArgb(90, 255, 0, 0)), new SolidColorBrush(Color.FromArgb(90, 0, 255, 0)), new SolidColorBrush(Color.FromArgb(90, 0, 0, 255))];
    static readonly IBrush Back = new SolidColorBrush(Color.FromArgb(100, 0, 0, 0));

    public override void Render(DrawingContext dc)
    {
        double w = Bounds.Width, h = Bounds.Height;
        dc.DrawRectangle(Back, null, new Rect(0, 0, w, h), 4, 4);
        if (histogram.Count == 0) return;
        double maxV = Math.Max(1, histogram.SelectMany(x => x).DefaultIfEmpty(1).Max());
        for (int c = 0; c < Math.Min(3, histogram.Count); c++)
        {
            var bins = histogram[c];
            if (bins.Length < 2) continue;
            var geo = new StreamGeometry();
            using (var g = geo.Open())
            {
                g.BeginFigure(new Point(0, h), true);
                for (int i = 0; i < bins.Length; i++)
                {
                    double x = w * i / (bins.Length - 1);
                    double y = h - h * Math.Log(1 + bins[i]) / Math.Log(1 + maxV);
                    g.LineTo(new Point(x, y));
                }
                g.LineTo(new Point(w, h));
                g.EndFigure(true);
            }
            dc.DrawGeometry(Colors[c], null, geo);
        }
    }
}
