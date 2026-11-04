import Foundation

/// An RGB image kept whole (not split into R, G and B channels) and blended into the result as
/// stars: a stars-only image from a star-removal tool, or any RGB frame whose stars are wanted on
/// top of a narrowband combination. Oriented like the channels, same size.
public final class StarsLayer {
    /// Identity of this layer for render caches.
    public let id = UUID()
    /// R, G, B planes (full resolution, oriented).
    public let planes: [PlanarImage]
    public let pyramids: [Pyramid]
    /// Whole-image statistics per plane (stretch decision, peak for the normalisation).
    public let stats: [ChannelStats]
    public let fileName: String
    public let url: URL?
    public let formatDescription: String
    /// Already processed stars (the render of the app's Stars tab): blended as they are, never stretched again.
    public let processed: Bool

    public var width: Int { planes[0].width }
    public var height: Int { planes[0].height }

    public init(planes: [PlanarImage], fileName: String, url: URL? = nil, formatDescription: String = "", processed: Bool = false) {
        precondition(planes.count == 3)
        self.planes = planes
        pyramids = planes.map { Pyramid(base: $0) }
        stats = planes.map { Statistics.compute(image: $0) }
        self.fileName = fileName; self.url = url; self.formatDescription = formatDescription; self.processed = processed
    }

    /// The layer after an orientation operation.
    public func applying(_ op: Orientation.Op) -> StarsLayer {
        StarsLayer(planes: planes.map { $0.applying(op) }, fileName: fileName, url: url, formatDescription: formatDescription, processed: processed)
    }

    /// The planes at a pyramid level, cropped (the request a render needs).
    public func input(level: Int, x: Int, y: Int, width: Int, height: Int) -> StarsInput {
        let lvl = pyramids.map { $0.level(level) }
        let whole = x == 0 && y == 0 && width == lvl[0].width && height == lvl[0].height
        return StarsInput(planes: whole ? lvl : lvl.map { $0.cropped(x: x, y: y, width: width, height: height) }, stats: stats, processed: processed)
    }

    public func input(region: PixelRect?) -> StarsInput {
        guard let r = region else { return StarsInput(planes: planes, stats: stats, processed: processed) }
        return StarsInput(planes: planes.map { $0.cropped(x: r.x, y: r.y, width: r.width, height: r.height) }, stats: stats, processed: processed)
    }
}

/// The RGB star planes a render blends (a region of the stars layer at the render level) with the
/// whole-image statistics of the layer.
public struct StarsInput {
    public var planes: [PlanarImage]
    public var stats: [ChannelStats]
    /// Processed stars (see `StarsLayer.processed`): no stretch.
    public var processed: Bool
    public init(planes: [PlanarImage], stats: [ChannelStats], processed: Bool = false) { self.planes = planes; self.stats = stats; self.processed = processed }
}
