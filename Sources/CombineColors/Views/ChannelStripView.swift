import SwiftUI
import AstroCore

struct ChannelStripView: View {
    @Bindable var model: ProjectModel
    @State private var targeted: ChannelKind? = nil

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(alignment: .top, spacing: 6) {
                let loaded = ChannelKind.allCases.filter { model.sources[$0] != nil }
                let empty = ChannelKind.allCases.filter { model.sources[$0] == nil }
                ForEach(loaded + empty) { kind in
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
            .padding(.horizontal, 8)
            .padding(.vertical, 5)
        }
        .frame(height: 90)
    }
}

struct ChannelSlotView: View {
    @Bindable var model: ProjectModel
    let kind: ChannelKind
    var targeted: Bool

    private var source: ChannelSource? { model.sources[kind] }
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
                .strokeBorder(frameColor, style: StrokeStyle(lineWidth: targeted ? 3 : (source == nil ? 1 : 2), dash: source == nil ? [4, 3] : []))
        }
        .frame(width: source == nil ? 96 : 120, height: 80)
        .gesture(
            DragGesture(minimumDistance: 0)
                .onChanged { _ in if source != nil && model.peekChannel != kind { model.peekChannel = kind } }
                .onEnded { _ in model.peekChannel = nil }
        )
        .overlay(alignment: .topLeading) {
            if source != nil {
                Text(kind.shortName)
                    .font(.system(size: 9, weight: .bold))
                    .foregroundStyle(.white)
                    .padding(.horizontal, 4).padding(.vertical, 1)
                    .background(kind.tint.opacity(0.85), in: RoundedRectangle(cornerRadius: 3))
                    .padding(4)
                    .allowsHitTesting(false)
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
        .help(source.map { "\(kind.displayName) — \($0.fileName)\n\($0.formatDescription)\nmedian \($0.stats.median.g4), background \($0.maskedStats.background.g4), noise \($0.stats.noise.g4), signal \($0.maskedStats.signalStrength.g4)σ\nPress and hold to see this channel full size" } ?? "Drop a \(kind.displayName) image here")
    }
}

import UniformTypeIdentifiers
