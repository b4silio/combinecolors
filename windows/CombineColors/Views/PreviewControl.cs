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
    /// <summary>Fine rotation already applied to the channels, and the one being edited with the crop (degrees, clockwise).
    /// While they differ the picture is turned on screen under the upright crop; the channels are resampled on Apply.</summary>
    double fineAngle; public double FineAngle { get => fineAngle; set { if (fineAngle == value) return; fineAngle = value; InvalidateVisual(); } }
    double cropAngle; public double CropAngle { get => cropAngle; set { if (cropAngle == value) return; cropAngle = value; InvalidateVisual(); } }
    /// <summary>Where the crop being edited may go (image coordinates): the canvas of the turned picture, sideways after an
    /// odd number of quarter turns, so it can reach outside the shown picture.</summary>
    Rect cropBounds; public Rect CropBounds
    {
        get => cropBounds;
        set
        {
            if (cropBounds == value) return;
            var old = cropBounds;
            cropBounds = value;
            // The turned canvas went sideways (or back): show all of it.
            if (cropEditing && old.Width > 0 && value.Size != old.Size) Fit(value);
            InvalidateVisual();
        }
    }
    Rect CropLimits => cropEditing && cropBounds.Width > 0 ? cropBounds : new Rect(new Point(0, 0), imageSize);
    public event Action<double>? CropAngleChange;
    /// <summary>Width (view units) of the band around the crop where a drag turns the picture.</summary>
    const double TurnZone = 160;
    double turnStartAngle, turnStartPointer;
    CropOverlayMode cropOverlay = CropOverlayMode.Frame; public CropOverlayMode CropOverlay { get => cropOverlay; set { cropOverlay = value; InvalidateVisual(); } }
    bool dustEditing; public bool DustEditing { get => dustEditing; set { dustEditing = value; UpdateCursor(); InvalidateVisual(); } }
    List<DustSpot> dustSpots = new(); public List<DustSpot> DustSpots { get => dustSpots; set { dustSpots = value; InvalidateVisual(); } }
    Guid? selectedDustSpot; public Guid? SelectedDustSpot { get => selectedDustSpot; set { selectedDustSpot = value; InvalidateVisual(); } }
    /// <summary>New spots are bright spots: a dotted circle of the largest fitted radius follows the pointer; right-drag changes it.</summary>
    bool dustBrightMode; public bool DustBrightMode { get => dustBrightMode; set { if (dustBrightMode == value) return; dustBrightMode = value; InvalidateVisual(); } }
    double dustMaxRadius = 40; public double DustMaxRadius { get => dustMaxRadius; set { if (dustMaxRadius == value) return; dustMaxRadius = value; InvalidateVisual(); } }
    Point? dustPointer; bool maxRadiusResizing; Point maxRadiusStart; double maxRadiusStartValue;
    bool gradientEditing; public bool GradientEditing { get => gradientEditing; set { gradientEditing = value; UpdateCursor(); InvalidateVisual(); } }
    GradientGesture? gradientGesture; public GradientGesture? GradientGesture { get => gradientGesture; set { gradientGesture = value; InvalidateVisual(); } }
    GradientGesture? gradientRefined; public GradientGesture? GradientRefined { get => gradientRefined; set { gradientRefined = value; InvalidateVisual(); } }
    GradientShape gradientShape = GradientShape.Ellipse; public GradientShape GradientShape { get => gradientShape; set => gradientShape = value; }
    List<GradientRegion> gradientRegions = new(); public List<GradientRegion> GradientRegions { get => gradientRegions; set { gradientRegions = value; InvalidateVisual(); } }
    Guid? selectedGradientRegion; public Guid? SelectedGradientRegion { get => selectedGradientRegion; set { selectedGradientRegion = value; InvalidateVisual(); } }
    bool gradientProtectMode; public bool GradientProtectMode { get => gradientProtectMode; set => gradientProtectMode = value; }
    /// <summary>Background samples of the edited channel (Samples shape), their radius and what the fit made of them.</summary>
    List<BackgroundSample> gradientSamples = new(); public List<BackgroundSample> GradientSamples { get => gradientSamples; set { gradientSamples = value; InvalidateVisual(); } }
    double gradientSampleRadius = 20; public double GradientSampleRadius { get => gradientSampleRadius; set { gradientSampleRadius = value; InvalidateVisual(); } }
    Dictionary<Guid, SampleResult> gradientSampleResults = new(); public Dictionary<Guid, SampleResult> GradientSampleResults { get => gradientSampleResults; set { gradientSampleResults = value; InvalidateVisual(); } }
    Guid? selectedGradientSample; public Guid? SelectedGradientSample { get => selectedGradientSample; set { selectedGradientSample = value; InvalidateVisual(); } }

    /// <summary>Corrections editor: paint with the brush; the map is drawn as a tint (red darkens, blue brightens).</summary>
    bool correctionEditing; public bool CorrectionEditing { get => correctionEditing; set { if (correctionEditing == value) return; correctionEditing = value; if (!value) { brushPoint = null; brushResizing = false; } UpdateCursor(); InvalidateVisual(); } }
    CorrectionMap? correctionMap; public CorrectionMap? CorrectionMap { get => correctionMap; set { if (ReferenceEquals(correctionMap, value)) return; correctionMap = value; correctionTint = null; InvalidateVisual(); } }
    int correctionVersion; public int CorrectionVersion { get => correctionVersion; set { if (correctionVersion == value) return; correctionVersion = value; correctionTint = null; InvalidateVisual(); } }
    bool correctionShowMap = false; public bool CorrectionShowMap { get => correctionShowMap; set { correctionShowMap = value; InvalidateVisual(); } }
    /// <summary>Brush radius in image pixels, and its sense.</summary>
    double brushRadius = 50; public double BrushRadius { get => brushRadius; set { brushRadius = value; InvalidateVisual(); } }
    bool brushDarken = true; public bool BrushDarken { get => brushDarken; set { brushDarken = value; InvalidateVisual(); } }
    System.Windows.Media.Imaging.WriteableBitmap? correctionTint;
    Point? brushPoint;
    Point paintLast;
    bool brushResizing; Point brushResizeStart; double brushResizeRadius;

    /// <summary>
    /// While the space bar is held the preview is in pan mode whatever the editor: a left drag pans
    /// instead of painting, adding spots, drawing guides or creating a crop, and the brush circle is hidden.
    /// </summary>
    bool spaceHeld;
    public bool SpaceHeld
    {
        get => spaceHeld;
        set
        {
            if (spaceHeld == value) return;
            spaceHeld = value;
            if (value)
            {
                // A stroke or gesture in progress ends here; the next press pans.
                if (drag != DragKind.None && drag != DragKind.Pan) { drag = DragKind.None; ReleaseMouseCapture(); }
                if (!brushResizing) brushPoint = null;
            }
            UpdateCursor();
            InvalidateVisual();
        }
    }
    public event Action? StrokeBegin;
    public event Action<Point, Point, bool>? Paint;
    public event Action<double>? BrushRadiusChanged;

    public event Action<Rect, double>? ViewportChanged;
    public event Action<Point?>? Hover;
    public event Action<PixelRect?>? CropDraftChanged;
    public event Action? DoubleClick;
    public event Action<List<DustSpot>>? DustSpotsChanged;
    public event Action<Point>? DustAdd;
    public event Action<Guid?>? DustSelect;
    public event Action? DustDelete;
    public event Action<double>? DustMaxRadiusChanged;
    public event Action<GradientGesture>? GradientGestureChanged;
    public event Action<List<GradientRegion>>? GradientRegionsChanged;
    public event Action<Point>? GradientRegionAdd;
    public event Action<Guid?>? GradientRegionSelect;
    public event Action? GradientDelete;
    public event Action<Point>? GradientSampleAdd;
    public event Action<List<BackgroundSample>>? GradientSamplesChanged;
    public event Action<Guid?>? GradientSampleSelect;

    enum DragKind { None, Pan, Move, Resize, Create, Turn, DustPan, DustMove, DustResize, GradientPan, GradientCreate, GradientHandle, GradientMove, RegionMove, RegionResize, SampleMove, Paint }
    Guid dragSample; BackgroundSample? dragSampleStart;
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

    static readonly Brush DimBrush = new SolidColorBrush(Color.FromArgb(153, 0, 0, 0));
    static readonly Brush BackBrush = new SolidColorBrush(Color.FromRgb(28, 28, 28));
    static readonly Pen CropPen = new(Brushes.White, 1) { DashStyle = new DashStyle([6, 4], 0) };
    static readonly Pen CropEditPen = new(Brushes.Gold, 1) { DashStyle = new DashStyle([6, 4], 0) };
    static readonly Pen HandlePen = new(Brushes.Black, 1);
    static readonly Pen DustPen = new(new SolidColorBrush(Color.FromArgb(217, 255, 255, 255)), 1) { DashStyle = new DashStyle([4, 3], 0) };
    static readonly Pen DustSelectedPen = new(Brushes.Gold, 1.5);
    static readonly Pen DustBrightPen = new(new SolidColorBrush(Color.FromArgb(230, 255, 149, 0)), 1.5) { DashStyle = new DashStyle([1, 2], 0) };
    static readonly Pen DustBrightSelectedPen = new(Brushes.Yellow, 1.5) { DashStyle = new DashStyle([1, 2], 0) };
    static readonly Pen DustMaxRadiusPen = new(new SolidColorBrush(Color.FromArgb(180, 255, 149, 0)), 1) { DashStyle = new DashStyle([1, 3], 0) };
    static readonly Pen CropGridPen = new(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 0.5);

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

    /// <summary>The zoom of "Fit" (the crop, else the whole image, with a small margin).</summary>
    public double FitZoom
    {
        get
        {
            var r = FitTarget;
            if (r.Width <= 0 || r.Height <= 0 || ActualWidth <= 0) return Zoom;
            return Math.Min(ActualWidth / r.Width, ActualHeight / r.Height) * 0.97;
        }
    }

    /// <summary>The view shows the fitted picture (zoom within 1 % of the fit zoom).</summary>
    public bool IsFitted => FitZoom > 0 && Math.Abs(Zoom - FitZoom) / FitZoom < 0.01;

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

    void UpdateCursor() => Cursor = spaceHeld ? Cursors.Hand : cropEditing || dustEditing || gradientEditing || correctionEditing ? Cursors.Cross : Cursors.Arrow;

    /// <summary>Cursor where a drag turns the picture: a rotate arrow in a dark disc (a PNG cursor; Hand if that fails).</summary>
    static Cursor? turnCursor;
    static Cursor TurnCursor
    {
        get
        {
            if (turnCursor != null) return turnCursor;
            try
            {
                const int size = 32;
                var v = new DrawingVisual();
                using (var dc = v.RenderOpen())
                {
                    dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Point(16, 16), 14, 14);
                    var pen = new Pen(Brushes.White, 2.5);
                    var g = new StreamGeometry();
                    using (var c = g.Open())
                    {
                        // Three quarters of a circle and an arrow head at its end.
                        c.BeginFigure(new Point(24, 16), false, false);
                        c.ArcTo(new Point(16, 8), new Size(8, 8), 0, true, SweepDirection.Clockwise, true, false);
                        c.BeginFigure(new Point(12, 4), false, false); c.LineTo(new Point(16, 8), true, false); c.LineTo(new Point(12, 12), true, false);
                    }
                    dc.DrawGeometry(null, pen, g);
                }
                var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bmp.Render(v);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                using var png = new System.IO.MemoryStream();
                enc.Save(png);
                // A .cur file holding the PNG, hot spot in the middle.
                var cur = new System.IO.MemoryStream();
                var w = new System.IO.BinaryWriter(cur);
                w.Write((short)0); w.Write((short)2); w.Write((short)1);
                w.Write((byte)size); w.Write((byte)size); w.Write((byte)0); w.Write((byte)0);
                w.Write((short)16); w.Write((short)16); w.Write((int)png.Length); w.Write(22);
                w.Write(png.ToArray());
                cur.Position = 0;
                turnCursor = new Cursor(cur);
            }
            catch { turnCursor = Cursors.Hand; }
            return turnCursor;
        }
    }

    /// <summary>In the band around the crop being edited (outside it, within TurnZone): a drag there turns the picture.</summary>
    bool InTurnZone(Point p)
    {
        if (!cropEditing || cropDraft is not PixelRect d) return false;
        var c = ViewRect(ToRect(d));
        return !Rect.Inflate(c, 8, 8).Contains(p) && Rect.Inflate(c, TurnZone, TurnZone).Contains(p);
    }

    static double AngleOf(Point p, Rect c) => Math.Atan2(p.Y - (c.Top + c.Height / 2), p.X - (c.Left + c.Width / 2)) * 180 / Math.PI;

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
        if (frame is PreviewFrame f)
        {
            double turn = cropEditing ? cropAngle - fineAngle : 0;
            if (turn != 0)
            {
                // Turn about the picture's centre (clockwise on screen), no render: the channels are resampled on Apply.
                var ic = ViewPoint(new Point(imageSize.Width / 2, imageSize.Height / 2));
                dc.PushTransform(new RotateTransform(turn, ic.X, ic.Y));
                dc.DrawImage(f.Bitmap, ViewRect(f.Rect));
                dc.Pop();
            }
            else dc.DrawImage(f.Bitmap, ViewRect(f.Rect));
        }
        DrawCropOverlay(dc);
        DrawDustOverlay(dc);
        DrawGradientOverlay(dc);
        DrawCorrectionOverlay(dc);
    }

    // ---- Corrections editor ----------------------------------------------------------------------

    static readonly Pen BrushDarkPen = new(new SolidColorBrush(Color.FromArgb(230, 255, 70, 60)), 1);
    static readonly Pen BrushLightPen = new(new SolidColorBrush(Color.FromArgb(230, 80, 150, 255)), 1);

    /// <summary>Tint image of the map: red where it darkens, blue where it brightens, alpha by amount (premultiplied BGRA).</summary>
    static System.Windows.Media.Imaging.WriteableBitmap MakeCorrectionTint(CorrectionMap m)
    {
        int w = m.Width, h = m.Height;
        var bytes = new byte[w * h * 4];
        var p = m.Plane.Data;
        for (int i = 0; i < w * h; i++)
        {
            float v = p[i];
            if (v == 0) continue;
            float a = Math.Min(Math.Abs(v), 1) * 0.55f;
            var (r, g, b) = v < 0 ? (1f, 0.25f, 0.2f) : (0.3f, 0.6f, 1f);
            bytes[i * 4] = (byte)(b * a * 255); bytes[i * 4 + 1] = (byte)(g * a * 255); bytes[i * 4 + 2] = (byte)(r * a * 255); bytes[i * 4 + 3] = (byte)(a * 255);
        }
        var bmp = new System.Windows.Media.Imaging.WriteableBitmap(w, h, 96, 96, PixelFormats.Pbgra32, null);
        bmp.WritePixels(new Int32Rect(0, 0, w, h), bytes, w * 4, 0);
        bmp.Freeze();
        return bmp;
    }

    void DrawCorrectionOverlay(DrawingContext dc)
    {
        if (!correctionEditing) return;
        if (correctionShowMap && correctionMap is CorrectionMap m)
        {
            correctionTint ??= MakeCorrectionTint(m);
            dc.DrawImage(correctionTint, ViewRect(new Rect(0, 0, imageSize.Width, imageSize.Height)));
        }
        if (brushPoint is Point bp)
        {
            double r = Math.Max(brushRadius * Zoom, 2);
            dc.DrawEllipse(null, brushDarken ? BrushDarkPen : BrushLightPen, bp, r, r);
            if (brushResizing)
            {
                var text = new FormattedText($"r = {brushRadius:0} px", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var at = new Point(bp.X - text.Width / 2, bp.Y - r - 22);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), null, new Rect(at.X - 4, at.Y - 2, text.Width + 8, text.Height + 4), 3, 3);
                dc.DrawText(text, at);
            }
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        if (dustEditing && dustBrightMode)
        {
            var rp = e.GetPosition(this);
            maxRadiusResizing = true; maxRadiusStart = rp; maxRadiusStartValue = dustMaxRadius;
            CaptureMouse(); e.Handled = true; InvalidateVisual();
            return;
        }
        if (!correctionEditing) { base.OnMouseRightButtonDown(e); return; }
        var p = e.GetPosition(this);
        brushPoint = p; brushResizing = true; brushResizeStart = p; brushResizeRadius = brushRadius;
        CaptureMouse(); e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (maxRadiusResizing) { maxRadiusResizing = false; dustPointer = e.GetPosition(this); ReleaseMouseCapture(); e.Handled = true; InvalidateVisual(); return; }
        if (brushResizing) { brushResizing = false; ReleaseMouseCapture(); e.Handled = true; InvalidateVisual(); return; }
        base.OnMouseRightButtonUp(e);
    }

    void DrawCropOverlay(DrawingContext dc)
    {
        PixelRect? rect = cropEditing ? cropDraft : (cropOverlay == CropOverlayMode.Hidden ? null : crop);
        bool cropOnly = !cropEditing && cropOverlay == CropOverlayMode.CropOnly;
        if (rect is not PixelRect r) return;
        var full = Rect.Intersect(ViewRect(Rect.Union(new Rect(new Point(0, 0), imageSize), CropLimits)), new Rect(-2, -2, ActualWidth + 4, ActualHeight + 4));
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
        {
            // Thirds; a finer grid while the picture is turned, to level it against.
            int n = cropAngle != fineAngle || drag == DragKind.Turn ? 8 : 3;
            for (int i = 1; i < n; i++)
            {
                double x = c.Left + c.Width * i / n, y = c.Top + c.Height * i / n;
                dc.DrawLine(CropGridPen, new Point(x, c.Top), new Point(x, c.Bottom));
                dc.DrawLine(CropGridPen, new Point(c.Left, y), new Point(c.Right, y));
            }
        }
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
            // Bright spots (star halos) are orange dotted circles, the selected one yellow dotted.
            bool bright = s.Polarity == SpotPolarity.Bright;
            if (s.Id == selectedDustSpot)
            {
                dc.DrawEllipse(null, bright ? DustBrightSelectedPen : DustSelectedPen, c, r, r);
                dc.DrawLine(DustSelectedPen, new Point(c.X - 4, c.Y), new Point(c.X + 4, c.Y));
                dc.DrawLine(DustSelectedPen, new Point(c.X, c.Y - 4), new Point(c.X, c.Y + 4));
                var h = DustHandlePoint(s);
                dc.DrawEllipse(Brushes.Gold, HandlePen, h, 5, 5);
            }
            else dc.DrawEllipse(null, bright ? DustBrightPen : DustPen, c, r, r);
        }
        // The largest radius a click gives a new bright spot, at the pointer.
        if (dustBrightMode && (maxRadiusResizing ? maxRadiusStart : dustPointer) is Point bp)
        {
            double r = Math.Max(dustMaxRadius * Zoom, 2);
            dc.DrawEllipse(null, DustMaxRadiusPen, bp, r, r);
            if (maxRadiusResizing)
            {
                var text = new FormattedText($"max r = {dustMaxRadius:0} px", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var at = new Point(bp.X - text.Width / 2, bp.Y - r - 22);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(128, 0, 0, 0)), null, new Rect(at.X - 4, at.Y - 2, text.Width + 8, text.Height + 4), 3, 3);
                dc.DrawText(text, at);
            }
        }
    }

    static readonly Pen GradientPen = new(Brushes.Gold, 1.5);
    static readonly Pen GradientRefinedPen = new(Brushes.Orange, 1) { DashStyle = new DashStyle([5, 4], 0) };
    static readonly Pen RegionPen = new(new SolidColorBrush(Color.FromArgb(204, 255, 59, 48)), 1) { DashStyle = new DashStyle([4, 3], 0) };
    static readonly Pen RegionSelectedPen = new(new SolidColorBrush(Color.FromRgb(255, 59, 48)), 1.5);
    static readonly Brush RegionFill = new SolidColorBrush(Color.FromArgb(24, 255, 59, 48));
    static readonly Pen SamplePen = new(new SolidColorBrush(Color.FromArgb(230, 52, 199, 89)), 1);
    static readonly Pen SampleRejectedPen = new(new SolidColorBrush(Color.FromArgb(230, 255, 59, 48)), 1) { DashStyle = new DashStyle([3, 3], 0) };
    static readonly Pen SampleSelectedPen = new(Brushes.Gold, 1.5);
    static readonly Brush SampleSelectedFill = new SolidColorBrush(Color.FromArgb(38, 255, 215, 0));

    static double Dist(Point a, Point b) => (a - b).Length;

    void DrawRotatedEllipse(DrawingContext dc, Brush? fill, Pen pen, Point c, double rx, double ry, double angle, bool axes)
    {
        dc.PushTransform(new RotateTransform(angle * 180 / Math.PI, c.X, c.Y));
        dc.DrawEllipse(fill, pen, c, rx, ry);
        if (axes) { dc.DrawLine(pen, new Point(c.X - rx, c.Y), new Point(c.X + rx, c.Y)); dc.DrawLine(pen, new Point(c.X, c.Y - ry), new Point(c.X, c.Y + ry)); }
        dc.Pop();
    }

    static void DrawHandleSquare(DrawingContext dc, Brush fill, Point h) => dc.DrawRoundedRectangle(fill, HandlePen, new Rect(h.X - 5, h.Y - 5, 10, 10), 2, 2);

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

    void DrawGradientGuide(DrawingContext dc, GradientGesture g, Pen pen, bool axes)
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

    /// <summary>The sample under a view point.</summary>
    BackgroundSample? GradientSampleAt(Point p)
    {
        double r = Math.Max(gradientSampleRadius * Zoom, 6);
        return gradientSamples.Where(s => Dist(p, ViewPoint(new Point(s.X, s.Y))) <= r).OrderBy(s => Dist(p, ViewPoint(new Point(s.X, s.Y)))).FirstOrDefault();
    }

    void DrawGradientOverlay(DrawingContext dc)
    {
        if (!gradientEditing) return;
        // Background samples: kept (green), rejected by the fit (red, dashed), selected (yellow).
        double sr = Math.Max(gradientSampleRadius * Zoom, 3);
        foreach (var s in gradientSamples)
        {
            var c = ViewPoint(new Point(s.X, s.Y));
            if (s.Id == selectedGradientSample) dc.DrawEllipse(SampleSelectedFill, SampleSelectedPen, c, sr, sr);
            else if (gradientSampleResults.TryGetValue(s.Id, out var res) && res.IsRejected) dc.DrawEllipse(null, SampleRejectedPen, c, sr, sr);
            else dc.DrawEllipse(null, SamplePen, c, sr, sr);
        }
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
        if (!gradientProtectMode && GradientSampleAt(p) is BackgroundSample bs)
        {
            if (bs.Id != selectedGradientSample) GradientSampleSelect?.Invoke(bs.Id);
            drag = DragKind.SampleMove; dragSample = bs.Id; dragSampleStart = bs.Clone(); dragAnchor = ImagePoint(p);
            return;
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
        if (!gradientProtectMode && gradientShape != GradientShape.Polynomial && gradientShape != GradientShape.Samples && (gradientGesture == null || gradientGesture.Shape == GradientShape.Polynomial))
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
            case DragKind.SampleMove:
            {
                int i = gradientSamples.FindIndex(s => s.Id == dragSample);
                if (i < 0 || dragSampleStart == null) return true;
                var samples = gradientSamples.Select(s => s.Clone()).ToList();
                samples[i].X = Math.Min(Math.Max(dragSampleStart.X + ip.X - dragAnchor.X, 0), imageSize.Width);
                samples[i].Y = Math.Min(Math.Max(dragSampleStart.Y + ip.Y - dragAnchor.Y, 0), imageSize.Height);
                gradientSamples = samples; InvalidateVisual();
                GradientSamplesChanged?.Invoke(samples);
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
            bool inside = ip.X >= 0 && ip.Y >= 0 && ip.X < imageSize.Width && ip.Y < imageSize.Height;
            if (gradientProtectMode && inside) GradientRegionAdd?.Invoke(ip);
            else if (gradientShape == GradientShape.Samples && inside) GradientSampleAdd?.Invoke(ip);
            else { GradientRegionSelect?.Invoke(null); GradientSampleSelect?.Invoke(null); }
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
        if (spaceHeld) { drag = DragKind.Pan; dragLast = p; CaptureMouse(); return; }
        if (correctionEditing)
        {
            var ip = ImagePoint(p);
            brushPoint = p; paintLast = ip; drag = DragKind.Paint;
            StrokeBegin?.Invoke();
            Paint?.Invoke(ip, ip, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
            CaptureMouse(); InvalidateVisual();
            return;
        }
        if (e.ClickCount == 2) { DoubleClick?.Invoke(); drag = DragKind.None; return; }
        if (dustEditing) { DustMouseDown(p); CaptureMouse(); return; }
        if (gradientEditing) { GradientMouseDown(p); CaptureMouse(); return; }
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
                // In the band around the crop: turn the picture (farther out, a drag draws a new crop).
                if (InTurnZone(p))
                {
                    drag = DragKind.Turn; turnStartAngle = cropAngle; turnStartPointer = AngleOf(p, c);
                    CaptureMouse(); InvalidateVisual(); return;
                }
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
        if (maxRadiusResizing)
        {
            // Horizontal movement scales the radius (exponentially, as for the brush).
            double lng = Math.Max(imageSize.Width, imageSize.Height);
            dustMaxRadius = Math.Round(Math.Min(Math.Max(maxRadiusStartValue * Math.Exp((p.X - maxRadiusStart.X) / 120), 4), Math.Max(lng * 0.1, 40)));
            DustMaxRadiusChanged?.Invoke(dustMaxRadius);
            InvalidateVisual();
            return;
        }
        if (dustEditing && dustBrightMode) { dustPointer = p; InvalidateVisual(); }
        if (correctionEditing && (!spaceHeld || brushResizing))
        {
            if (brushResizing)
            {
                // Horizontal movement scales the radius (exponentially: the same travel doubles or halves it).
                double lng = Math.Max(imageSize.Width, imageSize.Height);
                brushRadius = Math.Min(Math.Max(brushResizeRadius * Math.Exp((p.X - brushResizeStart.X) / 120), lng * 0.003), lng * 0.2);
                brushPoint = brushResizeStart;
                BrushRadiusChanged?.Invoke(brushRadius);
                InvalidateVisual();
                return;
            }
            brushPoint = p;
            if (drag == DragKind.Paint && e.LeftButton == MouseButtonState.Pressed)
            {
                Paint?.Invoke(paintLast, ip, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
                paintLast = ip;
            }
            else if (drag == DragKind.Paint) { drag = DragKind.None; ReleaseMouseCapture(); }
            InvalidateVisual();
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed && drag != DragKind.None) { drag = DragKind.None; ReleaseMouseCapture(); UpdateCursor(); }
        var lim = CropLimits;
        Point ClampPoint(Point q) => new(Math.Min(Math.Max(q.X, lim.Left), lim.Right), Math.Min(Math.Max(q.Y, lim.Top), lim.Bottom));
        if (cropEditing && drag == DragKind.None && !spaceHeld) Cursor = InTurnZone(p) ? TurnCursor : Cursors.Cross;
        if (GradientMouseMove(p, ip)) return;
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
                int lx0 = (int)Math.Round(lim.Left), ly0 = (int)Math.Round(lim.Top), lx1 = (int)Math.Round(lim.Right), ly1 = (int)Math.Round(lim.Bottom);
                x = Math.Min(Math.Max(lx0, x), lx1 - r.Width); y = Math.Min(Math.Max(ly0, y), ly1 - r.Height);
                cropDraft = new PixelRect(x, y, r.Width, r.Height); InvalidateVisual(); CropDraftChanged?.Invoke(cropDraft);
                break;
            }
            case DragKind.Turn:
            {
                if (cropDraft is not PixelRect d) break;
                double delta = AngleOf(p, ViewRect(ToRect(d))) - turnStartPointer;
                if (delta > 180) delta -= 360; else if (delta < -180) delta += 360;
                // A tenth of a degree; with Shift whole degrees. Quarter turns (and 0) catch the pointer within 1.5°.
                double a = turnStartAngle + delta;
                a = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? Math.Round(a) : Math.Round(a * 10) / 10;
                double quarter = Math.Round(a / 90) * 90;
                if (Math.Abs(a - quarter) < 1.5) a = quarter;
                CropAngleChange?.Invoke(a);
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
        GradientMouseUp();
        if (drag == DragKind.Turn) InvalidateVisual();
        drag = DragKind.None;
        ReleaseMouseCapture();
        UpdateCursor();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        Hover?.Invoke(null);
        if (brushPoint != null && !brushResizing) { brushPoint = null; InvalidateVisual(); }
        if (dustPointer != null && !maxRadiusResizing) { dustPointer = null; InvalidateVisual(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Ctrl / Alt combinations are menu shortcuts (Ctrl+1 / Ctrl+2 switch tabs), never the plain keys below.
        if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0) { base.OnKeyDown(e); return; }
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
