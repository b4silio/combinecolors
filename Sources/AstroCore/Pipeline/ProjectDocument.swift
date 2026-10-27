import Foundation

/// Saved project: channel file paths + settings.
public struct ProjectDocument: Codable {
    public struct ChannelRef: Codable {
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
