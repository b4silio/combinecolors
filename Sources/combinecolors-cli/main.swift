import Foundation
import AstroCore

// A small headless front-end used for batch rendering and for validating the pipeline on real data.
//
//   combinecolors-cli info <files…>
//   combinecolors-cli render [options] <files…>
//      --out <dir>            output directory (default: ./TestOutput)
//      --palette <id|all>     palette id (default: sho) or "all"
//      --norm <method>        none | backgroundEqualize | linearFit | backgroundDispersion | backgroundSignal | noiseWeighted | histogramMatch
//      --stage <pre|post>     stretch before or after combining
//      --stretch <method>     autoSTF | arcsinh | ghs | log | mtf | none
//      --linked               linked stretch
//      --max <pixels>         preview pixel budget (default 2000000); 0 = full resolution
//      --scnr <0..1>          green removal amount;  --scnr-magenta <0..1>  magenta removal amount
//      --colour-range <r>     all | shadows | midtones | highlights: tones the Colour block acts on
//      --exposure <EV>  --brightness <-1..1>  --contrast <-1..1>  --highlights <-1..1>  --shadows <-1..1>   Contrast block
//      --sat <factor>         saturation
//      --hue <deg>            hue shift
//      --balance-shadows <cr,mg,yb>   Photoshop-style colour balance of the shadows (−100..100 each: cyan–red, magenta–green, yellow–blue);
//      --balance-midtones / --balance-highlights alike;  --no-preserve-luminosity  lets the lightness change
//      --selective-whites <c,m,y,k>   selective colour of the whites (−100..100: cyan, magenta, yellow, black);
//      --selective-neutrals / --selective-blacks alike;  --selective-absolute  absolute instead of relative ink changes
//      --texture <-1..1>      fine local contrast;  --clarity <-0.5..2>  mid-scale;  --dehaze <-1..1>  large-scale
//      --micro <-1..1>        micro-structure contrast (0.2 % radius);  --lowfreq <-0.5..2>  largest structures (50 %)
//      --sharpen <0..2>       unsharp mask amount;  --sharpen-radius <px>  its radius in full-resolution pixels (default 1.5)
//      --sharpen-masking <0..1>  protect low-contrast (noise) areas from the sharpening
//      --denoise <0..1>       luminance noise reduction;  --denoise-method wavelets | nonLocalMeans | guided | bm3d
//      --denoise-color <0..1> chroma smoothing
//      --lum <preset|off>     luminance: ha | lum | haMostly | haOiii | balanced | lumHa | maxLines | meanLines | off (default ha)
//      --stars                blend RGB stars (from the R, G, B channels, or from --stars-file)
//      --stars-file <image>   RGB image kept whole and blended as stars;  --stars-blend screen | lighten | add
//                             --stars-brightness <stops>;  --stars-contrast <-1..1>;  --stars-white (brightest stars to white)
//      --stars-stage last | beforeDetail   blend the stars after denoise & detail (default) or before them
//      --format <fmt>         png8 | png16 | tiff16 | tiff32 | fits32 | jpeg (default png8)
//      --project <file.json>  load CombineSettings from a saved project / settings file
//      --assign <file>=<kind> force a channel assignment (kind: L R G B Ha OIII SII Hb)
//      --crop x,y,w,h         shared crop rectangle (full-resolution pixels); "auto" trims empty borders
//      --save-project <file>  write a project file (channel paths, crop, settings) instead of/besides rendering
//      --dust <kind>:x,y,r[;x,y,r…]  heal dust spots on a channel before anything else (repeatable; a project's
//                             spots are applied too);  --dust-model surface | radial;  --dust-smooth <px>;  --dust-feather <0..0.5>;  --dust-polarity dark | bright (star halos)
//                             (the three parameters apply to the spots given with --dust)
//      --gradient <kind>:line:x0,y0,x1,y1 | <kind>:ellipse:cx,cy,a,b[,angle°] | <kind>:poly[:degree] | <kind>:samples[:perRow[,tolerance]]
//                             (samples: a grid of background samples with a spline / kriging surface;  --gradient-surface spline | kriging;
//                             --gradient-sample-smooth <0..1>;  --gradient-sample-radius <px>)
//                             remove a gradient from a channel guided by the drawn shape (repeatable; a project's models are
//                             applied too);  --gradient-mode subtract | divide;  --gradient-residual none | plane | quadratic
//                             --gradient-sectors <1|4|8|12>;  --gradient-smooth <bins>;  --gradient-norefine (keep the geometry as given)
//                             --gradient-protect cx,cy,a,b  region left out of the estimate (repeatable, every channel)
//   combinecolors-cli starnet --starnet <path/to/starnet2> [options] <files…>
//      removes the stars with StarNet2 (separate program, starnetastro.com) and writes <name>_bg and
//      <name>_stars next to each file (or in --out <dir>)
//      --method starnetLinear | appStretch   StarNet2's linear mode (default) or the stretch below, undone afterwards
//      --stretch <method>     autoSTF | arcsinh | ghs | log | mtf (for appStretch);  --target <0..1>  --shadows <MADN>
//      --upsample             StarNet2's intermediate 2x upsampling (slower)

var args = Array(CommandLine.arguments.dropFirst())
guard let command = args.first else {
    print("usage: combinecolors-cli (info|render|palettes|starnet) …"); exit(1)
}
args.removeFirst()

func option(_ name: String) -> String? {
    guard let i = args.firstIndex(of: name), i + 1 < args.count else { return nil }
    let v = args[i + 1]; args.removeSubrange(i...(i + 1)); return v
}
func flag(_ name: String) -> Bool {
    guard let i = args.firstIndex(of: name) else { return false }
    args.remove(at: i); return true
}

func fmt(_ v: Float) -> String { String(format: "%.6g", v) }

func applyLum(_ l: String, _ s: inout CombineSettings) {
    if l == "off" || l == "none" { s.luminance.enabled = false; return }
    s.luminance.enabled = true
    if let p = LuminancePreset(rawValue: l) { s.luminance.preset = p }
    else { s.luminance.preset = .custom; s.luminance.customExpression = l }
}

switch command {
case "palettes":
    for p in Palettes.all {
        print("\(p.id.padding(toLength: 18, withPad: " ", startingAt: 0)) \(p.group.padding(toLength: 11, withPad: " ", startingAt: 0)) \(p.name)  [\(p.requiredChannels.map(\.rawValue).sorted().joined(separator: ","))]")
    }

case "info":
    for path in args {
        let url = URL(fileURLWithPath: path)
        do {
            let t0 = Date()
            let f = try ImageLoader.load(url: url)
            let load = Date().timeIntervalSince(t0)
            print("\(f.fileName): \(f.width)×\(f.height) \(f.formatDescription) planes=\(f.planes.count) filter=\(f.filterName ?? "-") detected=\(f.detectedKind?.rawValue ?? "?") load=\(Int(load * 1000))ms")
            for (i, p) in f.planes.enumerated() {
                let t1 = Date()
                let src = ChannelSource(kind: f.detectedKind ?? .luminance, image: p, fileName: f.fileName)
                let s = src.stats, m = src.maskedStats
                print("  plane \(i): min=\(fmt(s.min)) max=\(fmt(s.max)) mean=\(fmt(s.mean)) median=\(fmt(s.median)) madn=\(fmt(s.madn)) bg=\(fmt(s.background)) bgσ=\(fmt(s.backgroundSigma)) noise=\(fmt(s.noise)) valid=\(fmt(s.validFraction)) p99.5=\(fmt(s.percentile(0.995)))")
                print("    masked: median=\(fmt(m.median)) madn=\(fmt(m.madn)) bg=\(fmt(m.background)) p99.5=\(fmt(m.percentile(0.995))) signal=\(fmt(m.signalStrength)) maskCoverage=\(fmt(StarMask.coverage(src.mask))) levels=\(src.pyramid.count) analysis=\(Int(Date().timeIntervalSince(t1) * 1000))ms")
            }
        } catch { print("\(path): \(error.localizedDescription)") }
    }

case "render":
    let outDir = URL(fileURLWithPath: option("--out") ?? "TestOutput")
    let paletteArg = option("--palette") ?? "sho"
    let normArg = option("--norm")
    let stageArg = option("--stage")
    let stretchArg = option("--stretch")
    let linked = flag("--linked")
    let maxPixels = Int(option("--max") ?? "2000000") ?? 2_000_000
    let scnr = Float(option("--scnr") ?? "0") ?? 0
    let scnrMagenta = Float(option("--scnr-magenta") ?? "0") ?? 0
    let colourRange = option("--colour-range").flatMap(ColourRange.init(rawValue:))
    let sat = Float(option("--sat") ?? "1") ?? 1
    let hue = Float(option("--hue") ?? "0") ?? 0
    var tone = ToneSettings()
    if let v = option("--exposure").flatMap(Float.init) { tone.exposure = v }
    if let v = option("--brightness").flatMap(Float.init) { tone.brightness = v }
    if let v = option("--contrast").flatMap(Float.init) { tone.contrast = v }
    if let v = option("--highlights").flatMap(Float.init) { tone.highlights = v }
    if let v = option("--shadows").flatMap(Float.init) { tone.shadows = v }
    let texture = Float(option("--texture") ?? "0") ?? 0
    let clarity = Float(option("--clarity") ?? "0") ?? 0
    let dehaze = Float(option("--dehaze") ?? "0") ?? 0
    let micro = Float(option("--micro") ?? "0") ?? 0
    let lowFrequency = Float(option("--lowfreq") ?? "0") ?? 0
    let sharpen = Float(option("--sharpen") ?? "0") ?? 0
    let sharpenRadius = Float(option("--sharpen-radius") ?? "1.5") ?? 1.5
    let sharpenMasking = Float(option("--sharpen-masking") ?? "0") ?? 0
    let denoise = Float(option("--denoise") ?? "0") ?? 0
    let denoiseMethod = DenoiseMethod(rawValue: option("--denoise-method") ?? "wavelets") ?? .wavelets
    let denoiseColor = Float(option("--denoise-color") ?? "0") ?? 0
    let lum = option("--lum")
    let starsFile = option("--stars-file")
    let stars = flag("--stars") || starsFile != nil
    let starsBlend = option("--stars-blend").flatMap { StarsBlendMode(rawValue: $0) }
    let starsBrightness = Float(option("--stars-brightness") ?? "0") ?? 0
    let starsContrast = Float(option("--stars-contrast") ?? "0") ?? 0
    let starsWhite = flag("--stars-white")
    let starsStage = option("--stars-stage").flatMap { StarsBlendStage(rawValue: $0) }
    let formatArg = option("--format") ?? "png8"
    let projectArg = option("--project")
    let cropArg = option("--crop")
    let saveProject = option("--save-project")
    var assignments: [String: ChannelKind] = [:]
    while let a = option("--assign") {
        let parts = a.split(separator: "=", maxSplits: 1).map(String.init)
        if parts.count == 2, let k = ChannelKind(rawValue: parts[1]) ?? ChannelKind.fromPixelMathIdentifier(parts[1]) { assignments[parts[0]] = k }
    }
    // Dust spots: from the project file, then from --dust.
    var projectDoc: ProjectDocument? = nil
    if let pj = projectArg, let data = FileManager.default.contents(atPath: pj) { projectDoc = try? JSONDecoder().decode(ProjectDocument.self, from: data) }
    var dust = projectDoc?.dust ?? DustSettings()
    if let m = option("--dust-model").flatMap({ DustModel(rawValue: $0) }) { dust.model = m }
    if let v = option("--dust-smooth").flatMap(Double.init) { dust.smoothness = v }
    if let v = option("--dust-feather").flatMap(Double.init) { dust.feather = v }
    if let v = option("--dust-polarity").flatMap({ SpotPolarity(rawValue: $0) }) { dust.polarity = v }
    while let d = option("--dust") {
        let parts = d.split(separator: ":", maxSplits: 1).map(String.init)
        guard parts.count == 2, let k = ChannelKind(rawValue: parts[0]) ?? ChannelKind.fromPixelMathIdentifier(parts[0]) else { print("bad --dust \(d)"); continue }
        for item in parts[1].split(separator: ";") {
            let v = item.split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
            if v.count == 3 { dust.spots[k, default: []].append(dust.newSpot(x: v[0], y: v[1], radius: v[2])) } else { print("bad --dust item \(item)") }
        }
    }
    // Gradient models: from the project file, then from --gradient.
    var gradient = projectDoc?.gradient ?? GradientSettings()
    if let m = option("--gradient-mode").flatMap({ GradientMode(rawValue: $0) }) { gradient.mode = m }
    if let r = option("--gradient-residual").flatMap({ GradientResidual(rawValue: $0) }) { gradient.residual = r }
    if let v = option("--gradient-sectors").flatMap(Int.init) { gradient.sectors = v }
    if let v = option("--gradient-smooth").flatMap(Double.init) { gradient.smoothness = v }
    if flag("--gradient-norefine") { gradient.refine = false }
    if let v = option("--gradient-surface").flatMap({ SampleSurface(rawValue: $0) }) { gradient.surface = v }
    if let v = option("--gradient-sample-smooth").flatMap(Double.init) { gradient.sampleSmoothing = v }
    if let v = option("--gradient-sample-radius").flatMap(Double.init) { gradient.sampleRadius = v }
    while let p = option("--gradient-protect") {
        let v = p.split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) }
        if v.count == 4 { gradient.protect.append(GradientRegion(x: v[0], y: v[1], a: v[2], b: v[3])) } else { print("bad --gradient-protect \(p)") }
    }
    while let g = option("--gradient") {
        let parts = g.split(separator: ":", maxSplits: 2).map(String.init)
        guard parts.count >= 2, let k = ChannelKind(rawValue: parts[0]) ?? ChannelKind.fromPixelMathIdentifier(parts[0]) else { print("bad --gradient \(g)"); continue }
        let v = parts.count == 3 ? parts[2].split(separator: ",").compactMap { Double($0.trimmingCharacters(in: .whitespaces)) } : []
        var gesture: GradientGesture? = nil
        switch parts[1] {
        case "line" where v.count == 4: gesture = .line(from: CGPoint(x: v[0], y: v[1]), to: CGPoint(x: v[2], y: v[3]))
        case "ellipse" where v.count >= 4: gesture = .ellipse(center: CGPoint(x: v[0], y: v[1]), a: v[2], b: v[3], angle: (v.count > 4 ? v[4] : 0) * .pi / 180)
        case "poly", "polynomial": gesture = .polynomial(); if let d = v.first { gradient.degree = Int(d) }
        case "samples": gesture = .samples(); if let n = v.first { gradient.gridPerRow = Int(n) }; if v.count > 1 { gradient.gridTolerance = v[1] }
        default: break
        }
        guard let ge = gesture else { print("bad --gradient \(g)"); continue }
        gradient.specs[k] = gradient.newSpec(gesture: ge)
    }
    try? FileManager.default.createDirectory(at: outDir, withIntermediateDirectories: true)

    // Load channels (in parallel).
    var sources: [ChannelKind: ChannelSource] = [:]
    let lock = NSLock()
    let t0 = Date()
    DispatchQueue.concurrentPerform(iterations: args.count) { i in
        let url = URL(fileURLWithPath: args[i])
        do {
            let f = try ImageLoader.load(url: url)
            let kind = assignments[url.lastPathComponent] ?? assignments[args[i]] ?? f.detectedKind
            if f.isColor {
                for (p, k) in zip(f.planes, [ChannelKind.red, .green, .blue]) {
                    let src = ChannelSource(kind: k, image: p, fileName: f.fileName, url: url)
                    lock.lock(); sources[k] = src; lock.unlock()
                }
            } else if let k = kind {
                let spots = dust.spots(for: k)
                let t1 = Date()
                var plane = DustRemoval.heal(f.planes[0], spots: spots)
                let healMs = Int(Date().timeIntervalSince(t1) * 1000)
                var gradientNote = ""
                if let spec = gradient.spec(for: k) {
                    let t2 = Date()
                    if let (corrected, fit) = GradientRemoval.correct(plane, spec: spec, protect: gradient.protect) {
                        plane = corrected
                        let g = fit.gesture
                        let geo: String
                        switch g.shape {
                        case .line: geo = String(format: "line %.0f,%.0f → %.0f,%.0f", g.x, g.y, g.x2, g.y2)
                        case .ellipse: geo = String(format: "ellipse centre %.0f,%.0f axes %.0f×%.0f angle %.1f°", g.x, g.y, g.a, g.b, g.angle * 180 / .pi)
                        case .polynomial: geo = "polynomial degree \(spec.degree)"
                        case .samples:
                            let used = fit.sampleResults.filter { !$0.isRejected }.count
                            geo = "\(fit.spec.surface.rawValue) surface through \(used) of \(fit.sampleResults.count) samples"
                        }
                        gradientNote = String(format: ", gradient removed in %d ms (%@, %@, residual %.2fσ)", Int(Date().timeIntervalSince(t2) * 1000), geo, spec.mode.rawValue, fit.residualSigma)
                    } else { gradientNote = ", gradient model could not be fitted" }
                }
                let src = ChannelSource(kind: k, image: plane, fileName: f.fileName, url: url, formatDescription: f.formatDescription)
                lock.lock(); sources[k] = src; lock.unlock()
                print("loaded \(f.fileName) → \(k.rawValue) (\(Int(src.analysisMillis)) ms analysis\(spots.isEmpty ? "" : ", \(spots.count) dust spots healed in \(healMs) ms")\(gradientNote))")
            } else {
                print("skipped \(f.fileName): channel not recognised (use --assign)")
            }
        } catch { print("\(url.lastPathComponent): \(error.localizedDescription)") }
    }
    print("loaded \(sources.count) channels in \(Int(Date().timeIntervalSince(t0) * 1000)) ms: \(sources.keys.map(\.rawValue).sorted())")
    guard !sources.isEmpty else { exit(1) }

    var crop: PixelRect? = nil
    if let c = cropArg {
        if c == "auto" {
            let t = Date()
            crop = AutoCrop.detect(images: sources.values.map(\.image))
            print("auto crop: \(crop.map { "\($0.x),\($0.y),\($0.width),\($0.height)" } ?? "none") in \(Int(Date().timeIntervalSince(t) * 1000)) ms")
        } else {
            let p = c.split(separator: ",").compactMap { Int($0) }
            if p.count == 4 { crop = PixelRect(x: p[0], y: p[1], width: p[2], height: p[3]) }
        }
        if let r = crop { sources = sources.mapValues { ChannelSource($0, region: r) } }
    }

    var settings = CombineSettings()
    if let pj = projectArg, let data = FileManager.default.contents(atPath: pj) {
        if let s = try? JSONDecoder().decode(CombineSettings.self, from: data) { settings = s }
        else if let doc = projectDoc { settings = doc.settings }
    }
    if let n = normArg, let m = NormalizationMethod(rawValue: n) { settings.normalization.method = m }
    if let st = stageArg { settings.stretchStage = st == "post" ? .postCombine : .preCombine }
    if let sm = stretchArg, let m = StretchMethod(rawValue: sm) { settings.stretch.method = m }
    settings.stretch.linked = linked
    settings.post.scnrGreen = scnr
    settings.post.scnrMagenta = scnrMagenta
    if let colourRange { settings.post.colourRange = colourRange }
    if !tone.isNeutral { settings.tone = tone }
    settings.post.saturation = sat
    settings.post.hueShift = hue
    settings.detail.texture = texture; settings.detail.clarity = clarity; settings.detail.dehaze = dehaze
    settings.detail.micro = micro; settings.detail.lowFrequency = lowFrequency
    settings.detail.sharpen = sharpen; settings.detail.sharpenRadius = sharpenRadius; settings.detail.sharpenMasking = sharpenMasking
    settings.denoise.amount = denoise; settings.denoise.method = denoiseMethod; settings.denoise.color = denoiseColor
    func triple(_ name: String) -> [Float]? { option(name).map { $0.split(separator: ",").map { Float($0.trimmingCharacters(in: .whitespaces)) ?? 0 } } }
    for (t, name) in [(ToneRange.shadows, "--balance-shadows"), (.midtones, "--balance-midtones"), (.highlights, "--balance-highlights")] {
        if let v = triple(name), v.count == 3 { var a = ToneBalance(); a.cyanRed = v[0] / 100; a.magentaGreen = v[1] / 100; a.yellowBlue = v[2] / 100; settings.grading.setBalance(t, a) }
    }
    if flag("--no-preserve-luminosity") { settings.grading.preserveLuminosity = false }
    for (r, name) in [(SelectiveRange.whites, "--selective-whites"), (.neutrals, "--selective-neutrals"), (.blacks, "--selective-blacks")] {
        if let v = triple(name), v.count == 4 { var a = SelectiveAdjust(); a.cyan = v[0] / 100; a.magenta = v[1] / 100; a.yellow = v[2] / 100; a.black = v[3] / 100; settings.grading.setSelective(r, a) }
    }
    if flag("--selective-absolute") { settings.grading.relative = false }
    if let l = lum { applyLum(l, &settings) }
    settings.stars.enabled = stars
    if let b = starsBlend { settings.stars.mode = b }
    if let st = starsStage { settings.stars.stage = st }
    settings.stars.brightness = starsBrightness; settings.stars.contrast = starsContrast; settings.stars.normalize = starsWhite
    var starsLayer: StarsLayer? = nil
    if let p = starsFile {
        do {
            let f = try ImageLoader.load(url: URL(fileURLWithPath: p))
            guard f.isColor else { print("\(p): not a colour image"); exit(1) }
            starsLayer = StarsLayer(planes: Array(f.planes.prefix(3)), fileName: f.fileName, url: f.url, formatDescription: f.formatDescription)
            if settings.stars.useMask, f.fileName.lowercased().contains("star") { settings.stars.useMask = false }
            print("stars layer \(f.fileName) (\(f.formatDescription))")
        } catch { print("\(p): \(error.localizedDescription)"); exit(1) }
    }

    let t1 = Date()
    let context = RenderContext.make(settings: settings.normalization, sources: sources)
    print("normalisation (\(settings.normalization.method.rawValue), ref=\(context.reference?.rawValue ?? "-")) in \(Int(Date().timeIntervalSince(t1) * 1000)) ms")
    for (k, t) in context.transforms.sorted(by: { $0.key.rawValue < $1.key.rawValue }) { print("  \(k.rawValue): \(t.description)") }

    if let sp = saveProject {
        var doc = ProjectDocument()
        var s = settings
        if let pal = Palettes.palette(id: paletteArg) { s.select(palette: pal) }
        if let l = lum { applyLum(l, &s) }
        doc.settings = s
        doc.crop = crop
        doc.dust = dust
        doc.gradient = gradient
        doc.channels = sources.values.sorted { $0.kind.rawValue < $1.kind.rawValue }.compactMap { src in src.url.map { ProjectDocument.ChannelRef(kind: src.kind, path: $0.path) } }
        try doc.data().write(to: URL(fileURLWithPath: sp))
        print("project written to \(sp)")
    }

    let available = Set(sources.keys)
    let palettes: [Palette] = paletteArg == "all" ? Palettes.available(for: available) : [Palettes.palette(id: paletteArg)].compactMap { $0 }
    let format = RasterWriter.Format.allCases.first { $0.rawValue.lowercased().replacingOccurrences(of: " ", with: "").hasPrefix(formatArg.lowercased()) || ($0.rawValue.lowercased().contains(formatArg.lowercased())) } ?? .png8

    for palette in palettes {
        var s = settings
        s.select(palette: palette)
        if let l = lum { applyLum(l, &s) }
        let t2 = Date()
        do {
            let image: RGBImage
            if maxPixels > 0 {
                let any = sources.values.first!
                let lvl = any.pyramid.levelIndex(maxPixels: maxPixels)
                var planes: [ChannelKind: PlanarImage] = [:]
                let analysis = Renderer.analysisSamples(sources: sources)
                for (k, src) in sources {
                    let l = src.pyramid.level(lvl)
                    planes[k] = crop.map { l.cropped($0.scaled(level: lvl)) } ?? l
                }
                var req = RenderRequest(planes: planes, analysisPlanes: analysis, settings: s, context: context, scale: 1 / Float(1 << lvl))
                if let st = starsLayer {
                    let r = crop.map { $0.scaled(level: lvl) } ?? PixelRect(x: 0, y: 0, width: st.pyramids[0].level(lvl).width, height: st.pyramids[0].level(lvl).height)
                    req.stars = st.input(level: lvl, x: r.x, y: r.y, width: r.width, height: r.height)
                }
                let res = try Renderer.render(req)
                image = res.image
                print("\(palette.id): level \(lvl) \(image.width)×\(image.height) rendered in \(Int(res.millis)) ms")
            } else {
                image = try Renderer.renderFull(sources: sources, settings: s, context: context, postStretch: nil, region: crop, stars: starsLayer, progress: nil)!
                print("\(palette.id): full \(image.width)×\(image.height) rendered in \(Int(Date().timeIntervalSince(t2) * 1000)) ms")
            }
            let url = outDir.appendingPathComponent("\(palette.id).\(format.fileExtension)")
            try RasterWriter.write(planes: image.planes, to: url, format: format)
        } catch { print("\(palette.id): \(error.localizedDescription)") }
    }

case "starnet":
    guard let exe = option("--starnet") else { print("--starnet <path to the starnet2 executable> is required"); exit(1) }
    let tool = StarNetTool(executable: URL(fileURLWithPath: exe))
    var opts = StarRemovalOptions()
    if let m = option("--method") { guard let mm = StarRemovalMethod(rawValue: m) else { print("unknown method \(m)"); exit(1) }; opts.method = mm }
    if let m = option("--stretch") { guard let sm = StretchMethod(rawValue: m) else { print("unknown stretch \(m)"); exit(1) }; opts.stretch.method = sm }
    if let t = option("--target").flatMap(Float.init) { opts.stretch.targetBackground = t }
    if let c = option("--shadows").flatMap(Float.init) { opts.stretch.shadowsClipping = c }
    opts.upsample = flag("--upsample")
    let folder = option("--out").map { URL(fileURLWithPath: $0, isDirectory: true) }
    if let folder { try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) }
    do {
        let info = try tool.info()
        print("StarNet2 \(info.version) (\(info.backend))")
    } catch { print(error.localizedDescription); exit(1) }
    var failures = 0
    for path in args {
        let url = URL(fileURLWithPath: path)
        let t0 = Date()
        var last = -1
        do {
            let r = try StarRemoval.processFile(url, options: opts, tool: tool, folder: folder) { p in
                let pct = Int(p * 100)
                if pct / 10 != last / 10 { last = pct; FileHandle.standardError.write("  \(url.lastPathComponent): \(pct) %\n".data(using: .utf8)!) }
            }
            print("\(url.lastPathComponent): \(r.processing.displayName) -> \(r.starless.lastPathComponent), \(r.stars.lastPathComponent) in \(Int(Date().timeIntervalSince(t0))) s")
        } catch { failures += 1; print("\(url.lastPathComponent): \(error.localizedDescription)") }
    }
    if failures > 0 { exit(2) }

default:
    print("unknown command \(command)"); exit(1)
}
