import SwiftUI
import AstroCore

/// Channel mix (right pane, between the palettes and the presets): one compact row per loaded
/// channel — on / off, weight slider tinted with the channel colour — with the finer adjustments
/// (gain, pedestal, per-channel stretch tweaks) folded under a chevron. It sits next to the
/// palettes because weights and palette together make the colour combination.
struct ChannelMixView: View {
    @Bindable var model: ProjectModel

    var body: some View {
        let kinds = ChannelKind.allCases.filter { model.sources[$0] != nil }
        let pre = model.settings.stretchStage == .preCombine
        let neutral = model.settings.channels.values.allSatisfy(\.isNeutral)
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 6) {
                Text("Channel mix").font(.caption.bold())
                Text(pre ? "weights after the stretch" : "linear weights, before the stretch")
                    .font(.caption2).foregroundStyle(.tertiary).lineLimit(1)
                Spacer(minLength: 0)
                Button { model.settings.channels = [:] } label: { Image(systemName: "arrow.counterclockwise") }
                    .buttonStyle(.borderless).controlSize(.small)
                    .disabled(neutral)
                    .help("Reset the channel weights and gains")
            }
            if kinds.isEmpty {
                Text("No channels loaded.").font(.caption).foregroundStyle(.secondary)
            }
            ForEach(kinds) { k in ChannelMixRow(model: model, kind: k) }
        }
        .padding(.horizontal, 8).padding(.vertical, 5)
        .background(Color(nsColor: .controlBackgroundColor))
    }
}

/// One channel of the mix: checkbox, colour dot + name, weight; the chevron opens the rest.
struct ChannelMixRow: View {
    @Bindable var model: ProjectModel
    let kind: ChannelKind
    @State private var expanded = false

    private var adjust: Binding<ChannelAdjust> {
        Binding(get: { model.settings.adjust(kind) }, set: { model.settings.setAdjust(kind, $0) })
    }

    /// Anything but the weight and the checkbox changed (a dot on the closed chevron).
    static func advancedChanged(_ a: ChannelAdjust) -> Bool {
        var b = a
        b.weight = 1; b.enabled = true
        return !b.isNeutral
    }

    var body: some View {
        let a = adjust
        let pre = model.settings.stretchStage == .preCombine
        let used = model.settings.allChannels(available: model.availableKinds).contains(kind)
        let current = model.settings.adjust(kind)
        let advancedChanged = Self.advancedChanged(current)
        VStack(alignment: .leading, spacing: 2) {
            HStack(spacing: 4) {
                Toggle(isOn: a.enabled) { EmptyView() }.toggleStyle(.checkbox).controlSize(.small)
                    .help("Use \(kind.displayName) in the combination")
                Circle().fill(kind.tint).frame(width: 8, height: 8)
                SliderRow(title: kind.shortName, value: a.weight, range: 0...2, defaultValue: 1,
                          help: (pre ? "Weight of \(kind.displayName), applied after the stretch." : "Linear multiplier of \(kind.displayName) before the combined stretch.")
                              + (used ? "" : " Not used by the current palette.") + " Double-click to reset.",
                          labelWidth: 38)
                    .tint(kind.tint)
                    .opacity(used && current.enabled ? 1 : 0.5)
                Button { expanded.toggle() } label: {
                    Image(systemName: expanded ? "chevron.up" : "chevron.down")
                        .font(.system(size: 9, weight: .semibold))
                        .frame(width: 14, height: 14)
                        .overlay(alignment: .topTrailing) {
                            if advancedChanged && !expanded { Circle().fill(Color.accentColor).frame(width: 5, height: 5) }
                        }
                }
                .buttonStyle(.plain)
                .help(expanded ? "Hide the finer adjustments" : "Gain, pedestal" + (pre ? ", stretch boost, black point and gamma" : "") + (advancedChanged ? " (changed)" : ""))
            }
            if expanded {
                VStack(spacing: 2) {
                    SliderRow(title: "Linear gain", value: a.gain, range: 0.1...10, defaultValue: 1, logarithmic: true, help: "Multiplier on the normalised linear data (before the stretch).", labelWidth: 78)
                    SliderRow(title: "Pedestal σ", value: a.offsetSigma, range: -20...20, defaultValue: 0, format: "%.1f", help: "Adds a constant in units of the reference background sigma.", labelWidth: 78)
                    if pre {
                        SliderRow(title: "Target boost", value: a.stretchBoost, range: -0.15...0.4, defaultValue: 0, help: "Raises/lowers this channel's stretch target background.", labelWidth: 78)
                        SliderRow(title: "Black point", value: a.blackPoint, range: 0...0.5, defaultValue: 0, format: "%.3f", help: "Subtracted after the stretch.", labelWidth: 78)
                        SliderRow(title: "Gamma", value: a.gamma, range: 0.3...3, defaultValue: 1, logarithmic: true, labelWidth: 78)
                    }
                    if !current.isNeutral {
                        Button("Reset \(kind.shortName)") { model.settings.setAdjust(kind, ChannelAdjust()) }
                            .controlSize(.mini).buttonStyle(.link)
                            .frame(maxWidth: .infinity, alignment: .trailing)
                    }
                }
                .padding(.leading, 34).padding(.bottom, 3)
            }
        }
    }
}
