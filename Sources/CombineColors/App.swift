import SwiftUI
import AstroCore

@main
struct CombineColorsApp: App {
    @State private var model = ProjectModel(defaults: CombineColorsApp.launchDefaults)

    /// `-CCDefaults <suite>` on the command line keeps a test instance's recent sets (with their
    /// saved presets and dust spots) apart from the user's.
    static var launchDefaults: UserDefaults {
        if let suite = UserDefaults.standard.string(forKey: "CCDefaults"), !suite.isEmpty, let d = UserDefaults(suiteName: suite) { return d }
        return .standard
    }
    @NSApplicationDelegateAdaptor(AppDelegate.self) private var appDelegate

    var body: some Scene {
        WindowGroup {
            ContentView(model: model)
                .onAppear {
                    AppDelegate.onOpen = { urls in
                        let projects = urls.filter { $0.pathExtension.lowercased() == "json" }
                        if let p = projects.first { model.openProject(from: p) }
                        let images = DropHelpers.expandDirectories(urls.filter { $0.pathExtension.lowercased() != "json" })
                        if !images.isEmpty { model.loadFiles(images) }
                    }
                    if !AppDelegate.pendingURLs.isEmpty { AppDelegate.onOpen?(AppDelegate.pendingURLs); AppDelegate.pendingURLs = [] }
                }
        }
        .defaultSize(width: 1500, height: 950)
        .commands {
            CommandGroup(replacing: .newItem) {
                Button("Add Images…") { Panels.openImages { model.loadFiles($0) } }.keyboardShortcut("o")
                Button("Open Project…") { Panels.openProject { model.openProject(from: $0) } }.keyboardShortcut("o", modifiers: [.command, .shift])
                Menu("Open Recent Set") {
                    ForEach(model.recentSets) { set in
                        Button("\(set.name)  (\(set.summary))") { model.openRecentSet(set) }
                    }
                    if model.recentSets.isEmpty { Text("No recent sets") }
                    Divider()
                    Button("Clear Menu") { model.clearRecentSets() }.disabled(model.recentSets.isEmpty)
                }
                Divider()
                Button("Save Project") {
                    if let u = model.projectURL { try? model.saveProject(to: u) } else { saveProjectAs() }
                }.keyboardShortcut("s").disabled(model.sources.isEmpty)
                Button("Save Project As…") { saveProjectAs() }.keyboardShortcut("s", modifiers: [.command, .shift]).disabled(model.sources.isEmpty)
                Divider()
                Button("Export Combined Image…") { model.exportRequested = true }.keyboardShortcut("e").disabled(model.sources.isEmpty)
                Divider()
                Button("Close Image") { model.closeImage() }
                    .disabled((model.sources.isEmpty && !model.hasStars && model.projectURL == nil) || model.isRemovingStars)
            }
            CommandGroup(after: .pasteboard) {
                Button("Copy Preview Image") { model.copyPreviewToPasteboard() }.keyboardShortcut("c", modifiers: [.command, .shift]).disabled(model.frame == nil)
            }
            CommandMenu("Tools") {
                Button("Remove Stars with StarNet2…") {
                    // Star removal works on the Background tab's channels.
                    if model.activeLayer == .stars { model.switchLayer(to: .background) }
                    model.starRemovalRequested = model.activeLayer == .background
                }
            }
            CommandMenu("View") {
                // Always enabled: a disabled item would let ⌘1 / ⌘2 through to the preview as plain keys.
                Button("Background Tab") { model.switchLayer(to: .background) }.keyboardShortcut("1")
                Button("Stars Tab") { model.switchLayer(to: .stars) }.keyboardShortcut("2")
                Divider()
                Button("Fit to Window") { model.fitRequest += 1 }.keyboardShortcut("0")
                Toggle("High Quality Preview", isOn: Binding(get: { model.highQualityPreview }, set: { model.highQualityPreview = $0 }))
                Picker("Crop Limits", selection: Binding(get: { model.cropOverlay }, set: { model.cropOverlay = $0 })) {
                    ForEach(CropOverlayMode.allCases) { Text($0.displayName).tag($0) }
                }
                Divider()
                Button("Rotate Clockwise") { model.rotateClockwise() }.keyboardShortcut("r").disabled(model.sources.isEmpty || model.isLoading || model.editingTool != nil)
                Button("Flip Horizontal") { model.flipHorizontal() }.keyboardShortcut("h", modifiers: [.command, .shift]).disabled(model.sources.isEmpty || model.isLoading || model.editingTool != nil)
                Button("Flip Vertical") { model.flipVertical() }.keyboardShortcut("v", modifiers: [.command, .shift]).disabled(model.sources.isEmpty || model.isLoading || model.editingTool != nil)
                Button("Auto Trim Borders") { model.autoCrop() }.disabled(model.sources.isEmpty || model.editingTool != nil)
            }
        }
    }

    private func saveProjectAs() {
        Panels.save(name: "combination.json", type: .json, message: "Save the project (channel paths, crop and all settings)") { url in
            do { try model.saveProject(to: url) } catch { model.errorMessage = error.localizedDescription }
        }
    }
}

final class AppDelegate: NSObject, NSApplicationDelegate {
    /// Files passed on the command line or via Finder "Open With".
    static var pendingURLs: [URL] = []
    static var onOpen: (([URL]) -> Void)?

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }

    /// Files given at launch must not suppress the main window (there is no DocumentGroup).
    func applicationShouldOpenUntitledFile(_ sender: NSApplication) -> Bool { true }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool { true }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.regular)
        // A screenshot instance must not take the keyboard: keys meant for another app would press its buttons.
        if !AppDelegate.screenshotMode { NSApp.activate(ignoringOtherApps: true) }
        let args = CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") }
        let urls = args.map { URL(fileURLWithPath: $0) }.filter { FileManager.default.fileExists(atPath: $0.path) }
        if !urls.isEmpty { AppDelegate.deliver(urls) }
        // When files are given at launch AppKit treats them as documents and SwiftUI skips the
        // WindowGroup window (there is no DocumentGroup). A reopen request makes SwiftUI create it.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { AppDelegate.ensureWindow() }
        // Developer switch: `-screenshot <file.png> [-screenshotDelay <s>]` writes the main window
        // after the delay (default 8 s) and quits — no screen-recording permission needed.
        if let path = UserDefaults.standard.string(forKey: "screenshot"), !path.isEmpty {
            let delay = max(1, UserDefaults.standard.double(forKey: "screenshotDelay") == 0 ? 8 : UserDefaults.standard.double(forKey: "screenshotDelay"))
            DispatchQueue.main.asyncAfter(deadline: .now() + delay) {
                guard let main = NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain }) else { NSApp.terminate(nil); exit(0) }
                // An open sheet (e.g. `-openStarRemoval YES`) is captured instead of the window under it. Its
                // material background is not captured, so it is drawn in the light appearance (dark text on white).
                let target = main.attachedSheet ?? main
                if target !== main { target.appearance = NSAppearance(named: .aqua) }
                DispatchQueue.main.asyncAfter(deadline: .now() + (target === main ? 0 : 0.8)) {
                    if let v = target.contentView, let rep = v.bitmapImageRepForCachingDisplay(in: v.bounds) {
                        v.cacheDisplay(in: v.bounds, to: rep)
                        try? rep.representation(using: .png, properties: [:])?.write(to: URL(fileURLWithPath: path))
                    }
                    NSApp.terminate(nil)
                    exit(0)   // terminate is refused while a sheet is open
                }
            }
        }
    }

    static var screenshotMode: Bool { !(UserDefaults.standard.string(forKey: "screenshot") ?? "").isEmpty }

    static func ensureWindow() {
        guard !NSApp.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) else { return }
        let config = NSWorkspace.OpenConfiguration()
        config.activates = !screenshotMode
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: config)
    }

    func application(_ application: NSApplication, open urls: [URL]) { AppDelegate.deliver(urls) }

    static func deliver(_ urls: [URL]) {
        if let f = onOpen { f(urls) } else { pendingURLs += urls }
    }
}
