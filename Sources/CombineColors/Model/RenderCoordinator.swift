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
    /// The first pipeline stage that ran (earlier ones came from the render cache); nil when uncached.
    let ranFrom: RenderCache.Stage?
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
    /// RGB stars layer blended into the result (nil: the R, G, B channels serve as stars).
    var stars: StarsLayer? = nil
    /// Live hand-painted correction of one channel (the corrections editor).
    var correction: (map: CorrectionMap, strength: Float, kind: ChannelKind)? = nil
}

/// Serial, latest-wins background renderer.
final class RenderCoordinator {
    private let queue = DispatchQueue(label: "combinecolors.render", qos: .userInteractive)
    private let lock = NSLock()
    private var pending: PreviewRequest?
    private var busy = false
    /// Intermediates of the last render, reused when only later stages change (owned by the render queue).
    private let cache = RenderCache()
    var onFrame: ((PreviewFrame, Int) -> Void)?
    var onError: ((Error, Int) -> Void)?

    /// Drops the cached intermediates (e.g. when a project closes); the next render starts from scratch.
    func clearCache() { queue.async { self.cache.clear() } }

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
                let frame = try RenderCoordinator.render(req, cache: cache)
                DispatchQueue.main.async { self.onFrame?(frame, req.generation) }
            } catch {
                DispatchQueue.main.async { self.onError?(error, req.generation) }
            }
        }
    }

    /// Chooses the pyramid level and crop for a viewport, renders it and converts it for display.
    /// With a `cache`, stages whose inputs did not change since the last render are reused.
    static func render(_ req: PreviewRequest, cache: RenderCache? = nil) throws -> PreviewFrame {
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
        let inv = 1 / s
        let rect = CGRect(x: Double(x0) * inv, y: Double(y0) * inv, width: Double(cw) * inv, height: Double(ch) * inv)
        let overview = wholeLevel ? nil : RenderCoordinator.overview(sources: req.sources, level: level, rect: rect)
        var request = RenderRequest(planes: planes, analysisPlanes: req.analysisSamples, settings: req.settings, context: req.context, fixedPostStretch: req.fixedPostStretch, scale: Float(s),
                                    overview: overview?.planes, overviewRect: overview?.rect)
        if req.settings.stars.enabled, let st = req.stars, st.width == fullW, st.height == fullH { request.stars = st.input(level: level, x: x0, y: y0, width: cw, height: ch) }
        if let c = req.correction { request.correction = CorrectionInput(map: c.map, strength: c.strength, kind: c.kind, rect: rect) }
        let result: RenderResult
        if let cache {
            let key = RenderInputKey(sources: req.sources.mapValues(\.id), stars: request.stars == nil ? nil : req.stars?.id, context: req.context.id,
                                     level: level, x: x0, y: y0, width: cw, height: ch)
            result = try cache.render(request, inputs: key)
        } else {
            result = try Renderer.render(request)
        }
        guard let cg = RasterWriter.makeDisplayImage(rgb: result.image) else { throw RenderError.noOutput }
        return PreviewFrame(cgImage: cg, rgb: result.image, level: level, rect: rect, millis: result.millis, ranFrom: result.ranFrom, postStretch: result.postStretch, channelStretch: result.channelStretch, settings: req.settings)
    }
}

extension RenderCoordinator {
    /// The whole picture at a coarse pyramid level (no finer than `level`) for the detail stage of a
    /// cropped render covering `rect` (full-resolution pixels), with the crop's region in overview pixels.
    static func overview(sources: [ChannelKind: ChannelSource], level: Int, rect: CGRect, maxPixels: Int = 300_000) -> (planes: [ChannelKind: PlanarImage], rect: CGRect)? {
        guard let any = sources.values.first else { return nil }
        let ol = max(level, any.pyramid.levelIndex(maxPixels: maxPixels))
        let os = pow(2.0, Double(-ol))
        return (sources.mapValues { $0.pyramid.level(ol) }, CGRect(x: rect.minX * os, y: rect.minY * os, width: rect.width * os, height: rect.height * os))
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
        var stars: StarsLayer? = nil
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
            var wholeLevel = true
            for (k, src) in job.sources {
                let lvl = src.pyramid.level(level)
                let whole = x0 == 0 && y0 == 0 && cw == lvl.width && ch == lvl.height
                wholeLevel = wholeLevel && whole
                planes[k] = whole ? lvl : lvl.cropped(x: x0, y: y0, width: cw, height: ch)
            }
            let inv = 1 / s
            let overview = wholeLevel ? nil : RenderCoordinator.overview(sources: job.sources, level: level, rect: CGRect(x: Double(x0) * inv, y: Double(y0) * inv, width: Double(cw) * inv, height: Double(ch) * inv))
            for palette in job.palettes {
                self.lock.lock(); let stale = gen != self.generation; self.lock.unlock()
                if stale { return }
                var settings = job.settings
                settings.select(palette: palette)
                var request = RenderRequest(planes: planes, analysisPlanes: job.analysisSamples, settings: settings, context: job.context, scale: Float(s),
                                            overview: overview?.planes, overviewRect: overview?.rect)
                if settings.stars.enabled, let st = job.stars, st.width == any.width, st.height == any.height { request.stars = st.input(level: level, x: x0, y: y0, width: cw, height: ch) }
                guard let res = try? Renderer.render(request),
                      let cg = RasterWriter.makeDisplayImage(rgb: res.image) else { continue }
                DispatchQueue.main.async { self.onThumbnail?(palette.id, cg, gen) }
            }
        }
        return gen
    }
}
