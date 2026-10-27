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
        HStack(spacing: 6) {
            Text(title)
                .frame(width: 92, alignment: .leading)
                .lineLimit(1)
                .onTapGesture(count: 2) { value = defaultValue }
                .help(help ?? "Double-click to reset")
            Slider(value: position, in: 0...1)
            TextField("", value: $value, formatter: SliderRow.formatter(format))
                .textFieldStyle(.roundedBorder)
                .frame(width: 58)
                .multilineTextAlignment(.trailing)
                .font(.system(.caption, design: .monospaced))
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

struct SectionBox<Content: View>: View {
    let title: String
    var subtitle: String? = nil
    @ViewBuilder var content: Content
    @State private var expanded = true

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Button { withAnimation(.easeOut(duration: 0.15)) { expanded.toggle() } } label: {
                HStack {
                    Image(systemName: expanded ? "chevron.down" : "chevron.right").font(.caption).frame(width: 10)
                    Text(title).font(.headline)
                    if let s = subtitle { Text(s).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
                    Spacer()
                }
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            if expanded {
                VStack(alignment: .leading, spacing: 6) { content }
                    .padding(.leading, 4)
            }
        }
        .padding(8)
        .background(RoundedRectangle(cornerRadius: 6).fill(Color(nsColor: .controlBackgroundColor).opacity(0.5)))
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
            Text(title)
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
