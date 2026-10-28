import SwiftUI
import AppKit
import UniformTypeIdentifiers
import AstroCore

/// Labelled slider with an editable numeric field; double-click the label to reset.
struct SliderRow: View {
    let title: String
    @Binding var value: Float
    var range: ClosedRange<Float>
    var defaultValue: Float
    var format: String = "%.2f"
    var logarithmic: Bool = false
    var help: String? = nil
    /// Small grey note after the title, e.g. the radius of a filter.
    var note: String? = nil
    var labelWidth: CGFloat = 70

    private var position: Binding<Double> {
        Binding(
            get: {
                if logarithmic { return Double(log(max(value, range.lowerBound)) - log(range.lowerBound)) / Double(log(range.upperBound) - log(range.lowerBound)) }
                return Double((value - range.lowerBound) / (range.upperBound - range.lowerBound))
            },
            set: { p in
                let t = Float(min(max(p, 0), 1))
                if logarithmic { value = exp(log(range.lowerBound) + t * (log(range.upperBound) - log(range.lowerBound))) }
                else { value = range.lowerBound + t * (range.upperBound - range.lowerBound) }
            })
    }

    var body: some View {
        HStack(spacing: 4) {
            (Text(title) + Text(note.map { " (\($0))" } ?? "").font(.system(size: 9)).foregroundColor(.secondary))
                .frame(width: labelWidth, alignment: .leading)
                .lineLimit(1)
                .onTapGesture(count: 2) { value = defaultValue }
                .help(help ?? "Double-click or right-click to reset")
            Slider(value: position, in: 0...1)
                .overlay(RightClickReset { value = defaultValue })
            TextField("", value: $value, formatter: SliderRow.formatter(format))
                .textFieldStyle(.roundedBorder)
                .frame(width: 46)
                .multilineTextAlignment(.trailing)
                .font(.system(size: 10, design: .monospaced))
        }
        .controlSize(.small)
    }

    static func formatter(_ fmt: String) -> NumberFormatter {
        let f = NumberFormatter()
        f.numberStyle = .decimal
        let decimals = fmt.contains(".") ? Int(fmt.split(separator: ".").last!.filter(\.isNumber)) ?? 2 : 0
        f.minimumFractionDigits = 0
        f.maximumFractionDigits = decimals
        return f
    }
}

/// Transparent overlay that only reacts to right-clicks (and control-clicks); every other
/// event goes through to the view underneath. Used to reset sliders.
struct RightClickReset: NSViewRepresentable {
    var action: () -> Void

    final class RightClickView: NSView {
        var action: () -> Void = {}
        override func hitTest(_ point: NSPoint) -> NSView? {
            guard let e = NSApp.currentEvent, e.type == .rightMouseDown || (e.type == .leftMouseDown && e.modifierFlags.contains(.control)) else { return nil }
            return super.hitTest(point)
        }
        override func rightMouseDown(with event: NSEvent) { action() }
        override func mouseDown(with event: NSEvent) { if event.modifierFlags.contains(.control) { action() } else { super.mouseDown(with: event) } }
    }

    func makeNSView(context: Context) -> RightClickView { let v = RightClickView(); v.action = action; return v }
    func updateNSView(_ v: RightClickView, context: Context) { v.action = action }
}

/// Lets a drag on empty space scroll the enclosing horizontal `ScrollView` (sweeping through the
/// blocks). The NSScrollView is located from a hidden background view.
final class ScrollDragState {
    weak var scrollView: NSScrollView?
    var startX: CGFloat? = nil

    var gesture: some Gesture {
        // Global coordinates: the view carrying the gesture sits inside the scroll view, so a local
        // translation would shift with every scroll it causes (the strip jumped back and forth).
        DragGesture(minimumDistance: 3, coordinateSpace: .global)
            .onChanged { [self] g in
                guard let sv = scrollView, let doc = sv.documentView else { return }
                if startX == nil { startX = sv.contentView.bounds.origin.x }
                let maxX = max(0, doc.frame.width - sv.contentView.bounds.width)
                let x = min(max(0, startX! - g.translation.width), maxX)
                sv.contentView.scroll(to: NSPoint(x: x, y: sv.contentView.bounds.origin.y))
                sv.reflectScrolledClipView(sv.contentView)
            }
            .onEnded { [self] _ in startX = nil }
    }
}

struct ScrollViewFinder: NSViewRepresentable {
    let state: ScrollDragState

    final class FinderView: NSView {
        var state: ScrollDragState?
        override func viewDidMoveToWindow() { super.viewDidMoveToWindow(); state?.scrollView = enclosingScrollView }
        override func hitTest(_ point: NSPoint) -> NSView? { nil }
    }

    func makeNSView(context: Context) -> FinderView { let v = FinderView(); v.state = state; return v }
    func updateNSView(_ v: FinderView, context: Context) { v.state = state; if let sv = v.enclosingScrollView { state.scrollView = sv } }
}

/// Block of the horizontal controls strip: a vertical title bar on the left (click to collapse or
/// expand) and, when expanded, the parameters in a fixed-width column that scrolls if needed.
struct ControlBlock<Content: View>: View {
    let title: String
    var width: CGFloat = 320
    @Binding var expanded: Bool
    /// Dragging on the block's empty space scrolls the strip.
    var dragScroll: ScrollDragState? = nil
    /// Visible height of the block (the content stretches to it so empty space stays draggable).
    var minHeight: CGFloat = 0
    @ViewBuilder var content: Content

    @ViewBuilder private func dragBackground(_ color: Color) -> some View {
        let bg = color.contentShape(Rectangle())
        if let d = dragScroll { bg.gesture(d.gesture) } else { bg }
    }

    var body: some View {
        HStack(spacing: 0) {
            Button { withAnimation(.easeOut(duration: 0.12)) { expanded.toggle() } } label: {
                Text(title.uppercased())
                    .font(.system(size: 11, weight: .bold))
                    .tracking(1)
                .lineLimit(1)
                .fixedSize()
                .rotationEffect(.degrees(-90))
                .frame(width: 22)
                .frame(maxHeight: .infinity)
                .background(expanded ? Color.accentColor.opacity(0.22) : Color(nsColor: .controlBackgroundColor).opacity(0.8))
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .foregroundStyle(expanded ? Color.accentColor : Color.secondary)
            .help(expanded ? "Collapse \(title)" : "Expand \(title)")
            if expanded {
                ScrollView(.vertical, showsIndicators: false) {
                    VStack(alignment: .leading, spacing: 2) { content }
                        .padding(.horizontal, 6)
                        .padding(.vertical, 5)
                        .frame(width: width, alignment: .leading)
                        // Fill the block so that a drag on the empty space below the controls scrolls the strip too.
                        .frame(minHeight: minHeight, alignment: .top)
                        .background { dragBackground(Color.clear) }
                }
            }
        }
        .background { dragBackground(Color(nsColor: .controlBackgroundColor).opacity(0.35)) }
        .clipShape(RoundedRectangle(cornerRadius: 6))
        .overlay(RoundedRectangle(cornerRadius: 6).strokeBorder(Color.primary.opacity(0.08)))
    }
}

enum DropHelpers {
    static func urls(from providers: [NSItemProvider], completion: @escaping ([URL]) -> Void) {
        let group = DispatchGroup()
        var urls: [URL] = []
        let lock = NSLock()
        for p in providers where p.hasItemConformingToTypeIdentifier(UTType.fileURL.identifier) {
            group.enter()
            p.loadItem(forTypeIdentifier: UTType.fileURL.identifier, options: nil) { item, _ in
                defer { group.leave() }
                var url: URL? = nil
                if let d = item as? Data { url = URL(dataRepresentation: d, relativeTo: nil) }
                else if let u = item as? URL { url = u }
                else if let s = item as? String { url = URL(string: s) }
                if let u = url { lock.lock(); urls.append(u); lock.unlock() }
            }
        }
        group.notify(queue: .main) { completion(urls.sorted { $0.lastPathComponent < $1.lastPathComponent }) }
    }

    static func expandDirectories(_ urls: [URL]) -> [URL] {
        var out: [URL] = []
        for u in urls {
            var isDir: ObjCBool = false
            if FileManager.default.fileExists(atPath: u.path, isDirectory: &isDir), isDir.boolValue {
                let items = (try? FileManager.default.contentsOfDirectory(at: u, includingPropertiesForKeys: nil)) ?? []
                out += items.filter { ImageLoader.isSupported($0) }.sorted { $0.lastPathComponent < $1.lastPathComponent }
            } else {
                out.append(u)
            }
        }
        return out
    }
}

enum Panels {
    static func openImages(completion: @escaping ([URL]) -> Void) {
        let p = NSOpenPanel()
        p.allowsMultipleSelection = true
        p.canChooseDirectories = true
        p.allowedContentTypes = [.tiff, .png, .jpeg, UTType("public.fits") ?? .data, .data]
        p.message = "Choose FITS or TIFF channel images (or a folder)."
        p.begin { r in if r == .OK { completion(DropHelpers.expandDirectories(p.urls)) } }
    }

    static func save(name: String, type: UTType, message: String, completion: @escaping (URL) -> Void) {
        let p = NSSavePanel()
        p.nameFieldStringValue = name
        p.allowedContentTypes = [type]
        p.message = message
        p.canCreateDirectories = true
        p.begin { r in if r == .OK, let u = p.url { completion(u) } }
    }

    static func openProject(completion: @escaping (URL) -> Void) {
        let p = NSOpenPanel()
        p.allowedContentTypes = [.json]
        p.message = "Choose a CombineColors project (.json)."
        p.begin { r in if r == .OK, let u = p.url { completion(u) } }
    }
}

extension Float {
    var g4: String { String(format: "%.4g", self) }
}

extension ChannelKind {
    var tint: Color {
        switch self {
        case .ha: return .red
        case .oiii: return .teal
        case .sii: return .orange
        case .hb: return .blue
        case .red: return Color(red: 1, green: 0.3, blue: 0.3)
        case .green: return Color(red: 0.3, green: 0.9, blue: 0.3)
        case .blue: return Color(red: 0.4, green: 0.5, blue: 1)
        case .luminance: return .gray
        }
    }
}

/// Drop-down picker built on `Menu`, so that scrolling the inspector never changes the selection
/// (menu-style `Picker`s react to the scroll wheel when the pointer passes over them).
struct MenuPicker<T: Hashable>: View {
    let title: String
    @Binding var selection: T
    let options: [T]
    let label: (T) -> String

    var body: some View {
        HStack {
            Text(title).fixedSize().lineLimit(1)
            Menu {
                ForEach(options, id: \.self) { o in
                    Button {
                        selection = o
                    } label: {
                        if o == selection { Label(label(o), systemImage: "checkmark") } else { Text(label(o)) }
                    }
                }
            } label: {
                Text(label(selection)).lineLimit(1)
            }
            .menuStyle(.borderedButton)
            .fixedSize()
        }
    }
}
