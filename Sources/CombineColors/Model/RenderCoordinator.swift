import Foundation
import CoreGraphics
import AstroCore

/// A rendered preview frame: a region of a pyramid level, ready to display.
struct PreviewFrame {
    let cgImage: CGImage
    let rgb: RGBImage
    /// Pyramid level used.
    let level: Int
    /// Region covered by the frame, in full-resolution image pixels.
    let rect: CGRect
    let millis: Double
    let postStretch: [ResolvedStretch]?
    let channelStretch: [ChannelKind: ResolvedStretch]
    let settings: CombineSettings
}

struct PreviewRequest {
    var sources: [ChannelKind: ChannelSource]
    var settings: CombineSettings
    var context: RenderContext
    var analysisSamples: [ChannelKind: PlanarImage]
    var fixedPostStretch: [ResolvedStretch]?
    /// Visible region in full-resolution pixels (nil = whole image).
    var visibleRect: CGRect?
    /// Device pixels per full-resolution image pixel.
    var scale: Double
    var pixelBudget: Int
    var generation: Int
}

/// Serial, latest-wins background renderer.
final class RenderCoordinator {
    private let queue = DispatchQueue(label: "combinecolors.render", qos: .userInteractive)
    private let lock = NSLock()
    private var pending: PreviewRequest?
    private var busy = false
    var onFrame: ((PreviewFrame, Int) -> Void)?
    var onError: ((Error, Int) -> Void)?

    func submit(_ req: PreviewRequest) {
        lock.lock()
        pending = req
        let shouldStart = !busy
        if shouldStart { busy = true }
        lock.unlock()
        if shouldStart { queue.async { self.drain() } }
    }

    private func drain() {
        while true {
            lock.lock()
            guard let req = pending else { busy = false; lock.unlock(); return }
            pending = nil
            lock.unlock()
            do {
                let frame = try RenderCoordinator.render(req)
                DispatchQueue.main.async { self.onFrame?(frame, req.generation) }
            } catch {
                DispatchQueue.main.async { self.onError?(error, req.generation) }
            }
        }
    }

    /// Chooses the pyramid level and crop for a viewport, renders it and converts it for display.
    static func render(_ req: PreviewRequest) throws -> PreviewFrame {
        guard let any = req.sources.values.first else { throw RenderError.noOutput }
        let pyramid = any.pyramid
        let fullW = any.width, fullH = any.height
        let full = CGRect(x: 0, y: 0, width: fullW, height: fullH)
        var visible = (req.visibleRect ?? full).intersection(full)
        if visible.isNull || visible.isEmpty { visible = full }
        // Expand by a margin so small pans do not trigger new renders.
        let margin = max(visible.width, visible.height) * 0.35
        var region = visible.insetBy(dx: -margin, dy: -margin).intersection(full)
        if region.isNull || region.isEmpty { region = full }
        var level = pyramid.levelIndex(forScale: req.scale)
        func pixels(at l: Int) -> Int {
            let s = pow(2.0, Double(-l))
            return Int(region.width * s) * Int(region.height * s)
        }
        while level + 1 < pyramid.count && pixels(at: level) > req.pixelBudget { level += 1 }
        let s = pow(2.0, Double(-level))
        let lw = pyramid.level(level).width, lh = pyramid.level(level).height
        let x0 = max(0, Int((region.minX * s).rounded(.down))), y0 = max(0, Int((region.minY * s).rounded(.down)))
        let x1 = min(lw, Int((region.maxX * s).rounded(.up))), y1 = min(lh, Int((region.maxY * s).rounded(.up)))
        let cw = max(1, x1 - x0), ch = max(1, y1 - y0)
        let wholeLevel = x0 == 0 && y0 == 0 && cw == lw && ch == lh
        var planes: [ChannelKind: PlanarImage] = [:]
        for (k, src) in req.sources {
            let lvl = src.pyramid.level(level)
            planes[k] = wholeLevel ? lvl : lvl.cropped(x: x0, y: y0, width: cw, height: ch)
        }
        let result = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: req.analysisSamples, settings: req.settings, context: req.context, fixedPostStretch: req.fixedPostStretch))
        guard let cg = RasterWriter.makeDisplayImage(rgb: result.image) else { throw RenderError.noOutput }
        let inv = 1 / s
        let rect = CGRect(x: Double(x0) * inv, y: Double(y0) * inv, width: Double(cw) * inv, height: Double(ch) * inv)
        return PreviewFrame(cgImage: cg, rgb: result.image, level: level, rect: rect, millis: result.millis, postStretch: result.postStretch, channelStretch: result.channelStretch, settings: req.settings)
    }
}

/// Renders palette thumbnails in the background at low priority, one batch at a time.
final class ThumbnailCoordinator {
    private let queue = DispatchQueue(label: "combinecolors.thumbnails", qos: .utility)
    private var generation = 0
    private let lock = NSLock()
    var onThumbnail: ((String, CGImage, Int) -> Void)?

    struct Job {
        var palettes: [Palette]
        var sources: [ChannelKind: ChannelSource]
        var settings: CombineSettings
        var context: RenderContext
        var analysisSamples: [ChannelKind: PlanarImage]
        /// Region in full-resolution pixels (nil = whole image).
        var region: CGRect?
        var maxPixels: Int
    }

    func submit(_ job: Job) -> Int {
        lock.lock(); generation += 1; let gen = generation; lock.unlock()
        queue.async {
            guard let any = job.sources.values.first else { return }
            let full = CGRect(x: 0, y: 0, width: any.width, height: any.height)
            var region = (job.region ?? full).intersection(full)
            if region.isNull || region.isEmpty { region = full }
            // Choose the level so that the region fits the budget.
            var level = 0
            while level + 1 < any.pyramid.count {
                let s = pow(2.0, Double(-level))
                if Int(region.width * s) * Int(region.height * s) <= job.maxPixels { break }
                level += 1
            }
            let s = pow(2.0, Double(-level))
            let x0 = Int(region.minX * s), y0 = Int(region.minY * s)
            let cw = max(1, Int(region.width * s)), ch = max(1, Int(region.height * s))
            var planes: [ChannelKind: PlanarImage] = [:]
            for (k, src) in job.sources {
                let lvl = src.pyramid.level(level)
                planes[k] = (x0 == 0 && y0 == 0 && cw == lvl.width && ch == lvl.height) ? lvl : lvl.cropped(x: x0, y: y0, width: cw, height: ch)
            }
            for palette in job.palettes {
                self.lock.lock(); let stale = gen != self.generation; self.lock.unlock()
                if stale { return }
                var settings = job.settings
                settings.select(palette: palette)
                // Keep the user's luminance choice unless the palette suggests one.
                if palette.luminance == nil { settings.luminance.source = job.settings.luminance.source }
                guard let res = try? Renderer.render(RenderRequest(planes: planes, analysisPlanes: job.analysisSamples, settings: settings, context: job.context)),
                      let cg = RasterWriter.makeDisplayImage(rgb: res.image) else { continue }
                DispatchQueue.main.async { self.onThumbnail?(palette.id, cg, gen) }
            }
        }
        return gen
    }
}
