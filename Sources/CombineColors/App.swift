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
                Button("Remove All Channels") { model.removeAll() }.disabled(model.sources.isEmpty)
            }
            CommandGroup(after: .pasteboard) {
                Button("Copy Preview Image") { model.copyPreviewToPasteboard() }.keyboardShortcut("c", modifiers: [.command, .shift]).disabled(model.frame == nil)
            }
            CommandMenu("View") {
                Button("Fit to Window") { model.fitRequest += 1 }.keyboardShortcut("0")
                Toggle("High Quality Preview", isOn: Binding(get: { model.highQualityPreview }, set: { model.highQualityPreview = $0 }))
                Picker("Crop Limits", selection: Binding(get: { model.cropOverlay }, set: { model.cropOverlay = $0 })) {
                    ForEach(CropOverlayMode.allCases) { Text($0.displayName).tag($0) }
                }
                Divider()
                Button("Rotate Clockwise") { model.rotateClockwise() }.keyboardShortcut("r").disabled(model.sources.isEmpty || model.isLoading)
                Button("Flip Horizontal") { model.flipHorizontal() }.keyboardShortcut("h", modifiers: [.command, .shift]).disabled(model.sources.isEmpty || model.isLoading)
                Button("Flip Vertical") { model.flipVertical() }.keyboardShortcut("v", modifiers: [.command, .shift]).disabled(model.sources.isEmpty || model.isLoading)
                Button("Auto Trim Borders") { model.autoCrop() }.disabled(model.sources.isEmpty)
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
        NSApp.activate(ignoringOtherApps: true)
        let args = CommandLine.arguments.dropFirst().filter { !$0.hasPrefix("-") }
        let urls = args.map { URL(fileURLWithPath: $0) }.filter { FileManager.default.fileExists(atPath: $0.path) }
        if !urls.isEmpty { AppDelegate.deliver(urls) }
        // When files are given at launch AppKit treats them as documents and SwiftUI skips the
        // WindowGroup window (there is no DocumentGroup). A reopen request makes SwiftUI create it.
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.4) { AppDelegate.ensureWindow() }
    }

    static func ensureWindow() {
        guard !NSApp.windows.contains(where: { $0.isVisible && $0.canBecomeMain }) else { return }
        NSWorkspace.shared.openApplication(at: Bundle.main.bundleURL, configuration: NSWorkspace.OpenConfiguration())
    }

    func application(_ application: NSApplication, open urls: [URL]) { AppDelegate.deliver(urls) }

    static func deliver(_ urls: [URL]) {
        if let f = onOpen { f(urls) } else { pendingURLs += urls }
    }
}
