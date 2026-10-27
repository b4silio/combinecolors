import SwiftUI
import AstroCore

/// Always-visible strip of palette thumbnails (one row, grouped, horizontally scrollable)
/// so that every slider change can be judged on all combinations at once.
struct PaletteStripView: View {
    @Bindable var model: ProjectModel
    @Binding var showMatrixEditor: Bool
    @Binding var showPixelMath: Bool
    @AppStorage("paletteThumbSize") private var thumbWidth: Double = 112

    private var palettes: [Palette] { Palettes.available(for: model.availableKinds) }

    var body: some View {
        HStack(alignment: .top, spacing: 0) {
            VStack(alignment: .leading, spacing: 6) {
                Text("Palettes").font(.headline)
                MenuPicker(title: "From", selection: $model.thumbnailRegion, options: ThumbnailRegion.allCases) { $0.displayName }
                    .controlSize(.small)
                HStack(spacing: 4) {
                    Button("Matrix…") { showMatrixEditor = true }
                    Button("Pixel Math…") { showPixelMath = true }
                }
                .controlSize(.small)
                Slider(value: $thumbWidth, in: 72...200).controlSize(.mini).frame(width: 120).help("Thumbnail size")
                if let p = model.currentPalette {
                    Text(p.description).font(.caption2).foregroundStyle(.secondary).lineLimit(3).frame(width: 150, alignment: .leading)
                } else if model.settings.mode == .pixelMath {
                    Text("Custom Pixel Math").font(.caption2).foregroundStyle(.secondary)
                }
            }
            .padding(8)
            .frame(width: 170, alignment: .leading)
            Divider()
            ScrollView(.horizontal, showsIndicators: true) {
                HStack(alignment: .top, spacing: 14) {
                    if palettes.isEmpty {
                        Text("Load channels to see palettes.").font(.caption).foregroundStyle(.secondary).padding()
                    }
                    ForEach(Palettes.groups, id: \.self) { group in
                        let items = palettes.filter { $0.group == group }
                        if !items.isEmpty {
                            VStack(alignment: .leading, spacing: 3) {
                                Text(group).font(.caption).foregroundStyle(.secondary)
                                HStack(alignment: .top, spacing: 6) {
                                    ForEach(items) { p in
                                        PaletteCell(palette: p, image: model.thumbnails[p.id], selected: model.settings.paletteID == p.id, width: thumbWidth)
                                            .onTapGesture { model.select(palette: p) }
                                    }
                                }
                            }
                        }
                    }
                }
                .padding(8)
            }
        }
        .frame(height: thumbWidth * 0.68 + 52)
        .background(Color(nsColor: .windowBackgroundColor))
    }
}

struct PaletteCell: View {
    let palette: Palette
    let image: CGImage?
    let selected: Bool
    var width: Double = 112

    var body: some View {
        VStack(spacing: 2) {
            ZStack {
                RoundedRectangle(cornerRadius: 4).fill(Color.black.opacity(0.5))
                if let img = image {
                    Image(decorative: img, scale: 1).resizable().aspectRatio(contentMode: .fill)
                        .frame(width: width, height: width * 0.68).clipShape(RoundedRectangle(cornerRadius: 4))
                } else {
                    ProgressView().controlSize(.mini)
                }
                RoundedRectangle(cornerRadius: 4).strokeBorder(selected ? Color.accentColor : Color.white.opacity(0.15), lineWidth: selected ? 2 : 1)
                if palette.isPixelMath {
                    VStack { HStack { Spacer(); Text("ƒ").font(.caption2).bold().padding(2).background(.black.opacity(0.5)).clipShape(RoundedRectangle(cornerRadius: 3)).padding(2) }; Spacer() }
                }
            }
            .frame(width: width, height: width * 0.68)
            Text(palette.name).font(.caption2).lineLimit(1).foregroundStyle(selected ? Color.accentColor : .primary).frame(width: width)
        }
        .help(palette.description)
    }
}
