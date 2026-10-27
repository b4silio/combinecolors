import Foundation

/// The channel slots the application understands.
public enum ChannelKind: String, Codable, CaseIterable, Identifiable, Hashable, Sendable, CodingKeyRepresentable {
    case luminance = "L"
    case red = "R"
    case green = "G"
    case blue = "B"
    case ha = "Ha"
    case oiii = "OIII"
    case sii = "SII"
    case hb = "Hb"

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .luminance: return "Luminance"
        case .red: return "Red"
        case .green: return "Green"
        case .blue: return "Blue"
        case .ha: return "Hα"
        case .oiii: return "OIII"
        case .sii: return "SII"
        case .hb: return "Hβ"
        }
    }

    public var shortName: String {
        switch self {
        case .ha: return "Hα"
        case .oiii: return "OIII"
        case .sii: return "SII"
        case .hb: return "Hβ"
        default: return rawValue
        }
    }

    /// Identifiers accepted by Pixel Math for this channel (lower-case).
    public var pixelMathIdentifiers: [String] {
        switch self {
        case .luminance: return ["l", "lum", "luminance"]
        case .red: return ["r", "red"]
        case .green: return ["g", "green"]
        case .blue: return ["b", "blue"]
        case .ha: return ["ha", "h", "halpha", "h_alpha", "hydrogen"]
        case .oiii: return ["oiii", "o3", "o", "oxygen"]
        case .sii: return ["sii", "s2", "s", "sulphur", "sulfur"]
        case .hb: return ["hb", "hbeta", "h_beta"]
        }
    }

    /// The canonical Pixel Math identifier.
    public var pixelMathName: String {
        switch self {
        case .luminance: return "L"
        case .red: return "R"
        case .green: return "G"
        case .blue: return "B"
        case .ha: return "Ha"
        case .oiii: return "OIII"
        case .sii: return "SII"
        case .hb: return "Hb"
        }
    }

    public static let narrowband: [ChannelKind] = [.ha, .oiii, .sii, .hb]
    public static let broadband: [ChannelKind] = [.luminance, .red, .green, .blue]

    public var isNarrowband: Bool { ChannelKind.narrowband.contains(self) }

    /// Approximate emission wavelength in nm (used for "natural" tinting).
    public var wavelengthNm: Double? {
        switch self {
        case .ha: return 656.3
        case .oiii: return 500.7
        case .sii: return 672.4
        case .hb: return 486.1
        default: return nil
        }
    }

    public static func fromPixelMathIdentifier(_ s: String) -> ChannelKind? {
        let l = s.lowercased()
        return ChannelKind.allCases.first { $0.pixelMathIdentifiers.contains(l) }
    }
}
