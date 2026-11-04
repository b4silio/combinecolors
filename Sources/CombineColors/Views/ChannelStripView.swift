import SwiftUI
import UniformTypeIdentifiers
import AstroCore

/// Column of channel slots on the left of the preview, with the preview histogram underneath.
/// Press and hold a slot to see that channel full size; keep the button down and move over
/// other slots to switch between them.
struct ChannelStripView: View {
    @Bindable var model: ProjectModel
    @State private var targeted: ChannelKind? = nil
    @State private var mouseUp = MouseUpWatcher()

    static let slotWidth: CGFloat = 120
    static let slotHeight: CGFloat = 80
    static let slotSpacing: CGFloat = 6
    static let padding: CGFloat = 6

    private var order: [ChannelKind] {
        let loaded = ChannelKind.allCases.filter { model.sources[$0] != nil }
        return loaded + ChannelKind.allCases.filter { model.sources[$0] == nil }
    }

    var body: some View {
        VStack(spacing: 0) {
            // The three channel editors; the open one is highlighted. While one is open a click on
            // a slot shows that channel in it (no peek).
            HStack(spacing: 2) {
                ForEach(ProjectModel.CorrectionTool.allCases) { tool in
                    let active = model.editingTool == tool
                    Button { model.beginEditing(tool) } label: {
                        VStack(spacing: 1) {
                            Image(systemName: tool.symbol).font(.system(size: 11))
                            Text(tool.title).font(.system(size: 9))
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 3)
                        .background(active ? Color.accentColor.opacity(0.35) : Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 4))
                    }
                    .buttonStyle(.plain)
                    .disabled(model.sources.isEmpty)
                    .help(tool.help + (active ? " (open)" : ""))
                }
            }
            .padding(.horizontal, Self.padding).padding(.top, Self.padding)
            // Star removal (StarNet2) replaces the channels by their starless versions (Background
            // tab only; kept in place on the Stars tab so the tabs below do not move).
            Button { model.starRemovalRequested = true } label: {
                Label("Remove Stars…", systemImage: "star.slash")
                    .font(.system(size: 10))
                    .frame(maxWidth: .infinity)
                    .padding(.vertical, 3)
                    .background(model.isRemovingStars ? Color.accentColor.opacity(0.35) : Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 4))
            }
            .buttonStyle(.plain)
            .disabled(model.sources.isEmpty || model.activeLayer == .stars)
            .help("Remove the stars of the Background channels with StarNet2 (a free program, downloaded once): writes _bg and _stars files, switches the channels to the _bg ones and puts the stars on the Stars tab")
            .padding(.horizontal, Self.padding).padding(.top, 3)
            // Background / Stars tabs: two sets of channels, each with its own settings.
            HStack(spacing: 2) {
                ForEach(ImageLayer.allCases) { layer in
                    let active = model.activeLayer == layer
                    let count = model.layerSources(layer).count
                    Button { model.switchLayer(to: layer) } label: {
                        HStack(spacing: 3) {
                            Image(systemName: layer.symbol).font(.system(size: 10))
                            Text(layer == .background ? "BG" : "Stars").font(.system(size: 11, weight: active ? .bold : .regular))
                            if count > 0 { Text("\(count)").font(.system(size: 9)).foregroundStyle(.secondary) }
                        }
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 4)
                        .background(active ? Color.accentColor.opacity(0.45) : Color.primary.opacity(0.06), in: RoundedRectangle(cornerRadius: 5))
                        .overlay(RoundedRectangle(cornerRadius: 5).strokeBorder(active ? Color.accentColor : .clear, lineWidth: 1))
                        .contentShape(Rectangle())
                    }
                    .buttonStyle(.plain)
                    .disabled(!active && !model.canSwitchLayer)
                    .help(layer == .background
                          ? "Background: the starless channels and their settings (⌘1). The Combine stars block blends the Stars tab in."
                          : "Stars: the star channels with settings of their own (⌘2) — from Remove Stars, Load RGB stars or dropped here.")
                }
            }
            .padding(.horizontal, Self.padding).padding(.top, 5)
            ScrollView(.vertical, showsIndicators: false) {
                VStack(spacing: Self.slotSpacing) {
                    ForEach(order) { kind in
                        ChannelSlotView(model: model, kind: kind, targeted: targeted == kind)
                            .onDrop(of: [.fileURL], isTargeted: Binding(get: { targeted == kind }, set: { targeted = $0 ? kind : (targeted == kind ? nil : targeted) })) { providers in
                                DropHelpers.urls(from: providers) { urls in
                                    let files = DropHelpers.expandDirectories(urls)
                                    guard let first = files.first else { return }
                                    model.loadFile(first, into: kind)
                                    if files.count > 1 { model.loadFiles(Array(files.dropFirst())) }
                                }
                                return true
                            }
                    }
                }
                .padding(Self.padding)
                // One gesture for the whole column so that a held press can move between slots.
                .gesture(
                    DragGesture(minimumDistance: 0, coordinateSpace: .local)
                        .onChanged { g in
                            guard model.editingTool == nil else { return }
                            let k = slot(at: g.location)
                            if model.peekChannel != k { model.peekChannel = k }
                        }
                        .onEnded { g in
                            // While an editor is open a click switches it to that channel.
                            if model.editingTool != nil, let k = slot(at: g.location) { model.switchEditingChannel(k) }
                            model.peekChannel = nil
                        }
                )
            }
            Divider()
            HistogramView(histogram: model.histogram)
                .frame(width: Self.slotWidth, height: 64)
                .padding(Self.padding)
        }
        .frame(width: Self.slotWidth + 2 * Self.padding)
        // The drag gesture gets no end when a menu or a sheet takes over the event loop; any mouse
        // up anywhere ends the peek so that it can never stick.
        .onAppear { mouseUp.install { [weak model] in if model?.peekChannel != nil { model?.peekChannel = nil } } }
        .onDisappear { mouseUp.remove() }
    }

    /// Loaded channel under a point of the column's content (nil over empty slots, gaps and the
    /// slot's menu button, whose click must not start a peek).
    private func slot(at p: CGPoint) -> ChannelKind? {
        let pitch = Self.slotHeight + Self.slotSpacing
        let y = p.y - Self.padding
        guard y >= 0 else { return nil }
        let i = Int(y / pitch)
        let dy = y - CGFloat(i) * pitch
        guard i < order.count, dy <= Self.slotHeight else { return nil }
        let k = order[i]
        guard model.sources[k] != nil else { return nil }
        if dy < 26, p.x - Self.padding > Self.slotWidth - 26 { return nil }
        return k
    }
}

/// Runs a closure on every left mouse up seen by the application (local event monitor).
final class MouseUpWatcher {
    private var token: Any?
    func install(_ action: @escaping () -> Void) {
        remove()
        token = NSEvent.addLocalMonitorForEvents(matching: [.leftMouseUp]) { e in action(); return e }
    }
    func remove() { if let t = token { NSEvent.removeMonitor(t); token = nil } }
    deinit { remove() }
}

struct ChannelSlotView: View {
    @Bindable var model: ProjectModel
    let kind: ChannelKind
    var targeted: Bool

    private var source: ChannelSource? { model.sources[kind] }
    private var peeking: Bool { model.peekChannel == kind }
    private var frameColor: Color { targeted ? Color.accentColor : (source == nil ? kind.tint.opacity(0.55) : kind.tint) }

    var body: some View {
        ZStack {
            RoundedRectangle(cornerRadius: 5)
                .fill(Color.black.opacity(source == nil ? 0.12 : 0.6))
            if let thumb = model.slotThumbnails[kind] {
                Image(decorative: thumb, scale: 1)
                    .resizable()
                    .aspectRatio(contentMode: .fit)
                    .clipShape(RoundedRectangle(cornerRadius: 3))
                    .padding(2)
            } else {
                VStack(spacing: 2) {
                    Image(systemName: "plus").foregroundStyle(.secondary)
                    Text("Drop \(kind.shortName)").font(.caption2).foregroundStyle(.secondary)
                }
            }
            // The channel is identified by the frame colour and a small badge.
            RoundedRectangle(cornerRadius: 5)
                .strokeBorder(frameColor, style: StrokeStyle(lineWidth: targeted || peeking ? 3 : (source == nil ? 1 : 2), dash: source == nil ? [4, 3] : []))
        }
        .frame(width: ChannelStripView.slotWidth, height: ChannelStripView.slotHeight)
        .overlay(alignment: .topLeading) {
            if source != nil {
                // Label, with the corrections badge underneath it (dust: bottom-left, gradient: bottom-right).
                VStack(alignment: .leading, spacing: 2) {
                    Text(kind.shortName)
                        .font(.system(size: 9, weight: .bold))
                        .foregroundStyle(.white)
                        .padding(.horizontal, 4).padding(.vertical, 1)
                        .background(kind.tint.opacity(0.85), in: RoundedRectangle(cornerRadius: 3))
                    if model.hasCorrection(kind) {
                        Image(systemName: "paintbrush.pointed.fill")
                            .font(.system(size: 9, weight: .semibold))
                            .foregroundStyle(.white)
                            .padding(.horizontal, 4).padding(.vertical, 1)
                            .background(Color.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 3))
                            .help("Hand-painted corrections applied to this channel")
                    }
                }
                .padding(4)
                .allowsHitTesting(false)
            }
        }
        .overlay(alignment: .bottomLeading) {
            let n = model.dustSpots(for: kind).count
            if source != nil, n > 0 {
                Label("\(n)", systemImage: "circle.dotted")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 4).padding(.vertical, 1)
                    .background(Color.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 3))
                    .padding(4)
                    .allowsHitTesting(false)
                    .help("\(n) dust spot\(n == 1 ? "" : "s") healed on this channel")
            }
        }
        .overlay(alignment: .bottomTrailing) {
            if source != nil, let spec = model.gradientSpec(for: kind) {
                Image(systemName: "circle.lefthalf.filled")
                    .font(.system(size: 9, weight: .semibold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 4).padding(.vertical, 1)
                    .background(Color.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 3))
                    .padding(4)
                    .allowsHitTesting(false)
                    .help("Gradient removed on this channel (\(spec.gesture.shape.displayName.lowercased()) guide, \(spec.mode.displayName.lowercased()))")
            }
        }
        .overlay(alignment: .topTrailing) {
            if let s = source {
                Menu {
                    Section("Move to") {
                        ForEach(ChannelKind.allCases.filter { $0 != kind }) { k in
                            Button(k.displayName + (model.sources[k] != nil ? " (swap)" : "")) { model.reassign(kind, to: k) }
                        }
                    }
                    Divider()
                    if model.hasCorrection(kind) { Button("Remove Corrections") { model.removeCorrection(kind) } }
                    Button("Save channel as FITS…") {
                        Panels.save(name: s.fileName.replacingOccurrences(of: ".fits", with: "") + "-\(kind.rawValue).fits", type: UTType("public.fits") ?? .data, message: "Save the channel (cropped if a crop is set).") { url in
                            try? model.exportChannel(kind, to: url, format: .fits32)
                        }
                    }
                    Button("Remove", role: .destructive) { model.removeChannel(kind) }
                } label: {
                    Image(systemName: "ellipsis.circle.fill")
                        .font(.system(size: 13))
                        .symbolRenderingMode(.palette)
                        .foregroundStyle(.white, Color.black.opacity(0.55))
                }
                .menuStyle(.borderlessButton).menuIndicator(.hidden)
                .frame(width: 18, height: 18)
                .padding(3)
            }
        }
        .help(source.map { "\(kind.displayName) — \($0.fileName)\n\($0.formatDescription)\nmedian \($0.stats.median.g4), background \($0.maskedStats.background.g4), noise \($0.stats.noise.g4), signal \($0.maskedStats.signalStrength.g4)σ\nPress and hold to see this channel full size; move over other slots to switch. While an editor is open, a click shows this channel in it" } ?? "Drop a \(kind.displayName) image here")
    }
}
