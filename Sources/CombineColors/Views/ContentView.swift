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
            HStack(spacing: 4) {
                if model.cropEditing {
                    Button("Apply") { model.commitCropEditing() }.keyboardShortcut(.return, modifiers: [])
                    Button("Cancel") { model.cancelCropEditing() }.keyboardShortcut(.escape, modifiers: [])
                } else {
                    Button { model.beginCropEditing() } label: { Label("Crop", systemImage: "crop") }.disabled(model.sources.isEmpty)
                        .help("Draw a crop rectangle shared by every channel (Return applies, Esc cancels)")
                    Button("Auto") { model.autoCrop() }.disabled(model.sources.isEmpty)
                        .help("Crop away the empty borders left by registration")
                    if model.crop != nil {
                        Toggle(isOn: $model.showCropOverlay) { Label("Limits", systemImage: "rectangle.dashed") }
                        Button("Clear") { model.clearCrop() }.help("Remove the crop")
                    }
                    Button { model.rotateClockwise() } label: { Label("Rotate", systemImage: "rotate.right") }
                        .disabled(model.sources.isEmpty || model.isLoading).help("Rotate every channel 90° clockwise (⌘R)")
                    Button { model.flipHorizontal() } label: { Image(systemName: "arrow.left.and.right.righttriangle.left.righttriangle.right") }
                        .disabled(model.sources.isEmpty || model.isLoading).help("Flip horizontally (⇧⌘H)")
                    Button { model.flipVertical() } label: { Image(systemName: "arrow.up.and.down.righttriangle.up.righttriangle.down") }
                        .disabled(model.sources.isEmpty || model.isLoading).help("Flip vertically (⇧⌘V)")
                    Divider().frame(height: 14)
                    Toggle(isOn: $model.highQualityPreview) { Label("HQ", systemImage: "sparkles") }.help("High quality preview (more pixels)")
                    Divider().frame(height: 14)
                    Button { zoom(.zoomOut) } label: { Image(systemName: "minus.magnifyingglass") }
                    Button { zoom(.zoomIn) } label: { Image(systemName: "plus.magnifyingglass") }
                    Button("Fit") { zoom(.fit) }
                    Button("1:1") { zoom(.actualSize) }
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
