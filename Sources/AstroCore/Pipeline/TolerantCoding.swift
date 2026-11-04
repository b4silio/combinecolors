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
        // The absolute manual parameters of before 1.16 (mtfMidtones, arcsinhStretch, ghsLogD, ghsSP…, logK)
        // have no data-independent equivalent: such projects get the relative defaults.
        mtfMidtonesSigma = c.value("mtfMidtonesSigma", d.mtfMidtonesSigma)
        arcsinhStrength = c.value("arcsinhStrength", d.arcsinhStrength)
        ghsStrength = c.value("ghsStrength", d.ghsStrength); ghsB = c.value("ghsB", d.ghsB)
        ghsSPPercentile = c.value("ghsSPPercentile", d.ghsSPPercentile); ghsLPPercentile = c.value("ghsLPPercentile", d.ghsLPPercentile); ghsHPPercentile = c.value("ghsHPPercentile", d.ghsHPPercentile)
        logStrength = c.value("logStrength", d.logStrength); gamma = c.value("gamma", d.gamma)
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
        brightness = c.value("brightness", d.brightness); contrast = c.value("contrast", d.contrast); normalize = c.value("normalize", d.normalize)
        stage = c.value("stage", d.stage)
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
        post = c.value("post", d.post); stars = c.value("stars", d.stars); grading = c.value("grading", d.grading)
        colorBands = c.value("colorBands", d.colorBands); detail = c.value("detail", d.detail)
        denoise = c.value("denoise", d.denoise); tone = c.value("tone", d.tone)
    }
}

extension ToneSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = ToneSettings()
        exposure = c.value("exposure", d.exposure); brightness = c.value("brightness", d.brightness); contrast = c.value("contrast", d.contrast)
        highlights = c.value("highlights", d.highlights); shadows = c.value("shadows", d.shadows)
    }
}

extension ColorGradingSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        balance = c.value("balance", [:]); preserveLuminosity = c.value("preserveLuminosity", true)
        selective = c.value("selective", [:]); relative = c.value("relative", true)
    }
}

extension ToneBalance {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        cyanRed = c.value("cyanRed", 0); magentaGreen = c.value("magentaGreen", 0); yellowBlue = c.value("yellowBlue", 0)
    }
}

extension SelectiveAdjust {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        cyan = c.value("cyan", 0); magenta = c.value("magenta", 0); yellow = c.value("yellow", 0); black = c.value("black", 0)
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
        bands = c.value("bands", [:]); softness = c.value("softness", Float(0))
    }
}

extension DetailSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        micro = c.value("micro", 0); texture = c.value("texture", 0); clarity = c.value("clarity", 0); dehaze = c.value("dehaze", 0); lowFrequency = c.value("lowFrequency", 0)
        sharpen = c.value("sharpen", 0); sharpenRadius = c.value("sharpenRadius", 1.5); sharpenMasking = c.value("sharpenMasking", 0)
    }
}

extension DenoiseSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = DenoiseSettings()
        method = c.value("method", d.method); amount = c.value("amount", d.amount); layers = c.value("layers", d.layers)
        search = c.value("search", d.search); radius = c.value("radius", d.radius); blockSearch = c.value("blockSearch", d.blockSearch)
        protectBright = c.value("protectBright", d.protectBright); color = c.value("color", d.color)
        hotPixels = c.value("hotPixels", d.hotPixels)
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
        flipped = c.value("flipped", d.flipped)
        angle = c.value("angle", d.angle)
        dust = c.value("dust", d.dust)
        gradient = c.value("gradient", d.gradient)
        corrections = c.value("corrections", d.corrections)
        starsLayer = c.optionalValue("starsLayer", d.starsLayer)
    }
}

extension LayerDocument {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = LayerDocument()
        channels = c.value("channels", d.channels); settings = c.value("settings", d.settings)
        dust = c.value("dust", d.dust); gradient = c.value("gradient", d.gradient)
        corrections = c.value("corrections", d.corrections); presets = c.value("presets", d.presets)
    }
}

extension GradientGesture {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        shape = c.value("shape", GradientShape.ellipse)
        x = c.value("x", 0); y = c.value("y", 0); x2 = c.value("x2", 0); y2 = c.value("y2", 0)
        a = c.value("a", 1); b = c.value("b", 1); angle = c.value("angle", 0)
    }
}

extension GradientRegion {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        id = c.value("id", UUID()); x = c.value("x", 0); y = c.value("y", 0); a = max(c.value("a", 1), 1); b = max(c.value("b", 1), 1)
    }
}

extension GradientSpec {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = GradientSpec(gesture: .polynomial())
        gesture = c.value("gesture", d.gesture); mode = c.value("mode", d.mode); residual = c.value("residual", d.residual)
        sectors = c.value("sectors", d.sectors); smoothness = c.value("smoothness", d.smoothness); degree = c.value("degree", d.degree)
        refine = c.value("refine", d.refine)
        samples = c.value("samples", d.samples); sampleRadius = c.value("sampleRadius", d.sampleRadius)
        surface = c.value("surface", d.surface); sampleSmoothing = c.value("sampleSmoothing", d.sampleSmoothing); autoSmoothing = c.value("autoSmoothing", d.autoSmoothing)
        gridPerRow = c.value("gridPerRow", d.gridPerRow); gridTolerance = c.value("gridTolerance", d.gridTolerance)
    }
}

extension BackgroundSample {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        id = c.value("id", UUID()); x = c.value("x", 0); y = c.value("y", 0)
    }
}

extension GradientSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = GradientSettings()
        specs = c.value("specs", d.specs); protect = c.value("protect", d.protect)
        shape = c.value("shape", d.shape); mode = c.value("mode", d.mode); residual = c.value("residual", d.residual)
        sectors = c.value("sectors", d.sectors); smoothness = c.value("smoothness", d.smoothness); degree = c.value("degree", d.degree)
        refine = c.value("refine", d.refine)
        surface = c.value("surface", d.surface); sampleSmoothing = c.value("sampleSmoothing", d.sampleSmoothing); autoSmoothing = c.value("autoSmoothing", d.autoSmoothing); sampleRadius = c.value("sampleRadius", d.sampleRadius)
        gridPerRow = c.value("gridPerRow", d.gridPerRow); gridTolerance = c.value("gridTolerance", d.gridTolerance)
    }
}

extension SettingsPreset {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        id = c.value("id", UUID()); name = c.value("name", "Preset"); settings = c.value("settings", CombineSettings())
        thumbnailPNG = c.optionalValue("thumbnailPNG", nil); date = c.value("date", Date())
    }
}

extension CorrectionSettings {
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: AnyKey.self)
        let d = CorrectionSettings()
        strength = c.value("strength", d.strength); brushRadius = c.value("brushRadius", d.brushRadius); brushFlow = c.value("brushFlow", d.brushFlow)
        brushFeather = c.value("brushFeather", d.brushFeather); darken = c.value("darken", d.darken)
        // Older projects get the sky-relative mode too (the exposure mode made the strength erratic on linear data).
        mode = c.value("mode", d.mode)
    }
}
