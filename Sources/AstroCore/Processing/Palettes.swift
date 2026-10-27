import Foundation

/// Linear mixing matrix: for every channel, its weights into R, G, B.
public struct PaletteMatrix: Codable, Equatable, Hashable {
    public var weights: [ChannelKind: [Float]]

    public init(_ weights: [ChannelKind: [Float]]) {
        self.weights = weights.mapValues { w in
            var v = w; while v.count < 3 { v.append(0) }; return Array(v.prefix(3))
        }
    }

    public static let empty = PaletteMatrix([:])

    public var requiredChannels: Set<ChannelKind> {
        Set(weights.filter { $0.value.contains { $0 != 0 } }.keys)
    }

    public func weight(_ kind: ChannelKind, _ output: Int) -> Float {
        weights[kind]?[output] ?? 0
    }

    public mutating func set(_ kind: ChannelKind, _ output: Int, _ value: Float) {
        var w = weights[kind] ?? [0, 0, 0]
        w[output] = value
        weights[kind] = w
    }

    /// Channels that contribute anywhere, in canonical order.
    public var orderedChannels: [ChannelKind] {
        ChannelKind.allCases.filter { weights[$0] != nil }
    }
}

public enum PaletteKind: Equatable, Hashable {
    case matrix(PaletteMatrix)
    case pixelMath(PixelMathProgram)
}

public struct Palette: Identifiable, Equatable, Hashable {
    public let id: String
    public let name: String
    public let group: String
    public let kind: PaletteKind
    public let description: String
    /// Suggested luminance source (nil = none).
    public let luminance: ChannelKind?
    /// Mono check palettes must show the raw channel: no luminance injection.
    public var allowsLuminance: Bool { group != "Mono" }

    public init(id: String, name: String, group: String, kind: PaletteKind, description: String, luminance: ChannelKind? = nil) {
        self.id = id; self.name = name; self.group = group; self.kind = kind; self.description = description; self.luminance = luminance
    }

    public var requiredChannels: Set<ChannelKind> {
        switch kind {
        case .matrix(let m): return m.requiredChannels
        case .pixelMath(let p): return (try? p.compile().requiredChannels) ?? []
        }
    }

    public var isPixelMath: Bool { if case .pixelMath = kind { return true }; return false }
}

public enum Palettes {
    static func m(_ r: [ChannelKind: Float], _ g: [ChannelKind: Float], _ b: [ChannelKind: Float]) -> PaletteMatrix {
        var w: [ChannelKind: [Float]] = [:]
        for k in Set(r.keys).union(g.keys).union(b.keys) {
            w[k] = [r[k] ?? 0, g[k] ?? 0, b[k] ?? 0]
        }
        return PaletteMatrix(w)
    }

    public static let groups = ["Narrowband", "Blends", "Dynamic", "Broadband", "Mono"]

    public static let all: [Palette] = [
        // --- Classic narrowband permutations ---
        Palette(id: "sho", name: "SHO (Hubble)", group: "Narrowband", kind: .matrix(m([.sii: 1], [.ha: 1], [.oiii: 1])),
                description: "SII → red, Hα → green, OIII → blue. The Hubble palette; usually needs green removal (SCNR) or a hue shift to reach the classic gold/blue look."),
        Palette(id: "hoo", name: "HOO", group: "Narrowband", kind: .matrix(m([.ha: 1], [.oiii: 1], [.oiii: 1])),
                description: "Hα → red, OIII → green and blue. Natural-looking bicolour palette."),
        Palette(id: "hso", name: "HSO", group: "Narrowband", kind: .matrix(m([.ha: 1], [.sii: 1], [.oiii: 1])),
                description: "Hα → red, SII → green, OIII → blue."),
        Palette(id: "hos", name: "HOS (CFHT)", group: "Narrowband", kind: .matrix(m([.ha: 1], [.oiii: 1], [.sii: 1])),
                description: "Hα → red, OIII → green, SII → blue. Canada-France-Hawaii Telescope palette."),
        Palette(id: "ohs", name: "OHS", group: "Narrowband", kind: .matrix(m([.oiii: 1], [.ha: 1], [.sii: 1])),
                description: "OIII → red, Hα → green, SII → blue."),
        Palette(id: "osh", name: "OSH", group: "Narrowband", kind: .matrix(m([.oiii: 1], [.sii: 1], [.ha: 1])),
                description: "OIII → red, SII → green, Hα → blue."),
        Palette(id: "soh", name: "SOH", group: "Narrowband", kind: .matrix(m([.sii: 1], [.oiii: 1], [.ha: 1])),
                description: "SII → red, OIII → green, Hα → blue."),
        Palette(id: "hho", name: "HHO", group: "Narrowband", kind: .matrix(m([.ha: 1], [.ha: 1], [.oiii: 1])),
                description: "Hα → red and green, OIII → blue. Yellow/blue bicolour."),
        Palette(id: "sho_natural", name: "Natural (line colours)", group: "Narrowband",
                kind: .matrix(m([.ha: 0.85, .sii: 0.65], [.ha: 0.10, .oiii: 0.80], [.oiii: 0.60])),
                description: "Each line is tinted with the approximate colour of its wavelength (Hα 656 nm, SII 672 nm, OIII 501 nm). Add Hβ in the matrix editor if you have it."),

        // --- Blends ---
        Palette(id: "sho_gold", name: "SHO Golden", group: "Blends",
                kind: .matrix(m([.sii: 0.55, .ha: 0.45], [.ha: 0.75, .oiii: 0.25], [.oiii: 1])),
                description: "Hα feeds both red and green for golden nebulosity, SII warms the reds, OIII stays blue/teal. Close to the classic processed SHO look without a hue shift."),
        Palette(id: "sho_soft", name: "SHO Soft", group: "Blends",
                kind: .matrix(m([.sii: 0.7, .ha: 0.3], [.ha: 0.7, .oiii: 0.3], [.oiii: 0.8, .ha: 0.2])),
                description: "Every output gets a little of its neighbours. Less saturated, more pastel version of SHO."),
        Palette(id: "hoo_s", name: "HOO + SII in red", group: "Blends",
                kind: .matrix(m([.ha: 0.7, .sii: 0.3], [.oiii: 1], [.oiii: 1])),
                description: "HOO with 30 % SII mixed into the red channel."),
        Palette(id: "hoo_warm", name: "HOO Warm", group: "Blends",
                kind: .matrix(m([.ha: 1], [.oiii: 0.75, .ha: 0.25], [.oiii: 1])),
                description: "HOO with a touch of Hα in green: oranges instead of pure reds."),
        Palette(id: "sho_hgreen", name: "SHO (green reduced)", group: "Blends",
                kind: .matrix(m([.sii: 0.8, .ha: 0.2], [.ha: 0.55, .oiii: 0.45], [.oiii: 1])),
                description: "SHO with the Hα green diluted by OIII — a matrix-only approximation of SCNR."),

        // --- Dynamic (pixel math) ---
        Palette(id: "foraxx_sho", name: "Foraxx SHO", group: "Dynamic",
                kind: .pixelMath(PixelMathProgram(
                    red: "(OIII^~OIII)*SII + ~(OIII^~OIII)*Ha",
                    green: "((OIII*Ha)^~(OIII*Ha))*Ha + ~((OIII*Ha)^~(OIII*Ha))*OIII",
                    blue: "OIII")),
                description: "Foraxx dynamic palette (Paulyman Astro): the mix of SII/Hα in red and Hα/OIII in green depends on the local OIII strength. Expects stretched [0,1] channels."),
        Palette(id: "foraxx_hoo", name: "Foraxx HOO", group: "Dynamic",
                kind: .pixelMath(PixelMathProgram(
                    red: "Ha",
                    green: "((OIII*Ha)^~(OIII*Ha))*Ha + ~((OIII*Ha)^~(OIII*Ha))*OIII",
                    blue: "OIII")),
                description: "Foraxx bicolour variant: green is a dynamic mix of Hα and OIII."),
        Palette(id: "dyn_sho_ratio", name: "SHO Line-ratio", group: "Dynamic",
                kind: .pixelMath(PixelMathProgram(
                    red: "max(SII,Ha,OIII) * 3*SII/(SII+Ha+OIII+0.001)",
                    green: "max(SII,Ha,OIII) * 3*Ha/(SII+Ha+OIII+0.001)",
                    blue: "max(SII,Ha,OIII) * 3*OIII/(SII+Ha+OIII+0.001)")),
                description: "Hue is given by the proportion of each line, brightness by the strongest line. Very saturated, emphasises where each element dominates."),
        Palette(id: "dyn_sho_oprotect", name: "SHO OIII-protected", group: "Dynamic",
                kind: .pixelMath(PixelMathProgram(
                    red: "lerp(0.5*SII+0.5*Ha, SII, OIII)",
                    green: "lerp(Ha, 0.5*Ha+0.5*OIII, OIII)",
                    blue: "OIII")),
                description: "Where OIII is strong the palette tends to SHO (blue/teal kept), where it is weak Hα dominates the warm tones."),
        Palette(id: "dyn_hoo_boost", name: "HOO OIII-boost", group: "Dynamic",
                kind: .pixelMath(PixelMathProgram(
                    red: "Ha",
                    green: "OIII^0.8",
                    blue: "OIII^0.7")),
                description: "HOO with gamma-lifted OIII to bring out faint oxygen."),

        // --- Broadband ---
        Palette(id: "rgb", name: "RGB", group: "Broadband", kind: .matrix(m([.red: 1], [.green: 1], [.blue: 1])),
                description: "Plain RGB combination."),
        Palette(id: "lrgb", name: "LRGB", group: "Broadband", kind: .matrix(m([.red: 1], [.green: 1], [.blue: 1])),
                description: "RGB colour with the luminance channel injected.", luminance: .luminance),
        Palette(id: "hargb", name: "HαRGB", group: "Broadband", kind: .matrix(m([.red: 0.5, .ha: 0.5], [.green: 1], [.blue: 1])),
                description: "RGB with Hα blended into red.", luminance: nil),
        Palette(id: "ha_lrgb", name: "Hα as luminance + RGB", group: "Broadband", kind: .matrix(m([.red: 1], [.green: 1], [.blue: 1])),
                description: "RGB colour with Hα used as luminance.", luminance: .ha),
        Palette(id: "haoiiirgb", name: "Hα/OIII + RGB", group: "Broadband", kind: .matrix(m([.red: 0.5, .ha: 0.5], [.green: 0.5, .oiii: 0.5], [.blue: 0.5, .oiii: 0.5])),
                description: "Hα into red and OIII into green/blue, blended 50/50 with RGB."),

        // --- Mono checks ---
        Palette(id: "mono_ha", name: "Hα only", group: "Mono", kind: .matrix(m([.ha: 1], [.ha: 1], [.ha: 1])), description: "Grayscale Hα."),
        Palette(id: "mono_oiii", name: "OIII only", group: "Mono", kind: .matrix(m([.oiii: 1], [.oiii: 1], [.oiii: 1])), description: "Grayscale OIII."),
        Palette(id: "mono_sii", name: "SII only", group: "Mono", kind: .matrix(m([.sii: 1], [.sii: 1], [.sii: 1])), description: "Grayscale SII."),
        Palette(id: "mono_l", name: "Luminance only", group: "Mono", kind: .matrix(m([.luminance: 1], [.luminance: 1], [.luminance: 1])), description: "Grayscale luminance."),
    ]

    public static func palette(id: String) -> Palette? { all.first { $0.id == id } }

    /// Palettes whose required channels are all available.
    public static func available(for channels: Set<ChannelKind>) -> [Palette] {
        all.filter { !$0.requiredChannels.isEmpty && $0.requiredChannels.isSubset(of: channels) }
    }
}
