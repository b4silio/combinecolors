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

    public var width: Int { pyramid.base.width }
    public var height: Int { pyramid.base.height }
    public var image: PlanarImage { pyramid.base }

    public init(kind: ChannelKind, image: PlanarImage, fileName: String, url: URL? = nil, formatDescription: String = "",
                starMaskOptions: StarMask.Options = StarMask.Options(), maskMaxPixels: Int = 3_000_000) {
        let t0 = Date()
        self.kind = kind
        self.fileName = fileName
        self.url = url
        self.formatDescription = formatDescription
        pyramid = Pyramid(base: image)
        maskLevel = pyramid.levelIndex(maxPixels: maskMaxPixels)
        mask = StarMask.build(image: pyramid.level(maskLevel), options: starMaskOptions)
        stats = Statistics.compute(image: image)
        var opts = StatsOptions()
        opts.mask = mask
        opts.maskLevel = maskLevel
        maskedStats = Statistics.compute(image: image, options: opts)
        analysisMillis = Date().timeIntervalSince(t0) * 1000
    }

    public var normalizationInput: NormalizationInput {
        NormalizationInput(kind: kind, image: image, stats: stats, maskedStats: maskedStats, mask: mask, maskLevel: maskLevel)
    }

    /// A default auto-stretched grayscale version of a pyramid level (for thumbnails).
    public func displayPlane(maxPixels: Int, settings: StretchSettings = StretchSettings()) -> PlanarImage {
        let lvl = pyramid.level(pyramid.levelIndex(maxPixels: maxPixels)).copy()
        let r = Stretch.resolve(settings, stats: stats)
        r.apply(to: lvl)
        return lvl
    }
}
