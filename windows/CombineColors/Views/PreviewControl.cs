using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Views;

public enum ZoomAction { Fit, ActualSize, ZoomIn, ZoomOut }

/// <summary>
/// Displays the rendered preview with smooth zoom/pan, a crop overlay with interactive crop editing,
/// and the dust-spot editor (circles that can be added by clicking, moved, resized by their handle
/// and deleted). Coordinates: image pixels ↔ control DIPs through <see cref="Zoom"/> and <see cref="Origin"/>.
/// </summary>
public sealed class PreviewControl : FrameworkElement
{
    Size imageSize;
    public Size ImageSize
    {
        get => imageSize;
        set { if (imageSize == value) return; imageSize = value; needsFit = true; InvalidateVisual(); }
    }
    public double Zoom { get; private set; } = 1;
    /// <summary>Image (0,0) in control coordinates.</summary>
    public Point Origin { get; private set; }
    bool needsFit = true;
    Size lastSize;

    PreviewFrame? frame;
    public PreviewFrame? Frame { get => frame; set { frame = value; UpdateScalingMode(); InvalidateVisual(); } }
    PixelRect? crop; public PixelRect? Crop { get => crop; set { crop = value; InvalidateVisual(); } }
    bool cropEditing; public bool CropEditing { get => cropEditing; set { cropEditing = value; UpdateCursor(); InvalidateVisual(); } }
    PixelRect? cropDraft; public PixelRect? CropDraft { get => cropDraft; set { cropDraft = value; InvalidateVisual(); } }
    CropOverlayMode cropOverlay = CropOverlayMode.Frame; public CropOverlayMode CropOverlay { get => cropOverlay; set { cropOverlay = value; InvalidateVisual(); } }
    bool dustEditing; public bool DustEditing { get => dustEditing; set { dustEditing = value; UpdateCursor(); InvalidateVisual(); } }
    List<DustSpot> dustSpots = new(); public List<DustSpot> DustSpots { get => dustSpots; set { dustSpots = value; InvalidateVisual(); } }
    Guid? selectedDustSpot; public Guid? SelectedDustSpot { get => selectedDustSpot; set { selectedDustSpot = value; InvalidateVisual(); } }

    public event Action<Rect, double>? ViewportChanged;
    public event Action<Point?>? Hover;
    public event Action<PixelRect?>? CropDraftChanged;
    public event Action? DoubleClick;
    public event Action<List<DustSpot>>? DustSpotsChanged;
    public event Action<Point>? DustAdd;
    public event Action<Guid?>? DustSelect;
    public event Action? DustDelete;

    enum DragKind { None, Pan, Move, Resize, Create, DustPan, DustMove, DustResize }
    DragKind drag = DragKind.None;
    Point dragLast, dragStart, dragAnchor;
    bool dragMoved;
    PixelRect dragRect;
    int dragHandle;
    Guid dragSpot;
    DustSpot? dragSpotStart;

    static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0));
    static readonly Brush BackBrush = new SolidColorBrush(Color.FromRgb(28, 28, 28));
    static readonly Pen CropPen = new(Brushes.White, 1) { DashStyle = new DashStyle([6, 4], 0) };
    static readonly Pen CropEditPen = new(Brushes.Gold, 1) { DashStyle = new DashStyle([6, 4], 0) };
    static readonly Pen HandlePen = new(Brushes.Black, 1);
    static readonly Pen DustPen = new(new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)), 1) { DashStyle = new DashStyle([4, 3], 0) };
    static readonly Pen DustSelectedPen = new(Brushes.Gold, 1.5);

    public PreviewControl()
    {
        Focusable = true;
        ClipToBounds = true;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        AllowDrop = false;
        Cursor = Cursors.Arrow;
        UpdateCursor();
    }

    static Pen Freeze(Pen p) { p.Freeze(); return p; }

    double Backing => VisualTreeHelper.GetDpi(this).DpiScaleX;

    // Geometry helpers

    public Point ImagePoint(Point v) => new((v.X - Origin.X) / Zoom, (v.Y - Origin.Y) / Zoom);
    public Point ViewPoint(Point p) => new(Origin.X + p.X * Zoom, Origin.Y + p.Y * Zoom);
    public Rect ViewRect(Rect r) => new(Origin.X + r.X * Zoom, Origin.Y + r.Y * Zoom, r.Width * Zoom, r.Height * Zoom);
    static Rect ToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    public Rect VisibleImageRect
    {
        get
        {
            var r = new Rect(-Origin.X / Zoom, -Origin.Y / Zoom, ActualWidth / Zoom, ActualHeight / Zoom);
            return Rect.Intersect(r, new Rect(new Point(0, 0), imageSize));
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        // Keep what was in the middle of the view in the middle when the view is resized.
        if (lastSize.Width > 0 && !needsFit)
            Origin = new Point(Origin.X + (sizeInfo.NewSize.Width - lastSize.Width) / 2, Origin.Y + (sizeInfo.NewSize.Height - lastSize.Height) / 2);
        lastSize = sizeInfo.NewSize;
        InvalidateVisual();
    }

    void NotifyViewport()
    {
        if (imageSize.Width <= 0) return;
        ViewportChanged?.Invoke(VisibleImageRect, Zoom * Backing);
    }

    public void Fit(Rect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0 || ActualWidth <= 0) return;
        Zoom = Math.Min(ActualWidth / rect.Width, ActualHeight / rect.Height) * 0.97;
        Origin = new Point((ActualWidth - rect.Width * Zoom) / 2 - rect.X * Zoom, (ActualHeight - rect.Height * Zoom) / 2 - rect.Y * Zoom);
        needsFit = false;
        UpdateScalingMode();
        InvalidateVisual();
        NotifyViewport();
    }

    Rect FitTarget => crop is PixelRect c ? ToRect(c) : new Rect(new Point(0, 0), imageSize);

    public void Perform(ZoomAction action)
    {
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        switch (action)
        {
            case ZoomAction.Fit: Fit(FitTarget); break;
            case ZoomAction.ActualSize: SetZoom(1 / Backing, center); break;
            case ZoomAction.ZoomIn: SetZoom(Zoom * 1.5, center); break;
            case ZoomAction.ZoomOut: SetZoom(Zoom / 1.5, center); break;
        }
    }

    public bool IsActualSize => Math.Abs(Zoom * Backing - 1) < 0.01;

    public void SetZoom(double z, Point anchor)
    {
        if (imageSize.Width <= 0) return;
        double minZoom = Math.Min(ActualWidth / Math.Max(imageSize.Width, 1), ActualHeight / Math.Max(imageSize.Height, 1)) * 0.1;
        double nz = Math.Max(minZoom, Math.Min(z, 32 / Backing));
        var ip = ImagePoint(anchor);
        Zoom = nz;
        Origin = new Point(anchor.X - ip.X * Zoom, anchor.Y - ip.Y * Zoom);
        ClampOrigin();
        needsFit = false;
        UpdateScalingMode();
        InvalidateVisual();
        NotifyViewport();
    }

    void ClampOrigin()
    {
        // Keep at least a quarter of the image visible.
        double w = imageSize.Width * Zoom, h = imageSize.Height * Zoom;
        Origin = new Point(Math.Min(Math.Max(Origin.X, ActualWidth * 0.25 - w), ActualWidth * 0.75), Math.Min(Math.Max(Origin.Y, ActualHeight * 0.25 - h), ActualHeight * 0.75));
    }

    void Pan(Vector delta)
    {
        Origin += delta;
        ClampOrigin();
        InvalidateVisual();
        NotifyViewport();
    }

    void UpdateScalingMode()
    {
        RenderOptions.SetBitmapScalingMode(this, Zoom * Backing >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
    }

    void UpdateCursor() => Cursor = cropEditing || dustEditing ? Cursors.Cross : Cursors.Arrow;

    // Rendering

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(BackBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (imageSize.Width <= 0) return;
        if (needsFit && ActualWidth > 10)
        {
            Zoom = Math.Min(ActualWidth / FitTarget.Width, ActualHeight / FitTarget.Height) * 0.97;
            Origin = new Point((ActualWidth - FitTarget.Width * Zoom) / 2 - FitTarget.X * Zoom, (ActualHeight - FitTarget.Height * Zoom) / 2 - FitTarget.Y * Zoom);
            needsFit = false;
            UpdateScalingMode();
            Dispatcher.BeginInvoke(NotifyViewport);
        }
        if (frame is PreviewFrame f) dc.DrawImage(f.Bitmap, ViewRect(f.Rect));
        DrawCropOverlay(dc);
        DrawDustOverlay(dc);
    }

    void DrawCropOverlay(DrawingContext dc)
    {
        PixelRect? rect = cropEditing ? cropDraft : (cropOverlay == CropOverlayMode.Hidden ? null : crop);
        bool cropOnly = !cropEditing && cropOverlay == CropOverlayMode.CropOnly;
        if (rect is not PixelRect r) return;
        var full = Rect.Intersect(ViewRect(new Rect(new Point(0, 0), imageSize)), new Rect(-2, -2, ActualWidth + 4, ActualHeight + 4));
        var c = ViewRect(ToRect(r));
        var dim = cropOnly ? BackBrush : DimBrush;
        // Four rectangles around the crop.
        var parts = new[]
        {
            new Rect(full.Left, full.Top, full.Width, Math.Max(0, c.Top - full.Top)),
            new Rect(full.Left, c.Bottom, full.Width, Math.Max(0, full.Bottom - c.Bottom)),
            new Rect(full.Left, c.Top, Math.Max(0, c.Left - full.Left), c.Height),
            new Rect(c.Right, c.Top, Math.Max(0, full.Right - c.Right), c.Height),
        };
        foreach (var p in parts) if (p.Width > 0 && p.Height > 0) dc.DrawRectangle(dim, null, p);
        if (!cropOnly) dc.DrawRectangle(null, cropEditing ? CropEditPen : CropPen, Snap(c));
        if (cropEditing)
            for (int i = 0; i < 8; i++)
            {
                var h = HandlePoint(i, c);
                dc.DrawRoundedRectangle(Brushes.White, HandlePen, new Rect(h.X - 5, h.Y - 5, 10, 10), 2, 2);
            }
    }

    static Rect Snap(Rect r) => new(Math.Round(r.X) + 0.5, Math.Round(r.Y) + 0.5, Math.Max(0, Math.Round(r.Width) - 1), Math.Max(0, Math.Round(r.Height) - 1));

    static Point HandlePoint(int i, Rect c) => i switch
    {
        0 => new Point(c.Left, c.Top),
        1 => new Point((c.Left + c.Right) / 2, c.Top),
        2 => new Point(c.Right, c.Top),
        3 => new Point(c.Right, (c.Top + c.Bottom) / 2),
        4 => new Point(c.Right, c.Bottom),
        5 => new Point((c.Left + c.Right) / 2, c.Bottom),
        6 => new Point(c.Left, c.Bottom),
        _ => new Point(c.Left, (c.Top + c.Bottom) / 2),
    };

    Point DustHandlePoint(DustSpot s) => ViewPoint(new Point(s.X + s.Radius, s.Y));

    void DrawDustOverlay(DrawingContext dc)
    {
        if (!dustEditing) return;
        foreach (var s in dustSpots)
        {
            var c = ViewPoint(new Point(s.X, s.Y));
            double r = s.Radius * Zoom;
            if (s.Id == selectedDustSpot)
            {
                dc.DrawEllipse(null, DustSelectedPen, c, r, r);
                dc.DrawLine(DustSelectedPen, new Point(c.X - 4, c.Y), new Point(c.X + 4, c.Y));
                dc.DrawLine(DustSelectedPen, new Point(c.X, c.Y - 4), new Point(c.X, c.Y + 4));
                var h = DustHandlePoint(s);
                dc.DrawEllipse(Brushes.Gold, HandlePen, h, 5, 5);
            }
            else dc.DrawEllipse(null, DustPen, c, r, r);
        }
    }

    // Events

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (imageSize.Width <= 0) return;
        var p = e.GetPosition(this);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) Pan(new Vector(0, e.Delta * 0.5));
        else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) Pan(new Vector(e.Delta * 0.5, 0));
        else SetZoom(Zoom * Math.Pow(1.15, e.Delta / 120.0), p);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2) { DoubleClick?.Invoke(); drag = DragKind.None; return; }
        if (dustEditing) { DustMouseDown(p); CaptureMouse(); return; }
        if (cropEditing)
        {
            var ip = ImagePoint(p);
            if (cropDraft is PixelRect d)
            {
                var c = ViewRect(ToRect(d));
                for (int i = 0; i < 8; i++)
                {
                    var h = HandlePoint(i, c);
                    if ((p - h).Length <= 8) { drag = DragKind.Resize; dragHandle = i; dragRect = d; CaptureMouse(); return; }
                }
                if (c.Contains(p)) { drag = DragKind.Move; dragRect = d; dragAnchor = ip; CaptureMouse(); return; }
            }
            drag = DragKind.Create; dragStart = ip; CaptureMouse();
        }
        else { drag = DragKind.Pan; dragLast = p; CaptureMouse(); Cursor = Cursors.SizeAll; }
    }

    DustSpot? DustSpotAt(Point p)
    {
        var ip = ImagePoint(p);
        return dustSpots.Where(s => Math.Sqrt((ip.X - s.X) * (ip.X - s.X) + (ip.Y - s.Y) * (ip.Y - s.Y)) <= s.Radius).OrderBy(s => s.Radius).FirstOrDefault();
    }

    void DustMouseDown(Point p)
    {
        if (selectedDustSpot is Guid id && dustSpots.FirstOrDefault(s => s.Id == id) is DustSpot sel)
        {
            var h = DustHandlePoint(sel);
            if ((p - h).Length <= 8) { drag = DragKind.DustResize; dragSpot = id; return; }
        }
        if (DustSpotAt(p) is DustSpot s)
        {
            if (s.Id != selectedDustSpot) DustSelect?.Invoke(s.Id);
            drag = DragKind.DustMove; dragSpot = s.Id; dragSpotStart = s.Clone(); dragAnchor = ImagePoint(p);
            return;
        }
        drag = DragKind.DustPan; dragLast = p; dragStart = p; dragMoved = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        var ip = ImagePoint(p);
        Hover?.Invoke(ip);
        if (e.LeftButton != MouseButtonState.Pressed && drag != DragKind.None) { drag = DragKind.None; ReleaseMouseCapture(); UpdateCursor(); }
        Point ClampPoint(Point q) => new(Math.Min(Math.Max(q.X, 0), imageSize.Width), Math.Min(Math.Max(q.Y, 0), imageSize.Height));
        switch (drag)
        {
            case DragKind.Pan:
                Pan(p - dragLast); dragLast = p; break;
            case DragKind.DustPan:
                Pan(p - dragLast); dragLast = p;
                if ((p - dragStart).Length > 3) dragMoved = true;
                break;
            case DragKind.DustMove:
            {
                int i = dustSpots.FindIndex(s => s.Id == dragSpot);
                if (i < 0 || dragSpotStart == null) break;
                var spots = dustSpots.Select(s => s.Clone()).ToList();
                spots[i].X = Math.Min(Math.Max(dragSpotStart.X + ip.X - dragAnchor.X, 0), imageSize.Width);
                spots[i].Y = Math.Min(Math.Max(dragSpotStart.Y + ip.Y - dragAnchor.Y, 0), imageSize.Height);
                dustSpots = spots; InvalidateVisual();
                DustSpotsChanged?.Invoke(spots);
                break;
            }
            case DragKind.DustResize:
            {
                int i = dustSpots.FindIndex(s => s.Id == dragSpot);
                if (i < 0) break;
                var spots = dustSpots.Select(s => s.Clone()).ToList();
                spots[i].Radius = Math.Max(3, Math.Sqrt((ip.X - spots[i].X) * (ip.X - spots[i].X) + (ip.Y - spots[i].Y) * (ip.Y - spots[i].Y)));
                dustSpots = spots; InvalidateVisual();
                DustSpotsChanged?.Invoke(spots);
                break;
            }
            case DragKind.Create:
            {
                var a = ClampPoint(dragStart); var b = ClampPoint(ip);
                var r = new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
                if (r.Width >= 1 && r.Height >= 1) { cropDraft = PixelRect.FromDouble(r.Left, r.Top, r.Right, r.Bottom); InvalidateVisual(); CropDraftChanged?.Invoke(cropDraft); }
                break;
            }
            case DragKind.Move:
            {
                var r = dragRect;
                int x = (int)Math.Round(dragRect.X + ip.X - dragAnchor.X), y = (int)Math.Round(dragRect.Y + ip.Y - dragAnchor.Y);
                x = Math.Min(Math.Max(0, x), (int)imageSize.Width - r.Width); y = Math.Min(Math.Max(0, y), (int)imageSize.Height - r.Height);
                cropDraft = new PixelRect(x, y, r.Width, r.Height); InvalidateVisual(); CropDraftChanged?.Invoke(cropDraft);
                break;
            }
            case DragKind.Resize:
            {
                var q = ClampPoint(ip);
                double x0 = dragRect.X, y0 = dragRect.Y, x1 = dragRect.MaxX, y1 = dragRect.MaxY;
                if (dragHandle is 0 or 6 or 7) x0 = Math.Min(q.X, x1 - 1);
                if (dragHandle is 2 or 3 or 4) x1 = Math.Max(q.X, x0 + 1);
                if (dragHandle is 0 or 1 or 2) y0 = Math.Min(q.Y, y1 - 1);
                if (dragHandle is 4 or 5 or 6) y1 = Math.Max(q.Y, y0 + 1);
                cropDraft = PixelRect.FromDouble(x0, y0, x1, y1); InvalidateVisual(); CropDraftChanged?.Invoke(cropDraft);
                break;
            }
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (drag == DragKind.DustPan && !dragMoved)
        {
            var ip = ImagePoint(dragStart);
            if (ip.X >= 0 && ip.Y >= 0 && ip.X < imageSize.Width && ip.Y < imageSize.Height) DustAdd?.Invoke(ip); else DustSelect?.Invoke(null);
        }
        drag = DragKind.None;
        ReleaseMouseCapture();
        UpdateCursor();
    }

    protected override void OnMouseLeave(MouseEventArgs e) { Hover?.Invoke(null); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Add: case Key.OemPlus: Perform(ZoomAction.ZoomIn); e.Handled = true; break;
            case Key.Subtract: case Key.OemMinus: Perform(ZoomAction.ZoomOut); e.Handled = true; break;
            case Key.D0: case Key.NumPad0: Perform(ZoomAction.Fit); e.Handled = true; break;
            case Key.D1: case Key.NumPad1: Perform(ZoomAction.ActualSize); e.Handled = true; break;
            case Key.Delete: case Key.Back: if (dustEditing) { DustDelete?.Invoke(); e.Handled = true; } break;
        }
        if (!e.Handled) base.OnKeyDown(e);
    }
}
