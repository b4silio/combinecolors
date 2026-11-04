import Foundation

/// A named snapshot of the combination settings with a small preview image.
public struct SettingsPreset: Codable, Identifiable, Equatable {
    public var id: UUID = UUID()
    public var name: String
    public var settings: CombineSettings
    public var thumbnailPNG: Data?
    public var date: Date = Date()

    public init(name: String, settings: CombineSettings, thumbnailPNG: Data? = nil) {
        self.name = name; self.settings = settings; self.thumbnailPNG = thumbnailPNG
    }
}

/// One set of channels with its own settings (the Stars tab of a project).
public struct LayerDocument: Codable, Equatable {
    public var channels: [ProjectDocument.ChannelRef] = []
    public var settings: CombineSettings = CombineSettings()
    public var dust: DustSettings = DustSettings()
    public var gradient: GradientSettings = GradientSettings()
    public var corrections: CorrectionSettings = CorrectionSettings()
    public var presets: [SettingsPreset] = []
    public init() {}
}

/// Saved project: channel file paths + settings.
public struct ProjectDocument: Codable {
    public struct ChannelRef: Codable, Equatable {
        public var kind: ChannelKind
        public var path: String
        public var bookmark: Data?
        public init(kind: ChannelKind, path: String, bookmark: Data? = nil) { self.kind = kind; self.path = path; self.bookmark = bookmark }
    }
    public var version: Int = 1
    public var channels: [ChannelRef] = []
    public var settings: CombineSettings = CombineSettings()
    public var starsFile: String? = nil
    /// Shared crop applied to every channel (statistics, thumbnails, export).
    public var crop: PixelRect? = nil
    /// Saved presets (settings + thumbnail) for quick switching.
    public var presets: [SettingsPreset] = []
    /// Quarter turns clockwise applied to every channel at load (0…3); the crop is in oriented coordinates.
    public var rotation: Int = 0
    /// Horizontal mirror applied before the rotation (see `Orientation`).
    public var flipped: Bool = false
    /// Dust spots healed on the loaded channels (oriented coordinates) and the estimation parameters.
    public var dust: DustSettings = DustSettings()
    /// Gradient models removed from the loaded channels (oriented coordinates) and the shared protected regions.
    public var gradient: GradientSettings = GradientSettings()
    /// Strength of the hand-painted corrections per channel and the brush defaults (the maps are sidecar files).
    public var corrections: CorrectionSettings = CorrectionSettings()
    /// The Stars tab: star channels processed on their own and combined with the background
    /// (the fields above) by the "Combine stars" block. Crop and orientation are shared.
    public var starsLayer: LayerDocument? = nil

    public var orientation: Orientation {
        get { Orientation(rotation: rotation, flipped: flipped) }
        set { rotation = newValue.rotation; flipped = newValue.flipped }
    }

    public init() {}

    public func data() throws -> Data {
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try enc.encode(self)
    }

    public static func load(from url: URL) throws -> ProjectDocument {
        try JSONDecoder().decode(ProjectDocument.self, from: Data(contentsOf: url))
    }
}
