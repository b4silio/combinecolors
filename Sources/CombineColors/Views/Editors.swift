import SwiftUI
import AstroCore

// MARK: - Matrix editor

struct MatrixEditorView: View {
    @Bindable var model: ProjectModel
    @Environment(\.dismiss) private var dismiss

    private var kinds: [ChannelKind] {
        ChannelKind.allCases.filter { model.sources[$0] != nil || model.settings.matrix.weights[$0] != nil }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Combination matrix").font(.title3).bold()
            Text("Each output channel is a weighted sum of the (stretched or linear) input channels.").font(.caption).foregroundStyle(.secondary)
            Grid(alignment: .trailing, horizontalSpacing: 10, verticalSpacing: 6) {
                GridRow {
                    Text("")
                    Text("→ Red").bold().foregroundStyle(.red)
                    Text("→ Green").bold().foregroundStyle(.green)
                    Text("→ Blue").bold().foregroundStyle(.blue)
                }
                ForEach(kinds) { k in
                    GridRow {
                        HStack { Circle().fill(k.tint).frame(width: 8, height: 8); Text(k.displayName) }.gridColumnAlignment(.leading)
                        ForEach(0..<3, id: \.self) { o in
                            TextField("0", value: Binding(get: { model.settings.matrix.weight(k, o) }, set: { v in
                                model.settings.mode = .matrix
                                model.settings.matrix.set(k, o, v)
                            }), formatter: SliderRow.formatter("%.3f"))
                            .textFieldStyle(.roundedBorder).frame(width: 70).multilineTextAlignment(.trailing)
                        }
                    }
                }
            }
            HStack {
                Button("Normalise rows") {
                    var m = model.settings.matrix
                    for o in 0..<3 {
                        let sum = kinds.map { m.weight($0, o) }.reduce(0, +)
                        if sum > 0 { for k in kinds { m.set(k, o, m.weight(k, o) / sum) } }
                    }
                    model.settings.matrix = m
                }
                .help("Scales the weights so every output column sums to 1.")
                Button("Clear") { model.settings.matrix = .empty }
                Spacer()
                Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
            }
            .controlSize(.small)
        }
        .padding(16)
        .frame(width: 420)
    }
}

// MARK: - Pixel Math editor

struct PixelMathEditorView: View {
    @Bindable var model: ProjectModel
    @Environment(\.dismiss) private var dismiss
    @State private var program: PixelMathProgram = PixelMathProgram(red: "", green: "", blue: "")
    @State private var error: String? = nil
    @State private var presetName = ""
    @AppStorage("pixelMathPresets") private var presetsData: Data = Data()

    private var userPresets: [String: PixelMathProgram] {
        (try? JSONDecoder().decode([String: PixelMathProgram].self, from: presetsData)) ?? [:]
    }

    var body: some View {
        HStack(alignment: .top, spacing: 14) {
            VStack(alignment: .leading, spacing: 8) {
                HStack {
                    Text("Pixel Math").font(.title3).bold()
                    Spacer()
                    Menu("Presets") {
                        Section("Built-in") {
                            ForEach(Palettes.all.filter(\.isPixelMath)) { p in
                                Button(p.name) { if case .pixelMath(let prog) = p.kind { program = prog; validate() } }
                            }
                            Button("Permutation of current matrix") { program = matrixAsProgram(); validate() }
                        }
                        if !userPresets.isEmpty {
                            Section("Mine") {
                                ForEach(userPresets.keys.sorted(), id: \.self) { name in
                                    Button(name) { program = userPresets[name]!; validate() }
                                }
                            }
                        }
                    }
                    .frame(width: 110)
                }
                expressionField("R", $program.red, .red)
                expressionField("G", $program.green, .green)
                expressionField("B", $program.blue, .blue)
                expressionField("L (optional)", $program.luminance, .gray)
                if let e = error {
                    Label(e, systemImage: "exclamationmark.triangle").foregroundStyle(.red).font(.caption).fixedSize(horizontal: false, vertical: true)
                } else {
                    let missing = Renderer.missingChannels(settings: applied, available: model.availableKinds)
                    if missing.isEmpty { Label("Expressions compile. Applied live.", systemImage: "checkmark.circle").foregroundStyle(.green).font(.caption) }
                    else { Label("Missing channels: \(missing.map(\.displayName).joined(separator: ", "))", systemImage: "exclamationmark.triangle").foregroundStyle(.orange).font(.caption) }
                }
                HStack {
                    TextField("Preset name", text: $presetName).frame(width: 150)
                    Button("Save preset") {
                        var p = userPresets; p[presetName] = program
                        presetsData = (try? JSONEncoder().encode(p)) ?? Data()
                    }.disabled(presetName.isEmpty || error != nil)
                    if userPresets[presetName] != nil {
                        Button("Delete") { var p = userPresets; p.removeValue(forKey: presetName); presetsData = (try? JSONEncoder().encode(p)) ?? Data() }
                    }
                    Spacer()
                    Button("Done") { dismiss() }.keyboardShortcut(.defaultAction)
                }
                .controlSize(.small)
            }
            .frame(width: 440)
            VStack(alignment: .leading, spacing: 6) {
                Text("Reference").font(.headline)
                Text("Available: " + ChannelKind.allCases.filter { model.sources[$0] != nil }.map(\.pixelMathName).joined(separator: ", ")).font(.caption).bold()
                ForEach(PixelMath.help, id: \.0) { (title, text) in
                    Text(title).font(.caption).bold().padding(.top, 2)
                    Text(text).font(.system(size: 10, design: .monospaced)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Text("Channels are normalised and (for the pre-combine stage) stretched to [0,1] before the expressions run. Results are clamped to [0,1].").font(.caption2).foregroundStyle(.secondary).padding(.top, 4).fixedSize(horizontal: false, vertical: true)
            }
            .frame(width: 300)
        }
        .padding(16)
        .onAppear { program = model.settings.pixelMath; validate() }
        .onChange(of: program) { _, _ in validate() }
    }

    private var applied: CombineSettings { var s = model.settings; s.mode = .pixelMath; s.pixelMath = program; return s }

    private func expressionField(_ label: String, _ text: Binding<String>, _ color: Color) -> some View {
        HStack(alignment: .top) {
            Text(label).foregroundStyle(color).bold().frame(width: 80, alignment: .trailing)
            TextEditor(text: text)
                .font(.system(.body, design: .monospaced))
                .frame(minHeight: 44)
                .overlay(RoundedRectangle(cornerRadius: 4).strokeBorder(Color.secondary.opacity(0.3)))
        }
    }

    private func validate() {
        do {
            _ = try program.compile()
            error = nil
            if model.settings.pixelMath != program || model.settings.mode != .pixelMath {
                model.settings.mode = .pixelMath
                model.settings.pixelMath = program
                model.settings.paletteID = "custom"
            }
        } catch let e as PixelMathError {
            error = e.message
        } catch { self.error = error.localizedDescription }
    }

    private func matrixAsProgram() -> PixelMathProgram {
        let m = model.settings.matrix
        func expr(_ o: Int) -> String {
            let terms = m.orderedChannels.compactMap { k -> String? in
                let w = m.weight(k, o); guard w != 0 else { return nil }
                return w == 1 ? k.pixelMathName : "\(String(format: "%.3g", w))*\(k.pixelMathName)"
            }
            return terms.isEmpty ? "0" : terms.joined(separator: " + ")
        }
        return PixelMathProgram(red: expr(0), green: expr(1), blue: expr(2))
    }
}

// MARK: - Dual-band splitter

struct DualBandSplitView: View {
    @Bindable var model: ProjectModel
    var initialFile: LoadedFile? = nil
    @Environment(\.dismiss) private var dismiss
    @State private var file: LoadedFile? = nil
    @State private var settings = DualBandSettings()
    @State private var previews: (CGImage, CGImage)? = nil
    @State private var working = false
    @State private var message: String? = nil
    @State private var targeted = false

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("Split a dual-band colour image").font(.title3).bold()
            Text("One-shot-colour images taken through a dual-band filter hold one emission line in the red channel and the other in green/blue. This step extracts them into two monochrome channels that can then be combined like any other narrowband data.")
                .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack(alignment: .top, spacing: 14) {
                VStack(alignment: .leading, spacing: 8) {
                    ZStack {
                        RoundedRectangle(cornerRadius: 6).strokeBorder(targeted ? Color.accentColor : Color.secondary.opacity(0.4), style: StrokeStyle(lineWidth: 1, dash: [5, 3]))
                        if let f = file {
                            VStack { Text(f.fileName).bold(); Text(verbatim: "\(f.width) × \(f.height) · \(f.formatDescription)").font(.caption).foregroundStyle(.secondary) }
                        } else {
                            VStack { Image(systemName: "square.and.arrow.down"); Text("Drop a colour FITS/TIFF here").font(.caption) }
                        }
                    }
                    .frame(height: 70)
                    .onDrop(of: [.fileURL], isTargeted: $targeted) { providers in
                        DropHelpers.urls(from: providers) { urls in if let u = urls.first { load(u) } }
                        return true
                    }
                    Button("Choose file…") { Panels.openImages { urls in if let u = urls.first { load(u) } } }.controlSize(.small)
                    Picker("Filter", selection: $settings.preset) { ForEach(DualBandSettings.Preset.allCases) { p in Text(p.displayName).tag(p) } }
                    if settings.preset == .custom {
                        Picker("First", selection: $settings.customFirst) { ForEach(ChannelKind.allCases) { k in Text(k.displayName).tag(k) } }
                        Picker("Second", selection: $settings.customSecond) { ForEach(ChannelKind.allCases) { k in Text(k.displayName).tag(k) } }
                    }
                    Group {
                        Text("\(settings.outputKinds.0.displayName) from camera R/G/B").font(.caption).bold()
                        weightRow("R", $settings.firstWeights[0]); weightRow("G", $settings.firstWeights[1]); weightRow("B", $settings.firstWeights[2])
                        Text("\(settings.outputKinds.1.displayName) from camera R/G/B").font(.caption).bold()
                        weightRow("R", $settings.secondWeights[0]); weightRow("G", $settings.secondWeights[1]); weightRow("B", $settings.secondWeights[2])
                        SliderRow(title: "Cross-talk →2", value: $settings.crosstalkSecond, range: 0...1, defaultValue: 0, help: "Fraction of the first line subtracted from the second.")
                        SliderRow(title: "Cross-talk →1", value: $settings.crosstalkFirst, range: 0...1, defaultValue: 0, help: "Fraction of the second line subtracted from the first.")
                    }
                }
                .frame(width: 340)
                VStack(spacing: 6) {
                    previewBox(previews?.0, settings.outputKinds.0.displayName)
                    previewBox(previews?.1, settings.outputKinds.1.displayName)
                }
                .frame(width: 300)
            }
            if let m = message { Text(m).font(.caption).foregroundStyle(.secondary) }
            HStack {
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Save as FITS…") { saveFITS() }.disabled(file == nil || working)
                Button("Add to channels") { addToChannels() }.buttonStyle(.borderedProminent).disabled(file == nil || working)
            }
        }
        .padding(16)
        .frame(width: 700)
        .onAppear { if let f = initialFile { file = f; refreshPreview() } }
        .onChange(of: settings) { _, _ in refreshPreview() }
    }

    private func weightRow(_ label: String, _ value: Binding<Float>) -> some View {
        SliderRow(title: label, value: value, range: 0...1, defaultValue: 0)
    }

    private func previewBox(_ img: CGImage?, _ title: String) -> some View {
        VStack(spacing: 2) {
            ZStack {
                RoundedRectangle(cornerRadius: 4).fill(Color.black.opacity(0.5))
                if let img { Image(decorative: img, scale: 1).resizable().aspectRatio(contentMode: .fit) }
                else if working { ProgressView() }
            }
            .frame(height: 150)
            Text(title).font(.caption)
        }
    }

    private func load(_ url: URL) {
        working = true
        Task.detached {
            do {
                let f = try ImageLoader.load(url: url)
                await MainActor.run {
                    working = false
                    if f.isColor { file = f; message = nil; refreshPreview() }
                    else { message = "\(f.fileName) is a monochrome image; a colour (RGB) image is needed." }
                }
            } catch { await MainActor.run { working = false; message = error.localizedDescription } }
        }
    }

    private func refreshPreview() {
        guard let f = file else { return }
        let s = settings
        working = true
        Task.detached {
            // Work on a small version for the preview.
            var r = f.planes[0], g = f.planes[1], b = f.planes[2]
            while r.count > 200_000 { r = r.downsampled2x(); g = g.downsampled2x(); b = b.downsampled2x() }
            let (a, c) = DualBand.split(r: r, g: g, b: b, settings: s)
            for p in [a, c] { Stretch.resolve(StretchSettings(), stats: Statistics.compute(image: p)).apply(to: p) }
            let imgs = (RasterWriter.makeDisplayImage(gray: a), RasterWriter.makeDisplayImage(gray: c))
            await MainActor.run {
                working = false
                if let x = imgs.0, let y = imgs.1 { previews = (x, y) }
            }
        }
    }

    private func split() -> (PlanarImage, PlanarImage)? {
        guard let f = file else { return nil }
        return DualBand.split(r: f.planes[0], g: f.planes[1], b: f.planes[2], settings: settings)
    }

    private func addToChannels() {
        guard let f = file, let (a, b) = split() else { return }
        let kinds = settings.outputKinds
        model.addSplitChannels(first: (kinds.0, a), second: (kinds.1, b), fileName: f.fileName)
        dismiss()
    }

    private func saveFITS() {
        guard let f = file, let (a, b) = split() else { return }
        let kinds = settings.outputKinds
        let base = f.url.deletingPathExtension()
        let p = NSOpenPanel()
        p.canChooseDirectories = true; p.canChooseFiles = false; p.canCreateDirectories = true
        p.prompt = "Save here"
        p.message = "Choose the folder for \(base.lastPathComponent)-\(kinds.0.rawValue).fits and -\(kinds.1.rawValue).fits"
        p.directoryURL = base.deletingLastPathComponent()
        p.begin { r in
            guard r == .OK, let dir = p.url else { return }
            do {
                let u1 = dir.appendingPathComponent("\(base.lastPathComponent)-\(kinds.0.rawValue).fits")
                let u2 = dir.appendingPathComponent("\(base.lastPathComponent)-\(kinds.1.rawValue).fits")
                try FITSWriter.write(planes: [a], to: u1, extraCards: [("FILTER", kinds.0.rawValue, "extracted line")])
                try FITSWriter.write(planes: [b], to: u2, extraCards: [("FILTER", kinds.1.rawValue, "extracted line")])
                message = "Saved \(u1.lastPathComponent) and \(u2.lastPathComponent)"
            } catch { message = error.localizedDescription }
        }
    }
}

// MARK: - Export sheet

struct ExportSheet: View {
    @Bindable var model: ProjectModel
    @Environment(\.dismiss) private var dismiss
    @AppStorage("exportFormat") private var formatRaw = RasterWriter.Format.tiff16.rawValue
    @State private var linear = false
    @State private var error: String? = nil

    private var format: RasterWriter.Format { RasterWriter.Format(rawValue: formatRaw) ?? .tiff16 }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Combine & Export").font(.title3).bold()
            Picker("Format", selection: $formatRaw) {
                ForEach(RasterWriter.Format.allCases) { f in Text(f.rawValue).tag(f.rawValue) }
            }
            Toggle("Linear output (skip stretch and colour post-processing)", isOn: $linear)
                .help("Writes the normalised, weighted, combined channels without any stretch — for further processing in PixInsight or Siril.")
            if let size = model.imageSize {
                let c = model.crop
                Text(verbatim: "Output: \(c?.width ?? Int(size.width)) × \(c?.height ?? Int(size.height)) px\(c != nil ? " (cropped)" : ""), full resolution, rendered with the exact preview parameters.").font(.caption).foregroundStyle(.secondary)
            }
            if let e = error { Text(e).foregroundStyle(.red).font(.caption) }
            HStack {
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Export…") {
                    let base = (model.sources.values.first?.fileName as NSString?)?.deletingPathExtension ?? "combined"
                    let name = "\(base)-\(model.settings.paletteID)\(linear ? "-linear" : "").\(format.fileExtension)"
                    Panels.save(name: name, type: format.utType, message: "Save the combined image") { url in
                        model.export(to: url, format: format, linear: linear) { err in
                            Task { @MainActor in if let err { model.errorMessage = err.localizedDescription } }
                        }
                        dismiss()
                    }
                }
                .buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
            }
        }
        .padding(16)
        .frame(width: 440)
    }
}
