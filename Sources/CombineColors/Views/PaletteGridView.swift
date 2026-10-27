import SwiftUI
import AstroCore

struct PaletteGridView: View {
    @Bindable var model: ProjectModel
    @Binding var showMatrixEditor: Bool
    @Binding var showPixelMath: Bool

    private var palettes: [Palette] { Palettes.available(for: model.availableKinds) }

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            HStack {
                MenuPicker(title: "Thumbnails", selection: $model.thumbnailRegion, options: ThumbnailRegion.allCases) { $0.displayName }
                    .controlSize(.small)
                Spacer()
                Button("Matrix…") { showMatrixEditor = true }.controlSize(.small)
                Button("Pixel Math…") { showPixelMath = true }.controlSize(.small)
            }
            if palettes.isEmpty {
                Text("Load channels to see palettes.").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(Palettes.groups, id: \.self) { group in
                let items = palettes.filter { $0.group == group }
                if !items.isEmpty {
                    Text(group).font(.caption).foregroundStyle(.secondary).padding(.top, 2)
                    LazyVGrid(columns: [GridItem(.adaptive(minimum: 92, maximum: 120), spacing: 6)], spacing: 6) {
                        ForEach(items) { p in
                            PaletteCell(palette: p, image: model.thumbnails[p.id], selected: model.settings.paletteID == p.id)
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

    var body: some View {
        VStack(spacing: 2) {
            ZStack {
                RoundedRectangle(cornerRadius: 4).fill(Color.black.opacity(0.5))
                if let img = image {
                    Image(decorative: img, scale: 1).resizable().aspectRatio(contentMode: .fill)
                        .frame(height: 64).clipShape(RoundedRectangle(cornerRadius: 4))
                } else {
                    ProgressView().controlSize(.mini)
                }
                RoundedRectangle(cornerRadius: 4).strokeBorder(selected ? Color.accentColor : Color.white.opacity(0.15), lineWidth: selected ? 2 : 1)
                if palette.isPixelMath {
                    VStack { HStack { Spacer(); Text("ƒ").font(.caption2).bold().padding(2).background(.black.opacity(0.5)).clipShape(RoundedRectangle(cornerRadius: 3)).padding(2) }; Spacer() }
                }
            }
            .frame(height: 64)
            Text(palette.name).font(.caption2).lineLimit(1).foregroundStyle(selected ? Color.accentColor : .primary)
        }
        .help(palette.description)
    }
}
