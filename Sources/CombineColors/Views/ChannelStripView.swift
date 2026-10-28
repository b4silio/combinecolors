import SwiftUI
import AstroCore

struct ChannelStripView: View {
    @Bindable var model: ProjectModel
    @State private var targeted: ChannelKind? = nil

    var body: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(alignment: .top, spacing: 8) {
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
            .padding(.vertical, 6)
        }
        .frame(height: 142)
    }
}

struct ChannelSlotView: View {
    @Bindable var model: ProjectModel
    let kind: ChannelKind
    var targeted: Bool

    var body: some View {
        let source = model.sources[kind]
        VStack(spacing: 3) {
            ZStack {
                RoundedRectangle(cornerRadius: 4)
                    .fill(Color.black.opacity(source == nil ? 0.15 : 0.6))
                if let thumb = model.slotThumbnails[kind] {
                    Image(decorative: thumb, scale: 1)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                        .clipShape(RoundedRectangle(cornerRadius: 4))
                } else {
                    VStack(spacing: 2) {
                        Image(systemName: "plus").foregroundStyle(.secondary)
                        Text("Drop \(kind.shortName)").font(.caption2).foregroundStyle(.secondary)
                    }
                }
                RoundedRectangle(cornerRadius: 4)
                    .strokeBorder(targeted ? Color.accentColor : (source == nil ? Color.secondary.opacity(0.3) : kind.tint.opacity(0.8)), style: StrokeStyle(lineWidth: targeted ? 2 : 1, dash: source == nil ? [4, 3] : []))
            }
            .frame(width: source == nil ? 86 : 132, height: 88)
            .gesture(
                DragGesture(minimumDistance: 0)
                    .onChanged { _ in if source != nil && model.peekChannel != kind { model.peekChannel = kind } }
                    .onEnded { _ in model.peekChannel = nil }
            )
            .help(source == nil ? "" : "Press and hold to see this channel full size")
            HStack(spacing: 3) {
                Circle().fill(kind.tint).frame(width: 7, height: 7)
                Text(kind.displayName).font(.caption).bold()
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
                    } label: { Image(systemName: "ellipsis.circle") }
                        .menuStyle(.borderlessButton).menuIndicator(.hidden).frame(width: 16)
                }
            }
            if let s = source {
                Text(s.fileName).font(.caption2).foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle).frame(width: 132)
                Text("med \(s.stats.median.g4) · σ \(s.stats.noise.g4)").font(.system(size: 9, design: .monospaced)).foregroundStyle(.secondary)
            } else {
                Text(" ").font(.caption2)
            }
        }
        .help(source.map { "\($0.fileName)\n\($0.formatDescription)\nbackground \($0.maskedStats.background.g4), noise \($0.stats.noise.g4), signal \($0.maskedStats.signalStrength.g4)σ" } ?? "Drop a \(kind.displayName) image here")
    }
}

import UniformTypeIdentifiers
