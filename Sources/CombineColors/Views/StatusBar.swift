import SwiftUI
import AstroCore

struct StatusBar: View {
    @Bindable var model: ProjectModel

    var body: some View {
        HStack(spacing: 14) {
            if model.isLoading {
                ProgressView().controlSize(.small)
                Text(model.loadingMessage)
            } else {
                Text(model.statusText)
            }
            if let e = model.renderError { Label(e, systemImage: "exclamationmark.triangle").foregroundStyle(.orange).lineLimit(1) }
            Spacer()
            if let h = model.hover {
                HStack(spacing: 8) {
                    Text("(\(h.x), \(h.y))")
                    ForEach(h.values, id: \.0) { (k, v) in Text("\(k.shortName) \(v.g4)") }
                    if let rgb = h.rgb { Text(String(format: "RGB %.3f %.3f %.3f", rgb.0, rgb.1, rgb.2)) }
                }
                .font(.system(size: 10, design: .monospaced))
            }
            if model.frame != nil {
                Text("level \(model.lastRenderLevel) · \(Int(model.lastRenderMillis)) ms" + (model.isRendering ? " · rendering…" : ""))
                    .foregroundStyle(.secondary)
            }
            Text(String(format: "%.0f%%", model.viewport.scale * 100)).foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
        }
        .font(.caption)
        .padding(.horizontal, 10)
        .padding(.vertical, 4)
        .background(.bar)
    }
}

struct HistogramView: View {
    let histogram: [[Int]]

    var body: some View {
        GeometryReader { geo in
            let colors: [Color] = [.red, .green, .blue]
            let maxV = max(1, histogram.flatMap { $0 }.max() ?? 1)
            ZStack {
                ForEach(0..<min(3, histogram.count), id: \.self) { c in
                    Path { p in
                        let bins = histogram[c]
                        guard bins.count > 1 else { return }
                        let w = geo.size.width, h = geo.size.height
                        p.move(to: CGPoint(x: 0, y: h))
                        for (i, v) in bins.enumerated() {
                            let x = w * CGFloat(i) / CGFloat(bins.count - 1)
                            let y = h - h * CGFloat(log1p(Double(v))) / CGFloat(log1p(Double(maxV)))
                            p.addLine(to: CGPoint(x: x, y: y))
                        }
                        p.addLine(to: CGPoint(x: geo.size.width, y: geo.size.height))
                        p.closeSubpath()
                    }
                    .fill(colors[c].opacity(0.35))
                }
            }
        }
        .background(Color.black.opacity(0.4))
        .clipShape(RoundedRectangle(cornerRadius: 4))
    }
}
