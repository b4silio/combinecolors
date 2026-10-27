import SwiftUI
import AstroCore

struct ContentView: View {
    @Bindable var model: ProjectModel
    @Environment(\.undoManager) private var undoManager
    @State private var showMatrixEditor = false
    @State private var showPixelMath = false
    @State private var showExport = false
    @State private var showDualBand = false
    @State private var dropTargeted = false
    @State private var zoomRequest: (ZoomAction, Int)? = nil
    @State private var zoomCounter = 0
    @State private var showHistogram = true

    var body: some View {
        HSplitView {
            VStack(spacing: 0) {
                ChannelStripView(model: model)
                Divider()
                ZStack(alignment: .topTrailing) {
                    PreviewView(model: model, zoomRequest: $zoomRequest)
                    if model.sources.isEmpty { emptyState }
                    previewToolbar
                    if showHistogram, !model.histogram.isEmpty {
                        VStack { Spacer(); HStack { HistogramView(histogram: model.histogram).frame(width: 220, height: 70).padding(8); Spacer() } }
                    }
                }
                Divider()
                StatusBar(model: model)
            }
            .frame(minWidth: 600)
            InspectorView(model: model, showMatrixEditor: $showMatrixEditor, showPixelMath: $showPixelMath, showExport: $showExport, showDualBand: $showDualBand)
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
        .sheet(isPresented: $showExport) { ExportSheet(model: model) }
        .sheet(isPresented: $showDualBand) { DualBandSplitView(model: model) }
        .sheet(item: $model.pendingColorFile) { pending in
            colorFileSheet(pending)
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
            Text("Hα, OIII, SII, Hβ, L, R, G, B are detected from the FITS FILTER keyword or the file name.\nColour images can be split into their dual-band lines.").multilineTextAlignment(.center).foregroundStyle(.secondary)
            HStack {
                Button("Open images…") { Panels.openImages { model.loadFiles($0) } }
                Button("Split dual-band…") { showDualBand = true }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .allowsHitTesting(true)
    }

    private var previewToolbar: some View {
        HStack(spacing: 4) {
            if model.cropEditing {
                Text("Crop: drag to draw, Return to apply, Esc to cancel").font(.caption).padding(.horizontal, 6)
                Button("Apply") { model.commitCropEditing() }
                Button("Cancel") { model.cancelCropEditing() }
            } else {
                Button { model.beginCropEditing() } label: { Image(systemName: "crop") }.help("Set crop (applies to every channel)").disabled(model.sources.isEmpty)
                if model.crop != nil {
                    Toggle(isOn: $model.showCropOverlay) { Image(systemName: "rectangle.dashed") }.help("Show crop limits")
                }
                Toggle(isOn: $showHistogram) { Image(systemName: "chart.bar.xaxis") }.help("Histogram")
                Divider().frame(height: 14)
                Button("Store A") { model.snapshotA = model.settings; model.showingSnapshot = false }
                    .help("Remember the current settings as version A for comparison").disabled(model.sources.isEmpty)
                Toggle(isOn: $model.showingSnapshot) { Text("Show A") }
                    .help("Show the stored version A instead of the current settings (hold to compare)").disabled(model.snapshotA == nil)
                if model.snapshotA != nil {
                    Button("Restore A") { if let a = model.snapshotA { model.showingSnapshot = false; model.settings = a } }.help("Go back to version A")
                }
                Divider().frame(height: 14)
                Button { zoom(.zoomOut) } label: { Image(systemName: "minus.magnifyingglass") }
                Button { zoom(.zoomIn) } label: { Image(systemName: "plus.magnifyingglass") }
                Button("Fit") { zoom(.fit) }
                Button("1:1") { zoom(.actualSize) }
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
            Button { showExport = true } label: { Label("Export", systemImage: "square.and.arrow.up") }.disabled(model.sources.isEmpty)
        }
    }

    private func zoom(_ a: ZoomAction) { zoomCounter += 1; zoomRequest = (a, zoomCounter) }

    private func colorFileSheet(_ pending: PendingFile) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("\(pending.file.fileName) is a colour image").font(.headline)
            Text("Use it as the R, G and B channels (e.g. an RGB stars image), or split it into its dual-band emission lines first.").font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            HStack {
                Button("Cancel") { model.pendingColorFile = nil }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Split dual-band…") { model.pendingColorFile = nil; dualBandFile = pending.file; showDualBandFile = true }
                Button("Use as RGB") { model.assignColorFileAsRGB(pending) }.buttonStyle(.borderedProminent).keyboardShortcut(.defaultAction)
            }
        }
        .padding(16).frame(width: 440)
        .sheet(isPresented: $showDualBandFile) { DualBandSplitView(model: model, initialFile: dualBandFile) }
    }

    @State private var dualBandFile: LoadedFile? = nil
    @State private var showDualBandFile = false

    private func assignSheet(_ pending: PendingFile) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Which channel is \(pending.file.fileName)?").font(.headline)
            Text("The channel could not be detected from the file name or metadata. \(pending.file.width)×\(pending.file.height) · \(pending.file.formatDescription)").font(.caption).foregroundStyle(.secondary)
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
