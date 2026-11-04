import SwiftUI
import AstroCore

/// Horizontal strip of processing blocks under the preview. Every block collapses on its own
/// (several can be open at once); the collapsed set is remembered.
struct ControlsStripView: View {
    @Bindable var model: ProjectModel

    @AppStorage("collapsedBlocks") private var collapsed: String = ""
    @State private var dragScroll = ScrollDragState()

    static let height: CGFloat = 204
    /// Height taken by the horizontal scroller when scroll bars are always shown (legacy style);
    /// overlay scrollers take none. The strip keeps its total height either way.
    @State private var scrollerHeight: CGFloat = ControlsStripView.legacyScrollerHeight
    static var legacyScrollerHeight: CGFloat {
        NSScroller.preferredScrollerStyle == .legacy ? NSScroller.scrollerWidth(for: .regular, scrollerStyle: .legacy) : 0
    }
    private var contentHeight: CGFloat { Self.height - scrollerHeight }

    /// A block reset on the shown tab's settings.
    private func reset(_ neutral: Bool, _ what: String, _ apply: @escaping (inout CombineSettings) -> Void) -> BlockReset {
        BlockReset(enabled: !neutral, help: "Reset " + what) { var s = model.settings; apply(&s); model.settings = s }
    }

    /// The Combine stars reset keeps the combination on or off (only the blend parameters reset;
    /// the luminance mask goes off, as for any stars-only tab).
    private var starsReset: BlockReset {
        let current = model.combineStars
        var neutral = StarsBlendSettings()
        neutral.enabled = current.enabled
        neutral.useMask = false
        return BlockReset(enabled: current != neutral, help: "Reset the blend (amount, brightness, contrast, saturation, mode, mask)") {
            model.combineStars = neutral
        }
    }

    private func binding(_ id: String) -> Binding<Bool> {
        Binding(get: { !collapsed.split(separator: ",").contains(Substring(id)) },
                set: { open in
                    var set = Set(collapsed.split(separator: ",").map(String.init))
                    if open { set.remove(id) } else { set.insert(id) }
                    collapsed = set.sorted().joined(separator: ",")
                })
    }

    var body: some View {
        ScrollView(.horizontal, showsIndicators: true) {
            HStack(alignment: .top, spacing: 6) {
                // The channel weights live in the right pane (Channel mix, under the palettes).
                ControlBlock(title: "Stretch", width: 262, expanded: binding("stretch"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.stretch == StretchSettings() && model.settings.stretchStage == .preCombine, "the stretch") { $0.stretch = StretchSettings(); $0.stretchStage = .preCombine }) { stretchSection }
                ControlBlock(title: "Normalisation", width: 290, expanded: binding("normalisation"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.normalization == NormalizationSettings(), "the normalisation") { $0.normalization = NormalizationSettings() }) { normalizationSection }
                ControlBlock(title: "Luminance", width: 250, expanded: binding("luminance"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.luminance == LuminanceSettings(), "the luminance injection") { $0.luminance = LuminanceSettings() }) { luminanceSection }
                ControlBlock(title: "Colour bands", width: 226, expanded: binding("bands"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.colorBands == ColorBandsSettings(), "every colour band and the softness") { $0.colorBands = ColorBandsSettings() }) { colorBandsSection }
                ControlBlock(title: "Colour", width: 250, expanded: binding("colour"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.post.colourIsNeutral, "saturation, vibrance, SCNR and hue") {
                                 $0.post.saturation = 1; $0.post.vibrance = 0; $0.post.scnrGreen = 0; $0.post.hueShift = 0
                             }) { colourSection }
                ControlBlock(title: "Balance", width: 262, expanded: binding("balance"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.grading == ColorGradingSettings(), "the colour balance and the selective colour") { $0.grading = ColorGradingSettings() }) { balanceSection }
                ControlBlock(title: "Contrast", width: 250, expanded: binding("contrast"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.tone.isNeutral && model.settings.post.levelsAreNeutral, "exposure, brightness, contrast, highlights, shadows, black / white point and gamma") {
                                 $0.tone = ToneSettings(); $0.post.blackPoint = 0; $0.post.whitePoint = 1; $0.post.gamma = 1
                             }) { contrastSection }
                ControlBlock(title: "Denoise", width: 330, expanded: binding("denoise"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.denoise == DenoiseSettings(), "the noise reduction") { $0.denoise = DenoiseSettings() }) { denoiseSection }
                ControlBlock(title: "Detail", width: 262, expanded: binding("detail"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: reset(model.settings.detail == DetailSettings(), "local contrast and sharpening") { $0.detail = DetailSettings() }) { detailSection }
                // Blends the Stars tab into the background (both tabs show it).
                ControlBlock(title: "Combine stars", width: 250, expanded: binding("stars"), dragScroll: dragScroll, minHeight: contentHeight - 12,
                             reset: starsReset) { starsSection }
            }
            .padding(.horizontal, 6)
            .padding(.top, 6)
            .padding(.bottom, 6)
            .frame(height: contentHeight)
            .background(ScrollViewFinder(state: dragScroll))
            .background(Color.clear.contentShape(Rectangle()).gesture(dragScroll.gesture))
        }
        .frame(height: Self.height)
        .onReceive(NotificationCenter.default.publisher(for: NSScroller.preferredScrollerStyleDidChangeNotification)) { _ in
            scrollerHeight = Self.legacyScrollerHeight
        }
    }

    // MARK: Colour bands

    @AppStorage("colorBandComponent") private var bandComponentRaw = ColorBandComponent.saturation.rawValue
    private var bandComponent: ColorBandComponent { ColorBandComponent(rawValue: bandComponentRaw) ?? .saturation }

    @ViewBuilder var colorBandsSection: some View {
        HStack(spacing: 4) {
            Picker("", selection: $bandComponentRaw) {
                ForEach(ColorBandComponent.allCases) { c in Text(c.shortName).tag(c.rawValue) }
            }
            .pickerStyle(.segmented).labelsHidden().controlSize(.mini)
        }
        ForEach(ColorBand.allCases) { band in
            ColorBandRow(model: model, band: band, component: bandComponent)
        }
        SliderRow(title: "Softness", value: $model.settings.colorBands.softness, range: 0...1, defaultValue: 0,
                  help: "Feathers the bands: each one reaches further into its neighbours (up to twice as far) with a smooth fall-off, and fades in more gradually from grey, so an edit blends across colour edges instead of stopping at them.")
    }

    // MARK: Balance (Color Balance + Selective Color)

    @AppStorage("balanceTone") private var balanceToneRaw = ToneRange.midtones.rawValue
    @AppStorage("selectiveRange") private var selectiveRangeRaw = SelectiveRange.neutrals.rawValue

    /// A −100…100 slider over a −1…1 field of the selected tone range.
    private func balanceBinding(_ tone: ToneRange, _ kp: WritableKeyPath<ToneBalance, Float>) -> Binding<Float> {
        Binding(get: { (model.settings.grading.balance(tone)[keyPath: kp] * 100).rounded() },
                set: { var a = model.settings.grading.balance(tone); a[keyPath: kp] = $0 / 100; model.settings.grading.setBalance(tone, a) })
    }

    private func selectiveBinding(_ range: SelectiveRange, _ kp: WritableKeyPath<SelectiveAdjust, Float>) -> Binding<Float> {
        Binding(get: { (model.settings.grading.selective(range)[keyPath: kp] * 100).rounded() },
                set: { var a = model.settings.grading.selective(range); a[keyPath: kp] = $0 / 100; model.settings.grading.setSelective(range, a) })
    }

    @AppStorage("balanceMode") private var balanceModeRaw = "balance"

    @ViewBuilder var balanceSection: some View {
        let g = $model.settings.grading
        let tone = ToneRange(rawValue: balanceToneRaw) ?? .midtones
        let range = SelectiveRange(rawValue: selectiveRangeRaw) ?? .neutrals
        let lw: CGFloat = 92
        // One of the two tools at a time (• = adjusted).
        Picker("", selection: $balanceModeRaw) {
            Text("Color Balance" + (model.settings.grading.balanceIsNeutral ? "" : " •")).tag("balance")
            Text("Selective Color" + (model.settings.grading.selectiveIsNeutral ? "" : " •")).tag("selective")
        }
        .pickerStyle(.segmented).labelsHidden().controlSize(.small).fixedSize()
        .frame(maxWidth: .infinity, alignment: .center)
        .padding(.bottom, 4)
        if balanceModeRaw != "selective" {
            Picker("", selection: $balanceToneRaw) {
                ForEach(ToneRange.allCases) { t in Text(t.displayName + (model.settings.grading.balance(t).isNeutral ? "" : " •")).tag(t.rawValue) }
            }
            .pickerStyle(.segmented).labelsHidden().controlSize(.mini).fixedSize()
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 3)
            .help("Tone range the three sliders act on (• = adjusted); the ranges overlap smoothly by lightness, as in Photoshop")
            SliderRow(title: "Cyan – Red", value: balanceBinding(tone, \.cyanRed), range: -50...50, defaultValue: 0, format: "%.0f", help: "Negative towards cyan, positive towards red, in the \(tone.displayName.lowercased())", labelWidth: lw)
            SliderRow(title: "Magenta – Green", value: balanceBinding(tone, \.magentaGreen), range: -50...50, defaultValue: 0, format: "%.0f", help: "Negative towards magenta, positive towards green", labelWidth: lw)
            SliderRow(title: "Yellow – Blue", value: balanceBinding(tone, \.yellowBlue), range: -50...50, defaultValue: 0, format: "%.0f", help: "Negative towards yellow, positive towards blue", labelWidth: lw)
            Toggle("Preserve luminosity", isOn: g.preserveLuminosity).toggleStyle(.checkbox).font(.caption)
                .help("Keep each pixel's lightness: only hue and saturation move (Photoshop's default)")
        } else {
            Picker("", selection: $selectiveRangeRaw) {
                ForEach(SelectiveRange.allCases) { r in Text(r.displayName + (model.settings.grading.selective(r).isNeutral ? "" : " •")).tag(r.rawValue) }
            }
            .pickerStyle(.segmented).labelsHidden().controlSize(.mini).fixedSize()
            .frame(maxWidth: .infinity, alignment: .center)
            .padding(.vertical, 3)
            .help("Whites: the brightest nearly-grey pixels; Neutrals: greys around mid-tone; Blacks: the darkest. The hue ranges are the Colour bands block")
            SliderRow(title: "Cyan", value: selectiveBinding(range, \.cyan), range: -100...100, defaultValue: 0, format: "%.0f", help: "More cyan ink (less red) in the \(range.displayName.lowercased()); negative removes it", labelWidth: lw)
            SliderRow(title: "Magenta", value: selectiveBinding(range, \.magenta), range: -100...100, defaultValue: 0, format: "%.0f", help: "More magenta ink (less green); negative removes it", labelWidth: lw)
            SliderRow(title: "Yellow", value: selectiveBinding(range, \.yellow), range: -100...100, defaultValue: 0, format: "%.0f", help: "More yellow ink (less blue); negative removes it", labelWidth: lw)
            SliderRow(title: "Black", value: selectiveBinding(range, \.black), range: -100...100, defaultValue: 0, format: "%.0f", help: "Darkens (positive) or lightens (negative) the range", labelWidth: lw)
            HStack {
                Text("Method").font(.caption)
                Spacer()
                Picker("", selection: g.relative) { Text("Relative").tag(true); Text("Absolute").tag(false) }
                    .pickerStyle(.segmented).labelsHidden().controlSize(.mini).fixedSize()
                    .help("Relative changes the ink by a fraction of what is there (pure white cannot be touched); Absolute adds the amount outright")
            }
            .padding(.top, 3)
        }
    }

    // MARK: Denoise

    @ViewBuilder var denoiseSection: some View {
        let d = $model.settings.denoise
        MenuPicker(title: "Method", selection: d.method, options: DenoiseMethod.allCases) { $0.displayName }
            .help("Wavelets: starlet k·σ thresholding, the classic astronomical method, fast. Non-local means: patch averaging, best on fine texture, slower (export can take a while). Guided filter: edge-preserving smoothing, fastest. BM3D: block matching + collaborative 3-D filtering on the GPU, strongest on fine noise (falls back to wavelets without Metal).")
        SliderRow(title: "Luminance", value: d.amount, range: 0...1, defaultValue: 0, help: "Strength of the luminance noise reduction, relative to the measured noise level (so it adapts to the preview level and the export).")
        switch model.settings.denoise.method {
        case .wavelets:
            IntSliderRow(title: "Layers", value: d.layers, range: 1...5, defaultValue: 3, help: "Number of wavelet scales denoised (1 = finest only).")
        case .nonLocalMeans:
            IntSliderRow(title: "Search", value: d.search, range: 3...10, defaultValue: 5, help: "Search window radius in pixels; cost grows with its square.")
        case .guided:
            IntSliderRow(title: "Radius", value: d.radius, range: 1...8, defaultValue: 3, help: "Window radius in pixels.")
        case .bm3d:
            IntSliderRow(title: "Search", value: d.blockSearch, range: 6...24, defaultValue: 16, help: "Block-matching window radius in pixels; larger windows average more blocks (smoother sky), cost grows with the window.")
        }
        SliderRow(title: "Protect", value: d.protectBright, range: 0...1, defaultValue: 0.5, help: "Keeps bright structures (stars, nebula cores) untouched.")
        SliderRow(title: "Colour", value: d.color, range: 0...1, defaultValue: 0, help: "Smooths colour mottling (chroma only, luminance untouched).")
        SliderRow(title: "Hot pixels", value: d.hotPixels, range: 0...1, defaultValue: 0, help: "Replaces isolated single-pixel spikes (often brought out by strong colour edits) by their neighbours' median, channel by channel; stars, which are wider than one pixel, are kept. Higher catches fainter spikes. Works at full resolution: visible at 1:1 and in the export.")
        Text(model.settings.denoise.method.summary).font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }

    // MARK: Detail

    @ViewBuilder var detailSection: some View {
        let d = $model.settings.detail
        let lw: CGFloat = 84
        // One column, scrolled: local contrast by scale (fine → coarse), then the sharpening.
        SliderRow(title: "Micro", value: d.micro, range: -1...1, defaultValue: 0, help: "Micro-structure local contrast, the finest scale (radius ≈ 0.2 % of the width); stars are excluded from the blur to avoid dark halos.", note: "0.2%", labelWidth: lw)
        SliderRow(title: "Texture", value: d.texture, range: -1...1, defaultValue: 0, help: "Fine-detail local contrast (radius ≈ 0.6 % of the width); stars are excluded from the blur to avoid dark halos.", note: "0.6%", labelWidth: lw)
        SliderRow(title: "Clarity", value: d.clarity, range: -0.5...2, defaultValue: 0, help: "Mid-tone local contrast at a broader scale (radius ≈ 6 % of the width).", note: "6%", labelWidth: lw)
        SliderRow(title: "Dehaze", value: d.dehaze, range: -1...1, defaultValue: 0, help: "Subtracts the large-scale haze component (radius ≈ 25 % of the width); negative adds haze.", note: "25%", labelWidth: lw)
        SliderRow(title: "Low freq.", value: d.lowFrequency, range: -0.5...2, defaultValue: 0, help: "Local contrast of the largest structures (radius ≈ 50 % of the width): separates big nebula regions from the sky without touching fine detail.", note: "50%", labelWidth: lw)
        Divider().padding(.vertical, 8)
        SliderRow(title: "Sharpen", value: d.sharpen, range: 0...2, defaultValue: 0, help: "Unsharp mask on the luminance (stars excluded from the blur). The radius is in full-resolution pixels, so judge it at 1:1 with HQ on; a fitted preview shows little of it.", labelWidth: lw)
        SliderRow(title: "Radius", value: d.sharpenRadius, range: 0.5...5, defaultValue: 1.5, format: "%.1f", help: "Sharpening radius in full-resolution pixels.", note: "px", labelWidth: lw)
        SliderRow(title: "Masking", value: d.sharpenMasking, range: 0...1, defaultValue: 0, help: "Protects flat, noisy areas: only detail above this fraction of the noise level is sharpened (like Camera Raw's Masking).", labelWidth: lw)
    }

    // MARK: Stretch

    @ViewBuilder var stretchSection: some View {
        let s = $model.settings.stretch
        let st = model.settings.stretch
        // Highlight clipping shown as the share of pixels clipped to white (0…2 %).
        let clip = Binding<Float>(get: { (1 - model.settings.stretch.highlightsPercentile) * 100 },
                                  set: { model.settings.stretch.highlightsPercentile = 1 - min(max($0, 0), 100) / 100 })
        MenuPicker(title: "Method", selection: s.method, options: StretchMethod.allCases) { $0.displayName }
        MenuPicker(title: "Order", selection: $model.settings.stretchStage, options: StretchStage.allCases) { $0.shortName }
            .help("Stretch first: each channel is stretched, then combined. Combine first: the linear channels are combined, then the colour image is stretched.")
        if st.method != .none {
            Toggle("Linked channels", isOn: s.linked).help("Same stretch for all channels")
        }
        if st.usesTargetBackground {
            SliderRow(title: "Target bg", value: s.targetBackground, range: 0.02...0.6, defaultValue: 0.2, help: "Where the median background lands after the stretch (PixInsight STF default 0.25).")
        }
        if st.usesClipping {
            SliderRow(title: "Shadow clip", value: s.shadowsClipping, range: -6...0, defaultValue: -2.8, format: "%.1f", help: "Black point in noise units (MADN) below the median background (STF default −2.8). Closer to 0 darkens the sky.")
        }
        // The manual strengths are relative to the data's noise, so the same values suit any linear stack.
        switch st.method {
        case .mtf:
            SliderRow(title: "Midtones", value: s.mtfMidtonesSigma, range: 0.3...1000, defaultValue: 10, format: "%.1f", logarithmic: true,
                      help: "Midtones balance in noise units: lower is brighter. The sky (2.8 noises above the default black point) lands near 2.8 / (2.8 + value): 3 ≈ 0.5, 10 ≈ 0.2, 100 ≈ 0.03. The absolute midtones (PixInsight HistogramTransformation) are shown below.")
        case .arcsinh:
            SliderRow(title: "Stretch", value: s.arcsinhStrength, range: 0.01...100, defaultValue: 1, format: "%.2f", logarithmic: true,
                      help: "Stretch factor relative to the noise: 1 puts the sky near 0.2 on most linear stacks, 0.1 near 0.05, 10 near 0.35. The absolute factor (PixInsight ArcsinhStretch) is shown below.")
        case .ghs:
            SliderRow(title: "Stretch", value: s.ghsStrength, range: 0.001...100, defaultValue: 0.1, format: "%.3f", logarithmic: true,
                      help: "Stretch factor D relative to the noise. The absolute ln(D+1) (PixInsight / Siril GHS) is shown below.")
            SliderRow(title: "b", value: s.ghsB, range: 0...15, defaultValue: 2, format: "%.1f", help: "Local intensity: how concentrated the stretch is around the symmetry point (0 = exponential).")
            SliderRow(title: "SP", value: s.ghsSPPercentile, range: 0...100, defaultValue: 0, format: "%.1f",
                      help: "Symmetry point, as the share of the image's pixels darker than it: 0 = the black point, ≈ 50 = the sky background, 90+ = nebula. The contrast is greatest around it.", note: "%", curve: 2)
            SliderRow(title: "LP", value: s.ghsLPPercentile, range: 0...100, defaultValue: 0, format: "%.1f",
                      help: "Shadow protection: below this percentile the curve continues as a straight line (never above SP).", note: "%", curve: 2)
            SliderRow(title: "HP", value: s.ghsHPPercentile, range: 0...100, defaultValue: 100, format: "%.2f",
                      help: "Highlight protection: above this percentile the curve continues as a straight line, keeping star cores from saturating (100 = none; never below SP).", note: "%", curve: 0.5)
        case .log:
            SliderRow(title: "Strength", value: s.logStrength, range: 0.01...100, defaultValue: 1, format: "%.2f", logarithmic: true,
                      help: "k relative to the noise: 1 puts the sky near 0.17 on most linear stacks. The absolute k is shown below.")
        default: EmptyView()
        }
        if st.usesClipping {
            SliderRow(title: "Highlights", value: clip, range: 0...2, defaultValue: 0, format: "%.3f",
                      help: "Share of the pixels clipped to white (the white point). Auto STF keeps the background on target; the other methods keep their strength relative to the noise, so the picture brightens moderately.", note: "%", curve: 3)
        }
        SliderRow(title: "Gamma", value: s.gamma, range: 0.3...3, defaultValue: 1, logarithmic: true)
        if let note = resolvedStretchNote {
            Text(note).font(.system(size: 9, design: .monospaced)).foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true).textSelection(.enabled)
        }
        if !st.linked && st.method != .none && model.settings.stretchStage == .preCombine {
            Text("Unlinked stretches equalise every channel: normalisation and gain have little visible effect.")
                .font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
    }

    /// The absolute values the manual stretch resolved to (PixInsight-style numbers), per channel
    /// when they differ; nil for Auto STF and None.
    private var resolvedStretchNote: String? {
        let method = model.settings.stretch.method
        guard [.mtf, .arcsinh, .ghs, .log].contains(method), let f = model.frame, f.settings.stretch.method == method else { return nil }
        let entries: [(String, ResolvedStretch)]
        if model.settings.stretchStage == .preCombine {
            entries = ChannelKind.allCases.compactMap { k in f.channelStretch[k].map { (k.shortName, $0) } }
        } else {
            entries = Array(zip(["R", "G", "B", "L"], f.postStretch ?? []))
        }
        guard !entries.isEmpty else { return nil }
        func value(_ r: ResolvedStretch) -> String {
            switch method {
            case .mtf: return String(format: "m %.5f", r.midtones)
            case .arcsinh: return String(format: "factor %.0f", r.arcsinhStretch)
            case .log: return String(format: "k %.0f", r.logK)
            case .ghs: return String(format: "ln(D+1) %.2f, SP %.5f", log(r.ghsD + 1), r.ghsSP)
            default: return ""
            }
        }
        let values = entries.map { ($0.0, value($0.1)) }
        if Set(values.map(\.1)).count == 1 { return "Absolute: " + values[0].1 }
        return "Absolute: " + values.map { "\($0.0) \($0.1)" }.joined(separator: " · ")
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

    // MARK: Luminance

    @ViewBuilder var luminanceSection: some View {
        let l = $model.settings.luminance
        let lum = model.settings.luminance
        Toggle("Inject luminance", isOn: l.enabled)
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
        }
        if model.settings.mode == .pixelMath && model.settings.pixelMath.hasLuminance {
            Text("The Pixel Math luminance expression overrides this source.").font(.caption2).foregroundStyle(.secondary)
        }
    }

    // MARK: Colour

    @ViewBuilder var colourSection: some View {
        let p = $model.settings.post
        SliderRow(title: "Saturation", value: p.saturation, range: 0...3, defaultValue: 1)
        SliderRow(title: "Vibrance", value: p.vibrance, range: -1...2, defaultValue: 0, help: "Saturation boost that acts mostly on the less saturated colours.")
        SliderRow(title: "SCNR green", value: p.scnrGreen, range: 0...1, defaultValue: 0, help: "Average-neutral green removal.")
        SliderRow(title: "Hue shift", value: p.hueShift, range: -180...180, defaultValue: 0, format: "%.0f")
    }

    // MARK: Contrast (tone, after Balance)

    @ViewBuilder var contrastSection: some View {
        let t = $model.settings.tone, p = $model.settings.post
        let lw: CGFloat = 76
        SliderRow(title: "Exposure", value: t.exposure, range: -3...3, defaultValue: 0, format: "%.2f", help: "Gain in stops on the stretched image (clipped at white).", note: "EV", labelWidth: lw)
        SliderRow(title: "Brightness", value: t.brightness, range: -1...1, defaultValue: 0, format: "%.2f", help: "Lifts (positive) or lowers the mid-tones; black and white stay.", labelWidth: lw)
        SliderRow(title: "Contrast", value: t.contrast, range: -1...1, defaultValue: 0, format: "%.2f", help: "S-curve around the mid-tone: darker darks and brighter brights; negative flattens.", labelWidth: lw)
        SliderRow(title: "Highlights", value: t.highlights, range: -1...1, defaultValue: 0, format: "%.2f", help: "Brightens (positive) or recovers (negative) the bright tones; white and the mid-tone stay.", labelWidth: lw)
        SliderRow(title: "Shadows", value: t.shadows, range: -1...1, defaultValue: 0, format: "%.2f", help: "Opens up (positive) or deepens (negative) the dark tones; black and the mid-tone stay.", labelWidth: lw)
        SliderRow(title: "Black point", value: p.blackPoint, range: 0...0.5, defaultValue: 0, format: "%.3f", labelWidth: lw)
        SliderRow(title: "White point", value: p.whitePoint, range: 0.5...1, defaultValue: 1, format: "%.3f", labelWidth: lw)
        SliderRow(title: "Gamma", value: p.gamma, range: 0.3...3, defaultValue: 1, logarithmic: true, labelWidth: lw)
    }

    // MARK: Stars

    @ViewBuilder var starsSection: some View {
        if model.activeLayer == .stars {
            Text("Stars tab: with Combine on, the preview shows these stars over the Background tab.")
                .font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        combineStarsControls
    }

    @ViewBuilder private var combineStarsControls: some View {
        // The Background tab's blend settings, also editable from the Stars tab.
        let s = $model.combineStars
        let lw: CGFloat = 70
        let stars = model.layerSources(.stars)
        HStack(spacing: 6) {
            if !stars.isEmpty && model.activeLayer == .background {
                Image(systemName: "sparkles").foregroundStyle(.secondary)
                Text("Stars tab: " + ChannelKind.allCases.filter { stars[$0] != nil }.map(\.shortName).joined(separator: ", "))
                    .font(.caption).lineLimit(1).truncationMode(.tail)
                    .help(stars.values.map(\.fileName).sorted().joined(separator: "\n"))
                Spacer(minLength: 0)
                Button { model.switchLayer(to: .stars) } label: { Image(systemName: "arrow.right.circle") }
                    .buttonStyle(.borderless).controlSize(.small).disabled(!model.canSwitchLayer).help("Show the Stars tab (⌘2)")
                Button { model.removeStarsLayer() } label: { Image(systemName: "xmark.circle") }
                    .buttonStyle(.borderless).controlSize(.small).help("Empty the Stars tab")
            }
        }
        if model.activeLayer == .background {
            Button("Load RGB stars…") {
                Panels.openImages { urls in if let u = urls.first { Task { @MainActor in model.loadStarsLayer(from: u) } } }
            }
            .controlSize(.small)
        .help("Puts an already processed stars image on the Stars tab (as R, G, B; a grey image as L), replacing what is there, and switches the combination on. The tab shown does not change.")
        }
        Toggle("Combine BG and Stars", isOn: s.enabled).disabled(!model.hasStars || model.layerSources(.background).isEmpty)
        if !model.hasStars {
            Text("Fill the Stars tab first: drop star channels on it, use Remove Stars…, or Load RGB stars….").font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
        }
        if model.starsCompositeBusy || model.backgroundCompositeBusy {
            HStack(spacing: 6) {
                ProgressView().controlSize(.mini)
                Text(model.activeLayer == .background ? "Rendering the Stars tab…" : "Rendering the Background tab…").font(.caption2).foregroundStyle(.secondary)
            }
        }
        if model.combineStars.enabled && model.hasStars {
            // One column, scrolled: the layer's look, then the blend.
            SliderRow(title: "Amount", value: s.amount, range: 0...1, defaultValue: 1, labelWidth: lw)
            SliderRow(title: "Brightness", value: s.brightness, range: -2...2, defaultValue: 0, format: "%.2f", help: "Gain of the star layer in stops", note: "EV", labelWidth: lw)
            SliderRow(title: "Contrast", value: s.contrast, range: -1...1, defaultValue: 0, format: "%.2f", help: "Spreads the star layer around the mid-tone: positive darkens faint halos and keeps the cores, negative softens", labelWidth: lw)
            SliderRow(title: "Star sat.", value: s.saturation, range: 0...3, defaultValue: 1.3, labelWidth: lw)
            Toggle("Brightest stars to white", isOn: s.normalize).toggleStyle(.checkbox).font(.caption)
                .help("Scale the layer so its brightest stars reach white — the brightest values of the result (whole-image peak, so preview and export agree). With a lowered Amount the cores still stay white: the amount then fades the faint stars instead of greying everything")
            Divider().padding(.vertical, 8)
            MenuPicker(title: "Blend", selection: s.mode, options: StarsBlendMode.allCases) { $0.displayName }
                .help("Screen: adds without clipping, soft on bright nebulae (default).\nLighten: the brighter of the two.\nAdd: sum, clipped.")
            MenuPicker(title: "Blend at", selection: s.stage, options: StarsBlendStage.allCases) { $0.displayName }
                .help("After denoise & detail: the stars are not denoised, sharpened or haloed (the starless workflow), and a star slider re-blends only.\nBefore: the stars go through denoise and detail with the nebula.")
            Toggle("Mask by luminance", isOn: s.useMask).toggleStyle(.checkbox).font(.caption)
                .help("Only the parts of the Stars tab brighter than the threshold are blended — for a full RGB frame on the Stars tab. Off for stars-only images.")
            if model.combineStars.useMask {
                SliderRow(title: "Threshold", value: s.threshold, range: 0...1, defaultValue: 0.15, labelWidth: lw)
                SliderRow(title: "Softness", value: s.softness, range: 0.01...1, defaultValue: 0.25, labelWidth: lw)
            }
        }
    }

}

enum ColorBandComponent: String, CaseIterable, Identifiable {
    case hue, saturation, lightness
    var id: String { rawValue }
    var shortName: String {
        switch self { case .hue: return "Hue"; case .saturation: return "Sat"; case .lightness: return "Light" }
    }
}

struct ColorBandRow: View {
    @Bindable var model: ProjectModel
    let band: ColorBand
    let component: ColorBandComponent

    private var adjust: Binding<ColorBandAdjust> {
        Binding(get: { model.settings.colorBands.adjust(band) }, set: { model.settings.colorBands.set(band, $0) })
    }

    var body: some View {
        let a = adjust
        HStack(spacing: 4) {
            HStack(spacing: 4) {
                Circle().fill(Color(hue: Double(band.hue) / 360, saturation: 0.85, brightness: 0.95)).frame(width: 8, height: 8)
                Text(band.displayName).font(.caption)
            }
            .frame(width: 62, alignment: .leading)
            .onTapGesture(count: 2) { model.settings.colorBands.set(band, ColorBandAdjust()) }
            .help("Double-click to reset \(band.displayName)")
            switch component {
            case .hue: MiniSlider(value: a.hue, range: -60...60, format: "%+.0f°")
            case .saturation: MiniSlider(value: a.saturation, range: -1...1, format: "%+.2f")
            case .lightness: MiniSlider(value: a.lightness, range: -1...1, format: "%+.2f")
            }
        }
    }
}

/// Slider with a tiny value label on the right; double-click the label or right-click the slider to reset to 0.
struct MiniSlider: View {
    @Binding var value: Float
    var range: ClosedRange<Float>
    var format: String

    var body: some View {
        HStack(spacing: 4) {
            Slider(value: Binding(get: { Double(value) }, set: { value = Float($0) }), in: Double(range.lowerBound)...Double(range.upperBound))
                .controlSize(.mini)
                .overlay(RightClickReset { value = 0 })
            Text(String(format: format, value)).font(.system(size: 9, design: .monospaced)).foregroundStyle(value == 0 ? .tertiary : .secondary)
                .frame(width: 38, alignment: .trailing)
                .onTapGesture(count: 2) { value = 0 }
        }
        .frame(maxWidth: .infinity)
    }
}
