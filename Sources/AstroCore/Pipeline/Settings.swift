import Foundation

/// Per-channel user adjustments.
public struct ChannelAdjust: Codable, Equatable, Hashable {
    public var enabled: Bool = true
    /// Linear multiplier applied after normalisation, before any stretch.
    public var gain: Float = 1
    /// Linear pedestal in units of the reference background sigma.
    public var offsetSigma: Float = 0
    /// Change of the auto-stretch target background for this channel (pre-combine stretch).
    public var stretchBoost: Float = 0
    /// Multiplier applied after the stretch (0…2).
    public var weight: Float = 1
    /// Black point subtracted after the stretch (0…0.5), output renormalised.
    public var blackPoint: Float = 0
    /// Gamma applied after the stretch (1 = none).
    public var gamma: Float = 1

    public init() {}

    public var isNeutral: Bool { self == ChannelAdjust() }
}

public enum CombineMode: String, Codable, CaseIterable {
    case matrix
    case pixelMath
}

public enum StretchStage: String, Codable, CaseIterable, Identifiable {
    /// Each channel is stretched on its own, then combined (classic narrowband workflow).
    case preCombine
    /// Channels are combined linearly, then the colour image is stretched.
    case postCombine

    public var id: String { rawValue }
    public var displayName: String {
        switch self {
        case .preCombine: return "Stretch channels, then combine"
        case .postCombine: return "Combine linear, then stretch"
        }
    }
}

public enum LuminanceMethod: String, Codable, CaseIterable, Identifiable {
    case ratio
    case lab
    public var id: String { rawValue }
    public var displayName: String { self == .ratio ? "Luminance ratio (RGB)" : "CIE L*a*b* replacement" }
}

public struct LuminanceSettings: Codable, Equatable, Hashable {
    /// nil = no luminance injection.
    public var source: ChannelKind? = nil
    public var amount: Float = 1
    public var method: LuminanceMethod = .lab
    public init() {}
}

public struct PostSettings: Codable, Equatable, Hashable {
    public var saturation: Float = 1
    public var scnrGreen: Float = 0
    public var hueShift: Float = 0        // degrees
    public var gamma: Float = 1
    public var blackPoint: Float = 0
    public var whitePoint: Float = 1
    public var vibrance: Float = 0       // boosts low-saturation pixels only
    public init() {}
}

public enum StarsBlendMode: String, Codable, CaseIterable, Identifiable {
    case screen, lighten, add
    public var id: String { rawValue }
    public var displayName: String { rawValue.capitalized }
}

public struct StarsBlendSettings: Codable, Equatable, Hashable {
    public var enabled: Bool = false
    public var amount: Float = 1
    /// Luminance threshold above which the RGB image is considered "star".
    public var threshold: Float = 0.15
    public var softness: Float = 0.25
    public var mode: StarsBlendMode = .screen
    /// Use the whole RGB image (no mask) — for star-only images from star removal tools.
    public var useMask: Bool = true
    public var saturation: Float = 1.3
    public init() {}
}

/// Everything that defines a combination. Codable so projects and presets can be saved.
public struct CombineSettings: Codable, Equatable, Hashable {
    public var mode: CombineMode = .matrix
    public var paletteID: String = "sho"
    public var matrix: PaletteMatrix = Palettes.palette(id: "sho").flatMap { if case .matrix(let m) = $0.kind { return m }; return nil } ?? .empty
    public var pixelMath: PixelMathProgram = PixelMathProgram(red: "SII", green: "Ha", blue: "OIII")
    public var channels: [ChannelKind: ChannelAdjust] = [:]
    public var normalization = NormalizationSettings()
    public var stretch = StretchSettings()
    public var stretchStage: StretchStage = .preCombine
    public var luminance = LuminanceSettings()
    public var post = PostSettings()
    public var stars = StarsBlendSettings()

    public init() {}

    public func adjust(_ k: ChannelKind) -> ChannelAdjust { channels[k] ?? ChannelAdjust() }

    public mutating func setAdjust(_ k: ChannelKind, _ a: ChannelAdjust) {
        if a.isNeutral { channels.removeValue(forKey: k) } else { channels[k] = a }
    }

    /// Selects a preset palette, copying its definition so it can be edited.
    public mutating func select(palette: Palette) {
        paletteID = palette.id
        switch palette.kind {
        case .matrix(let m): mode = .matrix; matrix = m
        case .pixelMath(let p): mode = .pixelMath; pixelMath = p
        }
        luminance.source = palette.luminance
    }

    /// Channels used by the current combination (excluding luminance / stars).
    public var requiredChannels: Set<ChannelKind> {
        switch mode {
        case .matrix: return matrix.requiredChannels
        case .pixelMath: return (try? pixelMath.compile().requiredChannels) ?? []
        }
    }

    /// Every channel the pipeline will read.
    public var allChannels: Set<ChannelKind> {
        var s = requiredChannels
        if let l = luminance.source { s.insert(l) }
        if stars.enabled { s.formUnion([.red, .green, .blue]) }
        return s
    }

    /// Settings that influence the normalisation context (used for cache keys).
    public var normalizationKey: NormalizationSettings { normalization }
}
