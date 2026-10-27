import Foundation
import Accelerate

/// Splits a one-shot-colour dual-band image (e.g. Hα/OIII filter) into two emission-line planes.
public struct DualBandSettings: Codable, Equatable {
    public enum Preset: String, Codable, CaseIterable, Identifiable {
        case haOiii, siiHb, custom
        public var id: String { rawValue }
        public var displayName: String {
            switch self {
            case .haOiii: return "Hα / OIII (L-eXtreme, L-eNhance, ALP-T…)"
            case .siiHb: return "SII / Hβ"
            case .custom: return "Custom"
            }
        }
        public var outputs: (ChannelKind, ChannelKind) {
            switch self {
            case .haOiii, .custom: return (.ha, .oiii)
            case .siiHb: return (.sii, .hb)
            }
        }
    }

    public var preset: Preset = .haOiii
    /// Weights of the camera R, G, B channels into the first (red line) output.
    public var firstWeights: [Float] = [1, 0, 0]
    /// Weights into the second (blue/green line) output.
    public var secondWeights: [Float] = [0, 0.5, 0.5]
    /// Fraction of the first line subtracted from the second (filter cross-talk).
    public var crosstalkSecond: Float = 0
    /// Fraction of the second line subtracted from the first.
    public var crosstalkFirst: Float = 0
    /// Custom output kinds for the `.custom` preset.
    public var customFirst: ChannelKind = .ha
    public var customSecond: ChannelKind = .oiii

    public init() {}

    public var outputKinds: (ChannelKind, ChannelKind) {
        preset == .custom ? (customFirst, customSecond) : preset.outputs
    }
}

public enum DualBand {
    /// Returns (first, second) planes.
    public static func split(r: PlanarImage, g: PlanarImage, b: PlanarImage, settings s: DualBandSettings) -> (PlanarImage, PlanarImage) {
        let n = vDSP_Length(r.count)
        func mix(_ w: [Float]) -> PlanarImage {
            let out = PlanarImage(width: r.width, height: r.height)
            var w0 = w[0], w1 = w[1], w2 = w[2]
            vDSP_vsmul(r.pointer, 1, &w0, out.pointer, 1, n)
            vDSP_vsma(g.pointer, 1, &w1, out.pointer, 1, out.pointer, 1, n)
            vDSP_vsma(b.pointer, 1, &w2, out.pointer, 1, out.pointer, 1, n)
            return out
        }
        let first = mix(s.firstWeights), second = mix(s.secondWeights)
        if s.crosstalkSecond != 0 {
            var k = -s.crosstalkSecond
            vDSP_vsma(first.pointer, 1, &k, second.pointer, 1, second.pointer, 1, n)
        }
        if s.crosstalkFirst != 0 {
            var k = -s.crosstalkFirst
            vDSP_vsma(second.pointer, 1, &k, first.pointer, 1, first.pointer, 1, n)
        }
        var lo: Float = 0, hi = Float.greatestFiniteMagnitude
        vDSP_vclip(first.pointer, 1, &lo, &hi, first.pointer, 1, n)
        vDSP_vclip(second.pointer, 1, &lo, &hi, second.pointer, 1, n)
        return (first, second)
    }
}
