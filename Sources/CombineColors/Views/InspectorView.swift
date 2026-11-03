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

    private var hasRGB: Bool { model.sources[.red] != nil && model.sources[.green] != nil && model.sources[.blue] != nil }

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
                ControlBlock(title: "Channels", width: 250, expanded: binding("channels"), dragScroll: dragScroll, minHeight: contentHeight - 12) { channelsSection }
                ControlBlock(title: "Stretch", width: 262, expanded: binding("stretch"), dragScroll: dragScroll, minHeight: contentHeight - 12) { stretchSection }
                ControlBlock(title: "Normalisation", width: 290, expanded: binding("normalisation"), dragScroll: dragScroll, minHeight: contentHeight - 12) { normalizationSection }
                ControlBlock(title: "Luminance", width: 250, expanded: binding("luminance"), dragScroll: dragScroll, minHeight: contentHeight - 12) { luminanceSection }
                ControlBlock(title: "Colour bands", width: 226, expanded: binding("bands"), dragScroll: dragScroll, minHeight: contentHeight - 12) { colorBandsSection }
                ControlBlock(title: "Colour", width: 250, expanded: binding("colour"), dragScroll: dragScroll, minHeight: contentHeight - 12) { colourSection }
                ControlBlock(title: "Balance", width: 262, expanded: binding("balance"), dragScroll: dragScroll, minHeight: contentHeight - 12) { balanceSection }
                ControlBlock(title: "Denoise", width: 330, expanded: binding("denoise"), dragScroll: dragScroll, minHeight: contentHeight - 12) { denoiseSection }
                ControlBlock(title: "Detail", width: 262, expanded: binding("detail"), dragScroll: dragScroll, minHeight: contentHeight - 12) { detailSection }
                // Stays collapsed while no R, G and B channels are loaded (nothing to blend).
                ControlBlock(title: "RGB stars", width: 250, expanded: binding("stars"), dragScroll: dragScroll, minHeight: contentHeight - 12) { starsSection }
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
            Button { model.settings.colorBands = ColorBandsSettings() } label: { Image(systemName: "arrow.counterclockwise") }
                .controlSize(.mini).buttonStyle(.borderless).help("Reset all bands").disabled(model.settings.colorBands.isNeutral)
        }
        ForEach(ColorBand.allCases) { band in
            ColorBandRow(model: model, band: band, component: bandComponent)
        }
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

    @ViewBuilder var balanceSection: some View {
        let g = $model.settings.grading
        let tone = ToneRange(rawValue: balanceToneRaw) ?? .midtones
        let range = SelectiveRange(rawValue: selectiveRangeRaw) ?? .neutrals
        let lw: CGFloat = 92
        HStack(spacing: 4) {
            Text("Colour balance").font(.caption.bold())
            Spacer()
            Button { model.settings.grading.balance = [:] } label: { Image(systemName: "arrow.counterclockwise") }
                .controlSize(.mini).buttonStyle(.borderless).help("Reset the colour balance of every tone range")
                .disabled(model.settings.grading.balanceIsNeutral)
        }
        Picker("", selection: $balanceToneRaw) {
            ForEach(ToneRange.allCases) { t in Text(t.displayName + (model.settings.grading.balance(t).isNeutral ? "" : " •")).tag(t.rawValue) }
        }
        .pickerStyle(.segmented).labelsHidden().controlSize(.mini).fixedSize()
        .frame(maxWidth: .infinity, alignment: .center)
        .padding(.vertical, 3)
        .help("Tone range the three sliders act on (• = adjusted); the ranges overlap smoothly by lightness, as in Photoshop")
        SliderRow(title: "Cyan – Red", value: balanceBinding(tone, \.cyanRed), range: -100...100, defaultValue: 0, format: "%.0f", help: "Negative towards cyan, positive towards red, in the \(tone.displayName.lowercased())", labelWidth: lw)
        SliderRow(title: "Magenta – Green", value: balanceBinding(tone, \.magentaGreen), range: -100...100, defaultValue: 0, format: "%.0f", help: "Negative towards magenta, positive towards green", labelWidth: lw)
        SliderRow(title: "Yellow – Blue", value: balanceBinding(tone, \.yellowBlue), range: -100...100, defaultValue: 0, format: "%.0f", help: "Negative towards yellow, positive towards blue", labelWidth: lw)
        Toggle("Preserve luminosity", isOn: g.preserveLuminosity).toggleStyle(.checkbox).font(.caption)
            .help("Keep each pixel's lightness: only hue and saturation move (Photoshop's default)")
        Divider().padding(.vertical, 8)
        HStack(spacing: 4) {
            Text("Selective colour").font(.caption.bold())
            Spacer()
            Button { model.settings.grading.selective = [:] } label: { Image(systemName: "arrow.counterclockwise") }
                .controlSize(.mini).buttonStyle(.borderless).help("Reset the selective colour of every range")
                .disabled(model.settings.grading.selectiveIsNeutral)
        }
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
        HStack(alignment: .bottom, spacing: 4) {
            Text(model.settings.denoise.method.summary).font(.caption2).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            Button { model.settings.denoise = DenoiseSettings() } label: { Image(systemName: "arrow.counterclockwise") }
                .controlSize(.mini).buttonStyle(.borderless).help("Reset the denoise settings")
                .disabled(model.settings.denoise == DenoiseSettings())
        }
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
        HStack {
            Spacer()
            Button("Reset") { model.settings.detail = DetailSettings() }.controlSize(.small)
                .disabled(model.settings.detail == DetailSettings())
        }
    }

    // MARK: Stretch

    @ViewBuilder var stretchSection: some View {
        let s = $model.settings.stretch
        MenuPicker(title: "Method", selection: s.method, options: StretchMethod.allCases) { $0.displayName }
        MenuPicker(title: "Order", selection: $model.settings.stretchStage, options: StretchStage.allCases) { $0.shortName }
            .help("Stretch first: each channel is stretched, then combined. Combine first: the linear channels are combined, then the colour image is stretched.")
        Toggle("Linked channels", isOn: s.linked).help("Same stretch for all channels")
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
            Text("Unlinked auto-stretch equalises every channel: normalisation and gain have no visible effect.")
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
        let lw: CGFloat = 70
        HStack(spacing: 6) {
            if let layer = model.starsLayer {
                Image(systemName: "sparkles").foregroundStyle(.secondary)
                Text(layer.fileName).font(.caption).lineLimit(1).truncationMode(.middle)
                    .help("Stars layer: \(layer.fileName) (\(layer.formatDescription)), kept as an RGB image and blended on top")
                Spacer(minLength: 0)
                Button { model.removeStarsLayer() } label: { Image(systemName: "xmark.circle") }.buttonStyle(.borderless).controlSize(.small).help("Remove the stars layer")
            } else {
                Button("Load RGB stars…") {
                    Panels.openImages { urls in if let u = urls.first { Task { @MainActor in model.loadStarsLayer(from: u) } } }
                }.controlSize(.small)
                    .help("An RGB image kept whole (not split into channels) and blended as stars: a stars-only image, or any RGB frame. Dropping a colour file offers the same choice.")
                if hasRGB { Text("or the R, G, B channels").font(.caption2).foregroundStyle(.secondary) }
            }
        }
        Toggle("Blend RGB stars into the result", isOn: s.enabled).disabled(!model.hasStars)
        if !model.hasStars { Text("Load a stars layer, or R, G and B channels, to enable.").font(.caption2).foregroundStyle(.secondary) }
        if model.settings.stars.enabled {
            // One column, scrolled: the layer's look, then the blend.
            SliderRow(title: "Amount", value: s.amount, range: 0...1, defaultValue: 1, labelWidth: lw)
            SliderRow(title: "Brightness", value: s.brightness, range: -2...2, defaultValue: 0, format: "%.2f", help: "Gain of the star layer in stops, after its stretch", note: "EV", labelWidth: lw)
            SliderRow(title: "Contrast", value: s.contrast, range: -1...1, defaultValue: 0, format: "%.2f", help: "Spreads the star layer around the mid-tone: positive darkens faint halos and keeps the cores, negative softens", labelWidth: lw)
            SliderRow(title: "Star sat.", value: s.saturation, range: 0...3, defaultValue: 1.3, labelWidth: lw)
            Toggle("Brightest stars to white", isOn: s.normalize).toggleStyle(.checkbox).font(.caption)
                .help("Scale the layer so its brightest stars reach white — the brightest values of the result (whole-image peak, so preview and export agree). With a lowered Amount the cores still stay white: the amount then fades the faint stars instead of greying everything")
            Divider().padding(.vertical, 8)
            MenuPicker(title: "Blend", selection: s.mode, options: StarsBlendMode.allCases) { $0.displayName }
                .help("Screen: adds without clipping, soft on bright nebulae (default).\nLighten: the brighter of the two.\nAdd: sum, clipped.")
            MenuPicker(title: "Star data", selection: s.stretch, options: StarsStretchMode.allCases) { $0.displayName }
            Toggle("Mask by luminance", isOn: s.useMask).toggleStyle(.checkbox).font(.caption).help("Off for star-only images from StarNet/StarXTerminator.")
            if model.settings.stars.useMask {
                SliderRow(title: "Threshold", value: s.threshold, range: 0...1, defaultValue: 0.15, labelWidth: lw)
                SliderRow(title: "Softness", value: s.softness, range: 0.01...1, defaultValue: 0.25, labelWidth: lw)
            }
        }
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
