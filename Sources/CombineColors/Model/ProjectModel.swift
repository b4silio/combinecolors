import Foundation
import SwiftUI
import AppKit
import AstroCore

struct HoverInfo: Equatable {
    var x: Int
    var y: Int
    var values: [(ChannelKind, Float)]
    var rgb: (Float, Float, Float)?

    static func == (a: HoverInfo, b: HoverInfo) -> Bool { a.x == b.x && a.y == b.y }
}

struct Viewport: Equatable {
    var visibleRect: CGRect
    var scale: Double   // device pixels per image pixel
}

struct PendingFile: Identifiable {
    let id = UUID()
    let file: LoadedFile
}

enum ThumbnailRegion: String, CaseIterable, Identifiable {
    case wholeImage, currentView, crop
    var id: String { rawValue }
    var displayName: String {
        switch self {
        case .wholeImage: return "Whole image"
        case .currentView: return "Current view"
        case .crop: return "Crop"
        }
    }
}

/// Settings that determine the post-combine stretch (anything but colour post-processing).
struct PostStretchKey: Hashable {
    let normalization: NormalizationSettings
    let channels: [ChannelKind: ChannelAdjust]
    let mode: CombineMode
    let matrix: PaletteMatrix
    let pixelMath: PixelMathProgram
    let stretch: StretchSettings
    let stage: StretchStage
    let crop: PixelRect?
    let kinds: Set<ChannelKind>

    init(_ s: CombineSettings, crop: PixelRect?, kinds: Set<ChannelKind>) {
        normalization = s.normalization; channels = s.channels; mode = s.mode; matrix = s.matrix; pixelMath = s.pixelMath
        stretch = s.stretch; stage = s.stretchStage; self.crop = crop; self.kinds = kinds
    }
}

@MainActor
@Observable
final class ProjectModel {
    // MARK: Data
    private(set) var baseSources: [ChannelKind: ChannelSource] = [:]
    private(set) var sources: [ChannelKind: ChannelSource] = [:]
    var imageSize: CGSize? { sources.values.first.map { CGSize(width: $0.width, height: $0.height) } }
    var availableKinds: Set<ChannelKind> { Set(sources.keys) }
    private(set) var slotThumbnails: [ChannelKind: CGImage] = [:]
    var pendingFiles: [PendingFile] = []
    var pendingColorFile: PendingFile? = nil

    // MARK: Settings
    var settings = CombineSettings() {
        didSet { settingsChanged(from: oldValue) }
    }
    var crop: PixelRect? = nil {
        didSet { if crop != oldValue { cropChanged() } }
    }
    var cropEditing = false
    var cropDraft: PixelRect? = nil
    var showCropOverlay = true

    // MARK: Derived state
    private(set) var context: RenderContext? = nil
    private(set) var analysisSamples: [ChannelKind: PlanarImage] = [:]
    private var postStretchCache: [PostStretchKey: [ResolvedStretch]] = [:]
    private var contextGeneration = 0
    private var stretchHintShown = false
    private var statusHint: String? = nil

    // MARK: Preview
    private(set) var frame: PreviewFrame? = nil
    var histogramStorage: [[Int]] = []
    var viewport = Viewport(visibleRect: .zero, scale: 1) { didSet { if viewport != oldValue { viewportChanged() } } }
    var highQualityPreview = false { didSet { scheduleRender() } }
    private(set) var isRendering = false
    private(set) var renderError: String? = nil
    private(set) var lastRenderMillis: Double = 0
    private(set) var lastRenderLevel: Int = 0
    var hover: HoverInfo? = nil
    var fitRequest = 0   // incremented to ask the preview to fit
    /// A/B comparison: a stored snapshot of the settings and whether it is being shown.
    var snapshotA: CombineSettings? = nil
    var showingSnapshot = false { didSet { if showingSnapshot != oldValue { scheduleRender() } } }
    /// Settings currently displayed (the snapshot while comparing).
    var displayedSettings: CombineSettings { (showingSnapshot ? snapshotA : nil) ?? settings }

    // MARK: Thumbnails
    private(set) var thumbnails: [String: CGImage] = [:]
    var thumbnailRegion: ThumbnailRegion = .wholeImage { didSet { scheduleThumbnails(delay: 0) } }
    private var thumbnailGeneration = 0
    private var thumbnailTimer: Timer?

    // MARK: Status
    var isLoading = false
    var loadingMessage = ""
    var errorMessage: String? = nil
    var exportProgress: Double? = nil
    var projectURL: URL? = nil
    var statusText: String = "Drop FITS or TIFF images to begin."

    // MARK: Infrastructure
    private let renderer = RenderCoordinator()
    private let thumbnailer = ThumbnailCoordinator()
    private var renderGeneration = 0
    weak var undoManager: UndoManager?
    private var undoSnapshot: CombineSettings?
    private var undoTimer: Timer?

    // MARK: Palette visibility
    enum PaletteFilter: String { case favorites, all }
    private let defaults: UserDefaults
    private(set) var hiddenPaletteIDs: Set<String> = []
    var paletteFilter: PaletteFilter = .favorites { didSet { defaults.set(paletteFilter.rawValue, forKey: "paletteFilter") } }

    func isPaletteHidden(_ id: String) -> Bool { hiddenPaletteIDs.contains(id) }

    func setPalette(_ id: String, hidden: Bool) {
        if hidden { hiddenPaletteIDs.insert(id) } else { hiddenPaletteIDs.remove(id) }
        defaults.set(Array(hiddenPaletteIDs).sorted(), forKey: "hiddenPalettes")
    }

    /// Palettes to show for the loaded channels under the current filter.
    var visiblePalettes: [Palette] {
        let avail = Palettes.available(for: availableKinds)
        return paletteFilter == .all ? avail : avail.filter { !hiddenPaletteIDs.contains($0.id) }
    }

    // MARK: Recent sets
    private let recentStore: RecentSetsStore
    private(set) var recentSets: [RecentSet] = []
    /// Suppresses recording while a set/project is being (re)loaded.
    private var suppressRecent = false

    init(defaults: UserDefaults = .standard) {
        self.defaults = defaults
        recentStore = RecentSetsStore(defaults: defaults)
        recentSets = recentStore.load()
        hiddenPaletteIDs = Set(defaults.stringArray(forKey: "hiddenPalettes") ?? [])
        paletteFilter = PaletteFilter(rawValue: defaults.string(forKey: "paletteFilter") ?? "") ?? .favorites
        renderer.onFrame = { [weak self] frame, gen in
            guard let self else { return }
            self.frame = frame
            self.isRendering = gen != self.renderGeneration
            self.renderError = nil
            self.lastRenderMillis = frame.millis
            self.lastRenderLevel = frame.level
            self.histogramStorage = frame.rgb.planes.map { Statistics.histogram(image: $0, bins: 128, maxSamples: 300_000) }
            if let ps = frame.postStretch {
                self.postStretchCache[PostStretchKey(frame.settings, crop: self.crop, kinds: self.availableKinds)] = ps
            }
        }
        renderer.onError = { [weak self] error, gen in
            guard let self else { return }
            self.isRendering = gen != self.renderGeneration
            self.renderError = error.localizedDescription
        }
        thumbnailer.onThumbnail = { [weak self] id, image, gen in
            guard let self, gen == self.thumbnailGeneration else { return }
            self.thumbnails[id] = image
        }
    }

    // MARK: - Loading

    func loadFiles(_ urls: [URL]) {
        let supported = urls.filter { ImageLoader.isSupported($0) }
        guard !supported.isEmpty else { errorMessage = "No supported images (FITS, TIFF, PNG, JPEG)."; return }
        isLoading = true
        loadingMessage = "Loading \(supported.count) file\(supported.count == 1 ? "" : "s")…"
        Task.detached(priority: .userInitiated) {
            var results: [(LoadedFile?, Error?)] = Array(repeating: (nil, nil), count: supported.count)
            let lock = NSLock()
            DispatchQueue.concurrentPerform(iterations: supported.count) { i in
                do { let f = try ImageLoader.load(url: supported[i]); lock.lock(); results[i] = (f, nil); lock.unlock() }
                catch { lock.lock(); results[i] = (nil, error); lock.unlock() }
            }
            let done = results
            await MainActor.run { self.filesLoaded(done) }
        }
    }

    private func filesLoaded(_ results: [(LoadedFile?, Error?)]) {
        var errors: [String] = []
        var toAssign: [(ChannelKind, LoadedFile, Int)] = []
        for (file, error) in results {
            if let e = error { errors.append(e.localizedDescription); continue }
            guard let f = file else { continue }
            if let size = imageSize ?? toAssign.first.map({ CGSize(width: $0.1.width, height: $0.1.height) }),
               Int(size.width) != f.width || Int(size.height) != f.height {
                errors.append("\(f.fileName): \(f.width)×\(f.height) does not match the loaded channels (\(Int(size.width))×\(Int(size.height))).")
                continue
            }
            if f.isColor {
                pendingColorFile = PendingFile(file: f)
            } else if let k = f.detectedKind {
                toAssign.append((k, f, 0))
            } else {
                pendingFiles.append(PendingFile(file: f))
            }
        }
        if !errors.isEmpty { errorMessage = errors.joined(separator: "\n") }
        if toAssign.isEmpty { isLoading = false } else {
            addChannels(toAssign.map { ($0.0, $0.1.planes[0], $0.1.fileName, $0.1.url, $0.1.formatDescription) })
        }
    }

    /// Assigns a pending (unrecognised) file to a slot.
    func assignPending(_ pending: PendingFile, to kind: ChannelKind) {
        pendingFiles.removeAll { $0.id == pending.id }
        let f = pending.file
        addChannels([(kind, f.planes[0], f.fileName, f.url, f.formatDescription)])
    }

    /// Assigns the planes of a colour file to R, G, B.
    func assignColorFileAsRGB(_ pending: PendingFile) {
        pendingColorFile = nil
        let f = pending.file
        addChannels(zip([ChannelKind.red, .green, .blue], f.planes).map { ($0, $1, f.fileName, f.url, f.formatDescription) })
    }

    func addChannels(_ items: [(ChannelKind, PlanarImage, String, URL?, String)]) {
        guard !items.isEmpty else { return }
        isLoading = true
        loadingMessage = "Analysing \(items.map { $0.0.shortName }.joined(separator: ", "))…"
        let crop = self.crop
        Task.detached(priority: .userInitiated) {
            var built: [ChannelSource?] = Array(repeating: nil, count: items.count)
            let lock = NSLock()
            DispatchQueue.concurrentPerform(iterations: items.count) { i in
                let (kind, plane, name, url, fmt) = items[i]
                let src = ChannelSource(kind: kind, image: plane, fileName: name, url: url, formatDescription: fmt)
                lock.lock(); built[i] = src; lock.unlock()
            }
            let thumbs: [(ChannelKind, CGImage?)] = built.compactMap { $0 }.map { ($0.kind, RasterWriter.makeDisplayImage(gray: $0.displayPlane(maxPixels: 80_000))) }
            let cropped: [ChannelSource] = built.compactMap { $0 }.map { crop == nil ? $0 : ChannelSource($0, region: crop) }
            let base = built.compactMap { $0 }
            await MainActor.run {
                for (b, c) in zip(base, cropped) { self.baseSources[b.kind] = b; self.sources[c.kind] = c }
                for (k, t) in thumbs { if let t { self.slotThumbnails[k] = t } }
                self.isLoading = false
                self.sourcesChanged()
            }
        }
    }

    func removeChannel(_ kind: ChannelKind) {
        baseSources.removeValue(forKey: kind)
        sources.removeValue(forKey: kind)
        slotThumbnails.removeValue(forKey: kind)
        if sources.isEmpty { frame = nil; crop = nil; cropDraft = nil; cropEditing = false }
        sourcesChanged()
    }

    func removeAll() {
        baseSources = [:]; sources = [:]; slotThumbnails = [:]; frame = nil; thumbnails = [:]
        crop = nil; cropDraft = nil; cropEditing = false
        sourcesChanged()
    }

    /// Moves a channel to another slot (swapping if occupied).
    func reassign(_ kind: ChannelKind, to newKind: ChannelKind) {
        guard kind != newKind, let src = baseSources[kind], let cur = sources[kind] else { return }
        let otherBase = baseSources[newKind], otherCur = sources[newKind], otherThumb = slotThumbnails[newKind]
        baseSources[newKind] = ChannelSource(src, kind: newKind)
        sources[newKind] = ChannelSource(cur, kind: newKind)
        slotThumbnails[newKind] = slotThumbnails[kind]
        if let ob = otherBase, let oc = otherCur {
            baseSources[kind] = ChannelSource(ob, kind: kind); sources[kind] = ChannelSource(oc, kind: kind); slotThumbnails[kind] = otherThumb
        } else {
            baseSources.removeValue(forKey: kind); sources.removeValue(forKey: kind); slotThumbnails.removeValue(forKey: kind)
        }
        sourcesChanged()
    }

    private func sourcesChanged() {
        postStretchCache = [:]
        recordRecentSet()
        if sources.isEmpty {
            context = nil; analysisSamples = [:]; thumbnails = [:]
            statusText = "Drop FITS or TIFF images to begin."
            return
        }
        // Pick a sensible palette if the current one cannot be rendered.
        if !Renderer.missingChannels(settings: settings, available: availableKinds).isEmpty {
            if let p = Palettes.available(for: availableKinds).first { settings.select(palette: p) }
        }
        // Already-stretched data (medians far above a linear sky level): default to no stretch.
        if !stretchHintShown, sources.values.allSatisfy({ $0.stats.median > 0.08 }), settings.stretch.method == .autoSTF, settings == CombineSettings.defaultWith(palette: settings.paletteID) {
            settings.stretch.method = .none
            stretchHintShown = true
            statusHint = "stretch set to none because the data look already stretched"
            rebuildContext()
            return
        }
        rebuildContext()
    }

    // MARK: - Recent sets

    private func recordRecentSet() {
        guard !suppressRecent, !isLoading, !sources.isEmpty else { return }
        let entries = sources.values.compactMap { s in s.url.map { RecentSet.Entry(kind: s.kind, path: $0.path) } }
        // Only complete sets (every channel backed by a file) are remembered.
        guard entries.count == sources.count else { return }
        recentSets = recentStore.record(entries: entries)
    }

    func openRecentSet(_ set: RecentSet) {
        let missing = set.entries.filter { !FileManager.default.fileExists(atPath: $0.path) }
        guard missing.isEmpty else {
            errorMessage = "Files no longer exist:\n" + missing.map { ($0.path as NSString).lastPathComponent }.joined(separator: "\n")
            recentSets = recentStore.remove(id: set.id)
            return
        }
        removeAll()
        isLoading = true
        loadingMessage = "Loading \(set.name)…"
        let entries = set.entries
        Task.detached(priority: .userInitiated) {
            var items: [(ChannelKind, PlanarImage, String, URL?, String)] = []
            var errors: [String] = []
            let lock = NSLock()
            DispatchQueue.concurrentPerform(iterations: entries.count) { i in
                let e = entries[i]
                let u = URL(fileURLWithPath: e.path)
                do {
                    let f = try ImageLoader.load(url: u)
                    lock.lock(); items.append((e.kind, f.planes[0], f.fileName, u, f.formatDescription)); lock.unlock()
                } catch { lock.lock(); errors.append("\(u.lastPathComponent): \(error.localizedDescription)"); lock.unlock() }
            }
            let its = items, errs = errors
            await MainActor.run {
                if !errs.isEmpty { self.errorMessage = errs.joined(separator: "\n") }
                self.addChannels(its)
            }
        }
    }

    func removeRecentSet(_ set: RecentSet) { recentSets = recentStore.remove(id: set.id) }
    func clearRecentSets() { recentStore.clear(); recentSets = [] }

    // MARK: - Crop

    private func cropChanged() {
        guard !baseSources.isEmpty else { return }
        let crop = self.crop
        let base = baseSources
        isLoading = true
        loadingMessage = crop == nil ? "Removing crop…" : "Applying crop…"
        Task.detached(priority: .userInitiated) {
            let kinds = Array(base.keys)
            var out: [ChannelKind: ChannelSource] = [:]
            let lock = NSLock()
            DispatchQueue.concurrentPerform(iterations: kinds.count) { i in
                let k = kinds[i]
                let s = crop == nil ? base[k]! : ChannelSource(base[k]!, region: crop)
                lock.lock(); out[k] = s; lock.unlock()
            }
            let result = out
            await MainActor.run {
                self.sources = result
                self.isLoading = false
                self.postStretchCache = [:]
                self.rebuildContext()
                self.fitRequest += 1
            }
        }
    }

    func autoCrop() {
        guard !sources.isEmpty else { return }
        isLoading = true; loadingMessage = "Detecting empty borders…"
        let images = sources.values.map(\.image)
        Task.detached(priority: .userInitiated) {
            let r = AutoCrop.detect(images: images)
            await MainActor.run {
                self.isLoading = false
                if let r, r != PixelRect.full(width: images[0].width, height: images[0].height) {
                    self.cropDraft = r
                    if !self.cropEditing { self.crop = r }
                } else {
                    self.statusText = "No empty borders found."
                }
            }
        }
    }

    func beginCropEditing() {
        cropDraft = crop ?? imageSize.map { PixelRect(x: Int($0.width * 0.1), y: Int($0.height * 0.1), width: Int($0.width * 0.8), height: Int($0.height * 0.8)) }
        cropEditing = true
    }

    func commitCropEditing() {
        cropEditing = false
        if let d = cropDraft, let size = imageSize, let c = d.clamped(width: Int(size.width), height: Int(size.height)) {
            crop = c == PixelRect.full(width: Int(size.width), height: Int(size.height)) ? nil : c
        }
    }

    func cancelCropEditing() { cropEditing = false; cropDraft = crop }

    func clearCrop() { crop = nil; cropDraft = nil; cropEditing = false }

    // MARK: - Context / rendering

    private func rebuildContext() {
        guard !sources.isEmpty else { return }
        contextGeneration += 1
        let gen = contextGeneration
        let srcs = sources
        let norm = settings.normalization
        Task.detached(priority: .userInitiated) {
            let ctx = RenderContext.make(settings: norm, sources: srcs)
            let samples = Renderer.analysisSamples(sources: srcs)
            await MainActor.run {
                guard gen == self.contextGeneration else { return }
                self.context = ctx
                self.analysisSamples = samples
                self.postStretchCache = [:]
                self.scheduleRender()
                self.scheduleThumbnails(delay: 0.05)
                self.statusText = "\(srcs.count) channel\(srcs.count == 1 ? "" : "s") loaded · \(String(srcs.values.first!.width)) × \(String(srcs.values.first!.height))" + (self.statusHint.map { " · " + $0 } ?? "")
            }
        }
    }

    private func settingsChanged(from old: CombineSettings) {
        guard settings != old else { return }
        registerUndo(old)
        if settings.normalization != old.normalization {
            rebuildContext()
            return
        }
        scheduleRender()
        let paletteOnly = CombineSettings.differOnlyInPalette(settings, old)
        if !paletteOnly { scheduleThumbnails(delay: 0.6) }
    }

    private func viewportChanged() {
        guard let f = frame else { scheduleRender(); return }
        // Re-render when the visible area leaves the rendered region or the level should change.
        let visible = viewport.visibleRect
        let needsLevel = sources.values.first.map { $0.pyramid.levelIndex(forScale: viewport.scale) } ?? f.level
        if !f.rect.contains(visible.intersection(fullRect)) || needsLevel < f.level || (needsLevel > f.level + 1) {
            scheduleRender()
        } else if thumbnailRegion == .currentView {
            scheduleThumbnails(delay: 0.8)
        }
    }

    var fullRect: CGRect { imageSize.map { CGRect(origin: .zero, size: $0) } ?? .zero }

    func scheduleRender() {
        guard let ctx = context, !sources.isEmpty else { return }
        renderGeneration += 1
        isRendering = true
        let shown = displayedSettings
        if shown.normalization != settings.normalization {
            // The snapshot uses a different normalisation: render it with a context of its own.
            let snapshotCtx = RenderContext.make(settings: shown.normalization, sources: sources)
            let req = PreviewRequest(sources: sources, settings: shown, context: snapshotCtx, analysisSamples: analysisSamples, fixedPostStretch: nil,
                                     visibleRect: viewport.visibleRect.isEmpty ? nil : viewport.visibleRect,
                                     scale: viewport.scale, pixelBudget: highQualityPreview ? 12_000_000 : 3_000_000, generation: renderGeneration)
            renderer.submit(req)
            return
        }
        let key = PostStretchKey(shown, crop: crop, kinds: availableKinds)
        let req = PreviewRequest(sources: sources, settings: shown, context: ctx, analysisSamples: analysisSamples,
                                 fixedPostStretch: shown.stretchStage == .postCombine ? postStretchCache[key] : nil,
                                 visibleRect: viewport.visibleRect.isEmpty ? nil : viewport.visibleRect,
                                 scale: viewport.scale, pixelBudget: highQualityPreview ? 12_000_000 : 3_000_000, generation: renderGeneration)
        renderer.submit(req)
    }

    func scheduleThumbnails(delay: TimeInterval) {
        thumbnailTimer?.invalidate()
        thumbnailTimer = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.renderThumbnails() }
        }
    }

    private func renderThumbnails() {
        guard let ctx = context, !sources.isEmpty else { return }
        let region: CGRect?
        switch thumbnailRegion {
        case .wholeImage: region = crop?.cgRect
        case .currentView: region = viewport.visibleRect.isEmpty ? crop?.cgRect : viewport.visibleRect
        case .crop: region = crop?.cgRect
        }
        thumbnailGeneration = thumbnailer.submit(.init(palettes: Palettes.available(for: availableKinds), sources: sources, settings: settings, context: ctx,
                                                       analysisSamples: analysisSamples, region: region, maxPixels: 90_000))
    }

    // MARK: - Hover read-out

    func updateHover(imagePoint: CGPoint?) {
        guard let p = imagePoint, let size = imageSize, p.x >= 0, p.y >= 0, p.x < size.width, p.y < size.height else { hover = nil; return }
        let x = Int(p.x), y = Int(p.y)
        var values: [(ChannelKind, Float)] = []
        for k in ChannelKind.allCases { if let s = sources[k] { values.append((k, s.image[x, y])) } }
        var rgb: (Float, Float, Float)? = nil
        if let f = frame, f.rect.contains(p) {
            let s = pow(2.0, Double(-f.level))
            let fx = min(f.rgb.width - 1, max(0, Int((p.x - f.rect.minX) * s))), fy = min(f.rgb.height - 1, max(0, Int((p.y - f.rect.minY) * s)))
            rgb = (f.rgb.r[fx, fy], f.rgb.g[fx, fy], f.rgb.b[fx, fy])
        }
        hover = HoverInfo(x: x, y: y, values: values, rgb: rgb)
    }

    // MARK: - Palette selection helpers

    func select(palette: Palette) {
        settings.select(palette: palette)
    }

    var currentPalette: Palette? { Palettes.palette(id: settings.paletteID) }

    // MARK: - Dual band

    func addSplitChannels(first: (ChannelKind, PlanarImage), second: (ChannelKind, PlanarImage), fileName: String) {
        addChannels([(first.0, first.1, fileName + " [\(first.0.shortName)]", nil, "split"), (second.0, second.1, fileName + " [\(second.0.shortName)]", nil, "split")])
    }

    // MARK: - Export

    func export(to url: URL, format: RasterWriter.Format, linear: Bool, completion: @escaping (Error?) -> Void) {
        guard let ctx = context, !sources.isEmpty else { completion(RenderError.noOutput); return }
        var s = settings
        if linear {
            s.stretch.method = .none
            s.post = PostSettings()
            s.luminance.enabled = false
            s.stars.enabled = false
        }
        let key = PostStretchKey(s, crop: crop, kinds: availableKinds)
        let fixed = postStretchCache[key]
        let srcs = sources, crop = self.crop
        exportProgress = 0
        Task.detached(priority: .userInitiated) {
            do {
                let img = try Renderer.renderFull(sources: srcs, settings: s, context: ctx, postStretch: fixed, region: crop, progress: { p in
                    Task { @MainActor in self.exportProgress = p * 0.9 }
                })
                guard let img else { await MainActor.run { self.exportProgress = nil }; completion(nil); return }
                try RasterWriter.write(planes: img.planes, to: url, format: format)
                await MainActor.run { self.exportProgress = nil; self.statusText = "Exported \(url.lastPathComponent)" }
                completion(nil)
            } catch {
                await MainActor.run { self.exportProgress = nil }
                completion(error)
            }
        }
    }

    /// Writes single channels (e.g. split dual-band planes) as FITS.
    func exportChannel(_ kind: ChannelKind, to url: URL, format: RasterWriter.Format) throws {
        guard let s = sources[kind] else { return }
        let plane = crop.map { s.image.cropped($0) } ?? s.image
        try RasterWriter.write(planes: [plane], to: url, format: format)
    }

    // MARK: - Project persistence

    func projectDocument() -> ProjectDocument {
        var doc = ProjectDocument()
        doc.settings = settings
        doc.crop = crop
        doc.channels = sources.values.sorted { $0.kind.rawValue < $1.kind.rawValue }.compactMap { s in
            s.url.map { ProjectDocument.ChannelRef(kind: s.kind, path: $0.path) }
        }
        return doc
    }

    func saveProject(to url: URL) throws {
        try projectDocument().data().write(to: url, options: .atomic)
        projectURL = url
        statusText = "Saved \(url.lastPathComponent)"
    }

    func openProject(from url: URL) {
        do {
            let doc = try ProjectDocument.load(from: url)
            projectURL = url
            removeAll()
            let refs = doc.channels
            guard !refs.isEmpty else { settings = doc.settings; crop = doc.crop; return }
            isLoading = true
            loadingMessage = "Loading project…"
            Task.detached(priority: .userInitiated) {
                var items: [(ChannelKind, PlanarImage, String, URL?, String)] = []
                var errors: [String] = []
                let lock = NSLock()
                DispatchQueue.concurrentPerform(iterations: refs.count) { i in
                    let r = refs[i]
                    let u = URL(fileURLWithPath: r.path)
                    do {
                        let f = try ImageLoader.load(url: u)
                        lock.lock(); items.append((r.kind, f.planes[0], f.fileName, u, f.formatDescription)); lock.unlock()
                    } catch { lock.lock(); errors.append("\(u.lastPathComponent): \(error.localizedDescription)"); lock.unlock() }
                }
                let its = items, errs = errors
                await MainActor.run {
                    if !errs.isEmpty { self.errorMessage = errs.joined(separator: "\n") }
                    self.settings = doc.settings
                    self.crop = doc.crop
                    self.addChannels(its)
                }
            }
        } catch {
            errorMessage = "Could not open project: \(error.localizedDescription)"
        }
    }

    // MARK: - Undo

    private func registerUndo(_ old: CombineSettings) {
        guard let um = undoManager else { return }
        if undoSnapshot == nil {
            undoSnapshot = old
            um.registerUndo(withTarget: self) { target in
                Task { @MainActor in target.settings = old }
            }
            um.setActionName("Adjustment")
        }
        undoTimer?.invalidate()
        undoTimer = Timer.scheduledTimer(withTimeInterval: 0.8, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.undoSnapshot = nil }
        }
    }
}

extension CombineSettings {
    /// Default settings with a given palette selected (used to detect untouched settings).
    static func defaultWith(palette id: String) -> CombineSettings {
        var s = CombineSettings()
        if let p = Palettes.palette(id: id) { s.select(palette: p) }
        return s
    }

    /// True when the two settings differ only in the palette definition (thumbnails stay valid).
    static func differOnlyInPalette(_ a: CombineSettings, _ b: CombineSettings) -> Bool {
        var x = a, y = b
        x.mode = .matrix; y.mode = .matrix
        x.paletteID = ""; y.paletteID = ""
        x.matrix = .empty; y.matrix = .empty
        x.pixelMath = PixelMathProgram(red: "", green: "", blue: ""); y.pixelMath = x.pixelMath
        return x == y
    }
}

extension ProjectModel {
    /// Loads one file straight into a given slot, ignoring automatic detection.
    func loadFile(_ url: URL, into kind: ChannelKind) {
        guard ImageLoader.isSupported(url) else { errorMessage = "Unsupported file: \(url.lastPathComponent)"; return }
        isLoading = true
        loadingMessage = "Loading \(url.lastPathComponent)…"
        Task.detached(priority: .userInitiated) {
            do {
                let f = try ImageLoader.load(url: url)
                await MainActor.run {
                    if let size = self.imageSize, Int(size.width) != f.width || Int(size.height) != f.height {
                        self.isLoading = false
                        self.errorMessage = "\(f.fileName): \(f.width)×\(f.height) does not match the loaded channels (\(Int(size.width))×\(Int(size.height)))."
                        return
                    }
                    if f.isColor {
                        self.isLoading = false
                        self.pendingColorFile = PendingFile(file: f)
                    } else {
                        self.addChannels([(kind, f.planes[0], f.fileName, f.url, f.formatDescription)])
                    }
                }
            } catch {
                await MainActor.run { self.isLoading = false; self.errorMessage = error.localizedDescription }
            }
        }
    }
}

extension ProjectModel {
    /// Histogram of the current preview (256 bins per output channel).
    var histogram: [[Int]] { histogramStorage }
}
