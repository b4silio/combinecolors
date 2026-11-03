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

/// How the crop is shown on the preview when not editing it.
enum CropOverlayMode: String, CaseIterable, Identifiable {
    /// Nothing drawn.
    case hidden
    /// Dashed frame with the outside dimmed.
    case frame
    /// Only the cropped part is visible.
    case cropOnly
    var id: String { rawValue }
    var displayName: String {
        switch self { case .hidden: return "Hide Crop Limits"; case .frame: return "Show Crop Limits"; case .cropOnly: return "Show Only the Cropped Part" }
    }
}

@MainActor
@Observable
final class ProjectModel {
    // MARK: Data
    private(set) var baseSources: [ChannelKind: ChannelSource] = [:]
    private(set) var sources: [ChannelKind: ChannelSource] = [:]
    var imageSize: CGSize? { sources.values.first.map { CGSize(width: $0.width, height: $0.height) } }
    /// Size a new file must have (the loaded size before the orientation is applied).
    var requiredFileSize: CGSize? { imageSize.map { orientation.swapsAxes ? CGSize(width: $0.height, height: $0.width) : $0 } }
    var availableKinds: Set<ChannelKind> { Set(sources.keys) }
    private(set) var slotThumbnails: [ChannelKind: CGImage] = [:]
    var pendingFiles: [PendingFile] = []
    var pendingColorFile: PendingFile? = nil

    // MARK: Settings
    var settings = CombineSettings() {
        didSet { settingsChanged(from: oldValue) }
    }
    var crop: PixelRect? = nil {
        didSet { if crop != oldValue { cropChanged(); scheduleRecentStateSave() } }
    }
    var cropEditing = false
    var cropDraft: PixelRect? = nil
    /// How the crop limits are drawn (remembered in the preferences).
    var cropOverlay: CropOverlayMode = .frame { didSet { defaults.set(cropOverlay.rawValue, forKey: "cropOverlay") } }
    /// Rotation / mirroring applied to every channel. Stored in the project; new channels are oriented on load.
    private(set) var orientation = Orientation.identity
    var rotation: Int { orientation.rotation }
    /// Set by the File menu to show the export sheet.
    var exportRequested = false

    // MARK: Manual corrections
    /// Strength per channel and brush defaults (stored in the project).
    private(set) var corrections = CorrectionSettings()
    /// Painted maps applied to the channels (sidecar files next to the images).
    private(set) var correctionMaps: [ChannelKind: CorrectionMap] = [:]
    /// Channel shown in the corrections editor (nil = editor closed).
    private(set) var correctionEditing: ChannelKind? = nil
    /// Working copy while the editor is open, painted on directly.
    private(set) var correctionDraft: CorrectionMap? = nil
    /// Bumped by every stroke so the views refresh (the map is a reference type).
    private(set) var correctionVersion = 0
    private var correctionUndo: [CorrectionMap] = []
    /// Tint overlay of the map on the preview.
    var correctionShowMap = false
    /// Shows the edited channel without its corrections (before / after).
    var correctionBypass = false { didSet { if correctionBypass != oldValue, correctionEditing != nil { scheduleRender() } } }

    // MARK: Stars layer
    /// An RGB image kept whole and blended as stars (oriented like the channels).
    private(set) var starsLayer: StarsLayer? = nil
    /// Whether the RGB stars block has something to blend: a stars layer or R, G and B channels.
    var hasStars: Bool { starsLayer != nil || (sources[.red] != nil && sources[.green] != nil && sources[.blue] != nil) }

    // MARK: Dust spots
    /// Loaded (oriented) planes before dust healing; the same instance as the channel image when a
    /// channel has no spots.
    private(set) var originalImages: [ChannelKind: PlanarImage] = [:]
    /// Spots applied to the channels (stored in the project).
    private(set) var dust = DustSettings()
    /// Working copy while the dust editor is open; changes are previewed on the edited channel.
    var dustDraft: DustSettings? = nil {
        didSet { if dustDraft != oldValue, dustEditing != nil { scheduleDustPreview() } }
    }
    /// Channel shown in the dust editor (nil = editor closed).
    private(set) var dustEditing: ChannelKind? = nil
    var selectedDustSpot: UUID? = nil
    /// Shows the edited channel without any spot (before / after comparison).
    var dustBypass = false { didSet { if dustBypass != oldValue, dustEditing != nil { scheduleDustPreview() } } }
    private var dustPreviewTimer: Timer?
    private var dustPreviewGeneration = 0
    /// Channels whose live source currently shows draft spots (restored on cancel).
    private var dustPreviewed: Set<ChannelKind> = []

    // MARK: Gradient removal
    /// Gradient models applied to the channels (stored in the project).
    private(set) var gradient = GradientSettings()
    /// Working copy while the gradient editor is open; changes are previewed on the edited channel.
    var gradientDraft: GradientSettings? = nil {
        didSet { if gradientDraft != oldValue, gradientEditing != nil, !gradientDraftSilently { scheduleGradientPreview() } }
    }
    /// Channel shown in the gradient editor (nil = editor closed).
    private(set) var gradientEditing: ChannelKind? = nil
    /// What the preview shows while editing: the corrected channel, the untouched one, or the
    /// background model itself (what is taken away).
    var gradientView = GradientView.corrected { didSet { if gradientView != oldValue, gradientEditing != nil { scheduleGradientPreview() } } }
    /// Clicks on the preview add protected regions instead of drawing the guide.
    var gradientProtectMode = false
    var selectedGradientRegion: UUID? = nil
    var selectedGradientSample: UUID? = nil
    /// Set while the model writes a generated grid back into the draft (no preview round trip).
    private var gradientDraftSilently = false
    /// The latest preview fit of the edited channel (refined guide, residual, timing).
    private(set) var gradientFit: GradientFitInfo? = nil
    private var gradientPreviewTimer: Timer?
    private var gradientPreviewGeneration = 0
    /// Channels whose live source currently shows a draft model (restored on cancel).
    private var gradientPreviewed: Set<ChannelKind> = []
    /// Dust-healed plane of the edited channel, kept while the editor is open (the model is re-fitted on it).
    private var gradientHealed: (kind: ChannelKind, image: PlanarImage)? = nil
    private var launchEditorOpened = false

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
    /// Short note on what the last render reused ("from denoise": the stages before denoise came from the cache).
    private(set) var lastRenderNote: String = ""
    private(set) var lastRenderLevel: Int = 0
    var hover: HoverInfo? = nil
    var fitRequest = 0   // incremented to ask the preview to fit
    /// Saved presets (settings + thumbnail), shown in the bar under the preview.
    private(set) var presets: [SettingsPreset] = []
    private var presetThumbnailCache: [UUID: CGImage] = [:]
    /// Channel shown full-size while its slot thumbnail is pressed.
    var peekChannel: ChannelKind? = nil { didSet { if peekChannel != oldValue { scheduleRender() } } }
    /// Stars layer of a project being opened, loaded once the channels are in (size check).
    private var pendingStarsFile: URL? = nil

    /// Settings currently displayed (a single channel while peeking).
    var displayedSettings: CombineSettings {
        if let k = peekChannel ?? dustEditing ?? gradientEditing ?? correctionEditing, sources[k] != nil { return ProjectModel.peekSettings(for: k, base: settings) }
        return settings
    }

    // MARK: - Presets

    /// Stores the current settings with a thumbnail of the current preview.
    @discardableResult
    func savePreset(name: String? = nil) -> SettingsPreset {
        let n = name ?? "Preset \(presets.count + 1)"
        let png = frame.flatMap { try? RasterWriter.pngData(rgb: $0.rgb, maxWidth: 192) }
        let p = SettingsPreset(name: n, settings: settings, thumbnailPNG: png)
        presets.append(p)
        scheduleRecentStateSave()
        return p
    }

    func applyPreset(_ p: SettingsPreset) { settings = p.settings }

    func deletePreset(_ p: SettingsPreset) {
        presets.removeAll { $0.id == p.id }
        presetThumbnailCache.removeValue(forKey: p.id)
        scheduleRecentStateSave()
    }

    func renamePreset(_ p: SettingsPreset, to name: String) {
        guard let i = presets.firstIndex(where: { $0.id == p.id }), !name.isEmpty else { return }
        presets[i].name = name
        scheduleRecentStateSave()
    }

    /// Replaces the preset's settings and thumbnail with the current state.
    func updatePreset(_ p: SettingsPreset) {
        guard let i = presets.firstIndex(where: { $0.id == p.id }) else { return }
        presets[i].settings = settings
        presets[i].thumbnailPNG = frame.flatMap { try? RasterWriter.pngData(rgb: $0.rgb, maxWidth: 192) }
        presets[i].date = Date()
        presetThumbnailCache.removeValue(forKey: p.id)
        scheduleRecentStateSave()
    }

    func presetThumbnail(_ p: SettingsPreset) -> CGImage? {
        if let c = presetThumbnailCache[p.id] { return c }
        guard let d = p.thumbnailPNG, let img = RasterWriter.cgImage(fromPNG: d) else { return nil }
        presetThumbnailCache[p.id] = img
        return img
    }

    /// The preset whose settings equal the current ones (highlighted in the bar).
    var activePresetID: UUID? { presets.first { $0.settings == settings }?.id }

    /// Grayscale view of one channel with the current stretch, no colour processing.
    static func peekSettings(for kind: ChannelKind, base: CombineSettings) -> CombineSettings {
        var s = base
        s.mode = .monochrome
        s.monoChannels = [kind]
        s.paletteID = "monochrome"
        // The editors (dust, gradient) and the peek show the channel's data with the stretch only:
        // no gain / offset, normalisation or finishing stage (clarity, denoise, bands…), which would
        // otherwise hide what is being edited.
        s.setAdjust(kind, ChannelAdjust())
        s.normalization.method = .none
        s.luminance.enabled = false
        s.post = PostSettings()
        s.grading = ColorGradingSettings()
        s.detail = DetailSettings()
        s.denoise = DenoiseSettings()
        s.colorBands = ColorBandsSettings()
        s.stars.enabled = false
        if s.stretch.method == .autoSTF { s.stretch.linked = false }
        return s
    }

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
        // Tests must never write the user's recent sets (their saved presets and dust spots).
        assert(defaults !== UserDefaults.standard || ProcessInfo.processInfo.environment["XCTestConfigurationFilePath"] == nil,
               "use an isolated UserDefaults suite in tests")
        self.defaults = defaults
        recentStore = RecentSetsStore(defaults: defaults)
        recentSets = recentStore.load()
        hiddenPaletteIDs = Set(defaults.stringArray(forKey: "hiddenPalettes") ?? [])
        paletteFilter = PaletteFilter(rawValue: defaults.string(forKey: "paletteFilter") ?? "") ?? .favorites
        cropOverlay = CropOverlayMode(rawValue: defaults.string(forKey: "cropOverlay") ?? "") ?? .frame
        renderer.onFrame = { [weak self] frame, gen in
            guard let self else { return }
            self.frame = frame
            self.isRendering = gen != self.renderGeneration
            self.renderError = nil
            self.lastRenderMillis = frame.millis
            switch frame.ranFrom {
            case nil: self.lastRenderNote = ""
            case .combine?: self.lastRenderNote = frame.reusedChannels > 0 ? "\(frame.reusedChannels) ch reused" : ""
            case .nothing?: self.lastRenderNote = "cached"
            case let st?: self.lastRenderNote = "from " + st.rawValue
            }
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
            if let size = requiredFileSize ?? toAssign.first.map({ CGSize(width: $0.1.width, height: $0.1.height) }),
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

    /// Keeps a colour file whole as the stars layer (blended on top; the channels stay as they are).
    func assignColorFileAsStars(_ pending: PendingFile) {
        pendingColorFile = nil
        setStarsLayer(pending.file)
    }

    /// Sets the stars layer from a colour file (oriented like the channels; the size must match the loaded channels).
    func setStarsLayer(_ f: LoadedFile) {
        guard f.planes.count >= 3 else { errorMessage = "\(f.fileName) is not a colour image."; return }
        if let size = requiredFileSize, Int(size.width) != f.width || Int(size.height) != f.height {
            errorMessage = "\(f.fileName): \(f.width)×\(f.height) does not match the loaded channels (\(Int(size.width))×\(Int(size.height)))."
            return
        }
        let orient = orientation
        isLoading = true
        loadingMessage = "Analysing stars layer…"
        Task.detached(priority: .userInitiated) {
            let layer = StarsLayer(planes: Array(f.planes.prefix(3)).map { orient.apply(to: $0) }, fileName: f.fileName, url: f.url, formatDescription: f.formatDescription)
            await MainActor.run {
                self.starsLayer = layer
                if !self.settings.stars.enabled { self.settings.stars.enabled = true }
                if self.settings.stars.useMask, f.fileName.lowercased().contains("star") { self.settings.stars.useMask = false }
                self.isLoading = false
                self.scheduleRender()
                self.scheduleThumbnails(delay: 0.3)
                self.scheduleRecentStateSave()
            }
        }
    }

    func loadStarsLayer(from url: URL) {
        isLoading = true
        loadingMessage = "Loading \(url.lastPathComponent)…"
        Task.detached(priority: .userInitiated) {
            let result = Result { try ImageLoader.load(url: url) }
            await MainActor.run {
                switch result {
                case .success(let f):
                    self.isLoading = false
                    if f.isColor { self.setStarsLayer(f) } else { self.errorMessage = "\(f.fileName) is not a colour image (a stars layer needs R, G and B)." }
                case .failure(let e): self.isLoading = false; self.errorMessage = e.localizedDescription
                }
            }
        }
    }

    func removeStarsLayer() {
        guard starsLayer != nil else { return }
        starsLayer = nil
        if !hasStars { settings.stars.enabled = false }
        scheduleRender()
        scheduleThumbnails(delay: 0.3)
        scheduleRecentStateSave()
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
        let crop = self.crop, orient = orientation, dust = self.dust, gradient = self.gradient, corr = self.corrections
        Task.detached(priority: .userInitiated) {
            var built: [ChannelSource?] = Array(repeating: nil, count: items.count)
            var originals: [PlanarImage?] = Array(repeating: nil, count: items.count)
            var maps: [ChannelKind: CorrectionMap] = [:]
            let lock = NSLock()
            DispatchQueue.concurrentPerform(iterations: items.count) { i in
                let (kind, plane, name, url, fmt) = items[i]
                let oriented = orient.apply(to: plane)
                // A stored correction map (sidecar) comes in the file's orientation.
                var map: CorrectionMap? = nil
                if let u = url, let stored = CorrectionMap.read(for: u, kind: kind, imageWidth: plane.width, imageHeight: plane.height) {
                    var m = stored
                    if orient.flipped { m = m.applying(.flipHorizontal) }
                    for _ in 0..<orient.rotation { m = m.applying(.rotateClockwise) }
                    map = m
                }
                let healed = ProjectModel.process(oriented, kind: kind, dust: dust, gradient: gradient, correction: map.map { ($0, Float(corr.strength(for: kind))) })
                let src = ChannelSource(kind: kind, image: healed, fileName: name, url: url, formatDescription: fmt)
                lock.lock(); built[i] = src; originals[i] = oriented; if let m = map { maps[kind] = m }; lock.unlock()
            }
            let cropped: [ChannelSource] = built.compactMap { $0 }.map { crop == nil ? $0 : ChannelSource($0, region: crop) }
            let thumbs = cropped.map { ($0.kind, ProjectModel.slotThumbnail($0)) }
            let base = built.compactMap { $0 }
            let origs = originals.compactMap { $0 }
            let loadedMaps = maps
            await MainActor.run {
                for (b, c) in zip(base, cropped) { self.baseSources[b.kind] = b; self.sources[c.kind] = c }
                for (b, o) in zip(base, origs) { self.originalImages[b.kind] = o }
                for (k, m) in loadedMaps { self.correctionMaps[k] = m }
                for b in base where loadedMaps[b.kind] == nil { self.correctionMaps.removeValue(forKey: b.kind) }
                for (k, t) in thumbs { if let t { self.slotThumbnails[k] = t } }
                self.isLoading = false
                self.sourcesChanged()
                self.openEditorFromLaunchArguments()
                if let u = self.pendingStarsFile { self.pendingStarsFile = nil; self.loadStarsLayer(from: u) }
            }
        }
    }

    /// Developer switch for screenshots: `-openEditor gradient:Ha[:background]` (or `dust:Ha`) opens
    /// that editor once the channel is loaded.
    private func openEditorFromLaunchArguments() {
        guard !launchEditorOpened, let v = UserDefaults.standard.string(forKey: "openEditor") else { return }
        let parts = v.split(separator: ":").map(String.init)
        guard parts.count >= 2, let k = ChannelKind(rawValue: parts[1]) ?? ChannelKind.fromPixelMathIdentifier(parts[1]), sources[k] != nil else { return }
        launchEditorOpened = true
        if parts[0] == "gradient" {
            beginGradientEditing(k)
            if parts.count > 2, let view = GradientView(rawValue: parts[2]) { gradientView = view }
            if parts.count > 3, let shape = GradientShape(rawValue: parts[3]) { setGradientShape(shape) }
        } else if parts[0] == "dust" { beginDustEditing(k) }
        else if parts[0] == "corrections" { beginCorrectionEditing(k) }
        // `-editorAction removeModel|clearRegions|adopt|paintTest|paintApply` runs that toolbar action 5 s later (screenshots of a state reached by a click).
        if let action = UserDefaults.standard.string(forKey: "editorAction") {
            DispatchQueue.main.asyncAfter(deadline: .now() + 5) { [weak self] in
                guard let self else { return }
                switch action {
                case "removeModel": self.clearGradient()
                case "clearRegions": self.clearGradientRegions()
                case "adopt": self.adoptRefinedGradient()
                case "paintTest", "paintApply":
                    // Two strokes for screenshots: a darkening one top-left to centre, a brightening one at the right
                    // (paintApply also applies them, for the slot badge).
                    guard let size = self.imageSize else { break }
                    if action == "paintApply" { DispatchQueue.main.asyncAfter(deadline: .now() + 2) { [weak self] in self?.commitCorrectionEditing() } }
                    self.correctionBrushRadius = Double(max(size.width, size.height)) * 0.04
                    self.beginCorrectionStroke()
                    self.paintCorrection(from: CGPoint(x: size.width * 0.15, y: size.height * 0.2), to: CGPoint(x: size.width * 0.45, y: size.height * 0.5), invert: false)
                    self.beginCorrectionStroke()
                    self.paintCorrection(from: CGPoint(x: size.width * 0.7, y: size.height * 0.3), to: CGPoint(x: size.width * 0.8, y: size.height * 0.8), invert: true)
                default: break
                }
            }
        }
    }

    /// A loaded (oriented) channel after dust healing, gradient removal and the painted corrections, in that order.
    nonisolated static func process(_ oriented: PlanarImage, kind: ChannelKind, dust: DustSettings, gradient: GradientSettings,
                                    correction: (map: CorrectionMap, strength: Float)? = nil) -> PlanarImage {
        let healed = DustRemoval.heal(oriented, spots: dust.spots(for: kind))
        let corrected = GradientRemoval.correct(healed, kind: kind, settings: gradient)
        guard let c = correction, !c.map.isEmpty else { return corrected }
        return c.map.applied(to: corrected === oriented ? corrected.copy() : corrected, strength: c.strength)
    }

    /// The committed correction of a channel, for `process`.
    private func correction(for kind: ChannelKind, maps: [ChannelKind: CorrectionMap]? = nil, settings: CorrectionSettings? = nil) -> (map: CorrectionMap, strength: Float)? {
        guard let m = (maps ?? correctionMaps)[kind] else { return nil }
        return (m, Float((settings ?? corrections).strength(for: kind)))
    }

    /// Grayscale strip thumbnail of a source (cropped to its region when a crop is set).
    nonisolated static func slotThumbnail(_ s: ChannelSource) -> CGImage? {
        RasterWriter.makeDisplayImage(gray: s.displayPlane(maxPixels: 80_000))
    }

    func removeChannel(_ kind: ChannelKind) {
        if peekChannel == kind { peekChannel = nil }
        baseSources.removeValue(forKey: kind)
        sources.removeValue(forKey: kind)
        slotThumbnails.removeValue(forKey: kind)
        originalImages.removeValue(forKey: kind)
        dust.spots.removeValue(forKey: kind)
        dustDraft?.spots.removeValue(forKey: kind)
        dustPreviewed.remove(kind)
        if dustEditing == kind { dustEditing = nil; dustDraft = nil }
        gradient.specs.removeValue(forKey: kind)
        gradientDraft?.specs.removeValue(forKey: kind)
        gradientPreviewed.remove(kind)
        if gradientEditing == kind { gradientEditing = nil; gradientDraft = nil; gradientFit = nil; gradientHealed = nil }
        correctionMaps.removeValue(forKey: kind); corrections.strength.removeValue(forKey: kind)
        if correctionEditing == kind { correctionEditing = nil; correctionDraft = nil; correctionUndo = [] }
        if sources.isEmpty { frame = nil; crop = nil; cropDraft = nil; cropEditing = false }
        sourcesChanged()
    }

    func removeAll() {
        baseSources = [:]; sources = [:]; slotThumbnails = [:]; frame = nil; thumbnails = [:]
        crop = nil; cropDraft = nil; cropEditing = false; orientation = .identity
        originalImages = [:]; dust = DustSettings(); dustDraft = nil; dustEditing = nil; dustPreviewed = []
        gradient = GradientSettings(); gradientDraft = nil; gradientEditing = nil; gradientPreviewed = []; gradientFit = nil; gradientHealed = nil
        starsLayer = nil
        corrections = CorrectionSettings(); correctionMaps = [:]; correctionEditing = nil; correctionDraft = nil; correctionUndo = []
        sourcesChanged()
    }

    // MARK: - Rotation / flips

    func rotateClockwise() { transform(.rotateClockwise) }
    func flipHorizontal() { transform(.flipHorizontal) }
    func flipVertical() { transform(.flipVertical) }

    /// Rotates or mirrors every channel (and the crop); statistics and star masks are rebuilt.
    func transform(_ op: Orientation.Op) {
        guard !baseSources.isEmpty, !isLoading else { return }
        let base = baseSources
        let w = base.values.first!.width, h = base.values.first!.height
        orientation = orientation.applying(op)
        let newCrop = crop?.applying(op, imageWidth: w, imageHeight: h)
        cropDraft = cropDraft?.applying(op, imageWidth: w, imageHeight: h)
        func mapSpots(_ d: DustSettings) -> DustSettings { var m = d; m.spots = d.spots.mapValues { $0.map { $0.applying(op, imageWidth: w, imageHeight: h) } }; return m }
        dust = mapSpots(dust)
        if let d = dustDraft { dustDraft = mapSpots(d) }
        gradient = gradient.applying(op, imageWidth: w, imageHeight: h)
        if let g = gradientDraft { gradientDraft = g.applying(op, imageWidth: w, imageHeight: h) }
        gradientHealed = nil
        let dust = self.dust, draft = self.dustDraft, editing = self.dustEditing
        let gradient = self.gradient, gradientDraft = self.gradientDraft, gradientEditing = self.gradientEditing
        let originals = originalImages
        let stars = starsLayer
        // Correction maps (and the draft being painted) turn with the image.
        correctionMaps = correctionMaps.mapValues { $0.applying(op) }
        if let d = correctionDraft { correctionDraft = d.applying(op); correctionUndo = correctionUndo.map { $0.applying(op) }; correctionVersion += 1 }
        let maps = correctionMaps, corr = corrections
        isLoading = true
        loadingMessage = op == .rotateClockwise ? "Rotating…" : "Flipping…"
        frame = nil
        Task.detached(priority: .userInitiated) {
            let newStars = stars?.applying(op)
            let kinds = Array(base.keys)
            var newBase: [ChannelKind: ChannelSource] = [:], newCur: [ChannelKind: ChannelSource] = [:], newOrig: [ChannelKind: PlanarImage] = [:]
            let lock = NSLock()
            DispatchQueue.concurrentPerform(iterations: kinds.count) { i in
                let k = kinds[i], old = base[k]!
                let orig = (originals[k] ?? old.image).applying(op)
                // The edited channel keeps showing its draft spots / model.
                let d = (k == editing ? draft : nil) ?? dust
                let g = (k == gradientEditing ? gradientDraft : nil) ?? gradient
                let healed = ProjectModel.process(orig, kind: k, dust: d, gradient: g, correction: maps[k].map { ($0, Float(corr.strength(for: k))) })
                let b = ChannelSource(kind: k, image: healed, fileName: old.fileName, url: old.url, formatDescription: old.formatDescription)
                let c = newCrop == nil ? b : ChannelSource(b, region: newCrop)
                lock.lock(); newBase[k] = b; newCur[k] = c; newOrig[k] = orig; lock.unlock()
            }
            let nb = newBase, nc = newCur, no = newOrig
            let thumbs = nc.values.map { ($0.kind, ProjectModel.slotThumbnail($0)) }
            await MainActor.run {
                self.baseSources = nb
                self.sources = nc
                self.originalImages = no
                self.starsLayer = newStars
                for (k, t) in thumbs { if let t { self.slotThumbnails[k] = t } }
                self.isLoading = false
                self.postStretchCache = [:]
                self.suppressCropRebuild = true
                self.crop = newCrop
                self.suppressCropRebuild = false
                self.rebuildContext()
                self.fitRequest += 1
                self.scheduleRecentStateSave()
            }
        }
    }
    private var suppressCropRebuild = false

    /// Moves a channel to another slot (swapping if occupied).
    func reassign(_ kind: ChannelKind, to newKind: ChannelKind) {
        guard kind != newKind, let src = baseSources[kind], let cur = sources[kind] else { return }
        peekChannel = nil
        let otherBase = baseSources[newKind], otherCur = sources[newKind], otherThumb = slotThumbnails[newKind]
        let otherOrig = originalImages[newKind], otherSpots = dust.spots[newKind], otherDraft = dustDraft?.spots[newKind]
        baseSources[newKind] = ChannelSource(src, kind: newKind)
        sources[newKind] = ChannelSource(cur, kind: newKind)
        slotThumbnails[newKind] = slotThumbnails[kind]
        originalImages[newKind] = originalImages[kind]
        dust.spots[newKind] = dust.spots[kind]
        dustDraft?.spots[newKind] = dustDraft?.spots[kind]
        let otherGradient = gradient.specs[newKind], otherGradientDraft = gradientDraft?.specs[newKind]
        gradient.specs[newKind] = gradient.specs[kind]
        gradientDraft?.specs[newKind] = gradientDraft?.specs[kind]
        let otherMap = correctionMaps[newKind], otherStrength = corrections.strength[newKind]
        correctionMaps[newKind] = correctionMaps[kind]; corrections.strength[newKind] = corrections.strength[kind]
        if let ob = otherBase, let oc = otherCur {
            baseSources[kind] = ChannelSource(ob, kind: kind); sources[kind] = ChannelSource(oc, kind: kind); slotThumbnails[kind] = otherThumb
            originalImages[kind] = otherOrig; dust.spots[kind] = otherSpots; dustDraft?.spots[kind] = otherDraft
            gradient.specs[kind] = otherGradient; gradientDraft?.specs[kind] = otherGradientDraft
            correctionMaps[kind] = otherMap; corrections.strength[kind] = otherStrength
        } else {
            baseSources.removeValue(forKey: kind); sources.removeValue(forKey: kind); slotThumbnails.removeValue(forKey: kind)
            originalImages.removeValue(forKey: kind); dust.spots.removeValue(forKey: kind); dustDraft?.spots.removeValue(forKey: kind)
            gradient.specs.removeValue(forKey: kind); gradientDraft?.specs.removeValue(forKey: kind)
            correctionMaps.removeValue(forKey: kind); corrections.strength.removeValue(forKey: kind)
        }
        // The maps on disk are named after the channel: move them with the slots (old names first,
        // the two channels may share one file).
        let movedMap = correctionMaps[newKind], swappedMap = otherBase != nil ? correctionMaps[kind] : nil
        if let u = src.url { CorrectionMap.remove(for: u, kind: kind) }
        if let u = otherBase?.url { CorrectionMap.remove(for: u, kind: newKind) }
        if let u = src.url, let m = movedMap { try? m.unoriented(orientation).write(for: u, kind: newKind) }
        if let u = otherBase?.url, let m = swappedMap { try? m.unoriented(orientation).write(for: u, kind: kind) }
        if correctionEditing == kind { correctionEditing = newKind }
        if dustPreviewed.contains(kind) { dustPreviewed.remove(kind); dustPreviewed.insert(newKind) }
        if dustEditing == kind { dustEditing = newKind }
        if gradientPreviewed.contains(kind) { gradientPreviewed.remove(kind); gradientPreviewed.insert(newKind) }
        if gradientEditing == kind { gradientEditing = newKind; gradientHealed = nil }
        sourcesChanged()
    }

    private func sourcesChanged() {
        postStretchCache = [:]
        recordRecentSet()
        if sources.isEmpty {
            context = nil; analysisSamples = [:]; thumbnails = [:]
            renderer.clearCache()
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
        saveRecentState()
    }

    private var stateSaveTimer: Timer?

    /// Auto-saves the current settings/crop/orientation/presets with the recent set (debounced).
    private func scheduleRecentStateSave() {
        stateSaveTimer?.invalidate()
        stateSaveTimer = Timer.scheduledTimer(withTimeInterval: 0.7, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.saveRecentState() }
        }
    }

    func saveRecentState() {
        stateSaveTimer?.invalidate()
        guard !suppressRecent, !isLoading, !sources.isEmpty else { return }
        // Only complete sets (every channel backed by a file; a split colour file backs three).
        guard sources.values.allSatisfy({ $0.url != nil }) else { return }
        let paths = Set(sources.values.compactMap { $0.url?.path })
        var doc = projectDocument()
        doc.channels = []
        recentSets = recentStore.updateState(paths: paths, state: doc)
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
        let entries = set.entries.map { ($0.kind, $0.path) }
        Task.detached(priority: .userInitiated) {
            let (its, errs) = ProjectModel.loadEntries(entries)
            await MainActor.run {
                if !errs.isEmpty { self.errorMessage = errs.joined(separator: "\n") }
                if let st = set.state {
                    self.orientation = st.orientation
                    self.settings = st.settings
                    self.crop = st.crop
                    self.dust = st.dust
                    self.gradient = st.gradient
                    self.corrections = st.corrections
                    self.presets = st.presets
                    self.presetThumbnailCache = [:]
                }
                self.addChannels(its)
                if let p = set.state?.starsFile { self.pendingStarsFile = URL(fileURLWithPath: p) }
            }
        }
    }

    /// Loads the files of a recorded set or project, each path once, and hands every entry the
    /// plane it was made from: a colour file split into R, G and B gives its own plane to each of
    /// the three (they share the path), everything else comes from the first plane.
    nonisolated static func loadEntries(_ entries: [(ChannelKind, String)]) -> (items: [(ChannelKind, PlanarImage, String, URL?, String)], errors: [String]) {
        var byPath: [String: [ChannelKind]] = [:]
        var paths: [String] = []
        for (k, p) in entries { if byPath[p] == nil { paths.append(p) }; byPath[p, default: []].append(k) }
        var loaded: [(LoadedFile?, Error?)] = Array(repeating: (nil, nil), count: paths.count)
        let lock = NSLock()
        DispatchQueue.concurrentPerform(iterations: paths.count) { i in
            do { let f = try ImageLoader.load(url: URL(fileURLWithPath: paths[i])); lock.lock(); loaded[i] = (f, nil); lock.unlock() }
            catch { lock.lock(); loaded[i] = (nil, error); lock.unlock() }
        }
        var items: [(ChannelKind, PlanarImage, String, URL?, String)] = []
        var errors: [String] = []
        for (i, p) in paths.enumerated() {
            let u = URL(fileURLWithPath: p)
            if let e = loaded[i].1 { errors.append("\(u.lastPathComponent): \(e.localizedDescription)"); continue }
            guard let f = loaded[i].0 else { continue }
            for k in byPath[p] ?? [] { items.append((k, ProjectModel.plane(of: f, for: k), f.fileName, u, f.formatDescription)) }
        }
        return (items, errors)
    }

    /// The plane of a loaded file that a channel uses: R, G and B of a colour file are its three planes.
    nonisolated static func plane(of f: LoadedFile, for kind: ChannelKind) -> PlanarImage {
        guard f.isColor else { return f.planes[0] }
        switch kind {
        case .green: return f.planes[1]
        case .blue: return f.planes[2]
        default: return f.planes[0]
        }
    }

    func removeRecentSet(_ set: RecentSet) { recentSets = recentStore.remove(id: set.id) }
    func clearRecentSets() { recentStore.clear(); recentSets = [] }

    // MARK: - Crop

    private func cropChanged() {
        guard !baseSources.isEmpty, !suppressCropRebuild else { return }
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
            let thumbs = result.values.map { ($0.kind, ProjectModel.slotThumbnail($0)) }
            await MainActor.run {
                self.sources = result
                for (k, t) in thumbs { if let t { self.slotThumbnails[k] = t } }
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
        if dustEditing != nil { cancelDustEditing() }
        if gradientEditing != nil { cancelGradientEditing() }
        cropDraft = crop ?? imageSize.map { PixelRect(x: Int($0.width * 0.1), y: Int($0.height * 0.1), width: Int($0.width * 0.8), height: Int($0.height * 0.8)) }
        cropEditing = true
    }

    func commitCropEditing() {
        cropEditing = false
        if let d = cropDraft, let size = imageSize, let c = d.clamped(width: Int(size.width), height: Int(size.height)) {
            crop = c == PixelRect.full(width: Int(size.width), height: Int(size.height)) ? nil : c
        }
    }

    /// Keeps a draft around (the fields are hidden anyway) so bindings created while editing stay valid.
    func cancelCropEditing() { cropEditing = false; cropDraft = crop ?? cropDraft }

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
            scheduleRecentStateSave()
            return
        }
        scheduleRender()
        let paletteOnly = CombineSettings.differOnlyInPalette(settings, old)
        if !paletteOnly { scheduleThumbnails(delay: 0.6) }
        scheduleRecentStateSave()
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
        // The corrections editor paints on the channel live: the draft goes with the render (a
        // copy, the renderer runs while the next stroke lands), the committed map is bypassed by
        // rendering the original-based plane with the draft only. The channel's source holds the
        // committed map already, so the draft starts as that map and the source is swapped for the
        // uncorrected one while editing (see beginCorrectionEditing). The editor renders with its
        // own context (normalisation off), so the draft goes on both paths.
        var correction: (map: CorrectionMap, strength: Float, kind: ChannelKind)? = nil
        if let k = correctionEditing, let d = correctionDraft, !correctionBypass, !d.isEmpty {
            correction = (d.copy(), Float(corrections.strength(for: k)), k)
        }
        if shown.normalization != settings.normalization {
            // Different normalisation than the live context: render with a context of its own.
            let snapshotCtx = RenderContext.make(settings: shown.normalization, sources: sources)
            var req = PreviewRequest(sources: sources, settings: shown, context: snapshotCtx, analysisSamples: analysisSamples, fixedPostStretch: nil,
                                     visibleRect: viewport.visibleRect.isEmpty ? nil : viewport.visibleRect,
                                     scale: viewport.scale, pixelBudget: highQualityPreview ? 12_000_000 : 3_000_000, generation: renderGeneration, stars: starsLayer)
            req.correction = correction
            renderer.submit(req)
            return
        }
        let key = PostStretchKey(shown, crop: crop, kinds: availableKinds)
        var req = PreviewRequest(sources: sources, settings: shown, context: ctx, analysisSamples: analysisSamples,
                                 fixedPostStretch: shown.stretchStage == .postCombine ? postStretchCache[key] : nil,
                                 visibleRect: viewport.visibleRect.isEmpty ? nil : viewport.visibleRect,
                                 scale: viewport.scale, pixelBudget: highQualityPreview ? 12_000_000 : 3_000_000, generation: renderGeneration, stars: starsLayer)
        req.correction = correction
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
                                                       analysisSamples: analysisSamples, region: region, maxPixels: 90_000, stars: starsLayer))
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

    /// The preview as it is on screen (rendered region at the preview resolution), limited to the
    /// crop when one is set; nil when nothing is rendered yet.
    var previewImageForCopy: CGImage? {
        guard let f = frame else { return nil }
        guard let c = crop?.cgRect else { return f.cgImage }
        let r = c.intersection(f.rect)
        guard !r.isEmpty else { return nil }
        let s = CGFloat(f.cgImage.width) / f.rect.width
        let px = CGRect(x: (r.minX - f.rect.minX) * s, y: (r.minY - f.rect.minY) * s, width: r.width * s, height: r.height * s).integral
        return f.cgImage.cropping(to: px) ?? f.cgImage
    }

    /// Puts the preview as it is on screen (cropped part only when a crop is set) on the pasteboard
    /// as an image; returns false when nothing is rendered yet.
    @discardableResult
    /// Puts the combination formula (one PixelMath expression per output) on the pasteboard.
    func copyFormulaToPasteboard(_ pb: NSPasteboard = .general) {
        let lines = settings.formulaLines(available: availableKinds)
        pb.clearContents()
        pb.setString(lines.joined(separator: "\n"), forType: .string)
        statusText = "Formula copied"
    }

    func copyPreviewToPasteboard(_ pb: NSPasteboard = .general) -> Bool {
        guard let cg = previewImageForCopy else { return false }
        pb.clearContents()
        let img = NSImage(cgImage: cg, size: NSSize(width: cg.width, height: cg.height))
        let ok = pb.writeObjects([img])
        if ok { statusText = "Preview copied (\(cg.width) × \(cg.height) px)." }
        return ok
    }

    func export(to url: URL, format: RasterWriter.Format, linear: Bool, completion: @escaping (Error?) -> Void) {
        guard let ctx = context, !sources.isEmpty else { completion(RenderError.noOutput); return }
        var s = settings
        if linear {
            s.stretch.method = .none
            s.post = PostSettings()
            s.grading = ColorGradingSettings()
            s.colorBands = ColorBandsSettings()
            s.detail = DetailSettings()
            s.luminance.enabled = false
            s.stars.enabled = false
        }
        let key = PostStretchKey(s, crop: crop, kinds: availableKinds)
        let fixed = postStretchCache[key]
        let srcs = sources, crop = self.crop, stars = starsLayer
        exportProgress = 0
        Task.detached(priority: .userInitiated) {
            do {
                let img = try Renderer.renderFull(sources: srcs, settings: s, context: ctx, postStretch: fixed, region: crop, stars: stars, progress: { p in
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
        doc.orientation = orientation
        doc.dust = dust
        doc.gradient = gradient
        doc.corrections = corrections
        doc.presets = presets
        doc.channels = sources.values.sorted { $0.kind.rawValue < $1.kind.rawValue }.compactMap { s in
            s.url.map { ProjectDocument.ChannelRef(kind: s.kind, path: $0.path) }
        }
        doc.starsFile = starsLayer?.url?.path
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
            presets = doc.presets
            presetThumbnailCache = [:]
            let refs = doc.channels
            orientation = doc.orientation
            dust = doc.dust
            gradient = doc.gradient
            corrections = doc.corrections
            guard !refs.isEmpty else { settings = doc.settings; crop = doc.crop; return }
            isLoading = true
            loadingMessage = "Loading project…"
            Task.detached(priority: .userInitiated) {
                let (its, errs) = ProjectModel.loadEntries(refs.map { ($0.kind, $0.path) })
                await MainActor.run {
                    if !errs.isEmpty { self.errorMessage = errs.joined(separator: "\n") }
                    self.settings = doc.settings
                    self.crop = doc.crop
                    self.addChannels(its)
                    if let p = doc.starsFile { self.pendingStarsFile = URL(fileURLWithPath: p) }
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
        x.monoChannels = nil; y.monoChannels = nil
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
                    if let size = self.requiredFileSize, Int(size.width) != f.width || Int(size.height) != f.height {
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

// MARK: - Dust spots

extension ProjectModel {
    /// Spots the editor currently shows (the draft while editing, else the applied ones).
    func dustSpots(for kind: ChannelKind) -> [DustSpot] { (dustDraft ?? dust).spots(for: kind) }

    /// Radius given to a new spot when nothing is found around the click (small: the fit grows it
    /// to the shadow, and a stray click must not cost a huge window).
    var defaultDustRadius: Double { imageSize.map { max(12, min($0.width, $0.height) / 150) } ?? 20 }

    /// Opens the dust editor on a channel (the preview shows that channel alone, with the draft spots).
    /// The three channel editors, as the tool buttons above the channel column offer them.
    enum CorrectionTool: String, CaseIterable, Identifiable {
        case dust, gradient, manual
        var id: String { rawValue }
        var title: String { switch self { case .dust: return "Dust"; case .gradient: return "Gradient"; case .manual: return "Manual" } }
        var symbol: String { switch self { case .dust: return "circle.dotted"; case .gradient: return "circle.lefthalf.filled"; case .manual: return "paintbrush.pointed.fill" } }
        var help: String {
            switch self {
            case .dust: return "Heal dust spots (donuts, motes) on the channels"
            case .gradient: return "Remove a sky gradient from the channels"
            case .manual: return "Paint corrections on the channels"
            }
        }
    }

    /// The editor that is open, if any.
    var editingTool: CorrectionTool? {
        if dustEditing != nil { return .dust }
        if gradientEditing != nil { return .gradient }
        if correctionEditing != nil { return .manual }
        return nil
    }

    /// The channel an open editor shows.
    var editingChannel: ChannelKind? { dustEditing ?? gradientEditing ?? correctionEditing }

    /// Opens an editor on the channel being edited (another editor's work is applied first), else
    /// on the peeked channel, else on the first loaded one.
    func beginEditing(_ tool: CorrectionTool) {
        guard editingTool != tool else { return }
        let kind = editingChannel ?? peekChannel ?? ChannelKind.allCases.first { sources[$0] != nil }
        guard let kind else { return }
        switch editingTool {
        case .dust?: commitDustEditing()
        case .gradient?: commitGradientEditing()
        case .manual?: commitCorrectionEditing()
        case nil: break
        }
        switch tool {
        case .dust: beginDustEditing(kind)
        case .gradient: beginGradientEditing(kind)
        case .manual: beginCorrectionEditing(kind)
        }
    }

    /// Shows another channel in the open editor (a click on its slot while editing).
    func switchEditingChannel(_ kind: ChannelKind) {
        switch editingTool {
        case .dust?: switchDustChannel(kind)
        case .gradient?: switchGradientChannel(kind)
        case .manual?: switchCorrectionChannel(kind)
        case nil: break
        }
    }

    func beginDustEditing(_ kind: ChannelKind) {
        guard sources[kind] != nil else { return }
        if cropEditing { cancelCropEditing() }
        if gradientEditing != nil { cancelGradientEditing() }
        // A press-and-hold peek left behind by the slot menu would otherwise override the edited channel.
        peekChannel = nil
        if dustEditing == nil { dustDraft = dust; dustBypass = false }
        dustEditing = kind
        selectedDustSpot = nil
        scheduleDustPreview()
    }

    /// Switches the editor to another loaded channel, keeping every draft. The channel is always
    /// re-previewed: its draft may have changed while another one was shown (Copy to all) and the
    /// Original toggle applies to the channel on screen only.
    func switchDustChannel(_ kind: ChannelKind) {
        guard dustEditing != nil, dustEditing != kind, sources[kind] != nil else { return }
        peekChannel = nil
        dustEditing = kind
        selectedDustSpot = nil
        scheduleDustPreview()
    }

    /// Adds a spot at an image point, fitted to the shadow found there (plain spot of the default
    /// radius when nothing is found).
    func addDustSpot(at p: CGPoint) {
        guard let k = dustEditing, var d = dustDraft, let orig = originalImages[k] ?? baseSources[k]?.image else { return }
        var spot = d.newSpot(x: p.x, y: p.y, radius: defaultDustRadius)
        if let f = DustRemoval.fit(image: orig, x: p.x, y: p.y, radius: defaultDustRadius, smoothness: d.smoothness) { spot.x = f.x; spot.y = f.y; spot.radius = f.radius }
        d.spots[k, default: []].append(spot)
        dustDraft = d
        selectedDustSpot = spot.id
    }

    /// Replaces the spots of the edited channel (moves / resizes from the preview).
    func updateDustSpots(_ spots: [DustSpot]) {
        guard let k = dustEditing else { return }
        dustDraft?.spots[k] = spots
    }

    func removeSelectedDustSpot() {
        guard let k = dustEditing, let id = selectedDustSpot else { return }
        dustDraft?.spots[k]?.removeAll { $0.id == id }
        selectedDustSpot = nil
    }

    /// Re-runs the fit from the centre of the selected spot (after moving it by hand, say).
    func refitSelectedDustSpot() {
        guard let k = dustEditing, let id = selectedDustSpot, var d = dustDraft, let orig = originalImages[k] ?? baseSources[k]?.image,
              let i = d.spots[k]?.firstIndex(where: { $0.id == id }) else { return }
        var s = d.spots[k]![i]
        guard let f = DustRemoval.fit(image: orig, x: s.x, y: s.y, radius: s.radius / 2, smoothness: s.smoothness) else { statusText = "No shadow found around the spot."; return }
        s.x = f.x; s.y = f.y; s.radius = f.radius
        d.spots[k]![i] = s
        dustDraft = d
    }

    /// The selected spot of the edited channel (nil when none).
    var selectedDustSpotValue: DustSpot? {
        guard let k = dustEditing, let id = selectedDustSpot else { return nil }
        return dustDraft?.spots(for: k).first { $0.id == id }
    }

    /// Replaces the selected spot (parameter edits from the toolbar).
    func updateSelectedDustSpot(_ spot: DustSpot) {
        guard let k = dustEditing, let i = dustDraft?.spots[k]?.firstIndex(where: { $0.id == spot.id }) else { return }
        dustDraft?.spots[k]?[i] = spot
    }

    func clearDustSpots() {
        guard let k = dustEditing else { return }
        dustDraft?.spots[k] = []
        selectedDustSpot = nil
    }

    /// Copies the spots of the edited channel to every other loaded channel (dust on the sensor
    /// window shadows every filter at the same place).
    func copyDustSpotsToAllChannels() {
        guard let k = dustEditing, var d = dustDraft else { return }
        let spots = d.spots(for: k)
        for other in sources.keys where other != k {
            d.spots[other] = spots.map { var c = $0; c.id = UUID(); return c }
        }
        dustDraft = d
    }

    /// Applies the draft: channels whose spots (or parameters) changed are healed from the original
    /// and fully re-analysed; everything downstream is recomputed once.
    func commitDustEditing() {
        guard let draft = dustDraft else { dustEditing = nil; return }
        dustPreviewTimer?.invalidate()
        dustPreviewGeneration += 1
        let changed = Set(sources.keys).filter { draft.spots(for: $0) != dust.spots(for: $0) }
        dust = draft
        dustDraft = nil
        dustEditing = nil
        selectedDustSpot = nil
        dustBypass = false
        rebuildHealed(kinds: changed.union(dustPreviewed))
        dustPreviewed = []
        scheduleRecentStateSave()
    }

    /// Drops the draft and puts the previewed channels back to their applied state.
    func cancelDustEditing() {
        dustPreviewTimer?.invalidate()
        dustPreviewGeneration += 1
        dustDraft = nil
        dustEditing = nil
        selectedDustSpot = nil
        dustBypass = false
        let previewed = dustPreviewed
        dustPreviewed = []
        if previewed.isEmpty { scheduleRender() } else { rebuildHealed(kinds: previewed) }
    }

    /// Live preview of the draft on the edited channel: heals from the original and swaps the source
    /// in with the statistics it already has (statistics barely move with a few local spots), so
    /// only a render follows. Debounced and latest-wins.
    private func scheduleDustPreview() {
        dustPreviewTimer?.invalidate()
        dustPreviewTimer = Timer.scheduledTimer(withTimeInterval: 0.08, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.dustPreview() }
        }
    }

    private func dustPreview() {
        guard let k = dustEditing, let d = dustDraft, let orig = originalImages[k], let base = baseSources[k] else { return }
        dustPreviewGeneration += 1
        let gen = dustPreviewGeneration
        let spots = dustBypass ? [] : d.spots(for: k), crop = self.crop, gradient = self.gradient
        dustPreviewed.insert(k)
        Task.detached(priority: .userInitiated) {
            let healed = GradientRemoval.correct(DustRemoval.heal(orig, spots: spots), kind: k, settings: gradient)
            let b = ChannelSource(base, image: healed)
            let c = crop == nil ? b : ChannelSource(b, region: crop)
            await MainActor.run {
                guard gen == self.dustPreviewGeneration, self.dustEditing == k else { return }
                self.baseSources[k] = b
                self.sources[k] = c
                self.scheduleRender()
            }
        }
    }

    /// Re-processes `kinds` from their originals (applied dust spots, then the applied gradient
    /// models) and rebuilds their sources (statistics, star mask, thumbnails), then the render context.
    private func rebuildHealed(kinds: Set<ChannelKind>, completion: (() -> Void)? = nil) {
        let todo = kinds.filter { baseSources[$0] != nil && originalImages[$0] != nil }
        guard !todo.isEmpty else { scheduleRender(); completion?(); return }
        let base = baseSources, originals = originalImages, dust = self.dust, crop = self.crop, gradient = self.gradient
        let maps = correctionMaps, corr = corrections
        isLoading = true
        loadingMessage = gradient.isEmpty && maps.isEmpty ? "Healing dust spots…" : "Processing channels…"
        Task.detached(priority: .userInitiated) {
            let kinds = Array(todo)
            var out: [ChannelKind: (ChannelSource, ChannelSource)] = [:]
            let lock = NSLock()
            DispatchQueue.concurrentPerform(iterations: kinds.count) { i in
                let k = kinds[i], old = base[k]!
                let healed = ProjectModel.process(originals[k]!, kind: k, dust: dust, gradient: gradient, correction: maps[k].map { ($0, Float(corr.strength(for: k))) })
                let b = ChannelSource(kind: k, image: healed, fileName: old.fileName, url: old.url, formatDescription: old.formatDescription)
                let c = crop == nil ? b : ChannelSource(b, region: crop)
                lock.lock(); out[k] = (b, c); lock.unlock()
            }
            let result = out
            let thumbs = result.map { ($0.key, ProjectModel.slotThumbnail($0.value.1)) }
            await MainActor.run {
                for (k, (b, c)) in result { self.baseSources[k] = b; self.sources[k] = c }
                for (k, t) in thumbs { if let t { self.slotThumbnails[k] = t } }
                self.isLoading = false
                self.sourcesChanged()
                completion?()
            }
        }
    }
}

/// What the gradient editor shows.
enum GradientView: String, CaseIterable, Identifiable {
    case corrected, original, background
    var id: String { rawValue }
    var displayName: String {
        switch self { case .corrected: return "Corrected"; case .original: return "Original"; case .background: return "Background" }
    }
}

/// Result of a gradient fit shown in the editor.
struct GradientFitInfo: Equatable {
    var kind: ChannelKind
    /// The guide actually used (refined from the drawing when asked).
    var gesture: GradientGesture
    var residualSigma: Float
    var millis: Double
    /// What became of each background sample (`samples` shape), and the samples used (the grid
    /// generated when the model had none).
    var sampleResults: [SampleResult] = []
    var samples: [BackgroundSample] = []
}

// MARK: - Manual corrections

extension ProjectModel {
    /// Strength (stops) of the edited channel's map.
    var correctionStrength: Double {
        get { correctionEditing.map { corrections.strength(for: $0) } ?? CorrectionSettings.defaultStrength }
        set { guard let k = correctionEditing else { return }; corrections.strength[k] = newValue; scheduleRender() }
    }
    /// Brush radius in image pixels (the settings keep it as a fraction of the long side).
    var correctionBrushRadius: Double {
        get { corrections.brushRadius * Double(imageSize.map { max($0.width, $0.height) } ?? 1000) }
        set { let long = Double(imageSize.map { max($0.width, $0.height) } ?? 1000); corrections.brushRadius = min(max(newValue / long, 0.003), 0.2) }
    }
    var correctionBrushFlow: Double { get { corrections.brushFlow } set { corrections.brushFlow = min(max(newValue, 0.01), 1) } }
    var correctionBrushFeather: Double { get { corrections.brushFeather } set { corrections.brushFeather = min(max(newValue, 0), 1) } }
    var correctionDarken: Bool { get { corrections.darken } set { corrections.darken = newValue } }
    var canUndoCorrection: Bool { !correctionUndo.isEmpty }
    func hasCorrection(_ kind: ChannelKind) -> Bool { correctionMaps[kind] != nil }

    /// Opens the corrections editor on a channel: the preview shows that channel alone, without its
    /// committed map, with the draft (a copy of the committed map) painted on live.
    func beginCorrectionEditing(_ kind: ChannelKind) {
        guard sources[kind] != nil, let size = imageSize else { return }
        if cropEditing { cancelCropEditing() }
        if dustEditing != nil { cancelDustEditing() }
        if gradientEditing != nil { cancelGradientEditing() }
        peekChannel = nil
        if let k = correctionEditing, k != kind { commitCorrectionEditing(reopen: kind); return }
        correctionDraft = correctionMaps[kind]?.copy() ?? CorrectionMap(imageWidth: Int(size.width), imageHeight: Int(size.height))
        correctionUndo = []
        correctionBypass = false
        correctionEditing = kind
        correctionVersion += 1
        showUncorrectedSource(kind)
    }

    /// Swaps the edited channel's source for its uncorrected version (dust-healed, gradient-corrected).
    private func showUncorrectedSource(_ k: ChannelKind) {
        guard correctionMaps[k] != nil, let orig = originalImages[k], let base = baseSources[k] else { scheduleRender(); return }
        let dust = self.dust, gradient = self.gradient, crop = self.crop
        isLoading = true
        loadingMessage = "Preparing \(k.shortName)…"
        Task.detached(priority: .userInitiated) {
            let plain = ProjectModel.process(orig, kind: k, dust: dust, gradient: gradient)
            let b = ChannelSource(base, image: plain)
            let c = crop == nil ? b : ChannelSource(b, region: crop)
            await MainActor.run {
                guard self.correctionEditing == k else { return }
                self.baseSources[k] = b
                self.sources[k] = c
                self.isLoading = false
                self.scheduleRender()
            }
        }
    }

    func switchCorrectionChannel(_ kind: ChannelKind) {
        guard correctionEditing != nil, correctionEditing != kind, sources[kind] != nil else { return }
        commitCorrectionEditing(reopen: kind)
    }

    /// Paints a stroke segment (image points) with the current brush; `invert` swaps darken / brighten (⌥).
    func paintCorrection(from a: CGPoint, to b: CGPoint, invert: Bool) {
        guard correctionEditing != nil, let d = correctionDraft else { return }
        let sign: Float = (corrections.darken != invert) ? -1 : 1
        d.stroke(from: a, to: b, radius: correctionBrushRadius, amount: sign * Float(corrections.brushFlow), hardness: Float(1 - corrections.brushFeather))
        correctionVersion += 1
        scheduleRender()
    }

    /// Start of a stroke: the undo snapshot.
    func beginCorrectionStroke() {
        guard let d = correctionDraft else { return }
        correctionUndo.append(d.copy())
        if correctionUndo.count > 24 { correctionUndo.removeFirst() }
    }

    func undoCorrection() {
        guard correctionEditing != nil, let last = correctionUndo.popLast() else { return }
        correctionDraft = last
        correctionVersion += 1
        scheduleRender()
    }

    func clearCorrection() {
        guard correctionEditing != nil, let d = correctionDraft, !d.isEmpty else { return }
        beginCorrectionStroke()
        d.clear()
        correctionVersion += 1
        scheduleRender()
    }

    /// Applies the draft: the map is stored (sidecar), the channel re-processed.
    func commitCorrectionEditing(reopen: ChannelKind? = nil) {
        guard let k = correctionEditing else { return }
        let draft = correctionDraft
        correctionEditing = nil
        correctionDraft = nil
        correctionUndo = []
        correctionBypass = false
        if let d = draft, !d.isEmpty {
            correctionMaps[k] = d
            if let u = baseSources[k]?.url {
                do { try d.unoriented(orientation).write(for: u, kind: k) } catch { errorMessage = "The correction map could not be saved: \(error.localizedDescription)" }
            }
        } else {
            correctionMaps.removeValue(forKey: k)
            if let u = baseSources[k]?.url { CorrectionMap.remove(for: u, kind: k) }
        }
        correctionVersion += 1
        scheduleRecentStateSave()
        if let next = reopen {
            // The next channel opens once this one is re-processed.
            rebuildHealed(kinds: [k]) { [weak self] in self?.beginCorrectionEditing(next) }
        } else {
            rebuildHealed(kinds: [k])
        }
    }

    /// Drops the draft; the channel goes back to its committed state.
    func cancelCorrectionEditing() {
        guard let k = correctionEditing else { return }
        correctionEditing = nil
        correctionDraft = nil
        correctionUndo = []
        correctionBypass = false
        correctionVersion += 1
        if correctionMaps[k] != nil { rebuildHealed(kinds: [k]) } else { scheduleRender() }
    }

    /// Removes a channel's map (outside the editor).
    func removeCorrection(_ kind: ChannelKind) {
        guard correctionMaps[kind] != nil else { return }
        if correctionEditing == kind { cancelCorrectionEditing() }
        correctionMaps.removeValue(forKey: kind)
        if let u = baseSources[kind]?.url { CorrectionMap.remove(for: u, kind: kind) }
        rebuildHealed(kinds: [kind])
    }
}

// MARK: - Gradient removal

extension ProjectModel {
    /// The model the editor currently shows for a channel (the draft while editing, else the applied one).
    func gradientSpec(for kind: ChannelKind) -> GradientSpec? { (gradientDraft ?? gradient).spec(for: kind) }
    /// Protected regions shown (draft while editing).
    var gradientRegions: [GradientRegion] { (gradientDraft ?? gradient).protect }
    /// Model of the edited channel (nil: nothing drawn yet).
    var editedGradientSpec: GradientSpec? { gradientEditing.flatMap { gradientSpec(for: $0) } }
    /// Shape of the edited channel's guide, or the shape new guides get.
    var gradientShape: GradientShape { editedGradientSpec?.gesture.shape ?? (gradientDraft ?? gradient).shape }
    /// Semi-axes given to a new protected region.
    var defaultGradientRegionRadius: Double { imageSize.map { max(20, min($0.width, $0.height) / 10) } ?? 100 }

    /// Opens the gradient editor on a channel (the preview shows that channel alone, with its draft model).
    func beginGradientEditing(_ kind: ChannelKind) {
        guard sources[kind] != nil else { return }
        if cropEditing { cancelCropEditing() }
        if dustEditing != nil { cancelDustEditing() }
        peekChannel = nil
        if gradientEditing == nil { gradientDraft = gradient; gradientView = .corrected; gradientProtectMode = false }
        if gradientEditing != kind { gradientHealed = nil }
        gradientEditing = kind
        selectedGradientRegion = nil
        selectedGradientSample = nil
        gradientFit = nil
        scheduleGradientPreview()
    }

    /// Switches the editor to another loaded channel, keeping every draft.
    func switchGradientChannel(_ kind: ChannelKind) {
        guard gradientEditing != nil, gradientEditing != kind, sources[kind] != nil else { return }
        peekChannel = nil
        gradientEditing = kind
        gradientHealed = nil
        selectedGradientRegion = nil
        selectedGradientSample = nil
        gradientFit = nil
        scheduleGradientPreview()
    }

    /// Sets (or replaces) the guide of the edited channel; a new model takes the default parameters.
    func setGradientGesture(_ g: GradientGesture) {
        guard let k = gradientEditing, var d = gradientDraft else { return }
        if var spec = d.specs[k] { spec.gesture = g; d.specs[k] = spec } else { d.specs[k] = d.newSpec(gesture: g) }
        d.shape = g.shape
        gradientDraft = d
    }

    /// Replaces the model of the edited channel (parameter edits); the parameters become the defaults for new models.
    func updateGradientSpec(_ spec: GradientSpec) {
        guard let k = gradientEditing, var d = gradientDraft else { return }
        d.specs[k] = spec
        d.mode = spec.mode; d.residual = spec.residual; d.sectors = spec.sectors; d.smoothness = spec.smoothness
        d.degree = spec.degree; d.refine = spec.refine; d.shape = spec.gesture.shape
        d.surface = spec.surface; d.sampleSmoothing = spec.sampleSmoothing; d.autoSmoothing = spec.autoSmoothing; d.sampleRadius = spec.sampleRadius
        d.gridPerRow = spec.gridPerRow; d.gridTolerance = spec.gridTolerance
        gradientDraft = d
    }

    /// Switches the shape of the edited channel's guide (converting the drawing), or the shape to
    /// draw when nothing is drawn yet (the polynomial and the samples need no drawing and are
    /// created at once; a samples model starts with the generated grid).
    func setGradientShape(_ shape: GradientShape) {
        guard let k = gradientEditing, var d = gradientDraft else { return }
        d.shape = shape
        if var spec = d.specs[k] { spec.gesture = spec.gesture.converted(to: shape); d.specs[k] = spec }
        else if shape == .polynomial { d.specs[k] = d.newSpec(gesture: .polynomial()) }
        else if shape == .samples { d.specs[k] = d.newSpec(gesture: .samples()) }
        selectedGradientSample = nil
        gradientDraft = d
    }

    // Background samples of the edited channel (`samples` shape).

    var gradientSamples: [BackgroundSample] { editedGradientSpec?.samples ?? [] }
    /// Radius of the sample discs shown (the model's, or the automatic one).
    var gradientSampleRadius: Double {
        guard let spec = editedGradientSpec, let size = imageSize else { return 20 }
        return spec.resolvedSampleRadius(width: Int(size.width), height: Int(size.height))
    }

    func addGradientSample(at p: CGPoint) {
        guard let k = gradientEditing, var d = gradientDraft else { return }
        var spec = d.specs[k] ?? d.newSpec(gesture: .samples())
        guard spec.gesture.shape == .samples else { return }
        let sample = BackgroundSample(x: p.x, y: p.y)
        spec.samples.append(sample)
        d.specs[k] = spec
        selectedGradientSample = sample.id
        gradientDraft = d
    }

    /// Replaces the samples (moves from the preview).
    func updateGradientSamples(_ samples: [BackgroundSample]) {
        guard let k = gradientEditing, var spec = gradientDraft?.specs[k], spec.gesture.shape == .samples else { return }
        spec.samples = samples
        gradientDraft?.specs[k] = spec
    }

    func removeSelectedGradientSample() {
        guard let k = gradientEditing, let id = selectedGradientSample, var spec = gradientDraft?.specs[k] else { return }
        spec.samples.removeAll { $0.id == id }
        gradientDraft?.specs[k] = spec
        selectedGradientSample = nil
    }

    /// Drops the samples; the next fit generates the grid again with the current parameters.
    func regenerateGradientGrid() {
        guard let k = gradientEditing, var d = gradientDraft, var spec = d.specs[k], spec.gesture.shape == .samples else { return }
        spec.samples = []
        if spec.gridPerRow <= 0 { spec.gridPerRow = max(4, d.gridPerRow) }
        selectedGradientSample = nil
        d.specs[k] = spec
        gradientDraft = d
    }

    /// Removes every sample without generating a grid (click to place samples by hand).
    func clearGradientSamples() {
        guard let k = gradientEditing, var spec = gradientDraft?.specs[k], spec.gesture.shape == .samples else { return }
        spec.samples = []
        spec.gridPerRow = 0
        gradientDraft?.specs[k] = spec
        selectedGradientSample = nil
        gradientFit = nil
    }

    /// Removes the model of the edited channel.
    func clearGradient() {
        guard let k = gradientEditing else { return }
        gradientDraft?.specs.removeValue(forKey: k)
        gradientFit = nil
    }

    /// Copies the refined geometry of the latest fit into the drawn guide.
    func adoptRefinedGradient() {
        guard let k = gradientEditing, let f = gradientFit, f.kind == k, var spec = gradientDraft?.specs[k] else { return }
        spec.gesture = f.gesture
        gradientDraft?.specs[k] = spec
    }

    /// Gives every other loaded channel the edited channel's model (vignetting is the same for every filter).
    func copyGradientToAllChannels() {
        guard let k = gradientEditing, var d = gradientDraft, let spec = d.specs[k] else { return }
        for other in sources.keys where other != k { d.specs[other] = spec }
        gradientDraft = d
    }

    func addGradientRegion(at p: CGPoint) {
        guard gradientEditing != nil, gradientDraft != nil else { return }
        let r = defaultGradientRegionRadius
        let region = GradientRegion(x: p.x, y: p.y, a: r, b: r)
        gradientDraft?.protect.append(region)
        selectedGradientRegion = region.id
    }

    /// Replaces the protected regions (moves / resizes from the preview).
    func updateGradientRegions(_ regions: [GradientRegion]) {
        guard gradientEditing != nil else { return }
        gradientDraft?.protect = regions
    }

    /// ⌫ in the editor: the selected sample, else the selected region.
    func deleteGradientSelection() {
        if selectedGradientSample != nil { removeSelectedGradientSample() } else { removeSelectedGradientRegion() }
    }

    func removeSelectedGradientRegion() {
        guard gradientEditing != nil, let id = selectedGradientRegion else { return }
        gradientDraft?.protect.removeAll { $0.id == id }
        selectedGradientRegion = nil
    }

    func clearGradientRegions() {
        guard gradientEditing != nil else { return }
        gradientDraft?.protect = []
        selectedGradientRegion = nil
    }

    /// Applies the draft: channels whose model (or, when the protected regions changed, any model)
    /// differs are re-processed from the original and fully re-analysed.
    func commitGradientEditing() {
        guard let draft = gradientDraft else { gradientEditing = nil; return }
        gradientPreviewTimer?.invalidate()
        gradientPreviewGeneration += 1
        let protectChanged = draft.protect != gradient.protect
        let changed = Set(sources.keys).filter { draft.spec(for: $0) != gradient.spec(for: $0) || (protectChanged && draft.spec(for: $0) != nil) }
        gradient = draft
        gradientDraft = nil
        gradientEditing = nil
        selectedGradientRegion = nil
        gradientView = .corrected
        gradientProtectMode = false
        gradientFit = nil
        gradientHealed = nil
        rebuildHealed(kinds: changed.union(gradientPreviewed))
        gradientPreviewed = []
        scheduleRecentStateSave()
    }

    /// Drops the draft and puts the previewed channels back to their applied state.
    func cancelGradientEditing() {
        gradientPreviewTimer?.invalidate()
        gradientPreviewGeneration += 1
        gradientDraft = nil
        gradientEditing = nil
        selectedGradientRegion = nil
        gradientView = .corrected
        gradientProtectMode = false
        gradientFit = nil
        gradientHealed = nil
        let previewed = gradientPreviewed
        gradientPreviewed = []
        if previewed.isEmpty { scheduleRender() } else { rebuildHealed(kinds: previewed) }
    }

    /// Live preview of the draft on the edited channel: the model is fitted on the dust-healed plane
    /// (kept while the editor is open) and the corrected plane swapped in with the statistics the
    /// source already has, so only a render follows. Debounced and latest-wins.
    private func scheduleGradientPreview() {
        gradientPreviewTimer?.invalidate()
        gradientPreviewTimer = Timer.scheduledTimer(withTimeInterval: 0.08, repeats: false) { [weak self] _ in
            Task { @MainActor in self?.gradientPreview() }
        }
    }

    private func gradientPreview() {
        guard let k = gradientEditing, let d = gradientDraft, let orig = originalImages[k], let base = baseSources[k] else { return }
        gradientPreviewGeneration += 1
        let gen = gradientPreviewGeneration
        let view = gradientView
        let spec = view == .original ? nil : d.spec(for: k), protect = d.protect, crop = self.crop
        let spots = dust.spots(for: k)
        let healedCache = gradientHealed?.kind == k ? gradientHealed?.image : nil
        gradientPreviewed.insert(k)
        Task.detached(priority: .userInitiated) {
            let healed = healedCache ?? DustRemoval.heal(orig, spots: spots)
            var image = healed
            var info: GradientFitInfo? = nil
            if let spec {
                let t0 = Date()
                if let (corrected, fit) = GradientRemoval.correct(healed, spec: spec, protect: protect) {
                    image = view == .background ? fit.backgroundImage() : corrected
                    info = GradientFitInfo(kind: k, gesture: fit.gesture, residualSigma: fit.residualSigma, millis: Date().timeIntervalSince(t0) * 1000, sampleResults: fit.sampleResults, samples: fit.spec.samples)
                }
            }
            // The model alone spans a far smaller range than the channel: it gets its own statistics
            // so the auto-stretch shows its shape; the corrected channel keeps the source's.
            let b = view == .background && image !== healed
                ? ChannelSource(kind: k, image: image, fileName: base.fileName, url: base.url, formatDescription: base.formatDescription)
                : ChannelSource(base, image: image)
            let c = crop == nil ? b : ChannelSource(b, region: crop)
            let fitInfo = info
            await MainActor.run {
                guard gen == self.gradientPreviewGeneration, self.gradientEditing == k else { return }
                self.gradientHealed = (k, healed)
                self.baseSources[k] = b
                self.sources[k] = c
                self.gradientFit = fitInfo
                // A samples model without samples got its grid from the fit: keep it (editable).
                if let f = fitInfo, var sp = self.gradientDraft?.specs[k], sp.gesture.shape == .samples, sp.samples.isEmpty, !f.samples.isEmpty {
                    sp.samples = f.samples
                    self.gradientDraftSilently = true
                    self.gradientDraft?.specs[k] = sp
                    self.gradientDraftSilently = false
                }
                self.scheduleRender()
            }
        }
    }
}
