import Foundation

/// Settings are decoded leniently so that projects saved by older (or newer) versions
/// still open: missing or unreadable keys fall back to the defaults.
struct AnyKey: CodingKey {
    var stringValue: String
    var intValue: Int? { nil }
    init(stringValue: String) { self.stringValue = stringValue }
    init?(intValue: Int) { return nil }
    init(_ s: String) { stringValue = s }
}

extension KeyedDecodingContainer where Key == AnyKey {
    func value<T: Decodable>(_ key: String, _ fallback: T) -> T {
        (try? decodeIfPresent(T.self, forKey: AnyKey(key))) ?? fallback
    }

    func optionalValue<T: Decodable>(_ key: String, _ fallback: T?) -> T? {
        let k = AnyKey(key)
        guard contains(k) else { return fallback }
        if (try? decodeNil(forKey: k)) == true { return nil }
        return (try? decode(T.self, forKey: k)) ?? fallback
    }
}

extension ChannelAdjust {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = ChannelAdjust()
        enabled = c.value("enabled", d.enabled); gain = c.value("gain", d.gain); offsetSigma = c.value("offsetSigma", d.offsetSigma)
        stretchBoost = c.value("stretchBoost", d.stretchBoost); weight = c.value("weight", d.weight)
        blackPoint = c.value("blackPoint", d.blackPoint); gamma = c.value("gamma", d.gamma)
    }
}

extension NormalizationSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = NormalizationSettings()
        method = c.value("method", d.method); reference = c.optionalValue("reference", d.reference)
        useStarMask = c.value("useStarMask", d.useStarMask); signalPercentile = c.value("signalPercentile", d.signalPercentile)
        fitRejectSigma = c.value("fitRejectSigma", d.fitRejectSigma); fitIterations = c.value("fitIterations", d.fitIterations)
        histogramClip = c.value("histogramClip", d.histogramClip)
    }
}

extension StretchSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = StretchSettings()
        method = c.value("method", d.method); targetBackground = c.value("targetBackground", d.targetBackground)
        shadowsClipping = c.value("shadowsClipping", d.shadowsClipping); highlightsPercentile = c.value("highlightsPercentile", d.highlightsPercentile)
        linked = c.value("linked", d.linked)
        mtfShadows = c.value("mtfShadows", d.mtfShadows); mtfMidtones = c.value("mtfMidtones", d.mtfMidtones); mtfHighlights = c.value("mtfHighlights", d.mtfHighlights)
        arcsinhStretch = c.value("arcsinhStretch", d.arcsinhStretch)
        ghsLogD = c.value("ghsLogD", d.ghsLogD); ghsB = c.value("ghsB", d.ghsB); ghsSP = c.value("ghsSP", d.ghsSP); ghsLP = c.value("ghsLP", d.ghsLP); ghsHP = c.value("ghsHP", d.ghsHP)
        logK = c.value("logK", d.logK); gamma = c.value("gamma", d.gamma)
    }
}

extension LuminanceSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = LuminanceSettings()
        enabled = c.value("enabled", d.enabled); preset = c.value("preset", d.preset); customExpression = c.value("customExpression", d.customExpression)
        amount = c.value("amount", d.amount); method = c.value("method", d.method)
        // Projects from the first version stored a single optional channel.
        if c.contains(AnyKey("source")) {
            if let k: ChannelKind = c.optionalValue("source", nil) {
                enabled = true
                preset = k == .luminance ? .lum : (k == .ha ? .ha : .custom)
                if preset == .custom { customExpression = k.pixelMathName }
            } else {
                enabled = false
            }
        }
    }
}

extension PostSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = PostSettings()
        saturation = c.value("saturation", d.saturation); scnrGreen = c.value("scnrGreen", d.scnrGreen); hueShift = c.value("hueShift", d.hueShift)
        gamma = c.value("gamma", d.gamma); blackPoint = c.value("blackPoint", d.blackPoint); whitePoint = c.value("whitePoint", d.whitePoint)
        vibrance = c.value("vibrance", d.vibrance)
    }
}

extension StarsBlendSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = StarsBlendSettings()
        enabled = c.value("enabled", d.enabled); stretch = c.value("stretch", d.stretch); amount = c.value("amount", d.amount)
        threshold = c.value("threshold", d.threshold); softness = c.value("softness", d.softness); mode = c.value("mode", d.mode)
        useMask = c.value("useMask", d.useMask); saturation = c.value("saturation", d.saturation)
    }
}

extension PixelMathProgram {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        red = c.value("red", "0"); green = c.value("green", "0"); blue = c.value("blue", "0"); luminance = c.value("luminance", "")
    }
}

extension CombineSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = CombineSettings()
        mode = c.value("mode", d.mode); paletteID = c.value("paletteID", d.paletteID); matrix = c.value("matrix", d.matrix)
        pixelMath = c.value("pixelMath", d.pixelMath); channels = c.value("channels", d.channels)
        monoChannels = c.optionalValue("monoChannels", d.monoChannels)
        normalization = c.value("normalization", d.normalization); stretch = c.value("stretch", d.stretch)
        stretchStage = c.value("stretchStage", d.stretchStage); luminance = c.value("luminance", d.luminance)
        post = c.value("post", d.post); stars = c.value("stars", d.stars)
        colorBands = c.value("colorBands", d.colorBands); detail = c.value("detail", d.detail)
    }
}

extension ColorBandAdjust {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        hue = c.value("hue", 0); saturation = c.value("saturation", 0); lightness = c.value("lightness", 0)
    }
}

extension ColorBandsSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        bands = c.value("bands", [:])
    }
}

extension DetailSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        clarity = c.value("clarity", 0); dehaze = c.value("dehaze", 0)
    }
}

extension DualBandSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = DualBandSettings()
        preset = c.value("preset", d.preset); firstWeights = c.value("firstWeights", d.firstWeights); secondWeights = c.value("secondWeights", d.secondWeights)
        crosstalkSecond = c.value("crosstalkSecond", d.crosstalkSecond); crosstalkFirst = c.value("crosstalkFirst", d.crosstalkFirst)
        customFirst = c.value("customFirst", d.customFirst); customSecond = c.value("customSecond", d.customSecond)
        if firstWeights.count != 3 { firstWeights = d.firstWeights }
        if secondWeights.count != 3 { secondWeights = d.secondWeights }
    }
}

extension ProjectDocument {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = ProjectDocument()
        version = c.value("version", d.version); channels = c.value("channels", d.channels); settings = c.value("settings", d.settings)
        starsFile = c.optionalValue("starsFile", d.starsFile); crop = c.optionalValue("crop", d.crop)
        presets = c.value("presets", d.presets)
        rotation = ((c.value("rotation", d.rotation) % 4) + 4) % 4
    }
}

extension SettingsPreset {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        id = c.value("id", UUID()); name = c.value("name", "Preset"); settings = c.value("settings", CombineSettings())
        thumbnailPNG = c.optionalValue("thumbnailPNG", nil); date = c.value("date", Date())
    }
}
