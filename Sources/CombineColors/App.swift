import SwiftUI
import AstroCore

@main
struct CombineColorsApp: App {
    @State private var model = ProjectModel()
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
                Button("Remove All Channels") { model.removeAll() }.disabled(model.sources.isEmpty)
            }
            CommandMenu("View") {
                Button("Fit to Window") { model.fitRequest += 1 }.keyboardShortcut("0")
                Toggle("High Quality Preview", isOn: Binding(get: { model.highQualityPreview }, set: { model.highQualityPreview = $0 }))
                Toggle("Show Crop Limits", isOn: Binding(get: { model.showCropOverlay }, set: { model.showCropOverlay = $0 }))
                Divider()
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
    }

    func application(_ application: NSApplication, open urls: [URL]) { AppDelegate.deliver(urls) }

    static func deliver(_ urls: [URL]) {
        if let f = onOpen { f(urls) } else { pendingURLs += urls }
    }
}
