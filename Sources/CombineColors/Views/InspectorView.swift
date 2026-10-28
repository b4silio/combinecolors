import SwiftUI
import AstroCore

struct InspectorView: View {
    @Bindable var model: ProjectModel
    @Binding var showMatrixEditor: Bool
    @Binding var showPixelMath: Bool
    @Binding var showExport: Bool
    @Binding var showDualBand: Bool

    /// Accordion: one open block at a time (the palette block keeps its own state).
    @AppStorage("inspectorOpenSection") private var openSection: String = "channels"
    @AppStorage("paletteSectionOpen") private var paletteOpen: Bool = true

    private func binding(_ id: String) -> Binding<Bool> {
        Binding(get: { openSection == id }, set: { openSection = $0 ? id : "" })
    }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 0) {
                SectionBox(title: "Palette", subtitle: model.currentPalette?.name ?? (model.settings.mode == .pixelMath ? "custom Pixel Math" : "custom matrix"), expanded: $paletteOpen) {
                    PaletteGridView(model: model, showMatrixEditor: $showMatrixEditor, showPixelMath: $showPixelMath)
                }
                SectionBox(title: "Channels", subtitle: channelsSubtitle, expanded: binding("channels")) { channelsSection }
                SectionBox(title: "Stretch", subtitle: model.settings.stretch.method.displayName, expanded: binding("stretch")) { stretchSection }
                SectionBox(title: "Normalisation", subtitle: model.settings.normalization.method.displayName, expanded: binding("normalisation")) { normalizationSection }
                SectionBox(title: "Luminance", subtitle: model.settings.luminance.enabled ? (model.settings.luminance.resolvedExpression(available: model.availableKinds) ?? "unavailable") : "off", expanded: binding("luminance")) { luminanceSection }
                SectionBox(title: "Colour bands", subtitle: model.settings.colorBands.isNeutral ? "neutral" : "adjusted", expanded: binding("bands")) { colorBandsSection }
                SectionBox(title: "Colour", subtitle: model.settings.post == PostSettings() ? "neutral" : "adjusted", expanded: binding("colour")) { colourSection }
                SectionBox(title: "Detail", subtitle: model.settings.detail.isNeutral ? "neutral" : String(format: "clarity %.2f · dehaze %.2f", model.settings.detail.clarity, model.settings.detail.dehaze), expanded: binding("detail")) { detailSection }
                SectionBox(title: "RGB stars", subtitle: model.settings.stars.enabled ? "on" : "off", expanded: binding("stars")) { starsSection }
                SectionBox(title: "Crop", subtitle: model.crop.map { String($0.width) + "×" + String($0.height) } ?? "none", expanded: binding("crop")) { cropSection }
                SectionBox(title: "Output", expanded: binding("output")) { outputSection }
            }
        }
        .frame(minWidth: 320, idealWidth: 400, maxWidth: 480)
    }

    private var channelsSubtitle: String {
        let kinds = ChannelKind.allCases.filter { model.sources[$0] != nil }
        return kinds.isEmpty ? "none" : kinds.map(\.shortName).joined(separator: " ")
    }

    // MARK: Colour bands

    @ViewBuilder var colorBandsSection: some View {
        Text("Selective colour: hue, saturation and lightness per hue band (Camera Raw HSL).").font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        HStack(spacing: 6) {
            Text("").frame(width: 74)
            Text("Hue").font(.caption2).frame(maxWidth: .infinity)
            Text("Saturation").font(.caption2).frame(maxWidth: .infinity)
            Text("Lightness").font(.caption2).frame(maxWidth: .infinity)
        }
        ForEach(ColorBand.allCases) { band in
            ColorBandRow(model: model, band: band)
        }
        Button("Reset bands") { model.settings.colorBands = ColorBandsSettings() }.controlSize(.small)
            .disabled(model.settings.colorBands.isNeutral)
    }

    // MARK: Detail

    @ViewBuilder var detailSection: some View {
        let d = $model.settings.detail
        SliderRow(title: "Clarity", value: d.clarity, range: -1...1, defaultValue: 0, help: "Mid-tone local contrast (radius ≈ 1.5 % of the width).")
        SliderRow(title: "Dehaze", value: d.dehaze, range: -1...1, defaultValue: 0, help: "Subtracts the large-scale haze component (radius ≈ 12 % of the width); negative adds haze.")
        Text("Applied last, on the luminance of the final image. Radii scale with the image so preview and export match.").font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }

    // MARK: Stretch

    @ViewBuilder var stretchSection: some View {
        let s = $model.settings.stretch
        MenuPicker(title: "Method", selection: s.method, options: StretchMethod.allCases) { $0.displayName }
        MenuPicker(title: "Stage", selection: $model.settings.stretchStage, options: StretchStage.allCases) { $0.displayName }
        Toggle("Linked channels (same stretch for all)", isOn: s.linked)
        if model.settings.stretch.method != .none {
            SliderRow(title: "Target bg", value: s.targetBackground, range: 0.02...0.6, defaultValue: 0.2, help: "Where the median background lands after the stretch (PixInsight STF default 0.25).")
            SliderRow(title: "Shadow clip", value: s.shadowsClipping, range: -6...0, defaultValue: -2.8, format: "%.1f", help: "Black point in MADN units below the median (STF default −2.8).")
            SliderRow(title: "Highlights", value: s.highlightsPercentile, range: 0.9...1, defaultValue: 1, format: "%.4f", help: "Percentile mapped to white (1 = no highlight clipping).")
        }
        switch model.settings.stretch.method {
        case .mtf:
            SliderRow(title: "Shadows", value: s.mtfShadows, range: 0...0.5, defaultValue: 0, format: "%.3f")
            SliderRow(title: "Midtones", value: s.mtfMidtones, range: 0.001...0.999, defaultValue: 0.5, format: "%.3f", logarithmic: true)
            SliderRow(title: "Highlights", value: s.mtfHighlights, range: 0.5...1, defaultValue: 1, format: "%.3f")
        case .arcsinh:
            SliderRow(title: "Stretch", value: s.arcsinhStretch, range: 1...5000, defaultValue: 200, format: "%.0f", logarithmic: true)
        case .ghs:
            SliderRow(title: "ln(D+1)", value: s.ghsLogD, range: 0...12, defaultValue: 5, format: "%.2f", help: "Stretch factor (logarithmic).")
            SliderRow(title: "b", value: s.ghsB, range: 0...15, defaultValue: 2, help: "Local intensity: how concentrated the stretch is around the symmetry point.")
            SliderRow(title: "SP", value: s.ghsSP, range: 0...1, defaultValue: 0, format: "%.3f", help: "Symmetry point.")
            SliderRow(title: "LP", value: s.ghsLP, range: 0...1, defaultValue: 0, format: "%.3f", help: "Shadow protection point.")
            SliderRow(title: "HP", value: s.ghsHP, range: 0...1, defaultValue: 1, format: "%.3f", help: "Highlight protection point.")
        case .log:
            SliderRow(title: "k", value: s.logK, range: 1...100000, defaultValue: 2000, format: "%.0f", logarithmic: true)
        default: EmptyView()
        }
        SliderRow(title: "Gamma", value: s.gamma, range: 0.3...3, defaultValue: 1, logarithmic: true)
        if !model.settings.stretch.linked && model.settings.stretchStage == .preCombine && model.settings.stretch.method == .autoSTF {
            Text("Unlinked auto-stretch already equalises every channel: normalisation and linear gain have no visible effect in this mode. Use the per-channel target boost, weight and black point instead, or switch to linked.")
                .font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: Normalisation

    @ViewBuilder var normalizationSection: some View {
        let n = $model.settings.normalization
        MenuPicker(title: "Method", selection: n.method, options: NormalizationMethod.allCases) { $0.displayName }
        Text(model.settings.normalization.method.summary).font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        MenuPicker(title: "Reference", selection: n.reference, options: [ChannelKind?.none] + ChannelKind.allCases.filter { model.sources[$0] != nil }.map { ChannelKind?.some($0) }) {
            $0?.displayName ?? "Automatic (\(model.context?.reference?.displayName ?? "–"))"
        }
        Toggle("Use star mask for statistics", isOn: n.useStarMask)
        if model.settings.normalization.method == .backgroundSignal {
            SliderRow(title: "Signal pct", value: n.signalPercentile, range: 0.9...0.9999, defaultValue: 0.995, format: "%.4f", help: "Percentile of the star-masked image taken as the emission signal level.")
        }
        if model.settings.normalization.method == .linearFit {
            SliderRow(title: "Reject σ", value: n.fitRejectSigma, range: 1...5, defaultValue: 2.5, format: "%.1f")
        }
        if let ctx = model.context {
            VStack(alignment: .leading, spacing: 1) {
                ForEach(ChannelKind.allCases.filter { ctx.transforms[$0] != nil }) { k in
                    HStack {
                        Text(k.shortName).frame(width: 36, alignment: .leading)
                        Text(ctx.transforms[k]!.description)
                        Spacer()
                        if let st = ctx.normalizedMaskedStats[k] { Text("bg \(st.background.g4)  sig \(st.signalStrength.g4)σ") }
                    }
                    .font(.system(size: 9, design: .monospaced)).foregroundStyle(.secondary)
                }
            }
        }
    }

    // MARK: Channels

    @ViewBuilder var channelsSection: some View {
        let kinds = ChannelKind.allCases.filter { model.sources[$0] != nil }
        if kinds.isEmpty { Text("No channels loaded.").font(.caption).foregroundStyle(.secondary) }
        ForEach(kinds) { k in
            ChannelAdjustView(model: model, kind: k)
        }
    }

    // MARK: Luminance

    @ViewBuilder var luminanceSection: some View {
        let l = $model.settings.luminance
        let lum = model.settings.luminance
        Toggle("Inject luminance (adds contrast and detail)", isOn: l.enabled)
        if lum.enabled && !model.settings.luminanceApplies {
            Text("Not applied to the mono check palettes.").font(.caption2).foregroundStyle(.secondary)
        }
        if lum.enabled {
            MenuPicker(title: "Source", selection: l.preset, options: LuminancePreset.allCases) { p in
                p.displayName + (p.expression(available: model.availableKinds, custom: lum.customExpression) == nil && p != .custom ? " (unavailable)" : "")
            }
            if lum.preset == .custom {
                TextField("e.g. 0.8*Ha + 0.2*max(OIII, SII)", text: l.customExpression)
                    .font(.system(.caption, design: .monospaced))
                    .textFieldStyle(.roundedBorder)
            }
            if let e = lum.resolvedExpression(available: model.availableKinds) {
                let ok = (try? PixelMath.parse(e)) != nil
                let missing = lum.requiredChannels(available: model.availableKinds).subtracting(model.availableKinds)
                Text(ok ? (missing.isEmpty ? "Using: " + e : "Missing channels: " + missing.map(\.displayName).joined(separator: ", ")) : "Expression does not parse")
                    .font(.system(size: 9, design: .monospaced)).foregroundStyle(ok && missing.isEmpty ? Color.secondary : Color.red)
                    .fixedSize(horizontal: false, vertical: true)
            } else {
                Text("No usable channel loaded for this source.").font(.caption2).foregroundStyle(.orange)
            }
            SliderRow(title: "Amount", value: l.amount, range: 0...1, defaultValue: 1)
            MenuPicker(title: "Method", selection: l.method, options: LuminanceMethod.allCases) { $0.displayName }
            Text("Pre-combine stage: the stretched channels are mixed. Post-combine stage: the linear channels are mixed, then auto-stretched.").font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        if model.settings.mode == .pixelMath && model.settings.pixelMath.hasLuminance {
            Text("The Pixel Math luminance expression overrides this source.").font(.caption2).foregroundStyle(.secondary)
        }
    }

    // MARK: Colour

    @ViewBuilder var colourSection: some View {
        let p = $model.settings.post
        SliderRow(title: "Saturation", value: p.saturation, range: 0...3, defaultValue: 1)
        SliderRow(title: "Vibrance", value: p.vibrance, range: -1...2, defaultValue: 0)
        SliderRow(title: "SCNR green", value: p.scnrGreen, range: 0...1, defaultValue: 0, help: "Average-neutral green removal.")
        SliderRow(title: "Hue shift", value: p.hueShift, range: -180...180, defaultValue: 0, format: "%.0f")
        SliderRow(title: "Black point", value: p.blackPoint, range: 0...0.5, defaultValue: 0, format: "%.3f")
        SliderRow(title: "White point", value: p.whitePoint, range: 0.5...1, defaultValue: 1, format: "%.3f")
        SliderRow(title: "Gamma", value: p.gamma, range: 0.3...3, defaultValue: 1, logarithmic: true)
        HStack {
            Button("Reset colour") { model.settings.post = PostSettings() }.controlSize(.small)
            Button("Classic SHO look") {
                model.settings.post.scnrGreen = 0.8; model.settings.post.saturation = 1.3; model.settings.post.hueShift = -10
            }.controlSize(.small).help("SCNR 0.8, saturation 1.3, hue −10°")
        }
    }

    // MARK: Stars

    @ViewBuilder var starsSection: some View {
        let s = $model.settings.stars
        let hasRGB = model.sources[.red] != nil && model.sources[.green] != nil && model.sources[.blue] != nil
        Toggle("Blend RGB stars into the result", isOn: s.enabled).disabled(!hasRGB)
        if !hasRGB { Text("Load R, G and B channels (or a colour stars image) to enable.").font(.caption2).foregroundStyle(.secondary) }
        if model.settings.stars.enabled {
            SliderRow(title: "Amount", value: s.amount, range: 0...1, defaultValue: 1)
            MenuPicker(title: "Star data", selection: s.stretch, options: StarsStretchMode.allCases) { $0.displayName }
            Toggle("Mask stars by luminance", isOn: s.useMask).help("Off for star-only images from StarNet/StarXTerminator.")
            if model.settings.stars.useMask {
                SliderRow(title: "Threshold", value: s.threshold, range: 0...1, defaultValue: 0.15)
                SliderRow(title: "Softness", value: s.softness, range: 0.01...1, defaultValue: 0.25)
            }
            SliderRow(title: "Star sat.", value: s.saturation, range: 0...3, defaultValue: 1.3)
            MenuPicker(title: "Blend", selection: s.mode, options: StarsBlendMode.allCases) { $0.displayName }
        }
    }

    // MARK: Crop

    @ViewBuilder var cropSection: some View {
        HStack {
            if model.cropEditing {
                Button("Apply") { model.commitCropEditing() }.keyboardShortcut(.return, modifiers: [])
                Button("Cancel") { model.cancelCropEditing() }.keyboardShortcut(.escape, modifiers: [])
            } else {
                Button(model.crop == nil ? "Set crop…" : "Edit crop…") { model.beginCropEditing() }
                Button("Auto trim") { model.autoCrop() }.help("Removes the empty borders left by registration in any channel.")
                if model.crop != nil { Button("Clear") { model.clearCrop() } }
            }
        }
        .controlSize(.small)
        .disabled(model.sources.isEmpty)
        if model.cropEditing, let d = Binding($model.cropDraft) {
            HStack {
                Text("X").frame(width: 14); TextField("x", value: d.x, format: .number).frame(width: 60)
                Text("Y").frame(width: 14); TextField("y", value: d.y, format: .number).frame(width: 60)
            }
            HStack {
                Text("W").frame(width: 14); TextField("w", value: d.width, format: .number).frame(width: 60)
                Text("H").frame(width: 14); TextField("h", value: d.height, format: .number).frame(width: 60)
            }
            Text("Drag on the image to draw the rectangle; drag the handles or the inside to adjust. The crop applies to every channel.").font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        } else if let c = model.crop {
            Text(verbatim: "x \(c.x)  y \(c.y)  →  \(c.width) × \(c.height) px").font(.system(.caption, design: .monospaced))
            Toggle("Show crop limits on the preview", isOn: $model.showCropOverlay)
            Text("Statistics, thumbnails and the exported image use the cropped area only.").font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: Output

    @ViewBuilder var outputSection: some View {
        Toggle("High quality preview (more pixels)", isOn: $model.highQualityPreview)
        HStack {
            Button("Combine & Export…") { showExport = true }
                .buttonStyle(.borderedProminent)
                .disabled(model.sources.isEmpty || model.exportProgress != nil)
            Button("Split dual-band…") { showDualBand = true }
        }
        .controlSize(.regular)
        if let p = model.exportProgress {
            ProgressView(value: p).controlSize(.small)
        }
        Button("Reset all settings") { var s = CombineSettings(); if let pal = model.currentPalette { s.select(palette: pal) }; model.settings = s }.controlSize(.small)
    }
}

struct ChannelAdjustView: View {
    @Bindable var model: ProjectModel
    let kind: ChannelKind
    @State private var expanded = false

    private var adjust: Binding<ChannelAdjust> {
        Binding(get: { model.settings.adjust(kind) }, set: { model.settings.setAdjust(kind, $0) })
    }

    var body: some View {
        let a = adjust
        let pre = model.settings.stretchStage == .preCombine
        VStack(alignment: .leading, spacing: 4) {
            HStack {
                Toggle(isOn: a.enabled) { EmptyView() }.toggleStyle(.checkbox)
                Circle().fill(kind.tint).frame(width: 8, height: 8)
                Text(kind.displayName).font(.subheadline).bold()
                if !model.settings.allChannels(available: model.availableKinds).contains(kind) {
                    Text("unused").font(.caption2).foregroundStyle(.tertiary)
                }
                Spacer()
                if !model.settings.adjust(kind).isNeutral {
                    Button("Reset") { model.settings.setAdjust(kind, ChannelAdjust()) }.controlSize(.mini).buttonStyle(.link)
                }
                Button { expanded.toggle() } label: { Image(systemName: expanded ? "chevron.up" : "chevron.down") }.buttonStyle(.plain).controlSize(.small)
            }
            SliderRow(title: "Weight", value: a.weight, range: 0...2, defaultValue: 1, help: pre ? "Multiplier after the stretch." : "Linear multiplier before the combined stretch.")
            if expanded {
                SliderRow(title: "Linear gain", value: a.gain, range: 0.1...10, defaultValue: 1, logarithmic: true, help: "Multiplier on the normalised linear data (before the stretch).")
                SliderRow(title: "Pedestal σ", value: a.offsetSigma, range: -20...20, defaultValue: 0, format: "%.1f", help: "Adds a constant in units of the reference background sigma.")
                if pre {
                    SliderRow(title: "Target boost", value: a.stretchBoost, range: -0.15...0.4, defaultValue: 0, help: "Raises/lowers this channel's stretch target background.")
                    SliderRow(title: "Black point", value: a.blackPoint, range: 0...0.5, defaultValue: 0, format: "%.3f", help: "Subtracted after the stretch.")
                    SliderRow(title: "Gamma", value: a.gamma, range: 0.3...3, defaultValue: 1, logarithmic: true)
                }
            }
        }
        .padding(.vertical, 2)
    }
}


struct ColorBandRow: View {
    @Bindable var model: ProjectModel
    let band: ColorBand

    private var adjust: Binding<ColorBandAdjust> {
        Binding(get: { model.settings.colorBands.adjust(band) }, set: { model.settings.colorBands.set(band, $0) })
    }

    var body: some View {
        let a = adjust
        HStack(spacing: 6) {
            HStack(spacing: 4) {
                Circle().fill(Color(hue: Double(band.hue) / 360, saturation: 0.85, brightness: 0.95)).frame(width: 9, height: 9)
                Text(band.displayName).font(.caption)
            }
            .frame(width: 74, alignment: .leading)
            .onTapGesture(count: 2) { model.settings.colorBands.set(band, ColorBandAdjust()) }
            .help("Double-click to reset \(band.displayName)")
            MiniSlider(value: a.hue, range: -60...60, format: "%+.0f°")
            MiniSlider(value: a.saturation, range: -1...1, format: "%+.2f")
            MiniSlider(value: a.lightness, range: -1...1, format: "%+.2f")
        }
    }
}

/// Slider with a tiny value label below it; double-click the label to reset to 0.
struct MiniSlider: View {
    @Binding var value: Float
    var range: ClosedRange<Float>
    var format: String

    var body: some View {
        VStack(spacing: 0) {
            Slider(value: Binding(get: { Double(value) }, set: { value = Float($0) }), in: Double(range.lowerBound)...Double(range.upperBound))
                .controlSize(.mini)
            Text(String(format: format, value)).font(.system(size: 9, design: .monospaced)).foregroundStyle(value == 0 ? .tertiary : .secondary)
                .onTapGesture(count: 2) { value = 0 }
        }
        .frame(maxWidth: .infinity)
    }
}
