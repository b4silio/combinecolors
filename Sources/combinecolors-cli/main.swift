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
//      --scnr <0..1>          green removal amount
//      --sat <factor>         saturation
//      --hue <deg>            hue shift
//      --texture <-1..1>      fine local contrast;  --clarity <-0.5..2>  mid-scale;  --dehaze <-1..1>  large-scale
//      --sharpen <0..2>       unsharp mask amount;  --sharpen-radius <px>  its radius in full-resolution pixels (default 1.5)
//      --sharpen-masking <0..1>  protect low-contrast (noise) areas from the sharpening
//      --lum <preset|off>     luminance: ha | lum | haMostly | haOiii | balanced | lumHa | maxLines | meanLines | off (default ha)
//      --stars                blend RGB stars
//      --format <fmt>         png8 | png16 | tiff16 | tiff32 | fits32 | jpeg (default png8)
//      --project <file.json>  load CombineSettings from a saved project / settings file
//      --assign <file>=<kind> force a channel assignment (kind: L R G B Ha OIII SII Hb)
//      --crop x,y,w,h         shared crop rectangle (full-resolution pixels); "auto" trims empty borders
//      --save-project <file>  write a project file (channel paths, crop, settings) instead of/besides rendering

var args = Array(CommandLine.arguments.dropFirst())
guard let command = args.first else {
    print("usage: combinecolors-cli (info|render|palettes) …"); exit(1)
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
    let sat = Float(option("--sat") ?? "1") ?? 1
    let hue = Float(option("--hue") ?? "0") ?? 0
    let texture = Float(option("--texture") ?? "0") ?? 0
    let clarity = Float(option("--clarity") ?? "0") ?? 0
    let dehaze = Float(option("--dehaze") ?? "0") ?? 0
    let sharpen = Float(option("--sharpen") ?? "0") ?? 0
    let sharpenRadius = Float(option("--sharpen-radius") ?? "1.5") ?? 1.5
    let sharpenMasking = Float(option("--sharpen-masking") ?? "0") ?? 0
    let lum = option("--lum")
    let stars = flag("--stars")
    let formatArg = option("--format") ?? "png8"
    let projectArg = option("--project")
    let cropArg = option("--crop")
    let saveProject = option("--save-project")
    var assignments: [String: ChannelKind] = [:]
    while let a = option("--assign") {
        let parts = a.split(separator: "=", maxSplits: 1).map(String.init)
        if parts.count == 2, let k = ChannelKind(rawValue: parts[1]) ?? ChannelKind.fromPixelMathIdentifier(parts[1]) { assignments[parts[0]] = k }
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
                let src = ChannelSource(kind: k, image: f.planes[0], fileName: f.fileName, url: url, formatDescription: f.formatDescription)
                lock.lock(); sources[k] = src; lock.unlock()
                print("loaded \(f.fileName) → \(k.rawValue) (\(Int(src.analysisMillis)) ms analysis)")
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
        else if let doc = try? JSONDecoder().decode(ProjectDocument.self, from: data) { settings = doc.settings }
    }
    if let n = normArg, let m = NormalizationMethod(rawValue: n) { settings.normalization.method = m }
    if let st = stageArg { settings.stretchStage = st == "post" ? .postCombine : .preCombine }
    if let sm = stretchArg, let m = StretchMethod(rawValue: sm) { settings.stretch.method = m }
    settings.stretch.linked = linked
    settings.post.scnrGreen = scnr
    settings.post.saturation = sat
    settings.post.hueShift = hue
    settings.detail.texture = texture; settings.detail.clarity = clarity; settings.detail.dehaze = dehaze
    settings.detail.sharpen = sharpen; settings.detail.sharpenRadius = sharpenRadius; settings.detail.sharpenMasking = sharpenMasking
    if let l = lum { applyLum(l, &settings) }
    settings.stars.enabled = stars

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
                let res = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: analysis, settings: s, context: context, scale: 1 / Float(1 << lvl)))
                image = res.image
                print("\(palette.id): level \(lvl) \(image.width)×\(image.height) rendered in \(Int(res.millis)) ms")
            } else {
                image = try Renderer.renderFull(sources: sources, settings: s, context: context, postStretch: nil, region: crop, progress: nil)!
                print("\(palette.id): full \(image.width)×\(image.height) rendered in \(Int(Date().timeIntervalSince(t2) * 1000)) ms")
            }
            let url = outDir.appendingPathComponent("\(palette.id).\(format.fileExtension)")
            try RasterWriter.write(planes: image.planes, to: url, format: format)
        } catch { print("\(palette.id): \(error.localizedDescription)") }
    }

default:
    print("unknown command \(command)"); exit(1)
}
