import SwiftUI
import AstroCore

struct StatusBar: View {
    @Bindable var model: ProjectModel

    var body: some View {
        // Single fixed-height line: nothing here may wrap or change the bar's height.
        HStack(spacing: 14) {
            if model.isLoading {
                ProgressView().controlSize(.small)
                Text(model.loadingMessage).lineLimit(1)
            } else {
                Text(model.statusText).lineLimit(1).truncationMode(.middle)
            }
            if let e = model.renderError { Label(e, systemImage: "exclamationmark.triangle").foregroundStyle(.orange).lineLimit(1).truncationMode(.tail) }
            Spacer(minLength: 8)
            Text(readout)
                .font(.system(size: 10, design: .monospaced))
                .lineLimit(1)
                .truncationMode(.tail)
                .layoutPriority(1)
            if model.frame != nil {
                Text("L\(model.lastRenderLevel) · \(Int(model.lastRenderMillis)) ms" + (model.isRendering ? " ⟳" : ""))
                    .foregroundStyle(.secondary).lineLimit(1).frame(width: 80, alignment: .trailing)
            }
            Text(String(format: "%.0f%%", model.viewport.scale * 100)).foregroundStyle(.secondary).frame(width: 44, alignment: .trailing)
        }
        .font(.caption)
        .padding(.horizontal, 10)
        .frame(height: 24)
        .background(.bar)
    }

    /// Pixel read-out as one string (fixed format so the width barely changes).
    var readout: String {
        guard let h = model.hover else { return "" }
        var parts = [String(format: "(%5d, %5d)", h.x, h.y)]
        parts += h.values.map { String(format: "%@ %.5f", $0.0.shortName, $0.1) }
        if let rgb = h.rgb { parts.append(String(format: "RGB %.3f %.3f %.3f", rgb.0, rgb.1, rgb.2)) }
        return parts.joined(separator: "  ")
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
