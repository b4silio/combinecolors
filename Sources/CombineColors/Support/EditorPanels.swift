import AppKit
import SwiftUI

/// Movable, non-modal tool windows (Pixel Math, matrix): unlike a sheet they leave the preview
/// visible and usable, so the effect of an edit shows right away. One panel per id; asking for an
/// open one brings it to the front. Panels float above the main window, hide while the app is in
/// the background and remember where they were put.
@MainActor
final class EditorPanels: NSObject, NSWindowDelegate {
    static let shared = EditorPanels()
    private var panels: [String: NSPanel] = [:]

    /// Shows the panel `id`; `content` gets the action that closes it (the editor's Done button).
    func show<V: View>(_ id: String, title: String, @ViewBuilder content: (_ close: @escaping () -> Void) -> V) {
        if let p = panels[id] { p.makeKeyAndOrderFront(nil); return }
        let host = NSHostingController(rootView: content({ [weak self] in self?.close(id) }))
        host.sizingOptions = [.preferredContentSize]
        let panel = NSPanel(contentRect: NSRect(origin: .zero, size: host.view.fittingSize),
                            styleMask: [.titled, .closable, .utilityWindow], backing: .buffered, defer: false)
        panel.title = title
        panel.contentViewController = host
        panel.isFloatingPanel = true
        panel.hidesOnDeactivate = true
        panel.becomesKeyOnlyIfNeeded = false
        panel.isReleasedWhenClosed = false
        panel.delegate = self
        // First time: at the right edge of the main window (over the palettes, away from the
        // picture); afterwards where it was left.
        if !panel.setFrameUsingName("EditorPanel." + id) {
            if let main = NSApp.mainWindow ?? NSApp.windows.first(where: { $0.isVisible && $0.canBecomeMain }) {
                let f = main.frame
                panel.setFrameTopLeftPoint(NSPoint(x: max(f.minX, f.maxX - panel.frame.width - 16), y: f.maxY - 60))
            } else {
                panel.center()
            }
        }
        panel.setFrameAutosaveName("EditorPanel." + id)
        panels[id] = panel
        panel.makeKeyAndOrderFront(nil)
    }

    func close(_ id: String) { panels[id]?.close() }

    nonisolated func windowWillClose(_ notification: Notification) {
        MainActor.assumeIsolated {
            guard let w = notification.object as? NSPanel, let id = panels.first(where: { $0.value === w })?.key else { return }
            panels[id] = nil
        }
    }
}
