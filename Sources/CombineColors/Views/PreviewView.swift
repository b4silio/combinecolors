import SwiftUI
import AppKit
import AstroCore

enum ZoomAction { case fit, actualSize, zoomIn, zoomOut }

/// AppKit view that displays the rendered preview with smooth zoom/pan (CALayer composited),
/// a crop overlay with interactive crop editing, the dust-spot editor (circles that can be
/// added by clicking, moved, resized by their handle and deleted) and the gradient editor (a line
/// or an ellipse drawn over the gradient, with handles, plus protected regions).
final class ZoomPanNSView: NSView {
    var imageSize: CGSize = .zero { didSet { if imageSize != oldValue { needsFit = true; needsLayout = true } } }
    var zoom: CGFloat = 1
    var origin: CGPoint = .zero          // image (0,0) in top-left view coordinates
    private var needsFit = true
    private(set) var didFit = false

    var previewFrame: PreviewFrame? { didSet { updateImageLayer() } }
    var crop: PixelRect? { didSet { updateOverlay() } }
    var cropEditing = false { didSet { updateImageLayer(); updateOverlay(); updateCursor() } }
    var cropDraft: PixelRect? { didSet { updateOverlay(); if cropEditing { updateCursor() } } }
    var cropOverlay: CropOverlayMode = .frame { didSet { updateOverlay() } }
    /// Fine rotation already applied to the channels, and the one being edited with the crop
    /// (degrees, clockwise). While they differ the picture is turned on screen under the upright
    /// crop; the channels are resampled on Apply.
    var fineAngle: Double = 0 { didSet { if fineAngle != oldValue { updateImageLayer() } } }
    var cropAngle: Double = 0 { didSet { if cropAngle != oldValue { updateImageLayer(); updateOverlay() } } }
    var onCropAngleChange: ((Double) -> Void)?
    /// Where the crop being edited may go (image coordinates): the canvas of the turned picture,
    /// sideways after an odd number of quarter turns, so it can reach outside the shown picture.
    var cropBounds: CGRect = .zero {
        didSet {
            guard cropBounds != oldValue else { return }
            // The turned canvas went sideways (or back): show all of it.
            if cropEditing, oldValue.width > 0, cropBounds.size != oldValue.size { fit(rect: cropBounds) }
            updateOverlay()
        }
    }
    private var cropLimits: CGRect { cropEditing && cropBounds.width > 0 ? cropBounds : CGRect(origin: .zero, size: imageSize) }
    var dustEditing = false { didSet { updateDustOverlay(); updateCursor() } }
    var dustSpots: [DustSpot] = [] { didSet { updateDustOverlay() } }
    /// New spots are bright ones: the pointer shows the largest radius their fit may give
    /// (right-drag changes it).
    var dustBrightMode = false { didSet { if dustBrightMode != oldValue { updateBrushCursor() } } }
    var dustMaxRadius: CGFloat = 80 { didSet { if dustMaxRadius != oldValue { updateBrushCursor() } } }
    var onDustMaxRadiusChange: ((CGFloat) -> Void)?
    var selectedDustSpot: UUID? = nil { didSet { updateDustOverlay() } }
    var gradientEditing = false { didSet { updateGradientOverlay(); updateCursor() } }
    /// Guide drawn for the edited channel (nil: nothing drawn yet).
    var gradientGesture: GradientGesture? = nil { didSet { updateGradientOverlay() } }
    /// Guide actually used by the latest fit (shown dashed when it differs from the drawing).
    var gradientRefined: GradientGesture? = nil { didSet { updateGradientOverlay() } }
    /// Shape a drag on empty sky creates when nothing is drawn.
    var gradientShape: GradientShape = .ellipse
    var gradientRegions: [GradientRegion] = [] { didSet { updateGradientOverlay() } }
    var selectedGradientRegion: UUID? = nil { didSet { updateGradientOverlay() } }
    /// Clicks add protected regions instead of drawing.
    var gradientProtectMode = false
    /// Corrections editor: paint with the brush; the map is drawn as a tint (red darkens, blue brightens).
    var correctionEditing = false { didSet { updateCorrectionOverlay(); updateCursor(); if !correctionEditing { brushLayer.isHidden = true; radiusLabel.isHidden = true } } }
    var correctionMap: CorrectionMap? = nil
    var correctionVersion = 0 { didSet { if correctionVersion != oldValue { correctionTint = nil; updateCorrectionOverlay() } } }
    var correctionShowMap = false { didSet { updateCorrectionOverlay() } }
    /// Brush radius in image pixels, and its sense.
    var brushRadius: CGFloat = 50 { didSet { updateBrushCursor() } }
    var brushDarken = true { didSet { updateBrushCursor() } }
    private var correctionTint: CGImage? = nil
    private var brushPoint: CGPoint? = nil
    var onStrokeBegin: (() -> Void)?
    var onPaint: ((CGPoint, CGPoint, Bool) -> Void)?
    var onBrushRadiusChange: ((CGFloat) -> Void)?
    /// Background samples of the edited channel (`samples` shape), their radius and what the fit made of them.
    var gradientSamples: [BackgroundSample] = [] { didSet { updateGradientOverlay() } }
    var gradientSampleRadius: CGFloat = 20 { didSet { updateGradientOverlay() } }
    var gradientSampleResults: [UUID: SampleResult] = [:] { didSet { updateGradientOverlay() } }
    var selectedGradientSample: UUID? = nil { didSet { updateGradientOverlay() } }
    private var lastBounds: CGRect = .zero

    var onViewportChange: ((CGRect, Double) -> Void)?
    var onHover: ((CGPoint?) -> Void)?
    var onCropDraftChange: ((PixelRect?) -> Void)?
    var onDoubleClick: (() -> Void)?
    var onDustSpotsChange: (([DustSpot]) -> Void)?
    var onDustAdd: ((CGPoint) -> Void)?
    var onDustSelect: ((UUID?) -> Void)?
    var onDustDelete: (() -> Void)?
    var onGradientGestureChange: ((GradientGesture) -> Void)?
    var onGradientRegionsChange: (([GradientRegion]) -> Void)?
    var onGradientRegionAdd: ((CGPoint) -> Void)?
    var onGradientRegionSelect: ((UUID?) -> Void)?
    var onGradientDelete: (() -> Void)?
    var onGradientSampleAdd: ((CGPoint) -> Void)?
    var onGradientSamplesChange: (([BackgroundSample]) -> Void)?
    var onGradientSampleSelect: ((UUID?) -> Void)?

    private let imageLayer = CALayer()
    private let dimLayers = (0..<4).map { _ in CALayer() }
    private let cropBorder = CAShapeLayer()
    /// Thirds (finer while turning) inside the crop being edited, to level the picture against.
    private let cropGrid = CAShapeLayer()
    private let handleLayers = (0..<8).map { _ in CALayer() }
    private let dustLayer = CAShapeLayer()
    private let dustSelectedLayer = CAShapeLayer()
    /// Bright spots (star halos): orange, dotted, to tell them from dust shadows.
    private let dustBrightLayer = CAShapeLayer()
    private let dustHandle = CALayer()
    private let gradientLayer = CAShapeLayer()
    private let gradientRefinedLayer = CAShapeLayer()
    private let gradientHandles = (0..<3).map { _ in CALayer() }
    private let sampleLayer = CAShapeLayer()
    private let correctionLayer = CALayer()
    private let brushLayer = CAShapeLayer()
    private let radiusLabel = CATextLayer()
    private let sampleRejectedLayer = CAShapeLayer()
    private let sampleSelectedLayer = CAShapeLayer()
    private let regionLayer = CAShapeLayer()
    private let regionSelectedLayer = CAShapeLayer()
    private let regionHandles = (0..<2).map { _ in CALayer() }
    private var trackingArea: NSTrackingArea?

    private enum Drag {
        case none, pan(CGPoint), move(PixelRect, CGPoint), resize(Int, PixelRect), create(CGPoint)
        /// Crop editor: turning the picture by dragging outside a corner (angle at the start, pointer
        /// angle around the crop centre at the start, in degrees).
        case turn(Double, Double)
        /// Dust editor: a press on empty sky pans, and adds a spot when released without moving.
        case dustPan(last: CGPoint, start: CGPoint, moved: Bool)
        case dustMove(UUID, DustSpot, CGPoint)
        case dustResize(UUID)
        /// Gradient editor: a press on empty sky pans (and adds a protected region when released
        /// without moving in protect mode), or draws the guide when none exists.
        case gradientPan(last: CGPoint, start: CGPoint, moved: Bool)
        case gradientCreate(CGPoint)
        /// Handle 0 = line start / ellipse centre, 1 = line end / ellipse a-axis, 2 = ellipse b-axis.
        case gradientHandle(Int)
        case gradientMove(GradientGesture, CGPoint)
        case regionMove(UUID, GradientRegion, CGPoint)
        case sampleMove(UUID, BackgroundSample, CGPoint)
        /// Corrections editor: painting (last image point), or resizing the brush with the right button.
        case paint(CGPoint)
        case brushResize(start: CGPoint, radius: CGFloat)
        /// Dust editor, bright spots: the largest radius of new spots, with the right button.
        case maxRadiusResize(start: CGPoint, radius: CGFloat)
        /// Axis 0 = a (horizontal), 1 = b (vertical).
        case regionResize(UUID, Int)
    }
    private var drag: Drag = .none
    /// Space bar held: every mode pans on a drag (brush, spots, guides and crop wait).
    private var spaceHeld = false { didSet { if spaceHeld != oldValue { updateCursor(); updateBrushCursor() } } }
    private var keyMonitor: Any? = nil

    override init(frame: NSRect) {
        super.init(frame: frame)
        // The space bar is watched on the window, not only when the preview has focus, so the toolbar
        // sliders may keep it; typing in a text field is left alone.
        keyMonitor = NSEvent.addLocalMonitorForEvents(matching: [.keyDown, .keyUp]) { [weak self] event in
            guard let self, event.keyCode == 49, event.window === self.window, !self.isHidden,
                  !(self.window?.firstResponder is NSTextView) else { return event }
            if event.type == .keyDown { if !event.isARepeat { self.spaceHeld = true } } else { self.spaceHeld = false }
            return nil
        }
        wantsLayer = true
        layer?.masksToBounds = true
        layer?.backgroundColor = NSColor(calibratedWhite: 0.11, alpha: 1).cgColor
        imageLayer.contentsGravity = .resize
        imageLayer.magnificationFilter = .linear
        imageLayer.minificationFilter = .linear
        imageLayer.isOpaque = true
        layer?.addSublayer(imageLayer)
        for d in dimLayers { d.backgroundColor = NSColor(calibratedWhite: 0, alpha: 0.6).cgColor; layer?.addSublayer(d) }
        cropBorder.fillColor = nil
        cropBorder.strokeColor = NSColor.white.cgColor
        cropBorder.lineWidth = 1
        cropBorder.lineDashPattern = [6, 4]
        layer?.addSublayer(cropBorder)
        cropGrid.fillColor = nil
        cropGrid.strokeColor = NSColor.white.withAlphaComponent(0.35).cgColor
        cropGrid.lineWidth = 0.5
        cropGrid.isHidden = true
        layer?.addSublayer(cropGrid)
        for h in handleLayers {
            h.backgroundColor = NSColor.white.cgColor
            h.borderColor = NSColor.black.cgColor
            h.borderWidth = 1
            h.cornerRadius = 2
            layer?.addSublayer(h)
        }
        dustLayer.fillColor = nil
        dustLayer.strokeColor = NSColor.white.withAlphaComponent(0.85).cgColor
        dustLayer.lineWidth = 1
        dustLayer.lineDashPattern = [4, 3]
        dustSelectedLayer.fillColor = nil
        dustSelectedLayer.strokeColor = NSColor.systemYellow.cgColor
        dustSelectedLayer.lineWidth = 1.5
        dustHandle.backgroundColor = NSColor.systemYellow.cgColor
        dustHandle.borderColor = NSColor.black.cgColor
        dustHandle.borderWidth = 1
        dustHandle.cornerRadius = 5
        dustBrightLayer.fillColor = nil
        dustBrightLayer.strokeColor = NSColor.systemOrange.withAlphaComponent(0.95).cgColor
        dustBrightLayer.lineWidth = 1.2
        dustBrightLayer.lineDashPattern = [1.5, 3]
        dustBrightLayer.lineCap = .round
        layer?.addSublayer(dustLayer)
        layer?.addSublayer(dustBrightLayer)
        layer?.addSublayer(dustSelectedLayer)
        layer?.addSublayer(dustHandle)
        gradientLayer.fillColor = nil
        gradientLayer.strokeColor = NSColor.systemYellow.cgColor
        gradientLayer.lineWidth = 1.5
        gradientRefinedLayer.fillColor = nil
        gradientRefinedLayer.strokeColor = NSColor.systemOrange.cgColor
        gradientRefinedLayer.lineWidth = 1
        gradientRefinedLayer.lineDashPattern = [5, 4]
        for h in gradientHandles {
            h.backgroundColor = NSColor.systemYellow.cgColor
            h.borderColor = NSColor.black.cgColor
            h.borderWidth = 1
            h.cornerRadius = 2
        }
        regionLayer.fillColor = NSColor.systemRed.withAlphaComponent(0.08).cgColor
        regionLayer.strokeColor = NSColor.systemRed.withAlphaComponent(0.8).cgColor
        regionLayer.lineWidth = 1
        regionLayer.lineDashPattern = [4, 3]
        regionSelectedLayer.fillColor = NSColor.systemRed.withAlphaComponent(0.12).cgColor
        regionSelectedLayer.strokeColor = NSColor.systemRed.cgColor
        regionSelectedLayer.lineWidth = 1.5
        for h in regionHandles {
            h.backgroundColor = NSColor.systemRed.cgColor
            h.borderColor = NSColor.black.cgColor
            h.borderWidth = 1
            h.cornerRadius = 5
        }
        sampleLayer.fillColor = nil; sampleLayer.strokeColor = NSColor.systemGreen.withAlphaComponent(0.9).cgColor; sampleLayer.lineWidth = 1
        sampleRejectedLayer.fillColor = nil; sampleRejectedLayer.strokeColor = NSColor.systemRed.withAlphaComponent(0.9).cgColor; sampleRejectedLayer.lineWidth = 1; sampleRejectedLayer.lineDashPattern = [3, 3]
        sampleSelectedLayer.fillColor = NSColor.systemYellow.withAlphaComponent(0.15).cgColor; sampleSelectedLayer.strokeColor = NSColor.systemYellow.cgColor; sampleSelectedLayer.lineWidth = 1.5
        correctionLayer.magnificationFilter = .linear; correctionLayer.minificationFilter = .linear; correctionLayer.isHidden = true
        brushLayer.fillColor = nil; brushLayer.strokeColor = NSColor.white.withAlphaComponent(0.9).cgColor; brushLayer.lineWidth = 1; brushLayer.isHidden = true
        radiusLabel.fontSize = 11; radiusLabel.foregroundColor = NSColor.white.cgColor; radiusLabel.alignmentMode = .center; radiusLabel.isHidden = true
        radiusLabel.contentsScale = 2; radiusLabel.backgroundColor = NSColor.black.withAlphaComponent(0.5).cgColor; radiusLabel.cornerRadius = 3
        layer?.addSublayer(correctionLayer)
        for l in [sampleLayer, sampleRejectedLayer, sampleSelectedLayer, regionLayer, regionSelectedLayer, gradientRefinedLayer, gradientLayer] + regionHandles + gradientHandles { layer?.addSublayer(l) }
        layer?.addSublayer(brushLayer); layer?.addSublayer(radiusLabel)
        layer?.actions = ["sublayers": NSNull()]
        imageLayer.actions = ["contents": NSNull(), "bounds": NSNull(), "position": NSNull(), "frame": NSNull()]
        for l in dimLayers + handleLayers + gradientHandles + regionHandles + [cropBorder, cropGrid, dustLayer, dustBrightLayer, dustSelectedLayer, dustHandle, gradientLayer, gradientRefinedLayer, regionLayer, regionSelectedLayer, sampleLayer, sampleRejectedLayer, sampleSelectedLayer, correctionLayer, brushLayer, radiusLabel] { l.actions = ["bounds": NSNull(), "position": NSNull(), "frame": NSNull(), "path": NSNull(), "hidden": NSNull(), "lineDashPattern": NSNull()] }
    }

    deinit { if let k = keyMonitor { NSEvent.removeMonitor(k) } }

    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }
    var backing: CGFloat { window?.backingScaleFactor ?? 2 }

    // MARK: Geometry helpers (top-left coordinates)

    private func toAppKit(_ r: CGRect) -> CGRect { CGRect(x: r.minX, y: bounds.height - r.maxY, width: r.width, height: r.height) }
    private func topLeftPoint(_ event: NSEvent) -> CGPoint {
        let p = convert(event.locationInWindow, from: nil)
        return CGPoint(x: p.x, y: bounds.height - p.y)
    }
    func imagePoint(_ v: CGPoint) -> CGPoint { CGPoint(x: (v.x - origin.x) / zoom, y: (v.y - origin.y) / zoom) }
    func viewPoint(_ p: CGPoint) -> CGPoint { CGPoint(x: origin.x + p.x * zoom, y: origin.y + p.y * zoom) }
    func viewRect(_ r: CGRect) -> CGRect { CGRect(x: origin.x + r.minX * zoom, y: origin.y + r.minY * zoom, width: r.width * zoom, height: r.height * zoom) }

    var visibleImageRect: CGRect {
        let r = CGRect(x: -origin.x / zoom, y: -origin.y / zoom, width: bounds.width / zoom, height: bounds.height / zoom)
        return r.intersection(CGRect(origin: .zero, size: imageSize))
    }

    // MARK: Layout

    override func layout() {
        super.layout()
        // Keep what was in the middle of the view in the middle when the view is resized.
        if lastBounds.size != bounds.size {
            if lastBounds.width > 0, !needsFit {
                origin.x += (bounds.width - lastBounds.width) / 2
                origin.y += (bounds.height - lastBounds.height) / 2
            }
            lastBounds = bounds
        }
        if needsFit && imageSize != .zero && bounds.width > 10 {
            fit(rect: crop?.cgRect ?? CGRect(origin: .zero, size: imageSize))
            needsFit = false
            didFit = true
        }
        updateImageLayer()
        updateOverlay()
        updateDustOverlay()
        updateGradientOverlay()
        updateCorrectionOverlay()
        notifyViewport()
    }

    // MARK: Corrections editor

    /// Tint image of the map: red where it darkens, blue where it brightens, alpha by amount.
    private func makeCorrectionTint(_ m: CorrectionMap) -> CGImage? {
        let w = m.width, h = m.height
        var bytes = [UInt8](repeating: 0, count: w * h * 4)
        let p = m.plane.pointer
        for i in 0..<(w * h) {
            let v = p[i]
            guard v != 0 else { continue }
            let a = min(abs(v), 1) * 0.55
            // Premultiplied RGBA.
            let (r, g, b): (Float, Float, Float) = v < 0 ? (1, 0.25, 0.2) : (0.3, 0.6, 1)
            bytes[i * 4] = UInt8(r * a * 255); bytes[i * 4 + 1] = UInt8(g * a * 255); bytes[i * 4 + 2] = UInt8(b * a * 255); bytes[i * 4 + 3] = UInt8(a * 255)
        }
        let cs = CGColorSpaceCreateDeviceRGB()
        guard let provider = CGDataProvider(data: Data(bytes) as CFData) else { return nil }
        return CGImage(width: w, height: h, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: w * 4, space: cs,
                       bitmapInfo: CGBitmapInfo(rawValue: CGImageAlphaInfo.premultipliedLast.rawValue), provider: provider, decode: nil, shouldInterpolate: true, intent: .defaultIntent)
    }

    private func updateCorrectionOverlay() {
        guard correctionEditing, correctionShowMap, let m = correctionMap, imageSize != .zero else { correctionLayer.isHidden = true; return }
        if correctionTint == nil { correctionTint = makeCorrectionTint(m) }
        correctionLayer.contents = correctionTint
        correctionLayer.frame = toAppKit(viewRect(CGRect(origin: .zero, size: imageSize)))
        correctionLayer.isHidden = false
    }

    private var dustBrightCursor: Bool { dustEditing && dustBrightMode && !spaceHeld }

    private func updateBrushCursor() {
        if dustBrightCursor, let p = brushPoint {
            // The largest bright spot a click may give, around the pointer (orange, dotted).
            let r = max(dustMaxRadius * zoom, 2)
            brushLayer.path = CGPath(ellipseIn: toAppKit(CGRect(x: p.x - r, y: p.y - r, width: 2 * r, height: 2 * r)), transform: nil)
            brushLayer.strokeColor = NSColor.systemOrange.withAlphaComponent(0.6).cgColor
            brushLayer.lineDashPattern = [1.5, 4]
            brushLayer.isHidden = false
            return
        }
        brushLayer.lineDashPattern = nil
        guard correctionEditing, !spaceHeld, let p = brushPoint else { brushLayer.isHidden = true; return }
        let r = max(brushRadius * zoom, 2)
        brushLayer.path = CGPath(ellipseIn: toAppKit(CGRect(x: p.x - r, y: p.y - r, width: 2 * r, height: 2 * r)), transform: nil)
        brushLayer.strokeColor = (brushDarken ? NSColor.systemRed : NSColor.systemBlue).withAlphaComponent(0.9).cgColor
        brushLayer.isHidden = false
    }

    private func showRadiusLabel(at p: CGPoint, maxRadius: Bool = false) {
        let r = maxRadius ? dustMaxRadius : brushRadius
        radiusLabel.string = String(format: maxRadius ? "max r = %.0f px" : "r = %.0f px", r)
        let size = CGSize(width: maxRadius ? 110 : 90, height: 18)
        radiusLabel.frame = toAppKit(CGRect(x: p.x - size.width / 2, y: p.y - r * zoom - 24, width: size.width, height: size.height))
        radiusLabel.isHidden = false
    }

    override func rightMouseDown(with event: NSEvent) {
        if dustBrightCursor {
            let p = topLeftPoint(event)
            brushPoint = p
            drag = .maxRadiusResize(start: p, radius: dustMaxRadius)
            updateBrushCursor()
            showRadiusLabel(at: p, maxRadius: true)
            return
        }
        guard correctionEditing, !spaceHeld else { super.rightMouseDown(with: event); return }
        let p = topLeftPoint(event)
        brushPoint = p
        drag = .brushResize(start: p, radius: brushRadius)
        updateBrushCursor()
        showRadiusLabel(at: p)
    }

    override func rightMouseDragged(with event: NSEvent) {
        if case .maxRadiusResize(let start, let r0) = drag {
            let p = topLeftPoint(event)
            let long = max(imageSize.width, imageSize.height)
            let r = min(max(r0 * exp((p.x - start.x) / 120), 4), long * 0.1)
            dustMaxRadius = r
            brushPoint = start
            onDustMaxRadiusChange?(r)
            updateBrushCursor()
            showRadiusLabel(at: start, maxRadius: true)
            return
        }
        guard case .brushResize(let start, let r0) = drag else { super.rightMouseDragged(with: event); return }
        // Horizontal movement scales the radius (exponentially: the same travel doubles or halves it).
        let p = topLeftPoint(event)
        let long = max(imageSize.width, imageSize.height)
        let r = min(max(r0 * exp((p.x - start.x) / 120), long * 0.003), long * 0.2)
        brushRadius = r
        brushPoint = start
        onBrushRadiusChange?(r)
        updateBrushCursor()
        showRadiusLabel(at: start)
    }

    override func rightMouseUp(with event: NSEvent) {
        if case .brushResize = drag { drag = .none; radiusLabel.isHidden = true; return }
        if case .maxRadiusResize = drag { drag = .none; radiusLabel.isHidden = true; return }
        super.rightMouseUp(with: event)
    }

    override func viewDidChangeBackingProperties() { super.viewDidChangeBackingProperties(); notifyViewport() }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = trackingArea { removeTrackingArea(t) }
        let t = NSTrackingArea(rect: bounds, options: [.mouseMoved, .mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self, userInfo: nil)
        addTrackingArea(t)
        trackingArea = t
    }

    private func notifyViewport() {
        guard imageSize != .zero else { return }
        onViewportChange?(visibleImageRect, Double(zoom * backing))
    }

    /// The zoom of "Fit" (the crop, else the whole image, with a small margin).
    var fitZoom: CGFloat {
        let r = crop?.cgRect ?? CGRect(origin: .zero, size: imageSize)
        guard r.width > 0, r.height > 0 else { return zoom }
        return min(bounds.width / r.width, bounds.height / r.height) * 0.97
    }

    /// The view shows the fitted picture (zoom within 1 % of the fit zoom).
    var isFitted: Bool { fitZoom > 0 && abs(zoom - fitZoom) / fitZoom < 0.01 }

    func fit(rect: CGRect) {
        guard rect.width > 0, rect.height > 0, bounds.width > 0 else { return }
        zoom = min(bounds.width / rect.width, bounds.height / rect.height) * 0.97
        origin = CGPoint(x: (bounds.width - rect.width * zoom) / 2 - rect.minX * zoom, y: (bounds.height - rect.height * zoom) / 2 - rect.minY * zoom)
        needsLayout = true
    }

    func perform(_ action: ZoomAction) {
        let center = CGPoint(x: bounds.midX, y: bounds.midY)
        switch action {
        case .fit: fit(rect: crop?.cgRect ?? CGRect(origin: .zero, size: imageSize))
        case .actualSize: setZoom(1 / backing, anchor: center)
        case .zoomIn: setZoom(zoom * 1.5, anchor: center)
        case .zoomOut: setZoom(zoom / 1.5, anchor: center)
        }
        needsLayout = true
    }

    func setZoom(_ z: CGFloat, anchor: CGPoint) {
        let minZoom = min(bounds.width / max(imageSize.width, 1), bounds.height / max(imageSize.height, 1)) * 0.1
        let nz = max(minZoom, min(z, 32 / backing))
        let ip = imagePoint(anchor)
        zoom = nz
        origin = CGPoint(x: anchor.x - ip.x * zoom, y: anchor.y - ip.y * zoom)
        clampOrigin()
        needsLayout = true
    }

    private func clampOrigin() {
        // Keep at least a quarter of the image visible.
        let w = imageSize.width * zoom, h = imageSize.height * zoom
        origin.x = min(max(origin.x, bounds.width * 0.25 - w), bounds.width * 0.75)
        origin.y = min(max(origin.y, bounds.height * 0.25 - h), bounds.height * 0.75)
    }

    private func updateImageLayer() {
        guard let f = previewFrame else { imageLayer.isHidden = true; return }
        imageLayer.isHidden = false
        imageLayer.contents = f.cgImage
        let r = toAppKit(viewRect(f.rect))
        imageLayer.setAffineTransform(.identity)
        imageLayer.frame = r
        let turn = cropEditing ? cropAngle - fineAngle : 0
        if turn != 0 {
            // Turn about the picture's centre (clockwise on screen: negative in AppKit's y-up space).
            let ic = viewPoint(CGPoint(x: imageSize.width / 2, y: imageSize.height / 2))
            let d = CGPoint(x: ic.x - r.midX, y: (bounds.height - ic.y) - r.midY)
            let phi = -turn * .pi / 180, c = cos(phi), sn = sin(phi)
            imageLayer.setAffineTransform(CGAffineTransform(a: c, b: sn, c: -sn, d: c, tx: d.x - (c * d.x - sn * d.y), ty: d.y - (sn * d.x + c * d.y)))
        }
        let devicePerImage = zoom * backing
        imageLayer.magnificationFilter = devicePerImage >= 2 ? .nearest : .linear
    }

    private func updateOverlay() {
        let rect: PixelRect? = cropEditing ? cropDraft : (cropOverlay == .hidden ? nil : crop)
        let cropOnly = !cropEditing && cropOverlay == .cropOnly
        guard let r = rect, imageSize != .zero else {
            dimLayers.forEach { $0.isHidden = true }; cropBorder.isHidden = true; cropGrid.isHidden = true; handleLayers.forEach { $0.isHidden = true }
            return
        }
        let full = viewRect(CGRect(origin: .zero, size: imageSize).union(cropLimits)).intersection(bounds.insetBy(dx: -2, dy: -2))
        let c = viewRect(r.cgRect)
        // Four rectangles around the crop.
        let parts = [
            CGRect(x: full.minX, y: full.minY, width: full.width, height: max(0, c.minY - full.minY)),
            CGRect(x: full.minX, y: c.maxY, width: full.width, height: max(0, full.maxY - c.maxY)),
            CGRect(x: full.minX, y: c.minY, width: max(0, c.minX - full.minX), height: c.height),
            CGRect(x: c.maxX, y: c.minY, width: max(0, full.maxX - c.maxX), height: c.height),
        ]
        let dim = cropOnly ? (layer?.backgroundColor ?? NSColor.black.cgColor) : NSColor(calibratedWhite: 0, alpha: 0.6).cgColor
        for (l, p) in zip(dimLayers, parts) { l.isHidden = false; l.frame = toAppKit(p); l.backgroundColor = dim }
        cropBorder.isHidden = cropOnly
        cropBorder.path = CGPath(rect: toAppKit(c), transform: nil)
        if cropEditing {
            // Thirds; a finer grid while the picture is turned, to level it against.
            let n = cropAngle != fineAngle || isTurning ? 8 : 3
            let g = CGMutablePath(), a = toAppKit(c)
            for i in 1..<n {
                let x = a.minX + a.width * CGFloat(i) / CGFloat(n), y = a.minY + a.height * CGFloat(i) / CGFloat(n)
                g.move(to: CGPoint(x: x, y: a.minY)); g.addLine(to: CGPoint(x: x, y: a.maxY))
                g.move(to: CGPoint(x: a.minX, y: y)); g.addLine(to: CGPoint(x: a.maxX, y: y))
            }
            cropGrid.path = g
            cropGrid.isHidden = false
        } else {
            cropGrid.isHidden = true
        }
        cropBorder.strokeColor = cropEditing ? NSColor.systemYellow.cgColor : NSColor.white.cgColor
        for (i, h) in handleLayers.enumerated() {
            h.isHidden = !cropEditing
            let p = handlePoint(i, c)
            h.frame = toAppKit(CGRect(x: p.x - 5, y: p.y - 5, width: 10, height: 10))
        }
    }

    private func handlePoint(_ i: Int, _ c: CGRect) -> CGPoint {
        switch i {
        case 0: return CGPoint(x: c.minX, y: c.minY)
        case 1: return CGPoint(x: c.midX, y: c.minY)
        case 2: return CGPoint(x: c.maxX, y: c.minY)
        case 3: return CGPoint(x: c.maxX, y: c.midY)
        case 4: return CGPoint(x: c.maxX, y: c.maxY)
        case 5: return CGPoint(x: c.midX, y: c.maxY)
        case 6: return CGPoint(x: c.minX, y: c.maxY)
        default: return CGPoint(x: c.minX, y: c.midY)
        }
    }

    private func updateCursor() { window?.invalidateCursorRects(for: self) }

    /// Cursor shown where a drag turns the picture: the system's rotate symbol.
    static let turnCursor: NSCursor = {
        let cfg = NSImage.SymbolConfiguration(pointSize: 15, weight: .semibold)
        guard let sym = NSImage(systemSymbolName: "arrow.triangle.2.circlepath", accessibilityDescription: "Turn")?.withSymbolConfiguration(cfg) else { return .crosshair }
        let size = NSSize(width: 22, height: 22)
        let img = NSImage(size: size, flipped: false) { r in
            NSColor.black.withAlphaComponent(0.55).setFill()
            NSBezierPath(ovalIn: r).fill()
            let tinted = sym.copy() as! NSImage
            tinted.isTemplate = true
            let ir = NSRect(x: (r.width - sym.size.width) / 2, y: (r.height - sym.size.height) / 2, width: sym.size.width, height: sym.size.height)
            tinted.draw(in: ir)
            NSColor.white.set()
            ir.fill(using: .sourceAtop)
            return true
        }
        return NSCursor(image: img, hotSpot: NSPoint(x: 11, y: 11))
    }()

    override func resetCursorRects() {
        let editing = cropEditing || dustEditing || gradientEditing || correctionEditing
        addCursorRect(bounds, cursor: editing && !spaceHeld ? .crosshair : .openHand)
        // The band around the crop: turning (a rotate cursor). Four strips, corners included.
        if cropEditing, !spaceHeld, let d = cropDraft {
            let c = viewRect(d.cgRect), z = Self.turnZone
            let o = c.insetBy(dx: -z, dy: -z)
            for r in [CGRect(x: o.minX, y: o.minY, width: o.width, height: c.minY - o.minY),
                      CGRect(x: o.minX, y: c.maxY, width: o.width, height: o.maxY - c.maxY),
                      CGRect(x: o.minX, y: c.minY, width: c.minX - o.minX, height: c.height),
                      CGRect(x: c.maxX, y: c.minY, width: o.maxX - c.maxX, height: c.height)] {
                let v = r.intersection(bounds)
                if !v.isNull, v.width > 0, v.height > 0 { addCursorRect(toAppKit(v), cursor: Self.turnCursor) }
            }
        }
    }

    // MARK: Gradient editor

    /// Top-left view point → AppKit (bottom-left) point for layer paths.
    private func ak(_ p: CGPoint) -> CGPoint { CGPoint(x: p.x, y: bounds.height - p.y) }

    /// Handle positions of a guide in view coordinates (2 for a line, 3 for an ellipse, none for the polynomial).
    private func gradientHandlePoints(_ g: GradientGesture) -> [CGPoint] {
        switch g.shape {
        case .line: return [viewPoint(g.center), viewPoint(g.end)]
        case .ellipse:
            let c = g.center
            return [viewPoint(c),
                    viewPoint(CGPoint(x: c.x + g.a * cos(g.angle), y: c.y + g.a * sin(g.angle))),
                    viewPoint(CGPoint(x: c.x - g.b * sin(g.angle), y: c.y + g.b * cos(g.angle)))]
        case .polynomial, .samples: return []
        }
    }

    /// Path of a guide: the line with a small arrow head at its end, or the ellipse with its axes.
    private func gradientPath(_ g: GradientGesture, axes: Bool) -> CGPath {
        let path = CGMutablePath()
        switch g.shape {
        case .line:
            let p0 = ak(viewPoint(g.center)), p1 = ak(viewPoint(g.end))
            path.move(to: p0); path.addLine(to: p1)
            let ang = atan2(p1.y - p0.y, p1.x - p0.x)
            for d in [CGFloat(2.6), CGFloat(-2.6)] {
                path.move(to: p1); path.addLine(to: CGPoint(x: p1.x - 10 * cos(ang + d), y: p1.y - 10 * sin(ang + d)))
            }
        case .ellipse:
            let c = ak(viewPoint(g.center))
            // The view's y points down, AppKit's up: the rotation flips sign.
            var t = CGAffineTransform(translationX: c.x, y: c.y).rotated(by: -g.angle)
            let ra = g.a * zoom, rb = g.b * zoom
            path.addEllipse(in: CGRect(x: -ra, y: -rb, width: 2 * ra, height: 2 * rb), transform: t)
            if axes {
                path.move(to: CGPoint(x: -ra, y: 0), transform: t); path.addLine(to: CGPoint(x: ra, y: 0), transform: t)
                path.move(to: CGPoint(x: 0, y: -rb), transform: t); path.addLine(to: CGPoint(x: 0, y: rb), transform: t)
            }
            t = .identity
        case .polynomial, .samples: break
        }
        return path
    }

    private func regionRect(_ r: GradientRegion) -> CGRect {
        let c = viewPoint(r.center)
        return toAppKit(CGRect(x: c.x - r.a * zoom, y: c.y - r.b * zoom, width: 2 * r.a * zoom, height: 2 * r.b * zoom))
    }

    /// Handles of the selected region: on its rim to the right (a) and below (b) the centre.
    private func regionHandlePoints(_ r: GradientRegion) -> [CGPoint] {
        [viewPoint(CGPoint(x: r.x + r.a, y: r.y)), viewPoint(CGPoint(x: r.x, y: r.y + r.b))]
    }

    private func sampleRect(_ s: BackgroundSample) -> CGRect {
        let c = viewPoint(CGPoint(x: s.x, y: s.y)), r = max(gradientSampleRadius * zoom, 3)
        return toAppKit(CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r))
    }

    /// The sample under a view point.
    private func gradientSample(at p: CGPoint) -> BackgroundSample? {
        let r = max(gradientSampleRadius * zoom, 6)
        return gradientSamples.filter { let c = viewPoint(CGPoint(x: $0.x, y: $0.y)); return hypot(p.x - c.x, p.y - c.y) <= r }
            .min { let a = viewPoint(CGPoint(x: $0.x, y: $0.y)), b = viewPoint(CGPoint(x: $1.x, y: $1.y)); return hypot(p.x - a.x, p.y - a.y) < hypot(p.x - b.x, p.y - b.y) }
    }

    private func updateGradientOverlay() {
        let all = [gradientLayer, gradientRefinedLayer, regionLayer, regionSelectedLayer, sampleLayer, sampleRejectedLayer, sampleSelectedLayer] + gradientHandles + regionHandles
        guard gradientEditing, imageSize != .zero else { all.forEach { $0.isHidden = true }; return }
        // Background samples: kept (green), rejected by the fit (red, dashed), selected (yellow).
        let kept = CGMutablePath(), rejected = CGMutablePath(), chosen = CGMutablePath()
        for s in gradientSamples {
            let rect = sampleRect(s)
            if s.id == selectedGradientSample { chosen.addEllipse(in: rect) }
            else if gradientSampleResults[s.id]?.isRejected == true { rejected.addEllipse(in: rect) }
            else { kept.addEllipse(in: rect) }
        }
        sampleLayer.isHidden = false; sampleLayer.path = kept
        sampleRejectedLayer.isHidden = false; sampleRejectedLayer.path = rejected
        sampleSelectedLayer.isHidden = false; sampleSelectedLayer.path = chosen
        if let g = gradientGesture, g.shape != .polynomial {
            gradientLayer.isHidden = false
            gradientLayer.path = gradientPath(g, axes: true)
            let pts = gradientHandlePoints(g)
            for (i, h) in gradientHandles.enumerated() {
                h.isHidden = i >= pts.count
                if i < pts.count { h.frame = toAppKit(CGRect(x: pts[i].x - 5, y: pts[i].y - 5, width: 10, height: 10)) }
            }
            if let r = gradientRefined, r != g, r.shape == g.shape {
                gradientRefinedLayer.isHidden = false
                gradientRefinedLayer.path = gradientPath(r, axes: false)
            } else { gradientRefinedLayer.isHidden = true }
        } else {
            gradientLayer.isHidden = true; gradientRefinedLayer.isHidden = true
            gradientHandles.forEach { $0.isHidden = true }
        }
        let others = CGMutablePath(), selected = CGMutablePath()
        var handles: [CGPoint] = []
        for r in gradientRegions {
            if r.id == selectedGradientRegion { selected.addEllipse(in: regionRect(r)); handles = regionHandlePoints(r) }
            else { others.addEllipse(in: regionRect(r)) }
        }
        regionLayer.isHidden = false; regionLayer.path = others
        regionSelectedLayer.isHidden = false; regionSelectedLayer.path = selected
        for (i, h) in regionHandles.enumerated() {
            h.isHidden = i >= handles.count
            if i < handles.count { h.frame = toAppKit(CGRect(x: handles[i].x - 5, y: handles[i].y - 5, width: 10, height: 10)) }
        }
    }

    /// The region under a view point (the smallest when several overlap).
    private func gradientRegion(at p: CGPoint) -> GradientRegion? {
        let ip = imagePoint(p)
        return gradientRegions.filter { $0.contains(x: ip.x, y: ip.y) }.min { $0.a * $0.b < $1.a * $1.b }
    }

    /// Distance from a view point to the drawn line segment.
    private func distanceToGradientLine(_ p: CGPoint, _ g: GradientGesture) -> CGFloat {
        let a = viewPoint(g.center), b = viewPoint(g.end)
        let dx = b.x - a.x, dy = b.y - a.y
        let l2 = max(dx * dx + dy * dy, 1e-6)
        let t = min(max(((p.x - a.x) * dx + (p.y - a.y) * dy) / l2, 0), 1)
        return hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy))
    }

    private func gradientMouseDown(_ p: CGPoint) {
        if let g = gradientGesture, g.shape != .polynomial {
            for (i, h) in gradientHandlePoints(g).enumerated() where hypot(p.x - h.x, p.y - h.y) <= 8 { drag = .gradientHandle(i); return }
        }
        if let id = selectedGradientRegion, let r = gradientRegions.first(where: { $0.id == id }) {
            for (i, h) in regionHandlePoints(r).enumerated() where hypot(p.x - h.x, p.y - h.y) <= 8 { drag = .regionResize(id, i); return }
        }
        if !gradientProtectMode, let s = gradientSample(at: p) {
            if s.id != selectedGradientSample { onGradientSampleSelect?(s.id) }
            drag = .sampleMove(s.id, s, imagePoint(p))
            return
        }
        if let r = gradientRegion(at: p) {
            if r.id != selectedGradientRegion { onGradientRegionSelect?(r.id) }
            drag = .regionMove(r.id, r, imagePoint(p))
            return
        }
        if let g = gradientGesture, g.shape == .line, distanceToGradientLine(p, g) <= 6 { drag = .gradientMove(g, imagePoint(p)); return }
        if !gradientProtectMode, gradientShape != .polynomial, gradientShape != .samples, gradientGesture == nil || gradientGesture?.shape == .polynomial {
            drag = .gradientCreate(imagePoint(p)); return
        }
        drag = .gradientPan(last: p, start: p, moved: false)
    }

    private func gradientMouseDragged(_ p: CGPoint, _ ip: CGPoint) {
        switch drag {
        case .gradientPan(let last, let start, let moved):
            origin.x += p.x - last.x; origin.y += p.y - last.y
            drag = .gradientPan(last: p, start: start, moved: moved || hypot(p.x - start.x, p.y - start.y) > 3)
            clampOrigin()
            needsLayout = true
        case .gradientCreate(let start):
            guard hypot(ip.x - start.x, ip.y - start.y) >= 2 else { return }
            let g: GradientGesture
            if gradientShape == .line { g = .line(from: start, to: ip) }
            else { g = .ellipse(center: start, a: max(abs(ip.x - start.x), 1), b: max(abs(ip.y - start.y), 1)) }
            gradientGesture = g
            onGradientGestureChange?(g)
        case .gradientHandle(let i):
            guard var g = gradientGesture else { return }
            switch (g.shape, i) {
            case (.line, 0): g.x = ip.x; g.y = ip.y
            case (.line, _): g.x2 = ip.x; g.y2 = ip.y
            case (.ellipse, 0): g.x = ip.x; g.y = ip.y
            case (.ellipse, 1):
                g.a = max(hypot(ip.x - g.x, ip.y - g.y), 1); g.angle = atan2(ip.y - g.y, ip.x - g.x)
            case (.ellipse, _):
                // Projection of the pointer on the b-axis direction.
                let bx = -sin(g.angle), by = cos(g.angle)
                g.b = max(abs((ip.x - g.x) * bx + (ip.y - g.y) * by), 1)
            default: break
            }
            gradientGesture = g
            onGradientGestureChange?(g)
        case .gradientMove(let start, let anchor):
            var g = start
            let dx = ip.x - anchor.x, dy = ip.y - anchor.y
            g.x += dx; g.y += dy; g.x2 += dx; g.y2 += dy
            gradientGesture = g
            onGradientGestureChange?(g)
        case .sampleMove(let id, let start, let anchor):
            guard let i = gradientSamples.firstIndex(where: { $0.id == id }) else { return }
            var samples = gradientSamples
            samples[i].x = min(max(start.x + ip.x - anchor.x, 0), imageSize.width)
            samples[i].y = min(max(start.y + ip.y - anchor.y, 0), imageSize.height)
            gradientSamples = samples
            onGradientSamplesChange?(samples)
        case .regionMove(let id, let start, let anchor):
            guard let i = gradientRegions.firstIndex(where: { $0.id == id }) else { return }
            var regions = gradientRegions
            regions[i].x = min(max(start.x + ip.x - anchor.x, 0), imageSize.width)
            regions[i].y = min(max(start.y + ip.y - anchor.y, 0), imageSize.height)
            gradientRegions = regions
            onGradientRegionsChange?(regions)
        case .regionResize(let id, let axis):
            guard let i = gradientRegions.firstIndex(where: { $0.id == id }) else { return }
            var regions = gradientRegions
            if axis == 0 { regions[i].a = max(3, abs(ip.x - regions[i].x)) } else { regions[i].b = max(3, abs(ip.y - regions[i].y)) }
            gradientRegions = regions
            onGradientRegionsChange?(regions)
        default: break
        }
    }

    // MARK: Dust spots

    /// Handle used to resize the selected spot: on its rim, to the right of the centre.
    private func dustHandlePoint(_ s: DustSpot) -> CGPoint { viewPoint(CGPoint(x: s.x + s.radius, y: s.y)) }

    private func updateDustOverlay() {
        guard dustEditing, imageSize != .zero else { dustLayer.isHidden = true; dustBrightLayer.isHidden = true; dustSelectedLayer.isHidden = true; dustHandle.isHidden = true; return }
        let others = CGMutablePath(), brightOthers = CGMutablePath(), selected = CGMutablePath()
        var selectedBright = false
        var handle: CGPoint? = nil
        for s in dustSpots {
            let c = viewPoint(s.center), r = s.radius * zoom
            let rect = toAppKit(CGRect(x: c.x - r, y: c.y - r, width: 2 * r, height: 2 * r))
            if s.id == selectedDustSpot {
                selected.addEllipse(in: rect)
                // Small cross at the centre.
                let p = toAppKit(CGRect(x: c.x - 4, y: c.y - 4, width: 8, height: 8))
                selected.move(to: CGPoint(x: p.minX, y: p.midY)); selected.addLine(to: CGPoint(x: p.maxX, y: p.midY))
                selected.move(to: CGPoint(x: p.midX, y: p.minY)); selected.addLine(to: CGPoint(x: p.midX, y: p.maxY))
                handle = dustHandlePoint(s)
                selectedBright = s.polarity == .bright
            } else if s.polarity == .bright {
                brightOthers.addEllipse(in: rect)
            } else {
                others.addEllipse(in: rect)
            }
        }
        dustLayer.isHidden = false; dustLayer.path = others
        dustBrightLayer.isHidden = false; dustBrightLayer.path = brightOthers
        // The selected spot stays yellow; dotted when it is a bright one.
        dustSelectedLayer.lineDashPattern = selectedBright ? [2, 3] : nil
        dustSelectedLayer.isHidden = false; dustSelectedLayer.path = selected
        if let h = handle { dustHandle.isHidden = false; dustHandle.frame = toAppKit(CGRect(x: h.x - 5, y: h.y - 5, width: 10, height: 10)) }
        else { dustHandle.isHidden = true }
    }

    /// The spot under a view point (the smallest one when several overlap).
    private func dustSpot(at p: CGPoint) -> DustSpot? {
        let ip = imagePoint(p)
        return dustSpots.filter { hypot(ip.x - $0.x, ip.y - $0.y) <= $0.radius }.min { $0.radius < $1.radius }
    }

    private func dustMouseDown(_ p: CGPoint) {
        if let id = selectedDustSpot, let s = dustSpots.first(where: { $0.id == id }) {
            let h = dustHandlePoint(s)
            if hypot(p.x - h.x, p.y - h.y) <= 8 { drag = .dustResize(id); return }
        }
        if let s = dustSpot(at: p) {
            if s.id != selectedDustSpot { onDustSelect?(s.id) }
            drag = .dustMove(s.id, s, imagePoint(p))
            return
        }
        drag = .dustPan(last: p, start: p, moved: false)
    }

    private func dustMouseDragged(_ p: CGPoint, _ ip: CGPoint) {
        switch drag {
        case .dustPan(let last, let start, let moved):
            origin.x += p.x - last.x; origin.y += p.y - last.y
            drag = .dustPan(last: p, start: start, moved: moved || hypot(p.x - start.x, p.y - start.y) > 3)
            clampOrigin()
            needsLayout = true
        case .dustMove(let id, let start, let anchor):
            guard let i = dustSpots.firstIndex(where: { $0.id == id }) else { return }
            var spots = dustSpots
            spots[i].x = min(max(start.x + ip.x - anchor.x, 0), imageSize.width)
            spots[i].y = min(max(start.y + ip.y - anchor.y, 0), imageSize.height)
            dustSpots = spots
            onDustSpotsChange?(spots)
        case .dustResize(let id):
            guard let i = dustSpots.firstIndex(where: { $0.id == id }) else { return }
            var spots = dustSpots
            spots[i].radius = max(3, hypot(ip.x - spots[i].x, ip.y - spots[i].y))
            dustSpots = spots
            onDustSpotsChange?(spots)
        default: break
        }
    }

    // MARK: Events

    override func scrollWheel(with event: NSEvent) {
        guard imageSize != .zero else { return }
        // Scrolling zooms around the pointer; hold ⌘ or ⌥ (or drag) to pan.
        if event.modifierFlags.contains(.command) || event.modifierFlags.contains(.option) {
            let dx = event.scrollingDeltaX, dy = event.scrollingDeltaY
            origin.x += event.hasPreciseScrollingDeltas ? dx : dx * 10
            origin.y += event.hasPreciseScrollingDeltas ? dy : dy * 10
            clampOrigin()
            needsLayout = true
        } else {
            let dy = event.scrollingDeltaY
            let factor = event.hasPreciseScrollingDeltas ? pow(1.004, dy) : pow(1.15, dy)
            setZoom(zoom * factor, anchor: topLeftPoint(event))
        }
    }

    override func magnify(with event: NSEvent) {
        setZoom(zoom * (1 + event.magnification), anchor: topLeftPoint(event))
    }

    override func mouseDown(with event: NSEvent) {
        window?.makeFirstResponder(self)
        let p = topLeftPoint(event)
        if spaceHeld { drag = .pan(p); return }
        if correctionEditing {
            let ip = imagePoint(p)
            brushPoint = p
            drag = .paint(ip)
            onStrokeBegin?()
            onPaint?(ip, ip, event.modifierFlags.contains(.option))
            updateBrushCursor()
            return
        }
        if event.clickCount == 2 { onDoubleClick?(); return }
        if dustEditing { dustMouseDown(p); return }
        if gradientEditing { gradientMouseDown(p); return }
        if cropEditing {
            let ip = imagePoint(p)
            if let d = cropDraft {
                let c = viewRect(d.cgRect)
                for i in 0..<8 where hypot(p.x - handlePoint(i, c).x, p.y - handlePoint(i, c).y) <= 8 { drag = .resize(i, d); return }
                if c.contains(p) { drag = .move(d, ip); return }
                // In the band around the crop: turn the picture (farther out, a drag draws a new crop).
                if c.insetBy(dx: -Self.turnZone, dy: -Self.turnZone).contains(p) {
                    drag = .turn(cropAngle, atan2(p.y - c.midY, p.x - c.midX) * 180 / .pi)
                    updateOverlay()
                    return
                }
            }
            drag = .create(ip)
        } else {
            drag = .pan(p)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        let p = topLeftPoint(event)
        let ip = imagePoint(p)
        let lim = cropLimits
        func clampPoint(_ q: CGPoint) -> CGPoint { CGPoint(x: min(max(q.x, lim.minX), lim.maxX), y: min(max(q.y, lim.minY), lim.maxY)) }
        switch drag {
        case .none: break
        case .paint(let last):
            brushPoint = p
            onPaint?(last, ip, event.modifierFlags.contains(.option))
            drag = .paint(ip)
            updateBrushCursor()
        case .brushResize, .maxRadiusResize: break
        case .dustPan, .dustMove, .dustResize: dustMouseDragged(p, ip)
        case .gradientPan, .gradientCreate, .gradientHandle, .gradientMove, .regionMove, .regionResize, .sampleMove: gradientMouseDragged(p, ip)
        case .pan(let last):
            origin.x += p.x - last.x; origin.y += p.y - last.y
            drag = .pan(p)
            clampOrigin()
            needsLayout = true
        case .turn(let start, let pointer0):
            guard let d = cropDraft else { break }
            let c = viewRect(d.cgRect)
            var delta = atan2(p.y - c.midY, p.x - c.midX) * 180 / .pi - pointer0
            if delta > 180 { delta -= 360 } else if delta < -180 { delta += 360 }
            // A tenth of a degree; with ⇧ whole degrees. Quarter turns (and 0) catch the pointer within 1.5°.
            var a = start + delta
            a = event.modifierFlags.contains(.shift) ? a.rounded() : (a * 10).rounded() / 10
            let quarter = (a / 90).rounded() * 90
            if abs(a - quarter) < 1.5 { a = quarter }
            onCropAngleChange?(a)
        case .create(let start):
            let a = clampPoint(start), b = clampPoint(ip)
            let r = CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
            if r.width >= 1 && r.height >= 1 { cropDraft = PixelRect(r); onCropDraftChange?(cropDraft) }
        case .move(let start, let anchor):
            var r = start
            r.x = Int((CGFloat(start.x) + ip.x - anchor.x).rounded()); r.y = Int((CGFloat(start.y) + ip.y - anchor.y).rounded())
            r.x = min(max(Int(lim.minX.rounded()), r.x), Int(lim.maxX.rounded()) - r.width); r.y = min(max(Int(lim.minY.rounded()), r.y), Int(lim.maxY.rounded()) - r.height)
            cropDraft = r; onCropDraftChange?(r)
        case .resize(let i, let start):
            let q = clampPoint(ip)
            var x0 = CGFloat(start.x), y0 = CGFloat(start.y), x1 = CGFloat(start.maxX), y1 = CGFloat(start.maxY)
            if [0, 6, 7].contains(i) { x0 = min(q.x, x1 - 1) }
            if [2, 3, 4].contains(i) { x1 = max(q.x, x0 + 1) }
            if [0, 1, 2].contains(i) { y0 = min(q.y, y1 - 1) }
            if [4, 5, 6].contains(i) { y1 = max(q.y, y0 + 1) }
            cropDraft = PixelRect(CGRect(x: x0, y: y0, width: x1 - x0, height: y1 - y0)); onCropDraftChange?(cropDraft)
        }
    }

    override func mouseUp(with event: NSEvent) {
        if case .dustPan(_, let start, let moved) = drag, !moved {
            let ip = imagePoint(start)
            if ip.x >= 0, ip.y >= 0, ip.x < imageSize.width, ip.y < imageSize.height { onDustAdd?(ip) } else { onDustSelect?(nil) }
        }
        if case .gradientPan(_, let start, let moved) = drag, !moved {
            let ip = imagePoint(start)
            let inside = ip.x >= 0 && ip.y >= 0 && ip.x < imageSize.width && ip.y < imageSize.height
            if gradientProtectMode, inside { onGradientRegionAdd?(ip) }
            else if gradientShape == .samples, inside { onGradientSampleAdd?(ip) }
            else { onGradientRegionSelect?(nil); onGradientSampleSelect?(nil) }
        }
        let wasTurning = isTurning
        drag = .none
        if wasTurning { updateOverlay() }
    }

    /// Width (view points) of the band around the crop where a drag turns the picture.
    static let turnZone: CGFloat = 160
    private var isTurning: Bool { if case .turn = drag { return true }; return false }

    override func mouseMoved(with event: NSEvent) {
        let p = topLeftPoint(event)
        let ip = imagePoint(p)
        onHover?(ip)
        if correctionEditing || dustBrightCursor { brushPoint = p; updateBrushCursor() }
    }

    override func mouseExited(with event: NSEvent) { onHover?(nil); brushPoint = nil; brushLayer.isHidden = true }

    override func keyDown(with event: NSEvent) {
        // ⌘/⌃/⌥ combinations are menu shortcuts (⌘1 / ⌘2 switch tabs), never the plain keys below.
        if !event.modifierFlags.intersection([.command, .control, .option]).isEmpty { super.keyDown(with: event); return }
        switch event.charactersIgnoringModifiers {
        case "+", "=": perform(.zoomIn)
        case "-": perform(.zoomOut)
        case "0": perform(.fit)
        case "1": perform(.actualSize)
        case "\u{7F}", "\u{08}":
            if dustEditing { onDustDelete?() } else if gradientEditing { onGradientDelete?() } else { super.keyDown(with: event) }
        default: super.keyDown(with: event)
        }
    }
}

struct PreviewView: NSViewRepresentable {
    @Bindable var model: ProjectModel
    @Binding var zoomRequest: (ZoomAction, Int)?

    func makeNSView(context: Context) -> ZoomPanNSView {
        let v = ZoomPanNSView(frame: .zero)
        v.onViewportChange = { rect, scale in
            Task { @MainActor in model.viewport = Viewport(visibleRect: rect, scale: scale) }
        }
        v.onHover = { p in Task { @MainActor in model.updateHover(imagePoint: p) } }
        v.onCropDraftChange = { r in Task { @MainActor in model.cropDraft = r } }
        v.onCropAngleChange = { a in Task { @MainActor in model.setCropAngle(a) } }
        v.onDustSpotsChange = { spots in Task { @MainActor in model.updateDustSpots(spots) } }
        v.onDustAdd = { p in Task { @MainActor in model.addDustSpot(at: p) } }
        v.onDustSelect = { id in Task { @MainActor in model.selectedDustSpot = id } }
        v.onDustDelete = { Task { @MainActor in model.removeSelectedDustSpot() } }
        v.onGradientGestureChange = { g in Task { @MainActor in model.setGradientGesture(g) } }
        v.onGradientRegionsChange = { r in Task { @MainActor in model.updateGradientRegions(r) } }
        v.onGradientRegionAdd = { p in Task { @MainActor in model.addGradientRegion(at: p) } }
        v.onGradientRegionSelect = { id in Task { @MainActor in model.selectedGradientRegion = id } }
        v.onGradientDelete = { Task { @MainActor in model.deleteGradientSelection() } }
        v.onGradientSampleAdd = { p in Task { @MainActor in model.addGradientSample(at: p) } }
        v.onGradientSamplesChange = { s in Task { @MainActor in model.updateGradientSamples(s) } }
        v.onGradientSampleSelect = { id in Task { @MainActor in model.selectedGradientSample = id } }
        v.onStrokeBegin = { Task { @MainActor in model.beginCorrectionStroke() } }
        v.onPaint = { a, b, inv in Task { @MainActor in model.paintCorrection(from: a, to: b, invert: inv) } }
        v.onBrushRadiusChange = { r in Task { @MainActor in model.correctionBrushRadius = Double(r) } }
        v.onDustMaxRadiusChange = { r in Task { @MainActor in model.dustBrightMaxRadius = Double(r) } }
        v.onDoubleClick = { [weak v] in
            guard let v else { return }
            // Fit first; from the fitted view, 1:1.
            if v.isFitted { v.perform(.actualSize) } else { v.perform(.fit) }
        }
        return v
    }

    func updateNSView(_ v: ZoomPanNSView, context: Context) {
        v.imageSize = model.imageSize ?? .zero
        v.previewFrame = model.frame
        if v.crop != model.crop { v.crop = model.crop }
        if v.cropEditing != model.cropEditing { v.cropEditing = model.cropEditing }
        if v.cropDraft != model.cropDraft { v.cropDraft = model.cropDraft }
        if v.cropOverlay != model.cropOverlay { v.cropOverlay = model.cropOverlay }
        if v.fineAngle != model.orientation.angle { v.fineAngle = model.orientation.angle }
        let editAngle = model.cropEditing ? model.cropAngleDraft : model.orientation.angle
        if v.cropAngle != editAngle { v.cropAngle = editAngle }
        let bounds = model.cropEditing ? model.cropEditBounds : .zero
        if v.cropBounds != bounds { v.cropBounds = bounds }
        let dustEditing = model.dustEditing != nil
        if v.dustEditing != dustEditing { v.dustEditing = dustEditing }
        let spots = model.dustEditing.map { model.dustSpots(for: $0) } ?? []
        if v.dustSpots != spots { v.dustSpots = spots }
        if v.selectedDustSpot != model.selectedDustSpot { v.selectedDustSpot = model.selectedDustSpot }
        let brightMode = model.dustEditing != nil && model.dustDraft?.polarity == .bright
        if v.dustBrightMode != brightMode { v.dustBrightMode = brightMode }
        let maxR = CGFloat(model.dustBrightMaxRadius)
        if abs(v.dustMaxRadius - maxR) > 0.01 { v.dustMaxRadius = maxR }
        let gradientEditing = model.gradientEditing != nil
        if v.gradientEditing != gradientEditing { v.gradientEditing = gradientEditing }
        let gesture = model.editedGradientSpec?.gesture
        if v.gradientGesture != gesture { v.gradientGesture = gesture }
        let refined = model.gradientFit.flatMap { $0.kind == model.gradientEditing ? $0.gesture : nil }
        if v.gradientRefined != refined { v.gradientRefined = refined }
        if v.gradientShape != model.gradientShape { v.gradientShape = model.gradientShape }
        let regions = gradientEditing ? model.gradientRegions : []
        if v.gradientRegions != regions { v.gradientRegions = regions }
        if v.selectedGradientRegion != model.selectedGradientRegion { v.selectedGradientRegion = model.selectedGradientRegion }
        if v.gradientProtectMode != model.gradientProtectMode { v.gradientProtectMode = model.gradientProtectMode }
        let samples = gradientEditing ? model.gradientSamples : []
        if v.gradientSamples != samples { v.gradientSamples = samples }
        let sampleRadius = CGFloat(model.gradientSampleRadius)
        if v.gradientSampleRadius != sampleRadius { v.gradientSampleRadius = sampleRadius }
        let results = Dictionary((model.gradientFit.flatMap { $0.kind == model.gradientEditing ? $0.sampleResults : nil } ?? []).map { ($0.id, $0) }, uniquingKeysWith: { a, _ in a })
        if v.gradientSampleResults != results { v.gradientSampleResults = results }
        if v.selectedGradientSample != model.selectedGradientSample { v.selectedGradientSample = model.selectedGradientSample }
        let correcting = model.correctionEditing != nil
        if v.correctionEditing != correcting { v.correctionEditing = correcting }
        if v.correctionMap !== model.correctionDraft { v.correctionMap = model.correctionDraft }
        if v.correctionVersion != model.correctionVersion { v.correctionVersion = model.correctionVersion }
        if v.correctionShowMap != model.correctionShowMap { v.correctionShowMap = model.correctionShowMap }
        let brush = CGFloat(model.correctionBrushRadius)
        if abs(v.brushRadius - brush) > 0.01 { v.brushRadius = brush }
        if v.brushDarken != model.correctionDarken { v.brushDarken = model.correctionDarken }
        if context.coordinator.fitRequest != model.fitRequest {
            context.coordinator.fitRequest = model.fitRequest
            DispatchQueue.main.async { v.perform(.fit) }
        }
        if let (action, id) = zoomRequest, id != context.coordinator.zoomID {
            context.coordinator.zoomID = id
            DispatchQueue.main.async { v.perform(action) }
        }
    }

    func makeCoordinator() -> Coordinator { Coordinator() }

    final class Coordinator {
        var fitRequest = 0
        var zoomID = 0
    }
}
