import Foundation

/// Guesses which channel slot a file belongs to from FITS metadata and/or the file name.
public enum ChannelDetection {

    /// Try the FITS `FILTER` keyword first, then the file name.
    public static func detect(fileName: String, filterKeyword: String?) -> ChannelKind? {
        if let f = filterKeyword, let k = detect(filterName: f) { return k }
        return detect(fileName: fileName)
    }

    /// Match a filter name such as "Hydrogen-alpha", "OIII", "S2", "Red", "L".
    public static func detect(filterName: String) -> ChannelKind? {
        let tokens = tokenize(filterName)
        return match(tokens: tokens, strict: false)
    }

    /// Match a file name such as "Soul_Core-Oxygen_III-session_1-lpc.fits".
    public static func detect(fileName: String) -> ChannelKind? {
        var name = (fileName as NSString).lastPathComponent
        if let dot = name.lastIndex(of: ".") { name = String(name[..<dot]) }
        let tokens = tokenize(name)
        return match(tokens: tokens, strict: true)
    }

    static func tokenize(_ s: String) -> [String] {
        let lowered = s.lowercased()
            .replacingOccurrences(of: "α", with: "alpha")
            .replacingOccurrences(of: "β", with: "beta")
        return lowered.split(whereSeparator: { !$0.isLetter && !$0.isNumber }).map(String.init)
    }

    private static func match(tokens: [String], strict: Bool) -> ChannelKind? {
        let joined = tokens.joined(separator: " ")
        // Multi-token names first.
        if joined.contains("hydrogen alpha") || joined.contains("h alpha") || joined.contains("h a ") || joined.hasSuffix("h a") { return .ha }
        if joined.contains("hydrogen beta") || joined.contains("h beta") { return .hb }
        if joined.contains("oxygen iii") || joined.contains("oxygen 3") || joined.contains("o iii") || joined.contains("o 3") { return .oiii }
        if joined.contains("sulphur ii") || joined.contains("sulfur ii") || joined.contains("sulphur 2") || joined.contains("sulfur 2") || joined.contains("s ii") || joined.contains("s 2") { return .sii }

        let strong: [(Set<String>, ChannelKind)] = [
            (["ha", "halpha", "hydrogen", "h_alpha", "hα", "656", "656nm", "h1"], .ha),
            (["oiii", "o3", "oxygen", "o_iii", "501", "500", "oxy"], .oiii),
            (["sii", "s2", "sulphur", "sulfur", "s_ii", "672", "sul"], .sii),
            (["hb", "hbeta", "486"], .hb),
            (["red"], .red), (["green"], .green), (["blue"], .blue),
            (["lum", "luminance", "clear", "luminosity"], .luminance),
        ]
        for t in tokens {
            for (names, kind) in strong where names.contains(t) { return kind }
        }
        // Single-letter tokens are only trusted when they stand alone (e.g. "M31_R.fits").
        let weak: [String: ChannelKind] = ["r": .red, "g": .green, "b": .blue, "l": .luminance, "h": .ha, "o": .oiii, "s": .sii]
        for t in tokens.reversed() { // suffix tokens are the most informative
            if let k = weak[t] { return k }
        }
        if !strict {
            for t in tokens { if let k = weak[t] { return k } }
        }
        return nil
    }
}
