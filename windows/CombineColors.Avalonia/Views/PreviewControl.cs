using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Stretch = Avalonia.Media.Stretch;
using Avalonia.Threading;
using AstroCore;
using PixelRect = AstroCore.PixelRect;
using CombineColors.Model;

namespace CombineColors.Views;

public enum ZoomAction { Fit, ActualSize, ZoomIn, ZoomOut }

/// <summary>
/// Displays the rendered preview with smooth zoom/pan, a crop overlay with interactive crop editing,
/// and the dust-spot editor (circles that can be added by clicking, moved, resized by their handle
/// and deleted). Coordinates: image pixels ↔ control DIPs through <see cref="Zoom"/> and <see cref="Origin"/>.
/// </summary>
public sealed class PreviewControl : Control
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
    bool gradientEditing; public bool GradientEditing { get => gradientEditing; set { gradientEditing = value; UpdateCursor(); InvalidateVisual(); } }
    GradientGesture? gradientGesture; public GradientGesture? GradientGesture { get => gradientGesture; set { gradientGesture = value; InvalidateVisual(); } }
    GradientGesture? gradientRefined; public GradientGesture? GradientRefined { get => gradientRefined; set { gradientRefined = value; InvalidateVisual(); } }
    GradientShape gradientShape = GradientShape.Ellipse; public GradientShape GradientShape { get => gradientShape; set => gradientShape = value; }
    List<GradientRegion> gradientRegions = new(); public List<GradientRegion> GradientRegions { get => gradientRegions; set { gradientRegions = value; InvalidateVisual(); } }
    Guid? selectedGradientRegion; public Guid? SelectedGradientRegion { get => selectedGradientRegion; set { selectedGradientRegion = value; InvalidateVisual(); } }
    bool gradientProtectMode; public bool GradientProtectMode { get => gradientProtectMode; set => gradientProtectMode = value; }

    public event Action<Rect, double>? ViewportChanged;
    public event Action<Point?>? Hover;
    public event Action<PixelRect?>? CropDraftChanged;
    public event Action? DoubleClick;
    public event Action<List<DustSpot>>? DustSpotsChanged;
    public event Action<Point>? DustAdd;
    public event Action<Guid?>? DustSelect;
    public event Action? DustDelete;
    public event Action<GradientGesture>? GradientGestureChanged;
    public event Action<List<GradientRegion>>? GradientRegionsChanged;
    public event Action<Point>? GradientRegionAdd;
    public event Action<Guid?>? GradientRegionSelect;
    public event Action? GradientDelete;

    enum DragKind { None, Pan, Move, Resize, Create, DustPan, DustMove, DustResize, GradientPan, GradientCreate, GradientHandle, GradientMove, RegionMove, RegionResize }
    DragKind drag = DragKind.None;
    Point dragLast, dragStart, dragAnchor;
    bool dragMoved;
    PixelRect dragRect;
    int dragHandle;
    Guid dragSpot;
    DustSpot? dragSpotStart;
    GradientGesture? dragGestureStart;
    Guid dragRegion;
    GradientRegion? dragRegionStart;
    int dragAxis;

    static readonly IBrush DimBrush = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0));
    static readonly IBrush BackBrush = new SolidColorBrush(Color.FromRgb(28, 28, 28));
    static readonly IPen CropPen = new Pen(Brushes.White, 1, new DashStyle([6, 4], 0));
    static readonly IPen CropEditPen = new Pen(Brushes.Gold, 1, new DashStyle([6, 4], 0));
    static readonly IPen HandlePen = new Pen(Brushes.Black, 1);
    static readonly IPen DustPen = new Pen(new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)), 1, new DashStyle([4, 3], 0));
    static readonly IPen DustSelectedPen = new Pen(Brushes.Gold, 1.5);

    public PreviewControl()
    {
        Focusable = true;
        ClipToBounds = true;
        UpdateCursor();
    }

    double Backing => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    double ActualWidth => Bounds.Width;
    double ActualHeight => Bounds.Height;

    // Geometry helpers

    public Point ImagePoint(Point v) => new((v.X - Origin.X) / Zoom, (v.Y - Origin.Y) / Zoom);
    public Point ViewPoint(Point p) => new(Origin.X + p.X * Zoom, Origin.Y + p.Y * Zoom);
    public Rect ViewRect(Rect r) => new(Origin.X + r.X * Zoom, Origin.Y + r.Y * Zoom, r.Width * Zoom, r.Height * Zoom);
    static Rect ToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
    static double Dist(Point a, Point b) => ((Vector)(a - b)).Length;

    public Rect VisibleImageRect
    {
        get
        {
            var r = new Rect(-Origin.X / Zoom, -Origin.Y / Zoom, ActualWidth / Zoom, ActualHeight / Zoom);
            return r.Intersect(new Rect(new Point(0, 0), imageSize));
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        // Keep what was in the middle of the view in the middle when the view is resized.
        if (lastSize.Width > 0 && !needsFit)
            Origin = new Point(Origin.X + (e.NewSize.Width - lastSize.Width) / 2, Origin.Y + (e.NewSize.Height - lastSize.Height) / 2);
        lastSize = e.NewSize;
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
        RenderOptions.SetBitmapInterpolationMode(this, Zoom * Backing >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.MediumQuality);
    }

    void UpdateCursor() => Cursor = new Cursor(cropEditing || dustEditing || gradientEditing ? StandardCursorType.Cross : StandardCursorType.Arrow);

    // Rendering

    public override void Render(DrawingContext dc)
    {
        dc.FillRectangle(BackBrush, new Rect(0, 0, ActualWidth, ActualHeight));
        if (imageSize.Width <= 0) return;
        if (needsFit && ActualWidth > 10)
        {
            Zoom = Math.Min(ActualWidth / FitTarget.Width, ActualHeight / FitTarget.Height) * 0.97;
            Origin = new Point((ActualWidth - FitTarget.Width * Zoom) / 2 - FitTarget.X * Zoom, (ActualHeight - FitTarget.Height * Zoom) / 2 - FitTarget.Y * Zoom);
            needsFit = false;
            UpdateScalingMode();
            Dispatcher.UIThread.Post(NotifyViewport);
        }
        if (frame is PreviewFrame f) dc.DrawImage(f.Bitmap, ViewRect(f.Rect));
        DrawCropOverlay(dc);
        DrawDustOverlay(dc);
        DrawGradientOverlay(dc);
    }

    void DrawCropOverlay(DrawingContext dc)
    {
        PixelRect? rect = cropEditing ? cropDraft : (cropOverlay == CropOverlayMode.Hidden ? null : crop);
        bool cropOnly = !cropEditing && cropOverlay == CropOverlayMode.CropOnly;
        if (rect is not PixelRect r) return;
        var full = ViewRect(new Rect(new Point(0, 0), imageSize)).Intersect(new Rect(-2, -2, ActualWidth + 4, ActualHeight + 4));
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
        foreach (var p in parts) if (p.Width > 0 && p.Height > 0) dc.FillRectangle(dim, p);
        if (!cropOnly) dc.DrawRectangle(null, cropEditing ? CropEditPen : CropPen, Snap(c));
        if (cropEditing)
            for (int i = 0; i < 8; i++)
            {
                var h = HandlePoint(i, c);
                dc.DrawRectangle(Brushes.White, HandlePen, new Rect(h.X - 5, h.Y - 5, 10, 10), 2, 2);
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

    static readonly IPen GradientPen = new Pen(Brushes.Gold, 1.5);
    static readonly IPen GradientRefinedPen = new Pen(Brushes.Orange, 1, new DashStyle([5, 4], 0));
    static readonly IPen RegionPen = new Pen(new SolidColorBrush(Color.FromArgb(204, 255, 59, 48)), 1, new DashStyle([4, 3], 0));
    static readonly IPen RegionSelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(255, 59, 48)), 1.5);
    static readonly IBrush RegionFill = new SolidColorBrush(Color.FromArgb(24, 255, 59, 48));

    void DrawRotatedEllipse(DrawingContext dc, IBrush? fill, IPen pen, Point c, double rx, double ry, double angle, bool axes)
    {
        using (dc.PushTransform(Matrix.CreateTranslation(-c.X, -c.Y) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(c.X, c.Y)))
        {
            dc.DrawEllipse(fill, pen, c, rx, ry);
            if (axes) { dc.DrawLine(pen, new Point(c.X - rx, c.Y), new Point(c.X + rx, c.Y)); dc.DrawLine(pen, new Point(c.X, c.Y - ry), new Point(c.X, c.Y + ry)); }
        }
    }

    static void DrawHandleSquare(DrawingContext dc, IBrush fill, Point h) => dc.DrawRectangle(fill, HandlePen, new Rect(h.X - 5, h.Y - 5, 10, 10), 2, 2);

    // ---- Gradient editor -------------------------------------------------------------------------

    /// <summary>Handle positions of a guide in view coordinates (2 for a line, 3 for an ellipse, none for the polynomial).</summary>
    List<Point> GradientHandlePoints(GradientGesture g) => g.Shape switch
    {
        GradientShape.Line => [ViewPoint(new Point(g.X, g.Y)), ViewPoint(new Point(g.X2, g.Y2))],
        GradientShape.Ellipse => [ViewPoint(new Point(g.X, g.Y)),
                                  ViewPoint(new Point(g.X + g.A * Math.Cos(g.Angle), g.Y + g.A * Math.Sin(g.Angle))),
                                  ViewPoint(new Point(g.X - g.B * Math.Sin(g.Angle), g.Y + g.B * Math.Cos(g.Angle)))],
        _ => [],
    };

    void DrawGradientGuide(DrawingContext dc, GradientGesture g, IPen pen, bool axes)
    {
        switch (g.Shape)
        {
            case GradientShape.Line:
            {
                var p0 = ViewPoint(new Point(g.X, g.Y)); var p1 = ViewPoint(new Point(g.X2, g.Y2));
                dc.DrawLine(pen, p0, p1);
                double ang = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X);
                foreach (var d in new[] { 2.6, -2.6 }) dc.DrawLine(pen, p1, new Point(p1.X - 10 * Math.Cos(ang + d), p1.Y - 10 * Math.Sin(ang + d)));
                break;
            }
            case GradientShape.Ellipse:
                DrawRotatedEllipse(dc, null, pen, ViewPoint(new Point(g.X, g.Y)), g.A * Zoom, g.B * Zoom, g.Angle, axes);
                break;
        }
    }

    void DrawGradientOverlay(DrawingContext dc)
    {
        if (!gradientEditing) return;
        foreach (var r in gradientRegions)
        {
            var c = ViewPoint(new Point(r.X, r.Y));
            bool sel = r.Id == selectedGradientRegion;
            dc.DrawEllipse(RegionFill, sel ? RegionSelectedPen : RegionPen, c, r.A * Zoom, r.B * Zoom);
            if (sel) foreach (var h in RegionHandlePoints(r)) dc.DrawEllipse(Brushes.Red, HandlePen, h, 5, 5);
        }
        if (gradientGesture is GradientGesture g && g.Shape != GradientShape.Polynomial)
        {
            if (gradientRefined is GradientGesture rg && !rg.Equals(g) && rg.Shape == g.Shape) DrawGradientGuide(dc, rg, GradientRefinedPen, false);
            DrawGradientGuide(dc, g, GradientPen, true);
            foreach (var h in GradientHandlePoints(g)) DrawHandleSquare(dc, Brushes.Gold, h);
        }
    }

    /// <summary>Handles of the selected region: on its rim to the right (a) and below (b) the centre.</summary>
    List<Point> RegionHandlePoints(GradientRegion r) => [ViewPoint(new Point(r.X + r.A, r.Y)), ViewPoint(new Point(r.X, r.Y + r.B))];

    /// <summary>The region under a view point (the smallest when several overlap).</summary>
    GradientRegion? GradientRegionAt(Point p)
    {
        var ip = ImagePoint(p);
        return gradientRegions.Where(r => r.Contains(ip.X, ip.Y)).OrderBy(r => r.A * r.B).FirstOrDefault();
    }

    /// <summary>Distance from a view point to the drawn line segment.</summary>
    double DistanceToGradientLine(Point p, GradientGesture g)
    {
        var a = ViewPoint(new Point(g.X, g.Y)); var b = ViewPoint(new Point(g.X2, g.Y2));
        double dx = b.X - a.X, dy = b.Y - a.Y, l2 = Math.Max(dx * dx + dy * dy, 1e-6);
        double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2, 0, 1);
        return Dist(p, new Point(a.X + t * dx, a.Y + t * dy));
    }

    void GradientMouseDown(Point p)
    {
        if (gradientGesture is GradientGesture g && g.Shape != GradientShape.Polynomial)
        {
            var hs = GradientHandlePoints(g);
            for (int i = 0; i < hs.Count; i++) if (Dist(p, hs[i]) <= 8) { drag = DragKind.GradientHandle; dragHandle = i; return; }
        }
        if (selectedGradientRegion is Guid id && gradientRegions.FirstOrDefault(r => r.Id == id) is GradientRegion sel)
        {
            var hs = RegionHandlePoints(sel);
            for (int i = 0; i < hs.Count; i++) if (Dist(p, hs[i]) <= 8) { drag = DragKind.RegionResize; dragRegion = id; dragAxis = i; return; }
        }
        if (GradientRegionAt(p) is GradientRegion r)
        {
            if (r.Id != selectedGradientRegion) GradientRegionSelect?.Invoke(r.Id);
            drag = DragKind.RegionMove; dragRegion = r.Id; dragRegionStart = r.Clone(); dragAnchor = ImagePoint(p);
            return;
        }
        if (gradientGesture is GradientGesture lg && lg.Shape == GradientShape.Line && DistanceToGradientLine(p, lg) <= 6)
        {
            drag = DragKind.GradientMove; dragGestureStart = lg.Clone(); dragAnchor = ImagePoint(p); return;
        }
        if (!gradientProtectMode && gradientShape != GradientShape.Polynomial && (gradientGesture == null || gradientGesture.Shape == GradientShape.Polynomial))
        {
            drag = DragKind.GradientCreate; dragStart = ImagePoint(p); return;
        }
        drag = DragKind.GradientPan; dragLast = p; dragStart = p; dragMoved = false;
    }

    /// <summary>Drags of the gradient editor; true when handled.</summary>
    bool GradientMouseMove(Point p, Point ip)
    {
        switch (drag)
        {
            case DragKind.GradientPan:
                Pan(p - dragLast); dragLast = p;
                if (Dist(p, dragStart) > 3) dragMoved = true;
                return true;
            case DragKind.GradientCreate:
            {
                if (Dist(ip, dragStart) < 2) return true;
                var g = gradientShape == GradientShape.Line
                    ? GradientGesture.Line(dragStart.X, dragStart.Y, ip.X, ip.Y)
                    : GradientGesture.Ellipse(dragStart.X, dragStart.Y, Math.Max(Math.Abs(ip.X - dragStart.X), 1), Math.Max(Math.Abs(ip.Y - dragStart.Y), 1));
                gradientGesture = g; InvalidateVisual();
                GradientGestureChanged?.Invoke(g);
                return true;
            }
            case DragKind.GradientHandle:
            {
                if (gradientGesture is not GradientGesture g0) return true;
                var g = g0.Clone();
                if (g.Shape == GradientShape.Line) { if (dragHandle == 0) { g.X = ip.X; g.Y = ip.Y; } else { g.X2 = ip.X; g.Y2 = ip.Y; } }
                else if (dragHandle == 0) { g.X = ip.X; g.Y = ip.Y; }
                else if (dragHandle == 1) { g.A = Math.Max(Math.Sqrt((ip.X - g.X) * (ip.X - g.X) + (ip.Y - g.Y) * (ip.Y - g.Y)), 1); g.Angle = Math.Atan2(ip.Y - g.Y, ip.X - g.X); }
                else { double bx = -Math.Sin(g.Angle), by = Math.Cos(g.Angle); g.B = Math.Max(Math.Abs((ip.X - g.X) * bx + (ip.Y - g.Y) * by), 1); }
                gradientGesture = g; InvalidateVisual();
                GradientGestureChanged?.Invoke(g);
                return true;
            }
            case DragKind.GradientMove:
            {
                if (dragGestureStart == null) return true;
                var g = dragGestureStart.Clone();
                double dx = ip.X - dragAnchor.X, dy = ip.Y - dragAnchor.Y;
                g.X += dx; g.Y += dy; g.X2 += dx; g.Y2 += dy;
                gradientGesture = g; InvalidateVisual();
                GradientGestureChanged?.Invoke(g);
                return true;
            }
            case DragKind.RegionMove:
            {
                int i = gradientRegions.FindIndex(r => r.Id == dragRegion);
                if (i < 0 || dragRegionStart == null) return true;
                var regions = gradientRegions.Select(r => r.Clone()).ToList();
                regions[i].X = Math.Min(Math.Max(dragRegionStart.X + ip.X - dragAnchor.X, 0), imageSize.Width);
                regions[i].Y = Math.Min(Math.Max(dragRegionStart.Y + ip.Y - dragAnchor.Y, 0), imageSize.Height);
                gradientRegions = regions; InvalidateVisual();
                GradientRegionsChanged?.Invoke(regions);
                return true;
            }
            case DragKind.RegionResize:
            {
                int i = gradientRegions.FindIndex(r => r.Id == dragRegion);
                if (i < 0) return true;
                var regions = gradientRegions.Select(r => r.Clone()).ToList();
                if (dragAxis == 0) regions[i].A = Math.Max(3, Math.Abs(ip.X - regions[i].X)); else regions[i].B = Math.Max(3, Math.Abs(ip.Y - regions[i].Y));
                gradientRegions = regions; InvalidateVisual();
                GradientRegionsChanged?.Invoke(regions);
                return true;
            }
        }
        return false;
    }

    void GradientMouseUp()
    {
        if (drag == DragKind.GradientPan && !dragMoved)
        {
            var ip = ImagePoint(dragStart);
            if (gradientProtectMode && ip.X >= 0 && ip.Y >= 0 && ip.X < imageSize.Width && ip.Y < imageSize.Height) GradientRegionAdd?.Invoke(ip);
            else GradientRegionSelect?.Invoke(null);
        }
    }

    // Events

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (imageSize.Width <= 0) return;
        var p = e.GetPosition(this);
        double dy = e.Delta.Y, dx = e.Delta.X;
        // One wheel notch is 1.0; trackpads deliver fractions and a horizontal component.
        if ((e.KeyModifiers & KeyModifiers.Control) != 0 || (e.KeyModifiers & KeyModifiers.Meta) != 0) Pan(new Vector(dx * 60, dy * 60));
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0) Pan(new Vector((dx != 0 ? dx : dy) * 60, 0));
        else if (dy != 0) SetZoom(Zoom * Math.Pow(1.15, dy), p);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) { base.OnPointerPressed(e); return; }
        Focus();
        var p = e.GetPosition(this);
        if (e.ClickCount == 2) { DoubleClick?.Invoke(); drag = DragKind.None; e.Handled = true; return; }
        if (dustEditing) { DustMouseDown(p); e.Pointer.Capture(this); e.Handled = true; return; }
        if (gradientEditing) { GradientMouseDown(p); e.Pointer.Capture(this); e.Handled = true; return; }
        if (cropEditing)
        {
            var ip = ImagePoint(p);
            if (cropDraft is PixelRect d)
            {
                var c = ViewRect(ToRect(d));
                for (int i = 0; i < 8; i++)
                {
                    var h = HandlePoint(i, c);
                    if (Dist(p, h) <= 8) { drag = DragKind.Resize; dragHandle = i; dragRect = d; e.Pointer.Capture(this); e.Handled = true; return; }
                }
                if (c.Contains(p)) { drag = DragKind.Move; dragRect = d; dragAnchor = ip; e.Pointer.Capture(this); e.Handled = true; return; }
            }
            drag = DragKind.Create; dragStart = ip; e.Pointer.Capture(this);
        }
        else { drag = DragKind.Pan; dragLast = p; e.Pointer.Capture(this); Cursor = new Cursor(StandardCursorType.SizeAll); }
        e.Handled = true;
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
            if (Dist(p, h) <= 8) { drag = DragKind.DustResize; dragSpot = id; return; }
        }
        if (DustSpotAt(p) is DustSpot s)
        {
            if (s.Id != selectedDustSpot) DustSelect?.Invoke(s.Id);
            drag = DragKind.DustMove; dragSpot = s.Id; dragSpotStart = s.Clone(); dragAnchor = ImagePoint(p);
            return;
        }
        drag = DragKind.DustPan; dragLast = p; dragStart = p; dragMoved = false;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        var ip = ImagePoint(p);
        Hover?.Invoke(ip);
        bool left = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed;
        if (!left && drag != DragKind.None) { drag = DragKind.None; e.Pointer.Capture(null); UpdateCursor(); }
        Point ClampPoint(Point q) => new(Math.Min(Math.Max(q.X, 0), imageSize.Width), Math.Min(Math.Max(q.Y, 0), imageSize.Height));
        if (GradientMouseMove(p, ip)) return;
        switch (drag)
        {
            case DragKind.Pan:
                Pan((Vector)(p - dragLast)); dragLast = p; break;
            case DragKind.DustPan:
                Pan((Vector)(p - dragLast)); dragLast = p;
                if (Dist(p, dragStart) > 3) dragMoved = true;
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

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) { base.OnPointerReleased(e); return; }
        if (drag == DragKind.DustPan && !dragMoved)
        {
            var ip = ImagePoint(dragStart);
            if (ip.X >= 0 && ip.Y >= 0 && ip.X < imageSize.Width && ip.Y < imageSize.Height) DustAdd?.Invoke(ip); else DustSelect?.Invoke(null);
        }
        GradientMouseUp();
        drag = DragKind.None;
        e.Pointer.Capture(null);
        UpdateCursor();
    }

    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); Hover?.Invoke(null); }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Add: case Key.OemPlus: Perform(ZoomAction.ZoomIn); e.Handled = true; break;
            case Key.Subtract: case Key.OemMinus: Perform(ZoomAction.ZoomOut); e.Handled = true; break;
            case Key.D0: case Key.NumPad0: Perform(ZoomAction.Fit); e.Handled = true; break;
            case Key.D1: case Key.NumPad1: Perform(ZoomAction.ActualSize); e.Handled = true; break;
            case Key.Delete: case Key.Back: if (dustEditing) { DustDelete?.Invoke(); e.Handled = true; } else if (gradientEditing) { GradientDelete?.Invoke(); e.Handled = true; } break;
        }
        if (!e.Handled) base.OnKeyDown(e);
    }
}
