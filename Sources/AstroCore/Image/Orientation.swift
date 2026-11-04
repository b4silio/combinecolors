import Foundation

/// How the loaded channels are turned: an optional horizontal flip, then `rotation` quarter turns
/// clockwise, then a fine rotation by `angle` degrees about the centre on the same canvas
/// (`FineRotation`; positive = clockwise on screen). Composition stays in this form, so any
/// sequence of rotate / flip operations can be replayed on channels loaded later.
public struct Orientation: Codable, Equatable, Hashable {
    /// Quarter turns clockwise applied after the flip (0…3).
    public var rotation: Int = 0
    /// Horizontal mirror applied before the rotation.
    public var flipped: Bool = false
    /// Fine rotation in degrees (−45…45), applied last.
    public var angle: Double = 0

    public init(rotation: Int = 0, flipped: Bool = false, angle: Double = 0) {
        self.rotation = ((rotation % 4) + 4) % 4
        self.flipped = flipped
        self.angle = angle
    }

    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        self.init(rotation: (try? c.decodeIfPresent(Int.self, forKey: .rotation)) ?? 0,
                  flipped: (try? c.decodeIfPresent(Bool.self, forKey: .flipped)) ?? false,
                  angle: (try? c.decodeIfPresent(Double.self, forKey: .angle)) ?? 0)
    }

    public static let identity = Orientation()
    public var isIdentity: Bool { rotation == 0 && !flipped && angle == 0 }
    /// Whether width and height are swapped.
    public var swapsAxes: Bool { rotation % 2 == 1 }
    /// The same without the fine rotation (what the quarter turns and the flip alone do).
    public var withoutAngle: Orientation { Orientation(rotation: rotation, flipped: flipped) }

    public enum Op: String, Codable, CaseIterable {
        case rotateClockwise, flipHorizontal, flipVertical
    }

    /// The orientation after applying `op` to an image that already carries this orientation.
    /// With R the clockwise turn and H the mirror: H·R = R⁻¹·H and a vertical flip is R²·H.
    /// The fine rotation commutes with the quarter turns (both turn about the centre) and changes
    /// sign under a mirror.
    public func applying(_ op: Op) -> Orientation {
        switch op {
        case .rotateClockwise: return Orientation(rotation: rotation + 1, flipped: flipped, angle: angle)
        case .flipHorizontal: return Orientation(rotation: -rotation, flipped: !flipped, angle: -angle)
        case .flipVertical: return Orientation(rotation: 2 - rotation, flipped: !flipped, angle: -angle)
        }
    }

    public func apply(to image: PlanarImage) -> PlanarImage {
        var img = flipped ? image.flippedHorizontally() : image
        img = img.rotated90CW(times: rotation)
        if angle != 0 { img = img.rotated(byDegrees: angle) }
        return img
    }

    /// The rectangle after the image (`imageWidth` × `imageHeight`) is flipped and quarter-turned
    /// (the fine rotation is not applied: a turned rectangle is no longer a rectangle).
    public func apply(to rect: PixelRect, imageWidth: Int, imageHeight: Int) -> PixelRect {
        var r = flipped ? rect.flippedHorizontally(imageWidth: imageWidth) : rect
        var w = imageWidth, h = imageHeight
        for _ in 0..<rotation { r = r.rotated90CW(imageWidth: w, imageHeight: h); swap(&w, &h) }
        return r
    }
}

extension PlanarImage {
    /// Applies a single operation (used on the loaded channels; `Orientation` records the result).
    public func applying(_ op: Orientation.Op) -> PlanarImage {
        switch op {
        case .rotateClockwise: return rotated90CW()
        case .flipHorizontal: return flippedHorizontally()
        case .flipVertical: return flippedVertically()
        }
    }
}

extension PixelRect {
    public func applying(_ op: Orientation.Op, imageWidth: Int, imageHeight: Int) -> PixelRect {
        switch op {
        case .rotateClockwise: return rotated90CW(imageWidth: imageWidth, imageHeight: imageHeight)
        case .flipHorizontal: return flippedHorizontally(imageWidth: imageWidth)
        case .flipVertical: return flippedVertically(imageHeight: imageHeight)
        }
    }

    /// Mirrored left-right within an image of `imageWidth`.
    public func flippedHorizontally(imageWidth: Int) -> PixelRect {
        PixelRect(x: imageWidth - (x + width), y: y, width: width, height: height)
    }

    /// Mirrored top-bottom within an image of `imageHeight`.
    public func flippedVertically(imageHeight: Int) -> PixelRect {
        PixelRect(x: x, y: imageHeight - (y + height), width: width, height: height)
    }
}
