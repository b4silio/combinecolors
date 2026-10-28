import SwiftUI
import AppKit
import AstroCore

enum ZoomAction { case fit, actualSize, zoomIn, zoomOut }

/// AppKit view that displays the rendered preview with smooth zoom/pan (CALayer composited),
/// a crop overlay and interactive crop editing.
final class ZoomPanNSView: NSView {
    var imageSize: CGSize = .zero { didSet { if imageSize != oldValue { needsFit = true; needsLayout = true } } }
    var zoom: CGFloat = 1
    var origin: CGPoint = .zero          // image (0,0) in top-left view coordinates
    private var needsFit = true
    private(set) var didFit = false

    var previewFrame: PreviewFrame? { didSet { updateImageLayer() } }
    var crop: PixelRect? { didSet { updateOverlay() } }
    var cropEditing = false { didSet { updateOverlay(); updateCursor() } }
    var cropDraft: PixelRect? { didSet { updateOverlay() } }
    var cropOverlay: CropOverlayMode = .frame { didSet { updateOverlay() } }
    private var lastBounds: CGRect = .zero

    var onViewportChange: ((CGRect, Double) -> Void)?
    var onHover: ((CGPoint?) -> Void)?
    var onCropDraftChange: ((PixelRect?) -> Void)?
    var onDoubleClick: (() -> Void)?

    private let imageLayer = CALayer()
    private let dimLayers = (0..<4).map { _ in CALayer() }
    private let cropBorder = CAShapeLayer()
    private let handleLayers = (0..<8).map { _ in CALayer() }
    private var trackingArea: NSTrackingArea?

    private enum Drag { case none, pan(CGPoint), move(PixelRect, CGPoint), resize(Int, PixelRect), create(CGPoint) }
    private var drag: Drag = .none

    override init(frame: NSRect) {
        super.init(frame: frame)
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
        for h in handleLayers {
            h.backgroundColor = NSColor.white.cgColor
            h.borderColor = NSColor.black.cgColor
            h.borderWidth = 1
            h.cornerRadius = 2
            layer?.addSublayer(h)
        }
        layer?.actions = ["sublayers": NSNull()]
        imageLayer.actions = ["contents": NSNull(), "bounds": NSNull(), "position": NSNull(), "frame": NSNull()]
        for l in dimLayers + handleLayers + [cropBorder] { l.actions = ["bounds": NSNull(), "position": NSNull(), "frame": NSNull(), "path": NSNull(), "hidden": NSNull()] }
    }

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
        notifyViewport()
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
        imageLayer.frame = toAppKit(viewRect(f.rect))
        let devicePerImage = zoom * backing
        imageLayer.magnificationFilter = devicePerImage >= 2 ? .nearest : .linear
    }

    private func updateOverlay() {
        let rect: PixelRect? = cropEditing ? cropDraft : (cropOverlay == .hidden ? nil : crop)
        let cropOnly = !cropEditing && cropOverlay == .cropOnly
        guard let r = rect, imageSize != .zero else {
            dimLayers.forEach { $0.isHidden = true }; cropBorder.isHidden = true; handleLayers.forEach { $0.isHidden = true }
            return
        }
        let full = viewRect(CGRect(origin: .zero, size: imageSize)).intersection(bounds.insetBy(dx: -2, dy: -2))
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

    override func resetCursorRects() {
        addCursorRect(bounds, cursor: cropEditing ? .crosshair : .openHand)
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
        if event.clickCount == 2 { onDoubleClick?(); return }
        if cropEditing {
            let ip = imagePoint(p)
            if let d = cropDraft {
                let c = viewRect(d.cgRect)
                for i in 0..<8 where hypot(p.x - handlePoint(i, c).x, p.y - handlePoint(i, c).y) <= 8 { drag = .resize(i, d); return }
                if c.contains(p) { drag = .move(d, ip); return }
            }
            drag = .create(ip)
        } else {
            drag = .pan(p)
        }
    }

    override func mouseDragged(with event: NSEvent) {
        let p = topLeftPoint(event)
        let ip = imagePoint(p)
        func clampPoint(_ q: CGPoint) -> CGPoint { CGPoint(x: min(max(q.x, 0), imageSize.width), y: min(max(q.y, 0), imageSize.height)) }
        switch drag {
        case .none: break
        case .pan(let last):
            origin.x += p.x - last.x; origin.y += p.y - last.y
            drag = .pan(p)
            clampOrigin()
            needsLayout = true
        case .create(let start):
            let a = clampPoint(start), b = clampPoint(ip)
            let r = CGRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
            if r.width >= 1 && r.height >= 1 { cropDraft = PixelRect(r); onCropDraftChange?(cropDraft) }
        case .move(let start, let anchor):
            var r = start
            r.x = Int((CGFloat(start.x) + ip.x - anchor.x).rounded()); r.y = Int((CGFloat(start.y) + ip.y - anchor.y).rounded())
            r.x = min(max(0, r.x), Int(imageSize.width) - r.width); r.y = min(max(0, r.y), Int(imageSize.height) - r.height)
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

    override func mouseUp(with event: NSEvent) { drag = .none }

    override func mouseMoved(with event: NSEvent) {
        let ip = imagePoint(topLeftPoint(event))
        onHover?(ip)
    }

    override func mouseExited(with event: NSEvent) { onHover?(nil) }

    override func keyDown(with event: NSEvent) {
        switch event.charactersIgnoringModifiers {
        case "+", "=": perform(.zoomIn)
        case "-": perform(.zoomOut)
        case "0": perform(.fit)
        case "1": perform(.actualSize)
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
        v.onDoubleClick = { [weak v] in
            guard let v else { return }
            if abs(v.zoom * v.backing - 1) < 0.01 { v.perform(.fit) } else { v.perform(.actualSize) }
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
