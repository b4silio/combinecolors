import Foundation

/// A loaded channel with its pyramid, statistics and star mask. Immutable once built.
public final class ChannelSource {
    public let kind: ChannelKind
    public let pyramid: Pyramid
    public let stats: ChannelStats
    public let maskedStats: ChannelStats
    public let mask: PlanarImage
    public let maskLevel: Int
    public let fileName: String
    public let url: URL?
    public let formatDescription: String
    public let analysisMillis: Double
    /// Region used for statistics (nil = whole image).
    public let region: PixelRect?

    public var width: Int { pyramid.base.width }
    public var height: Int { pyramid.base.height }
    public var image: PlanarImage { pyramid.base }

    public init(kind: ChannelKind, image: PlanarImage, fileName: String, url: URL? = nil, formatDescription: String = "",
                starMaskOptions: StarMask.Options = StarMask.Options(), maskMaxPixels: Int = 3_000_000, region: PixelRect? = nil) {
        let t0 = Date()
        self.kind = kind
        self.fileName = fileName
        self.url = url
        self.formatDescription = formatDescription
        let pyr = Pyramid(base: image)
        pyramid = pyr
        let ml = pyr.levelIndex(maxPixels: maskMaxPixels)
        maskLevel = ml
        let m = StarMask.build(image: pyr.level(ml), options: starMaskOptions)
        mask = m
        let r = region?.clamped(width: image.width, height: image.height)
        self.region = r
        (stats, maskedStats) = ChannelSource.computeStats(image: image, mask: m, maskLevel: ml, region: r)
        analysisMillis = Date().timeIntervalSince(t0) * 1000
    }

    /// Re-uses the pyramid and star mask of another source but recomputes statistics for a region.
    public init(_ other: ChannelSource, region: PixelRect?) {
        let t0 = Date()
        kind = other.kind; pyramid = other.pyramid; mask = other.mask; maskLevel = other.maskLevel
        fileName = other.fileName; url = other.url; formatDescription = other.formatDescription
        let r = region?.clamped(width: other.width, height: other.height)
        self.region = r
        (stats, maskedStats) = ChannelSource.computeStats(image: other.image, mask: other.mask, maskLevel: other.maskLevel, region: r)
        analysisMillis = Date().timeIntervalSince(t0) * 1000
    }

    /// Same source with a different channel kind (no recomputation).
    public init(_ other: ChannelSource, kind: ChannelKind) {
        self.kind = kind; pyramid = other.pyramid; mask = other.mask; maskLevel = other.maskLevel
        fileName = other.fileName; url = other.url; formatDescription = other.formatDescription
        region = other.region; stats = other.stats; maskedStats = other.maskedStats; analysisMillis = other.analysisMillis
    }

    static func computeStats(image: PlanarImage, mask: PlanarImage, maskLevel: Int, region: PixelRect?) -> (ChannelStats, ChannelStats) {
        var opts = StatsOptions()
        opts.region = region
        let stats = Statistics.compute(image: image, options: opts)
        opts.mask = mask
        opts.maskLevel = maskLevel
        let masked = Statistics.compute(image: image, options: opts)
        return (stats, masked)
    }

    public var normalizationInput: NormalizationInput {
        NormalizationInput(kind: kind, image: image, stats: stats, maskedStats: maskedStats, mask: mask, maskLevel: maskLevel, region: region)
    }

    /// A default auto-stretched grayscale version of a pyramid level (for thumbnails).
    /// Auto-stretched grayscale preview of the channel, limited to the source's region (crop) when one is set.
    public func displayPlane(maxPixels: Int, settings: StretchSettings = StretchSettings()) -> PlanarImage {
        let plane: PlanarImage
        if let region, region != PixelRect.full(width: width, height: height) {
            // Pick the level at which the region (not the whole image) fits the budget.
            var level = 0
            while level + 1 < pyramid.count && region.scaled(level: level).count > maxPixels { level += 1 }
            let r = region.scaled(level: level)
            plane = pyramid.level(level).cropped(x: r.x, y: r.y, width: r.width, height: r.height)
        } else {
            plane = pyramid.level(pyramid.levelIndex(maxPixels: maxPixels)).copy()
        }
        Stretch.resolve(settings, stats: stats).apply(to: plane)
        return plane
    }
}
