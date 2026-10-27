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
//      --lum <channel>        luminance source (L, Ha, …)
//      --stars                blend RGB stars
//      --format <fmt>         png8 | png16 | tiff16 | tiff32 | fits32 | jpeg (default png8)
//      --project <file.json>  load CombineSettings from a saved project / settings file
//      --assign <file>=<kind> force a channel assignment (kind: L R G B Ha OIII SII Hb)

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
    let lum = option("--lum")
    let stars = flag("--stars")
    let formatArg = option("--format") ?? "png8"
    let projectArg = option("--project")
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
    if let l = lum { settings.luminance.source = ChannelKind(rawValue: l) ?? ChannelKind.fromPixelMathIdentifier(l) }
    settings.stars.enabled = stars

    let t1 = Date()
    let context = RenderContext.make(settings: settings.normalization, sources: sources)
    print("normalisation (\(settings.normalization.method.rawValue), ref=\(context.reference?.rawValue ?? "-")) in \(Int(Date().timeIntervalSince(t1) * 1000)) ms")
    for (k, t) in context.transforms.sorted(by: { $0.key.rawValue < $1.key.rawValue }) { print("  \(k.rawValue): \(t.description)") }

    let available = Set(sources.keys)
    let palettes: [Palette] = paletteArg == "all" ? Palettes.available(for: available) : [Palettes.palette(id: paletteArg)].compactMap { $0 }
    let format = RasterWriter.Format.allCases.first { $0.rawValue.lowercased().replacingOccurrences(of: " ", with: "").hasPrefix(formatArg.lowercased()) || ($0.rawValue.lowercased().contains(formatArg.lowercased())) } ?? .png8

    for palette in palettes {
        var s = settings
        s.select(palette: palette)
        if let l = lum { s.luminance.source = ChannelKind(rawValue: l) ?? ChannelKind.fromPixelMathIdentifier(l) }
        let t2 = Date()
        do {
            let image: RGBImage
            if maxPixels > 0 {
                let any = sources.values.first!
                let lvl = any.pyramid.levelIndex(maxPixels: maxPixels)
                var planes: [ChannelKind: PlanarImage] = [:]
                let analysis = Renderer.analysisSamples(sources: sources)
                for (k, src) in sources { planes[k] = src.pyramid.level(lvl) }
                let res = try Renderer.render(RenderRequest(planes: planes, analysisPlanes: analysis, settings: s, context: context))
                image = res.image
                print("\(palette.id): level \(lvl) \(image.width)×\(image.height) rendered in \(Int(res.millis)) ms")
            } else {
                image = try Renderer.renderFull(sources: sources, settings: s, context: context, postStretch: nil, progress: nil)!
                print("\(palette.id): full \(image.width)×\(image.height) rendered in \(Int(Date().timeIntervalSince(t2) * 1000)) ms")
            }
            let url = outDir.appendingPathComponent("\(palette.id).\(format.fileExtension)")
            try RasterWriter.write(planes: image.planes, to: url, format: format)
        } catch { print("\(palette.id): \(error.localizedDescription)") }
    }

default:
    print("unknown command \(command)"); exit(1)
}
