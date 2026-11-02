import SwiftUI
import AstroCore

/// Window layout: channel column | preview | palettes + presets, with the processing strip underneath.
struct ContentView: View {
    @Bindable var model: ProjectModel
    @Environment(\.undoManager) private var undoManager
    @State private var showMatrixEditor = false
    @State private var showPixelMath = false
    @State private var dropTargeted = false
    @State private var zoomRequest: (ZoomAction, Int)? = nil
    @State private var zoomCounter = 0

    var body: some View {
        VStack(spacing: 0) {
            HSplitView {
                HStack(spacing: 0) {
                    ChannelStripView(model: model)
                    Divider()
                    ZStack(alignment: .topTrailing) {
                        PreviewView(model: model, zoomRequest: $zoomRequest)
                        if model.sources.isEmpty { emptyState }
                        previewToolbar
                    }
                    .overlay(alignment: .bottomTrailing) { zoomToolbar }
                }
                .frame(minWidth: 560)
                .layoutPriority(1)
                VStack(spacing: 0) {
                    PaletteGridView(model: model, showMatrixEditor: $showMatrixEditor, showPixelMath: $showPixelMath)
                    Divider()
                    PresetBarView(model: model)
                }
                .frame(minWidth: 380, idealWidth: 460, maxWidth: 760)
            }
            Divider()
            ControlsStripView(model: model)
            Divider()
            StatusBar(model: model)
        }
        .frame(minWidth: 1100, minHeight: 700)
        .onDrop(of: [.fileURL], isTargeted: $dropTargeted) { providers in
            DropHelpers.urls(from: providers) { urls in model.loadFiles(DropHelpers.expandDirectories(urls)) }
            return true
        }
        .overlay {
            if dropTargeted {
                RoundedRectangle(cornerRadius: 8).strokeBorder(Color.accentColor, lineWidth: 3).padding(4).allowsHitTesting(false)
            }
        }
        .onAppear { model.undoManager = undoManager }
        .onChange(of: undoManager) { _, um in model.undoManager = um }
        .sheet(isPresented: $showMatrixEditor) { MatrixEditorView(model: model) }
        .sheet(isPresented: $showPixelMath) { PixelMathEditorView(model: model) }
        .sheet(isPresented: $model.exportRequested) { ExportSheet(model: model) }
        .sheet(item: $model.pendingColorFile) { pending in
            ColorFileSheet(model: model, pending: pending)
        }
        .sheet(item: Binding(get: { model.pendingFiles.first }, set: { _ in })) { pending in
            assignSheet(pending)
        }
        .alert("Error", isPresented: Binding(get: { model.errorMessage != nil }, set: { if !$0 { model.errorMessage = nil } })) {
            Button("OK") { model.errorMessage = nil }
        } message: { Text(model.errorMessage ?? "") }
        .toolbar { toolbarContent }
        .navigationTitle(model.projectURL?.deletingPathExtension().lastPathComponent ?? "CombineColors")
    }

    private var emptyState: some View {
        VStack(spacing: 10) {
            Image(systemName: "photo.stack").font(.system(size: 48)).foregroundStyle(.secondary)
            Text("Drop stacked FITS or TIFF channels here").font(.title2)
            Text("Hα, OIII, SII, Hβ, L, R, G, B are detected from the FITS FILTER keyword or the file name.\nA colour image can be used as RGB or split into its dual-band lines.").multilineTextAlignment(.center).foregroundStyle(.secondary)
            Button("Open images…") { Panels.openImages { model.loadFiles($0) } }
            if !model.recentSets.isEmpty {
                VStack(alignment: .leading, spacing: 4) {
                    Text("Recent sets").font(.headline).padding(.top, 12)
                    ForEach(model.recentSets.prefix(8)) { set in
                        Button {
                            model.openRecentSet(set)
                        } label: {
                            HStack {
                                Image(systemName: "photo.stack")
                                VStack(alignment: .leading) {
                                    Text(set.name).bold()
                                    Text(set.summary + "  ·  " + (set.folder as NSString).abbreviatingWithTildeInPath).font(.caption).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                                }
                                Spacer()
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                        .padding(6)
                        .background(RoundedRectangle(cornerRadius: 6).fill(Color(nsColor: .controlBackgroundColor).opacity(0.5)))
                        .contextMenu { Button("Remove from recent") { model.removeRecentSet(set) } }
                    }
                }
                .frame(width: 520)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .allowsHitTesting(true)
    }

    private var previewToolbar: some View {
        VStack(alignment: .trailing, spacing: 4) {
            if model.dustEditing != nil { dustToolbar }
            if model.gradientEditing != nil { gradientToolbar }
            HStack(spacing: 4) {
                if model.dustEditing != nil || model.gradientEditing != nil {
                    EmptyView()
                } else if model.cropEditing {
                    Button("Apply") { model.commitCropEditing() }.keyboardShortcut(.return, modifiers: [])
                    Button("Cancel") { model.cancelCropEditing() }.keyboardShortcut(.escape, modifiers: [])
                } else {
                    Button { model.beginCropEditing() } label: { Label("Crop", systemImage: "crop") }.disabled(model.sources.isEmpty)
                        .help("Draw a crop rectangle shared by every channel (Return applies, Esc cancels)")
                    Button("Auto") { model.autoCrop() }.disabled(model.sources.isEmpty)
                        .help("Crop away the empty borders left by registration")
                    if model.crop != nil {
                        Button("Clear") { model.clearCrop() }.help("Remove the crop")
                        TriStateCheckbox(title: "Limits", mode: $model.cropOverlay)
                            .fixedSize()
                            .help("Crop limits: unchecked hides them, partial shows the dashed frame over the whole image, checked shows only the cropped part")
                    }
                    Button { model.rotateClockwise() } label: { Label("Rotate", systemImage: "rotate.right") }
                        .disabled(model.sources.isEmpty || model.isLoading).help("Rotate every channel 90° clockwise (⌘R)")
                    Button { model.flipHorizontal() } label: { Image(systemName: "arrow.left.and.right.righttriangle.left.righttriangle.right") }
                        .disabled(model.sources.isEmpty || model.isLoading).help("Flip horizontally (⇧⌘H)")
                    Button { model.flipVertical() } label: { Image(systemName: "arrow.up.and.down.righttriangle.up.righttriangle.down") }
                        .disabled(model.sources.isEmpty || model.isLoading).help("Flip vertically (⇧⌘V)")
                }
            }
            if model.cropEditing, model.cropDraft != nil {
                // Not a force-unwrapping Binding($model.cropDraft): the fields can still be updated after
                // Cancel sets the draft to nil (focused text field committing), which would trap.
                let d = Binding(get: { model.cropDraft ?? PixelRect(x: 0, y: 0, width: 0, height: 0) }, set: { model.cropDraft = $0 })
                Grid(alignment: .trailing, horizontalSpacing: 4, verticalSpacing: 2) {
                    GridRow { Text("X"); TextField("x", value: d.x, format: .number).frame(width: 56) }
                    GridRow { Text("Y"); TextField("y", value: d.y, format: .number).frame(width: 56) }
                    GridRow { Text("W"); TextField("w", value: d.width, format: .number).frame(width: 56) }
                    GridRow { Text("H"); TextField("h", value: d.height, format: .number).frame(width: 56) }
                }
                .font(.caption)
                .textFieldStyle(.roundedBorder)
            }
        }
        .controlSize(.small)
        .padding(6)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 6))
        .padding(8)
    }

    /// Dust-spot editor: channel switch, model and parameters (of the selected spot, else of new
    /// spots), spot actions, before / after toggle.
    private var dustToolbar: some View {
        let kind = model.dustEditing ?? .luminance
        let draft = Binding(get: { model.dustDraft ?? DustSettings() }, set: { model.dustDraft = $0 })
        let loaded = ChannelKind.allCases.filter { model.sources[$0] != nil }
        let spots = model.dustSpots(for: kind)
        let selected = model.selectedDustSpotValue
        // Parameters: the selected spot's, or those given to new spots.
        let modelBinding = Binding<DustModel>(
            get: { selected?.model ?? draft.wrappedValue.model },
            set: { v in if var sp = model.selectedDustSpotValue { sp.model = v; model.updateSelectedDustSpot(sp) } else { draft.wrappedValue.model = v } })
        let smoothBinding = Binding<Float>(
            get: { Float(selected?.smoothness ?? draft.wrappedValue.smoothness) },
            set: { v in if var sp = model.selectedDustSpotValue { sp.smoothness = Double(v); model.updateSelectedDustSpot(sp) } else { draft.wrappedValue.smoothness = Double(v) } })
        let featherBinding = Binding<Float>(
            get: { Float(selected?.feather ?? draft.wrappedValue.feather) },
            set: { v in if var sp = model.selectedDustSpotValue { sp.feather = Double(v); model.updateSelectedDustSpot(sp) } else { draft.wrappedValue.feather = Double(v) } })
        return VStack(alignment: .trailing, spacing: 4) {
            HStack(spacing: 6) {
                Label("Dust spots", systemImage: "circle.dotted").font(.caption.bold())
                Picker("", selection: Binding(get: { kind }, set: { model.switchDustChannel($0) })) {
                    ForEach(loaded) { k in Text(k.shortName).tag(k) }
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize()
                .help("Channel shown; the spots of every channel are kept until Apply")
                Toggle(isOn: $model.dustBypass) { Text("Original") }
                    .toggleStyle(.button).keyboardShortcut("b", modifiers: [])
                    .help("Show the channel without any spot, to compare before and after (B)")
                Button("Apply") { model.commitDustEditing() }.keyboardShortcut(.return, modifiers: [])
                Button("Cancel") { model.cancelDustEditing() }.keyboardShortcut(.escape, modifiers: [])
            }
            HStack(spacing: 6) {
                Text(selected == nil ? "New spots" : "Selected").font(.caption).foregroundStyle(.secondary).frame(width: 60, alignment: .trailing)
                Picker("", selection: modelBinding) { ForEach(DustModel.allCases) { Text($0.displayName).tag($0) } }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                    .help("Local flat: free-form shadow (star-masked, multi-scale estimate of the ratio to the surrounding sky, noise left untouched), follows irregular motes and fringes.\nRadial profile: one transmission per distance to the centre, robust to nebulosity, for round motes and donuts.\nEdits the selected spot, or the parameters given to new spots.")
                SliderRow(title: "Smooth", value: smoothBinding, range: 1...12, defaultValue: 3, format: "%.1f",
                          help: "Finest scale of the shadow estimate in pixels (σ of the local flat, window of the radial profile)", labelWidth: 46)
                    .frame(width: 170)
                SliderRow(title: "Feather", value: featherBinding, range: 0...0.5, defaultValue: 0.2, format: "%.2f",
                          help: "Fraction of the radius over which the correction fades out beyond the shadow", labelWidth: 46)
                    .frame(width: 170)
            }
            HStack(spacing: 6) {
                Text("\(spots.count) spot\(spots.count == 1 ? "" : "s")").font(.caption).foregroundStyle(.secondary)
                Button("Fit") { model.refitSelectedDustSpot() }.disabled(selected == nil)
                    .help("Re-centre and resize the selected spot on the shadow found around it")
                Button("Delete") { model.removeSelectedDustSpot() }.disabled(selected == nil).help("Remove the selected spot (⌫)")
                Button("Clear") { model.clearDustSpots() }.disabled(spots.isEmpty).help("Remove every spot of this channel")
                Button("Copy to all") { model.copyDustSpotsToAllChannels() }.disabled(spots.isEmpty || loaded.count < 2)
                    .help("Give every other loaded channel the same spots (dust on the sensor window shadows every filter alike)")
            }
            if let sel = selected {
                let s = Binding(get: { model.selectedDustSpotValue ?? sel }, set: { model.updateSelectedDustSpot($0) })
                Grid(alignment: .trailing, horizontalSpacing: 4, verticalSpacing: 2) {
                    GridRow {
                        Text("X"); TextField("x", value: s.x, format: .number.precision(.fractionLength(1))).frame(width: 64)
                        Text("Y"); TextField("y", value: s.y, format: .number.precision(.fractionLength(1))).frame(width: 64)
                        Text("R"); TextField("r", value: s.radius, format: .number.precision(.fractionLength(1))).frame(width: 56)
                    }
                }
                .font(.caption)
                .textFieldStyle(.roundedBorder)
            }
            Text("Click a shadow to add a fitted spot · drag to move · drag the handle to resize · drag empty sky to pan")
                .font(.system(size: 9)).foregroundStyle(.secondary)
        }
    }

    /// Gradient editor: channel switch, guide shape and model parameters, protected regions,
    /// before / after toggle, fit read-out.
    private var gradientToolbar: some View {
        let kind = model.gradientEditing ?? .luminance
        let loaded = ChannelKind.allCases.filter { model.sources[$0] != nil }
        let spec = model.editedGradientSpec
        let defaults = model.gradientDraft ?? GradientSettings()
        // Parameters: the edited channel's model, or the defaults given to the next one.
        func edit<T>(_ get: @escaping (GradientSpec) -> T, _ fallback: T, _ set: @escaping (inout GradientSpec, T) -> Void, _ setDefault: @escaping (inout GradientSettings, T) -> Void) -> Binding<T> {
            Binding(get: { model.editedGradientSpec.map(get) ?? fallback },
                    set: { v in if var sp = model.editedGradientSpec { set(&sp, v); model.updateGradientSpec(sp) } else { var d = model.gradientDraft ?? GradientSettings(); setDefault(&d, v); model.gradientDraft = d } })
        }
        let shape = Binding<GradientShape>(get: { model.gradientShape }, set: { model.setGradientShape($0) })
        let mode = edit({ $0.mode }, defaults.mode, { $0.mode = $1 }, { $0.mode = $1 })
        let residual = edit({ $0.residual }, defaults.residual, { $0.residual = $1 }, { $0.residual = $1 })
        let sectors = edit({ $0.sectors }, defaults.sectors, { $0.sectors = $1 }, { $0.sectors = $1 })
        let degree = edit({ $0.degree }, defaults.degree, { $0.degree = $1 }, { $0.degree = $1 })
        let refine = edit({ $0.refine }, defaults.refine, { $0.refine = $1 }, { $0.refine = $1 })
        let smooth = edit({ Float($0.smoothness) }, Float(defaults.smoothness), { $0.smoothness = Double($1) }, { $0.smoothness = Double($1) })
        let fit = model.gradientFit.flatMap { $0.kind == kind ? $0 : nil }
        let regions = model.gradientRegions
        return VStack(alignment: .trailing, spacing: 4) {
            HStack(spacing: 6) {
                Label("Gradient", systemImage: "circle.lefthalf.filled").font(.caption.bold())
                Picker("", selection: Binding(get: { kind }, set: { model.switchGradientChannel($0) })) {
                    ForEach(loaded) { k in Text(k.shortName).tag(k) }
                }
                .pickerStyle(.segmented).labelsHidden().fixedSize()
                .help("Channel shown; the models of every channel are kept until Apply")
                Picker("", selection: $model.gradientView) { ForEach(GradientView.allCases) { Text($0.displayName).tag($0) } }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                    .help("Corrected: the channel with the model taken out.\nOriginal: the channel untouched (B toggles).\nBackground: the model alone — what is taken away — auto-stretched on its own (M toggles).")
                // Keyboard toggles for the view (invisible buttons carry the shortcuts).
                Button("") { model.gradientView = model.gradientView == .original ? .corrected : .original }
                    .keyboardShortcut("b", modifiers: []).buttonStyle(.plain).frame(width: 0, height: 0).opacity(0)
                Button("") { model.gradientView = model.gradientView == .background ? .corrected : .background }
                    .keyboardShortcut("m", modifiers: []).buttonStyle(.plain).frame(width: 0, height: 0).opacity(0)
                Button("Apply") { model.commitGradientEditing() }.keyboardShortcut(.return, modifiers: [])
                Button("Cancel") { model.cancelGradientEditing() }.keyboardShortcut(.escape, modifiers: [])
            }
            HStack(spacing: 6) {
                Picker("", selection: shape) { ForEach(GradientShape.allCases) { Text($0.displayName).tag($0) } }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                    .help("Line: the background varies along the line and is constant across it (light pollution from one side).\nEllipse: the background is a function of the distance to the centre, which may lie outside the frame (vignetting, a glow from a bright source).\nPolynomial: no guide, a plain 2-D polynomial of the chosen degree.")
                Picker("", selection: mode) { ForEach(GradientMode.allCases) { Text($0.displayName).tag($0) } }
                    .pickerStyle(.segmented).labelsHidden().fixedSize()
                    .help("Subtract: the background is taken off, the overall level kept (sky glow, stretched data).\nDivide: the pixels are divided by the background normalised to its brightest level (vignetting / missing flats on linear data: stars at the edge get their flux back).")
                MenuPicker(title: "Residual", selection: residual, options: GradientResidual.allCases) { $0.displayName }
                    .help("Low-order 2-D term fitted after the guided profile, for what the profile cannot explain (a sky gradient on top of the vignetting)")
                if shape.wrappedValue == .ellipse {
                    MenuPicker(title: "Sectors", selection: sectors, options: [1, 4, 8, 12]) { $0 == 1 ? "1 (round)" : "\($0)" }
                        .help("Angular sectors of the profile: 1 = the same fall-off in every direction; 4–12 let it differ with the direction (tilted sensor, asymmetric vignetting)")
                }
                if shape.wrappedValue == .polynomial {
                    MenuPicker(title: "Degree", selection: degree, options: [1, 2, 3, 4, 5, 6]) { "\($0)" }
                }
                SliderRow(title: "Smooth", value: smooth, range: 0...6, defaultValue: 1.5, format: "%.1f",
                          help: "Smoothing of the profile along the guide, in bins (120 bins across the frame)", labelWidth: 46)
                    .frame(width: 160)
            }
            HStack(spacing: 6) {
                if shape.wrappedValue != .polynomial {
                    Toggle("Refine", isOn: refine).toggleStyle(.checkbox)
                        .help("Optimise the drawing from the data (line direction; ellipse centre, axis ratio and angle). The guide used is shown dashed in orange when it differs from the drawing.")
                    Button("Adopt") { model.adoptRefinedGradient() }
                        .disabled(fit == nil || fit?.gesture == spec?.gesture)
                        .help("Replace the drawing by the refined guide")
                }
                Divider().frame(height: 14)
                Toggle(isOn: $model.gradientProtectMode) { Text("Protect") }
                    .toggleStyle(.button).keyboardShortcut("p", modifiers: [])
                    .help("Click the preview to add a protected region (nebula, galaxy) that is left out of the background estimate, for every channel; drag to move, drag the handles to resize, ⌫ removes the selected one (P)")
                Button("Delete") { model.removeSelectedGradientRegion() }.disabled(model.selectedGradientRegion == nil).help("Remove the selected protected region (⌫)")
                Button("Clear regions") { model.clearGradientRegions() }.disabled(regions.isEmpty)
                Divider().frame(height: 14)
                Button("Remove model") { model.clearGradient() }.disabled(spec == nil).help("Drop this channel's model (the channel is shown untouched)")
                Button("Copy to all") { model.copyGradientToAllChannels() }.disabled(spec == nil || loaded.count < 2)
                    .help("Give every other loaded channel the same model (vignetting is the same for every filter)")
            }
            HStack(spacing: 6) {
                if let f = fit {
                    let g = f.gesture
                    let geo: String = {
                        switch g.shape {
                        case .line: return String(format: "line %.0f,%.0f → %.0f,%.0f", g.x, g.y, g.x2, g.y2)
                        case .ellipse: return String(format: "ellipse centre %.0f,%.0f · axes %.0f × %.0f · %.0f°", g.x, g.y, g.a, g.b, g.angle * 180 / .pi)
                        case .polynomial: return "polynomial, degree \(spec?.degree ?? 0)"
                        }
                    }()
                    Text(verbatim: String(format: "Fit: %@ · residual %.2fσ · %d ms", geo, f.residualSigma, Int(f.millis)))
                        .font(.caption).foregroundStyle(.secondary).lineLimit(1)
                } else if spec == nil {
                    Text(shape.wrappedValue == .line ? "Drag along the gradient, from the dark side towards the bright side" : "Drag from the centre of the gradient outwards (the centre may be dragged outside the frame afterwards)")
                        .font(.caption).foregroundStyle(.secondary)
                }
            }
            Text("Drag the handles to adjust the guide · drag empty sky to pan · regions: Protect then click · B original, M background")
                .font(.system(size: 9)).foregroundStyle(.secondary)
        }
    }

    /// Bottom-right of the preview: quality and zoom.
    private var zoomToolbar: some View {
        HStack(spacing: 4) {
            Toggle(isOn: $model.highQualityPreview) { Label("HQ", systemImage: "sparkles") }.help("High quality preview (more pixels)")
            Divider().frame(height: 14)
            Button { zoom(.zoomOut) } label: { Image(systemName: "minus.magnifyingglass").font(.system(size: 11, weight: .medium)) }
            Button { zoom(.zoomIn) } label: { Image(systemName: "plus.magnifyingglass").font(.system(size: 11, weight: .medium)) }
            Button("Fit") { zoom(.fit) }
            Button("1:1") { zoom(.actualSize) }
            Divider().frame(height: 14)
            Button { model.copyPreviewToPasteboard() } label: { Image(systemName: "doc.on.doc").font(.system(size: 11, weight: .medium)) }
                .keyboardShortcut("c", modifiers: [.command, .shift])
                .disabled(model.frame == nil)
                .help("Copy the preview as shown to the clipboard (⇧⌘C)")
        }
        .controlSize(.small)
        .padding(6)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 6))
        .padding(8)
        .opacity(model.sources.isEmpty ? 0 : 1)
    }

    @ToolbarContentBuilder private var toolbarContent: some ToolbarContent {
        ToolbarItemGroup(placement: .primaryAction) {
            Button { Panels.openImages { model.loadFiles($0) } } label: { Label("Add images", systemImage: "plus") }.help("Add channel images")
            Button { model.exportRequested = true } label: { Label("Export", systemImage: "square.and.arrow.up") }.disabled(model.sources.isEmpty)
        }
    }

    private func zoom(_ a: ZoomAction) { zoomCounter += 1; zoomRequest = (a, zoomCounter) }

    private func assignSheet(_ pending: PendingFile) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Which channel is \(pending.file.fileName)?").font(.headline)
            Text(verbatim: "The channel could not be detected from the file name or metadata. \(pending.file.width) × \(pending.file.height) · \(pending.file.formatDescription)").font(.caption).foregroundStyle(.secondary)
            LazyVGrid(columns: [GridItem(.adaptive(minimum: 90))]) {
                ForEach(ChannelKind.allCases) { k in
                    Button { model.assignPending(pending, to: k) } label: {
                        HStack { Circle().fill(k.tint).frame(width: 8, height: 8); Text(k.displayName) }.frame(maxWidth: .infinity)
                    }
                }
            }
            HStack { Spacer(); Button("Skip") { model.pendingFiles.removeAll { $0.id == pending.id } }.keyboardShortcut(.cancelAction) }
        }
        .padding(16).frame(width: 420)
    }
}
