import SwiftUI
import AstroCore

struct PaletteGridView: View {
    @Bindable var model: ProjectModel
    @Binding var showMatrixEditor: Bool
    @Binding var showPixelMath: Bool

    var body: some View {
        let palettes = model.visiblePalettes
        let hiddenCount = Palettes.available(for: model.availableKinds).filter { model.isPaletteHidden($0.id) }.count
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                MenuPicker(title: "From", selection: $model.thumbnailRegion, options: ThumbnailRegion.allCases) { $0.displayName }
                    .controlSize(.small)
                Spacer()
                Button("Matrix…") { showMatrixEditor = true }.controlSize(.small)
                Button("Pixel Math…") { showPixelMath = true }.controlSize(.small)
            }
            HStack {
                Picker("", selection: $model.paletteFilter) {
                    Text("Favourites").tag(ProjectModel.PaletteFilter.favorites)
                    Text("All").tag(ProjectModel.PaletteFilter.all)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .controlSize(.small)
                .frame(width: 160)
                Text(hiddenCount == 0 ? "Hover a thumbnail and click the eye to hide it." : "\(hiddenCount) hidden")
                    .font(.caption2).foregroundStyle(.secondary).lineLimit(1)
                Spacer()
            }
            if palettes.isEmpty {
                Text(model.sources.isEmpty ? "Load channels to see palettes." : "Everything is hidden — switch to All and click the eye icons.").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(Palettes.groups, id: \.self) { group in
                let items = palettes.filter { $0.group == group }
                if !items.isEmpty {
                    Text(group).font(.caption).foregroundStyle(.secondary).padding(.top, 2)
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 92, maximum: 120), spacing: 6)], spacing: 6) {
                        ForEach(items) { p in
                            PaletteCell(palette: p, image: model.thumbnails[p.id], selected: model.settings.paletteID == p.id,
                                        hidden: model.isPaletteHidden(p.id), showEye: model.paletteFilter == .all,
                                        toggleHidden: { model.setPalette(p.id, hidden: !model.isPaletteHidden(p.id)) })
                                .onTapGesture { model.select(palette: p) }
                        }
                    }
                }
            }
            if let p = model.currentPalette {
                Text(p.description).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true).padding(.top, 2)
            }
            let mode = model.settings.mode
            Text(mode == .pixelMath ? "Combination: Pixel Math (editable)" : "Combination: matrix (editable)").font(.caption2).foregroundStyle(.tertiary)
        }
    }
}

struct PaletteCell: View {
    let palette: Palette
    let image: CGImage?
    let selected: Bool
    var hidden: Bool = false
    /// Always show the eye button (All mode); otherwise only while hovering.
    var showEye: Bool = false
    var toggleHidden: () -> Void = {}
    @State private var hovering = false

    var body: some View {
        VStack(spacing: 2) {
            ZStack {
                RoundedRectangle(cornerRadius: 4).fill(Color.black.opacity(0.5))
                if let img = image {
                    Image(decorative: img, scale: 1).resizable().aspectRatio(contentMode: .fill)
                        .frame(height: 64).clipShape(RoundedRectangle(cornerRadius: 4))
                        .opacity(hidden ? 0.35 : 1)
                } else {
                    ProgressView().controlSize(.mini)
                }
                RoundedRectangle(cornerRadius: 4).strokeBorder(selected ? Color.accentColor : Color.white.opacity(0.15), lineWidth: selected ? 2 : 1)
                VStack {
                    HStack {
                        if showEye || hovering {
                            Button(action: toggleHidden) {
                                Image(systemName: hidden ? "eye.slash" : "eye")
                                    .font(.caption2)
                                    .padding(3)
                                    .background(.black.opacity(0.55), in: RoundedRectangle(cornerRadius: 3))
                            }
                            .buttonStyle(.plain)
                            .foregroundStyle(hidden ? Color.orange : Color.white)
                            .help(hidden ? "Show in favourites" : "Hide from favourites")
                        }
                        Spacer()
                        if palette.isPixelMath {
                            Text("ƒ").font(.caption2).bold().padding(2).background(.black.opacity(0.5)).clipShape(RoundedRectangle(cornerRadius: 3))
                        }
                    }
                    .padding(2)
                    Spacer()
                }
            }
            .frame(height: 64)
            Text(palette.name).font(.caption2).lineLimit(1).foregroundStyle(selected ? Color.accentColor : (hidden ? Color.secondary : Color.primary))
        }
        .onHover { hovering = $0 }
        .help(palette.description)
    }
}
