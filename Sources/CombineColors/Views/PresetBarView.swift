import SwiftUI
import AstroCore

/// Bar under the preview: saved presets as thumbnails, one click to switch.
struct PresetBarView: View {
    @Bindable var model: ProjectModel
    @State private var renaming: SettingsPreset? = nil
    @State private var renameText = ""

    var body: some View {
        HStack(spacing: 8) {
            Button {
                model.savePreset()
            } label: {
                VStack(spacing: 2) {
                    Image(systemName: "plus.square.on.square").font(.title3)
                    Text("Save preset").font(.caption2)
                }
                .frame(width: 70, height: 62)
            }
            .buttonStyle(.bordered)
            .disabled(model.sources.isEmpty || model.frame == nil)
            .help("Remember the current settings with a thumbnail")
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(model.presets) { p in
                        let active = model.activePresetID == p.id
                        VStack(spacing: 2) {
                            ZStack {
                                RoundedRectangle(cornerRadius: 4).fill(Color.black.opacity(0.5))
                                if let img = model.presetThumbnail(p) {
                                    Image(decorative: img, scale: 1).resizable().aspectRatio(contentMode: .fill)
                                        .frame(width: 84, height: 50).clipShape(RoundedRectangle(cornerRadius: 4))
                                        .allowsHitTesting(false)
                                }
                                RoundedRectangle(cornerRadius: 4).strokeBorder(active ? Color.accentColor : Color.white.opacity(0.15), lineWidth: active ? 2 : 1)
                            }
                            .frame(width: 84, height: 50)
                            .clipped()
                            Text(p.name).font(.caption2).lineLimit(1).frame(width: 84).foregroundStyle(active ? Color.accentColor : .primary)
                        }
                        .contentShape(Rectangle())
                        .onTapGesture { model.applyPreset(p) }
                        .contextMenu {
                            Button("Rename…") { renaming = p; renameText = p.name }
                            Button("Update with current settings") { model.updatePreset(p) }
                            Divider()
                            Button("Delete", role: .destructive) { model.deletePreset(p) }
                        }
                        .help(p.name + " — click to apply, right-click for options")
                    }
                    if model.presets.isEmpty {
                        Text("Save a preset to come back to a look later, then click thumbnails to compare them.")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }
                .padding(.vertical, 2)
            }
        }
        .padding(.horizontal, 8)
        .frame(height: 74)
        .background(Color(nsColor: .windowBackgroundColor))
        .alert("Rename preset", isPresented: Binding(get: { renaming != nil }, set: { if !$0 { renaming = nil } })) {
            TextField("Name", text: $renameText)
            Button("Rename") { if let p = renaming { model.renamePreset(p, to: renameText) }; renaming = nil }
            Button("Cancel", role: .cancel) { renaming = nil }
        }
    }
}
