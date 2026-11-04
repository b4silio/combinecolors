import SwiftUI
import AppKit
import AstroCore

/// Star removal with StarNet2: guides the one-time setup (download or locate the program, accept
/// its license) and then runs it on the loaded channels. Written for people who rarely install
/// programs: one big button per step, plain words, no Terminal.
struct StarRemovalSheet: View {
    @Bindable var model: ProjectModel
    @Environment(\.dismiss) private var dismiss
    @AppStorage("starRemovalMethod") private var methodRaw = StarRemovalMethod.starnetLinear.rawValue
    @State private var selected: Set<String> = []
    @State private var folder: URL? = nil
    @State private var accepted = false
    @State private var showManualSteps = false
    @State private var showOptions = false

    private var setup: StarNetSetup { model.starNet }
    private var method: StarRemovalMethod { StarRemovalMethod(rawValue: methodRaw) ?? .starnetLinear }

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HStack(spacing: 10) {
                Image(systemName: "star.slash").font(.system(size: 26)).foregroundStyle(.tint)
                VStack(alignment: .leading, spacing: 2) {
                    Text("Remove Stars").font(.title2).bold()
                    Text("with StarNet2 by Mikita Misiura").font(.callout).foregroundStyle(.secondary)
                }
            }
            Divider()
            content
        }
        .padding(22)
        .frame(width: 640)
        .font(.system(size: 14))
        .controlSize(.large)
        .interactiveDismissDisabled(model.isRemovingStars)
        // The channels switch to the new files when the run ends: back to the main window.
        .onChange(of: model.isRemovingStars) { was, now in if was && !now { dismiss() } }
        .onAppear {
            if setup.phase == .checking || setup.tool == nil { setup.refresh() } else { setup.checkForUpdate() }
            selected = Set(model.starRemovalItems().filter { !$0.alreadyStarless }.map(\.id))
        }
    }

    @ViewBuilder private var content: some View {
        if model.isRemovingStars, let p = model.starRemovalProgress {
            runningView(p)
        } else {
            switch setup.phase {
            case .checking: checkingView
            case .missing: missingView
            case .downloading(let received, let total): downloadingView(received: received, total: total)
            case .installing: busyView("Unpacking StarNet2…")
            case .needsLicense: licenseView
            case .ready: readyView
            case .failed(let message): failedView(message)
            }
        }
    }

    // MARK: Setup steps

    private var checkingView: some View {
        VStack(alignment: .leading, spacing: 14) {
            busyView("Looking for StarNet2 on this Mac…")
            footer { Button("Close") { dismiss() }.keyboardShortcut(.cancelAction) }
        }
    }

    private var missingView: some View {
        VStack(alignment: .leading, spacing: 14) {
            Text("Star removal is done by **StarNet2**, a free program made by Mikita Misiura. It is not part of CombineColors: it has to be downloaded once from its official website, starnetastro.com.")
                .fixedSize(horizontal: false, vertical: true)
            Text("CombineColors can do this for you. It takes a minute or two (about \(sizeLabel) to download) and does not ask for your password. Afterwards you will be asked to read and accept the StarNet2 license.")
                .fixedSize(horizontal: false, vertical: true)
            Button { setup.download() } label: {
                Label("Download and Install StarNet2", systemImage: "arrow.down.circle.fill").frame(maxWidth: .infinity).padding(.vertical, 4)
            }
            .buttonStyle(.borderedProminent)
            // No Return shortcut: a stray key press must not start a 130 MB download.
            HStack(spacing: 12) {
                Button("I already have StarNet2…") { locate() }
                Button("Open the StarNet2 Website") { NSWorkspace.shared.open(StarNet.website) }
            }
            DisclosureGroup("I prefer to install it myself", isExpanded: $showManualSteps) { manualSteps.padding(.top, 6) }
            footer { Button("Close") { dismiss() }.keyboardShortcut(.cancelAction) }
        }
    }

    private var manualSteps: some View {
        VStack(alignment: .leading, spacing: 6) {
            step(1, "Click “Open the StarNet2 Website”.")
            step(2, StarNet.platformKey == "macos-arm64"
                 ? "In the macOS section, download the **ZIP** for **Apple Silicon (arm64, CoreML)**."
                 : "In the macOS section, download the **ZIP** for **Intel (x64)**.")
            step(3, "Open your Downloads folder. If the ZIP is still packed, double-click it to unpack it.")
            step(4, "Come back here, click “I already have StarNet2…” and choose the file called **starnet2** inside the unpacked folder (you can also choose the ZIP itself).")
            Text("If you used the StarNet2 installer (.pkg) instead, CombineColors finds it by itself — just open this window again.")
                .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func step(_ n: Int, _ text: LocalizedStringKey) -> some View {
        HStack(alignment: .firstTextBaseline, spacing: 8) {
            Text("\(n).").bold().frame(width: 18, alignment: .trailing)
            Text(text).fixedSize(horizontal: false, vertical: true)
        }
    }

    private func downloadingView(received: Int64, total: Int64) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Downloading StarNet2\(setup.latest.map { " \($0.version)" } ?? "") from starnetastro.com…")
            if total > 0 {
                ProgressView(value: Double(received), total: Double(total))
                Text("\(mb(received)) of \(mb(total))").font(.callout).foregroundStyle(.secondary)
            } else {
                ProgressView().progressViewStyle(.linear)
            }
            footer { Button("Cancel") { setup.cancelDownload() }.keyboardShortcut(.cancelAction) }
        }
    }

    private var licenseView: some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("StarNet2\(setup.info.map { " \($0.version)" } ?? "") is installed. Before CombineColors uses it, please read its license. In short: it is free for astrophotography, your images remain yours, and the program must not be passed on to others.")
                .fixedSize(horizontal: false, vertical: true)
            if let text = setup.licenseText {
                ScrollView {
                    Text(text).font(.system(size: 11, design: .monospaced)).textSelection(.enabled).frame(maxWidth: .infinity, alignment: .leading).padding(8)
                }
                .frame(height: 260)
                .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 6))
            } else {
                Text("The license file was not found next to the program. Please read it on the StarNet2 website.").foregroundStyle(.secondary)
                Button("Read the License on the Website") { NSWorkspace.shared.open(StarNet.website) }
            }
            Toggle("I have read and I accept the StarNet2 license agreement", isOn: $accepted)
            footer {
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Spacer()
                Button("Continue") { setup.acceptLicense() }.buttonStyle(.borderedProminent).disabled(!accepted).keyboardShortcut(.defaultAction)
            }
        }
    }

    private func failedView(_ message: String) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Label(message, systemImage: "exclamationmark.triangle.fill").foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            HStack(spacing: 12) {
                Button("Try Again") { setup.download() }.buttonStyle(.borderedProminent)
                Button("Choose the Program…") { locate() }
                Button("Open the Website") { NSWorkspace.shared.open(StarNet.website) }
            }
            DisclosureGroup("How to install it yourself", isExpanded: $showManualSteps) { manualSteps.padding(.top, 6) }
            footer { Button("Close") { dismiss() }.keyboardShortcut(.cancelAction) }
        }
    }

    // MARK: Ready

    private var readyView: some View {
        let items = model.starRemovalItems(folder: folder)
        let chosen = items.filter { selected.contains($0.id) }
        return VStack(alignment: .leading, spacing: 12) {
            if let info = setup.info {
                HStack(spacing: 6) {
                    Image(systemName: "checkmark.seal.fill").foregroundStyle(.green)
                    Text("StarNet2 \(info.version) is ready\(info.backend == "CoreML" ? " — it uses this Mac’s graphics processor" : "").").font(.callout)
                    if setup.updateAvailable, let l = setup.latest {
                        Spacer()
                        Button("Update to \(l.version)") { setup.download() }.controlSize(.small)
                    }
                }
            }
            if items.isEmpty {
                Text("Load your channels first, then open this window again.").foregroundStyle(.secondary)
                footer { Button("Close") { dismiss() }.keyboardShortcut(.cancelAction) }
            } else {
                Text("Choose the images to clean:").bold()
                VStack(alignment: .leading, spacing: 6) {
                    ForEach(items) { item in itemRow(item) }
                }
                .padding(10)
                .background(Color.primary.opacity(0.05), in: RoundedRectangle(cornerRadius: 6))
                if !model.channelsWithoutFile.isEmpty {
                    Text("\(model.channelsWithoutFile.map(\.shortName).joined(separator: ", ")) \(model.channelsWithoutFile.count == 1 ? "has" : "have") no file of its own and stays as it is.").font(.callout).foregroundStyle(.secondary)
                }
                Text("For each image, two new files are saved \(folder == nil ? "next to it" : "in \(folder!.lastPathComponent)"): one ending in **_bg** (the image without stars) and one ending in **_stars** (only the stars). The channels then switch to the _bg files. Your original files are not changed — File ▸ Open Recent Set brings them back.")
                    .fixedSize(horizontal: false, vertical: true)
                if chosen.contains(where: \.outputsExist) {
                    Label("_bg / _stars files from an earlier run will be replaced.", systemImage: "exclamationmark.triangle").foregroundStyle(.orange).font(.callout)
                }
                if model.editingTool != nil {
                    Label("Close the open editor (Dust, Gradient or Corrections) first.", systemImage: "info.circle").foregroundStyle(.orange).font(.callout)
                }
                DisclosureGroup("Options", isExpanded: $showOptions) {
                    VStack(alignment: .leading, spacing: 8) {
                        Picker("Brighten linear images with", selection: $methodRaw) {
                            ForEach(StarRemovalMethod.allCases) { Text($0.displayName).tag($0.rawValue) }
                        }
                        .pickerStyle(.radioGroup)
                        Text("StarNet2 was trained on stretched images. Linear data are stretched for it and the result is brought back to linear, so the normalisation and stretch of this project keep working. Images that already look stretched are processed as they are.")
                            .font(.callout).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        HStack {
                            Text("Save the new files:")
                            Text(folder?.path ?? "next to the originals").foregroundStyle(.secondary).lineLimit(1).truncationMode(.middle)
                            Spacer()
                            Button("Change…") { chooseFolder() }.controlSize(.regular)
                            if folder != nil { Button("Reset") { folder = nil }.controlSize(.regular) }
                        }
                        Button("Use a Different StarNet2 Program…") { locate() }.controlSize(.regular)
                    }
                    .padding(.top, 6)
                }
                Text("Each image takes about a minute.").font(.callout).foregroundStyle(.secondary)
                footer {
                    Button("Close") { dismiss() }.keyboardShortcut(.cancelAction)
                    Spacer()
                    Button {
                        model.removeStars(chosen, options: StarRemovalOptions(method: method, stretch: model.settings.stretch), folder: folder)
                    } label: {
                        Label(chosen.count > 1 ? "Remove Stars from \(chosen.count) Images" : "Remove Stars", systemImage: "star.slash").padding(.horizontal, 6)
                    }
                    .buttonStyle(.borderedProminent)
                    .keyboardShortcut(.defaultAction)
                    .disabled(chosen.isEmpty || model.editingTool != nil || model.isLoading)
                }
            }
        }
    }

    private func itemRow(_ item: StarRemovalItem) -> some View {
        Toggle(isOn: Binding(get: { selected.contains(item.id) }, set: { on in if on { selected.insert(item.id) } else { selected.remove(item.id) } })) {
            HStack(spacing: 8) {
                HStack(spacing: 3) {
                    ForEach(item.kinds, id: \.self) { k in
                        Text(k.shortName).font(.system(size: 11, weight: .bold)).foregroundStyle(.white)
                            .padding(.horizontal, 5).padding(.vertical, 1)
                            .background(k.tint.opacity(0.85), in: RoundedRectangle(cornerRadius: 3))
                    }
                }
                Text(item.source.lastPathComponent).lineLimit(1).truncationMode(.middle)
                if item.alreadyStarless { Text("already without stars").font(.callout).foregroundStyle(.secondary) }
            }
        }
    }

    // MARK: Running

    private func runningView(_ p: StarRemovalProgress) -> some View {
        VStack(alignment: .leading, spacing: 12) {
            Text("Removing stars…").bold()
            Text(p.label).lineLimit(1).truncationMode(.middle)
            ProgressView(value: p.fraction)
            Text(p.remaining.isEmpty ? "Starting…" : p.remaining).font(.callout).foregroundStyle(.secondary)
            Text("You can keep this window open; the channels switch to the new files when it is done.").font(.callout).foregroundStyle(.secondary)
            footer {
                Button("Stop") { model.stopStarRemoval() }.keyboardShortcut(.cancelAction)
                Spacer()
            }
        }
    }

    // MARK: Helpers

    private func busyView(_ text: String) -> some View {
        HStack(spacing: 10) { ProgressView().controlSize(.small); Text(text) }
    }

    private func footer<C: View>(@ViewBuilder _ c: () -> C) -> some View {
        HStack { c() }.padding(.top, 4)
    }

    private var sizeLabel: String {
        if let s = setup.latest?.sizeBytes ?? StarNetRelease.fallback().sizeBytes { return mb(Int64(s)) }
        return "130 MB"
    }

    private func mb(_ bytes: Int64) -> String { "\(Int((Double(bytes) / 1_000_000).rounded())) MB" }

    private func locate() {
        let panel = NSOpenPanel()
        panel.message = "Choose the file called “starnet2” (or the StarNet2 ZIP you downloaded)"
        panel.prompt = "Use This"
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        panel.directoryURL = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask).first
        if panel.runModal() == .OK, let u = panel.url { setup.choose(u) }
    }

    private func chooseFolder() {
        let panel = NSOpenPanel()
        panel.message = "Choose where to save the _bg and _stars files"
        panel.prompt = "Choose"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.canCreateDirectories = true
        if panel.runModal() == .OK, let u = panel.url { folder = u }
    }
}
