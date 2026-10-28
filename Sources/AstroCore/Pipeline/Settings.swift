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
    /// Weighted average of channels (see `CombineSettings.monoChannels`).
    case monochrome
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

/// Ready-made luminance recipes. Weighted presets drop missing channels and renormalise.
public enum LuminancePreset: String, Codable, CaseIterable, Identifiable {
    case ha, lum, haMostly, haOiii, balanced, lumHa, maxLines, meanLines, custom

    public var id: String { rawValue }

    public var displayName: String {
        switch self {
        case .ha: return "Hα"
        case .lum: return "Luminance (L)"
        case .haMostly: return "0.8 Hα + 0.1 OIII + 0.1 SII"
        case .haOiii: return "0.7 Hα + 0.3 OIII (HOO)"
        case .balanced: return "0.5 Hα + 0.25 OIII + 0.25 SII"
        case .lumHa: return "0.5 L + 0.5 Hα"
        case .maxLines: return "Max of narrowband lines"
        case .meanLines: return "Mean of narrowband lines"
        case .custom: return "Custom expression…"
        }
    }

    /// Weights per channel for the weighted presets (nil for the others).
    public var weights: [ChannelKind: Float]? {
        switch self {
        case .ha: return [.ha: 1]
        case .lum: return [.luminance: 1]
        case .haMostly: return [.ha: 0.8, .oiii: 0.1, .sii: 0.1]
        case .haOiii: return [.ha: 0.7, .oiii: 0.3]
        case .balanced: return [.ha: 0.5, .oiii: 0.25, .sii: 0.25]
        case .lumHa: return [.luminance: 0.5, .ha: 0.5]
        default: return nil
        }
    }

    /// Pixel Math expression for the available channels; nil when nothing usable is loaded.
    public func expression(available: Set<ChannelKind>, custom: String) -> String? {
        switch self {
        case .custom:
            let t = custom.trimmingCharacters(in: .whitespacesAndNewlines)
            return t.isEmpty ? nil : t
        case .maxLines, .meanLines:
            let lines = ChannelKind.narrowband.filter { available.contains($0) }
            guard !lines.isEmpty else { return nil }
            if lines.count == 1 { return lines[0].pixelMathName }
            return "\(self == .maxLines ? "max" : "mean")(\(lines.map(\.pixelMathName).joined(separator: ", ")))"
        default:
            guard let w = weights else { return nil }
            let present = ChannelKind.allCases.filter { w[$0] != nil && available.contains($0) }
            guard !present.isEmpty else { return nil }
            let total = present.map { w[$0]! }.reduce(0, +)
            if present.count == 1 { return present[0].pixelMathName }
            return present.map { k -> String in
                let v = w[k]! / total
                return String(format: "%.3g", v) + "*" + k.pixelMathName
            }.joined(separator: " + ")
        }
    }
}

public struct LuminanceSettings: Codable, Equatable, Hashable {
    /// Inject a luminance into the colour result.
    public var enabled: Bool = true
    public var preset: LuminancePreset = .ha
    /// Expression used by the `.custom` preset (stretched channels in [0,1] for the pre-combine stage).
    public var customExpression: String = "0.8*Ha + 0.1*OIII + 0.1*SII"
    public var amount: Float = 1
    public var method: LuminanceMethod = .lab
    public init() {}

    /// The expression that will actually be used for the loaded channels
    /// (falls back from Hα to L to nothing when channels are missing).
    public func resolvedExpression(available: Set<ChannelKind>) -> String? {
        guard enabled else { return nil }
        if let e = preset.expression(available: available, custom: customExpression) { return e }
        if preset != .custom {
            for fallback in [LuminancePreset.ha, .lum] where fallback != preset {
                if let e = fallback.expression(available: available, custom: customExpression) { return e }
            }
        }
        return nil
    }

    /// Channels referenced by the resolved expression.
    public func requiredChannels(available: Set<ChannelKind>) -> Set<ChannelKind> {
        guard let e = resolvedExpression(available: available), let expr = try? PixelMath.parse(e) else { return [] }
        return Set(expr.variables.compactMap { ChannelKind.fromPixelMathIdentifier($0) })
    }
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

public enum StarsStretchMode: String, Codable, CaseIterable, Identifiable {
    case auto, none, autoSTF
    public var id: String { rawValue }
    public var displayName: String {
        switch self {
        case .auto: return "Automatic"
        case .none: return "Already stretched"
        case .autoSTF: return "Linear (auto-stretch)"
        }
    }
}

public struct StarsBlendSettings: Codable, Equatable, Hashable {
    public var enabled: Bool = false
    /// Whether the RGB star channels need stretching before blending.
    public var stretch: StarsStretchMode = .auto
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
    /// Channels used by the monochrome mode; nil = every loaded channel.
    public var monoChannels: Set<ChannelKind>? = nil
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
        case .monochrome: mode = .monochrome; monoChannels = nil
        }
        if let l = palette.luminance {
            luminance.enabled = true
            luminance.preset = l == .luminance ? .lum : (l == .ha ? .ha : .custom)
            if luminance.preset == .custom { luminance.customExpression = l.pixelMathName }
        }
    }

    /// Channels used by the current combination (excluding luminance / stars).
    public var requiredChannels: Set<ChannelKind> {
        switch mode {
        case .matrix: return matrix.requiredChannels
        case .pixelMath: return (try? pixelMath.compile().requiredChannels) ?? []
        case .monochrome: return monoChannels ?? []
        }
    }

    /// Luminance injection is skipped for the mono check palettes (they would all look alike).
    public var luminanceApplies: Bool {
        luminance.enabled && mode != .monochrome && (Palettes.palette(id: paletteID)?.allowsLuminance ?? true)
    }

    /// Channels averaged by the monochrome mode for the loaded set.
    public func monochromeChannels(available: Set<ChannelKind>) -> Set<ChannelKind> {
        (monoChannels ?? available).intersection(available)
    }

    /// Every channel the pipeline will read, given what is loaded.
    public func allChannels(available: Set<ChannelKind>) -> Set<ChannelKind> {
        var s = requiredChannels
        if mode == .monochrome { s.formUnion(monochromeChannels(available: available)) }
        if luminanceApplies { s.formUnion(luminance.requiredChannels(available: available)) }
        if stars.enabled { s.formUnion([.red, .green, .blue]) }
        return s
    }

    /// Settings that influence the normalisation context (used for cache keys).
    public var normalizationKey: NormalizationSettings { normalization }
}
