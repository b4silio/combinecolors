using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Orientation = System.Windows.Controls.Orientation;
using Stretch = System.Windows.Media.Stretch;
using System.Windows.Input;
using System.Windows.Media;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Views;

/// <summary>
/// Horizontal strip of processing blocks under the preview. Every block collapses on its own
/// (several can be open at once); the collapsed set is remembered. Dragging on empty space scrolls
/// the strip sideways.
/// </summary>
public sealed class ControlsStripView : Border
{
    public const double StripHeight = 204;
    readonly ProjectModel model;
    readonly ScrollViewer scroller;
    readonly StackPanel blocks = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(3, 6, 3, 6) };
    readonly Dictionary<string, ControlBlock> blockById = new();
    readonly HashSet<string> collapsed;
    readonly HashSet<ChannelKind> expandedChannels = new();
    string structure = "";
    ColorBandComponent bandComponent;
    ToneRange balanceTone;
    SelectiveRange selectiveRange;
    Point? dragStart; double dragOffset;

    enum ColorBandComponent { Hue, Saturation, Lightness }

    public ControlsStripView(ProjectModel model)
    {
        this.model = model;
        Height = StripHeight;
        Background = (Brush)Application.Current.Resources["PanelBackground"];
        collapsed = model.Preferences.GetString("collapsedBlocks")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() ?? new HashSet<string>();
        bandComponent = Enum.TryParse<ColorBandComponent>(model.Preferences.GetString("colorBandComponent") ?? "", true, out var bc) ? bc : ColorBandComponent.Saturation;
        balanceTone = EnumRaw.Parse<ToneRange>(model.Preferences.GetString("balanceTone")) ?? ToneRange.Midtones;
        selectiveRange = EnumRaw.Parse<SelectiveRange>(model.Preferences.GetString("selectiveRange")) ?? SelectiveRange.Neutrals;
        scroller = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = blocks, Background = Brushes.Transparent };
        scroller.PreviewMouseWheel += (_, e) => { if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 || e.OriginalSource is not Slider) { } };
        scroller.PreviewMouseLeftButtonDown += OnDragStart;
        scroller.PreviewMouseMove += OnDragMove;
        scroller.PreviewMouseLeftButtonUp += (_, _) => { dragStart = null; if (scroller.IsMouseCaptured) scroller.ReleaseMouseCapture(); };
        Child = scroller;
        Add("channels", "Channels", 250); Add("stretch", "Stretch", 262); Add("normalisation", "Normalisation", 290); Add("luminance", "Luminance", 250);
        Add("bands", "Colour bands", 226); Add("colour", "Colour", 250); Add("balance", "Balance", 262); Add("contrast", "Contrast", 250);
        Add("denoise", "Denoise", 330); Add("detail", "Detail", 262); Add("stars", "Combine stars", 250);
        balanceMode = model.Preferences.GetString("balanceMode") == "selective" ? "selective" : "balance";
        SetResets();
        model.Changed += OnChanged;
        Rebuild();
    }

    string balanceMode;

    /// <summary>A block reset on the shown tab's settings.</summary>
    void Reset(string id, string what, Func<CombineSettings, bool> neutral, Action<CombineSettings> apply)
        => blockById[id].SetReset("Reset " + what, () => !neutral(model.Settings), () => model.UpdateSettings(apply));

    void SetResets()
    {
        Reset("channels", "the channel weights and gains", s => s.Channels.Values.All(a => a.IsNeutral), s => s.Channels.Clear());
        Reset("stretch", "the stretch", s => s.Stretch.Equals(new StretchSettings()) && s.StretchStage == StretchStage.PreCombine, s => { s.Stretch = new StretchSettings(); s.StretchStage = StretchStage.PreCombine; });
        Reset("normalisation", "the normalisation", s => s.Normalization.Equals(new NormalizationSettings()), s => s.Normalization = new NormalizationSettings());
        Reset("luminance", "the luminance injection", s => s.Luminance.Equals(new LuminanceSettings()), s => s.Luminance = new LuminanceSettings());
        Reset("bands", "every colour band and the softness", s => s.ColorBands.Equals(new ColorBandsSettings()), s => s.ColorBands = new ColorBandsSettings());
        Reset("colour", "saturation, vibrance, SCNR and hue", s => s.Post.ColourIsNeutral, s => { s.Post.Saturation = 1; s.Post.Vibrance = 0; s.Post.ScnrGreen = 0; s.Post.HueShift = 0; });
        Reset("balance", "the colour balance and the selective colour", s => s.Grading.Equals(new ColorGradingSettings()), s => s.Grading = new ColorGradingSettings());
        Reset("contrast", "exposure, brightness, contrast, highlights, shadows, black / white point and gamma", s => s.Tone.IsNeutral && s.Post.LevelsAreNeutral,
            s => { s.Tone = new ToneSettings(); s.Post.BlackPoint = 0; s.Post.WhitePoint = 1; s.Post.Gamma = 1; });
        Reset("denoise", "the noise reduction", s => s.Denoise.Equals(new DenoiseSettings()), s => s.Denoise = new DenoiseSettings());
        Reset("detail", "local contrast and sharpening", s => s.Detail.Equals(new DetailSettings()), s => s.Detail = new DetailSettings());
        // The Combine stars reset keeps the combination on or off (only the blend parameters reset;
        // the luminance mask goes off, as for any stars-only tab).
        blockById["stars"].SetReset("Reset the blend (amount, brightness, contrast, saturation, mode, mask)", () => !model.CombineStars.Equals(NeutralStars()),
            () => { var n = NeutralStars(); model.UpdateCombineStars(s => CopyStars(n, s)); });
    }

    StarsBlendSettings NeutralStars() => new() { Enabled = model.CombineStars.Enabled, UseMask = false };

    static void CopyStars(StarsBlendSettings from, StarsBlendSettings to)
    {
        foreach (var f in typeof(StarsBlendSettings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)) f.SetValue(to, f.GetValue(from));
    }

    void Add(string id, string title, double width)
    {
        var b = new ControlBlock(title, width) { Expanded = !collapsed.Contains(id) };
        b.ExpandedChanged += open =>
        {
            if (open) collapsed.Remove(id); else collapsed.Add(id);
            model.Preferences.Set("collapsedBlocks", string.Join(",", collapsed.OrderBy(s => s)));
        };
        blockById[id] = b;
        blocks.Children.Add(b);
    }

    // Drag on empty space scrolls the strip.
    void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractive(e.OriginalSource as DependencyObject)) return;
        dragStart = e.GetPosition(this); dragOffset = scroller.HorizontalOffset;
    }

    void OnDragMove(object sender, MouseEventArgs e)
    {
        if (dragStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(this);
        if (!scroller.IsMouseCaptured && Math.Abs(p.X - start.X) > 3) scroller.CaptureMouse();
        if (scroller.IsMouseCaptured) scroller.ScrollToHorizontalOffset(dragOffset - (p.X - start.X));
    }

    static bool IsInteractive(DependencyObject? d)
    {
        for (; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is Slider or TextBox or ComboBox or System.Windows.Controls.Primitives.ButtonBase or ScrollBar or ComboBoxItem or MenuItem) return true;
        return false;
    }

    void OnChanged(string what)
    {
        switch (what)
        {
            case Change.Settings:
                if (Signature() != structure) Rebuild(); else RefreshAll();
                break;
            case Change.Sources: case Change.Context: case Change.Stars: case Change.Layer: Rebuild(); break;
            case Change.Frame: stretchNote?.Refresh(); break;
        }
    }

    bool HasRgb => model.Sources.ContainsKey(ChannelKind.Red) && model.Sources.ContainsKey(ChannelKind.Green) && model.Sources.ContainsKind(ChannelKind.Blue);

    string Signature()
    {
        var s = model.Settings;
        var avail = model.AvailableKinds;
        var parts = new List<string>
        {
            string.Join(",", avail.SortedByRaw().Select(k => k.RawValue())), s.Stretch.Method.ToString(), s.StretchStage.ToString(), s.Stretch.Linked.ToString(), s.Detail.SharpenMethod.ToString(),
            s.Normalization.Method.ToString(), s.Luminance.Enabled.ToString(), s.Luminance.Preset.ToString(), s.Luminance.ResolvedExpression(avail) ?? "-",
            s.Denoise.Method.ToString(), s.Stars.Enabled.ToString(), s.Stars.UseMask.ToString(), s.Mode.ToString(), s.PixelMath.HasLuminance.ToString(),
            model.Context?.Reference?.ToString() ?? "-", s.ColorBands.IsNeutral.ToString(), s.Detail.Equals(new DetailSettings()).ToString(), s.Denoise.Equals(new DenoiseSettings()).ToString(),
            bandComponent.ToString(), balanceTone.ToString(), selectiveRange.ToString(), s.Grading.Relative.ToString(), balanceMode, s.Grading.BalanceIsNeutral.ToString(), s.Grading.SelectiveIsNeutral.ToString(),
            model.ActiveLayer.ToString(), model.HasStars.ToString(), model.CombineStars.Enabled.ToString(), model.CombineStars.UseMask.ToString(), model.StarsCompositeBusy.ToString(), model.BackgroundCompositeBusy.ToString(),
            string.Join(",", ToneRanges.All.Select(t => s.Grading.GetBalance(t).IsNeutral)), string.Join(",", SelectiveRanges.All.Select(r => s.Grading.GetSelective(r).IsNeutral)),
        };
        parts.AddRange(avail.Select(k => k + ":" + s.Adjust(k).IsNeutral));
        if (model.Context is RenderContext ctx) parts.AddRange(ctx.Transforms.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value.Description + (ctx.NormalizedMaskedStats.TryGetValue(kv.Key, out var st) ? $" {st.Background:G4} {st.SignalStrength:G4}" : "")));
        return string.Join("|", parts);
    }

    void RefreshAll() { foreach (var b in blockById.Values) b.RefreshAll(); }

    void Rebuild()
    {
        structure = Signature();
        Build("channels", ChannelsSection); Build("stretch", StretchSection); Build("normalisation", NormalizationSection); Build("luminance", LuminanceSection);
        Build("bands", ColorBandsSection); Build("colour", ColourSection); Build("balance", BalanceSection); Build("contrast", ContrastSection);
        Build("denoise", DenoiseSection); Build("detail", DetailSection); Build("stars", StarsSection);
        RefreshAll();
    }

    void Build(string id, Action<StackPanel> builder)
    {
        var panel = blockById[id].Content;
        panel.Children.Clear();
        builder(panel);
    }

    // Helpers binding into the settings.

    SliderRow S(string title, Func<CombineSettings, float> get, Action<CombineSettings, float> set, float lo, float hi, float def, string format = "0.00", bool log = false, string? help = null, string? note = null, double labelWidth = 70, float curve = 1)
        => new(title, () => get(model.Settings), v => model.UpdateSettings(s => set(s, v)), lo, hi, def, format, log, help, note, labelWidth, curve);

    static readonly FontFamily MonoFont = new("Consolas");

    IntSliderRow I(string title, Func<CombineSettings, int> get, Action<CombineSettings, int> set, int lo, int hi, int def, string? help = null)
        => new(title, () => get(model.Settings), v => model.UpdateSettings(s => set(s, v)), lo, hi, def, help);

    CheckRow C(string title, Func<CombineSettings, bool> get, Action<CombineSettings, bool> set, string? help = null)
        => new(title, () => get(model.Settings), v => model.UpdateSettings(s => set(s, v)), help);

    MenuPicker<T> M<T>(string title, Func<CombineSettings, T> get, Action<CombineSettings, T> set, IReadOnlyList<T> options, Func<T, string> label, string? help = null) where T : notnull
        => new(title, () => get(model.Settings), v => model.UpdateSettings(s => set(s, v)), options, label, help);

    // Channels

    void ChannelsSection(StackPanel p)
    {
        var kinds = ChannelKinds.All.Where(k => model.Sources.ContainsKey(k)).ToList();
        if (kinds.Count == 0) p.Children.Add(Ui.Caption("No channels loaded.", 11));
        foreach (var k in kinds) p.Children.Add(ChannelAdjustView(k));
    }

    UIElement ChannelAdjustView(ChannelKind kind)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        bool pre = model.Settings.StretchStage == StretchStage.PreCombine;
        var head = new DockPanel();
        var enabled = C("", s => s.Adjust(kind).Enabled, (s, v) => { var a = s.Adjust(kind).Clone(); a.Enabled = v; s.SetAdjust(kind, a); });
        enabled.Margin = new Thickness(0, 0, 2, 0); enabled.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(enabled);
        head.Children.Add(Ui.Dot(Ui.Tint(kind)));
        head.Children.Add(new TextBlock { Text = kind.DisplayName(), FontWeight = FontWeights.Bold, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        if (!model.Settings.AllChannels(model.AvailableKinds).Contains(kind)) head.Children.Add(new TextBlock { Text = "unused", FontSize = 9, Foreground = Brushes.DarkGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) });
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (!model.Settings.Adjust(kind).IsNeutral) right.Children.Add(Ui.SmallButton("Reset", () => model.UpdateSettings(s => s.SetAdjust(kind, new ChannelAdjust()))));
        bool expanded = expandedChannels.Contains(kind);
        var toggle = Ui.SmallButton(expanded ? "▴" : "▾", () => { if (!expandedChannels.Remove(kind)) expandedChannels.Add(kind); Rebuild(); });
        toggle.Margin = new Thickness(0);
        right.Children.Add(toggle);
        head.Children.Add(right);
        panel.Children.Add(head);
        panel.Children.Add(S("Weight", s => s.Adjust(kind).Weight, (s, v) => Edit(s, kind, a => a.Weight = v), 0, 2, 1, help: pre ? "Multiplier after the stretch." : "Linear multiplier before the combined stretch."));
        if (expanded)
        {
            panel.Children.Add(S("Linear gain", s => s.Adjust(kind).Gain, (s, v) => Edit(s, kind, a => a.Gain = v), 0.1f, 10, 1, log: true, help: "Multiplier on the normalised linear data (before the stretch)."));
            panel.Children.Add(S("Pedestal σ", s => s.Adjust(kind).OffsetSigma, (s, v) => Edit(s, kind, a => a.OffsetSigma = v), -20, 20, 0, "0.0", help: "Adds a constant in units of the reference background sigma."));
            if (pre)
            {
                panel.Children.Add(S("Target boost", s => s.Adjust(kind).StretchBoost, (s, v) => Edit(s, kind, a => a.StretchBoost = v), -0.15f, 0.4f, 0, help: "Raises/lowers this channel's stretch target background."));
                panel.Children.Add(S("Black point", s => s.Adjust(kind).BlackPoint, (s, v) => Edit(s, kind, a => a.BlackPoint = v), 0, 0.5f, 0, "0.000", help: "Subtracted after the stretch."));
                panel.Children.Add(S("Gamma", s => s.Adjust(kind).Gamma, (s, v) => Edit(s, kind, a => a.Gamma = v), 0.3f, 3, 1, log: true));
            }
        }
        return panel;
    }

    static void Edit(CombineSettings s, ChannelKind k, Action<ChannelAdjust> edit) { var a = s.Adjust(k).Clone(); edit(a); s.SetAdjust(k, a); }

    // Stretch

    void StretchSection(StackPanel p)
    {
        const double lw = 82;
        var s = model.Settings;
        var st = s.Stretch;
        p.Children.Add(M("Method", x => x.Stretch.Method, (x, v) => x.Stretch.Method = v, StretchMethods.All, m => m.DisplayName()));
        p.Children.Add(M("Order", x => x.StretchStage, (x, v) => x.StretchStage = v, StretchStages.All, m => m.ShortName(), "Stretch first: each channel is stretched, then combined. Combine first: the linear channels are combined, then the colour image is stretched."));
        if (st.Method != StretchMethod.None) p.Children.Add(C("Linked channels", x => x.Stretch.Linked, (x, v) => x.Stretch.Linked = v, "Same stretch for all channels"));
        if (st.UsesTargetBackground)
            p.Children.Add(S("Target bg", x => x.Stretch.TargetBackground, (x, v) => x.Stretch.TargetBackground = v, 0.02f, 0.6f, 0.2f, help: "Where the median background lands after the stretch (PixInsight STF default 0.25).", labelWidth: lw));
        if (st.UsesClipping)
            p.Children.Add(S("Shadow clip", x => x.Stretch.ShadowsClipping, (x, v) => x.Stretch.ShadowsClipping = v, -6, 0, -2.8f, "0.0", help: "Black point in noise units (MADN) below the median background (STF default −2.8). Closer to 0 darkens the sky.", labelWidth: lw));
        // The manual strengths are relative to the data's noise, so the same values suit any linear stack.
        switch (st.Method)
        {
            case StretchMethod.Mtf:
                p.Children.Add(S("Midtones", x => x.Stretch.MtfMidtonesSigma, (x, v) => x.Stretch.MtfMidtonesSigma = v, 0.3f, 1000, 10, "0.0", log: true,
                    help: "Midtones balance in noise units: lower is brighter. The sky (2.8 noises above the default black point) lands near 2.8 / (2.8 + value): 3 ≈ 0.5, 10 ≈ 0.2, 100 ≈ 0.03. The absolute midtones (PixInsight HistogramTransformation) are shown below.", labelWidth: lw));
                break;
            case StretchMethod.Arcsinh:
                p.Children.Add(S("Stretch", x => x.Stretch.ArcsinhStrength, (x, v) => x.Stretch.ArcsinhStrength = v, 0.01f, 100, 1, "0.00", log: true,
                    help: "Stretch factor relative to the noise: 1 puts the sky near 0.2 on most linear stacks, 0.1 near 0.05, 10 near 0.35. The absolute factor (PixInsight ArcsinhStretch) is shown below.", labelWidth: lw));
                break;
            case StretchMethod.Ghs:
                p.Children.Add(S("Stretch", x => x.Stretch.GhsStrength, (x, v) => x.Stretch.GhsStrength = v, 0.001f, 100, 0.1f, "0.000", log: true,
                    help: "Stretch factor D relative to the noise. The absolute ln(D+1) (PixInsight / Siril GHS) is shown below.", labelWidth: lw));
                p.Children.Add(S("b", x => x.Stretch.GhsB, (x, v) => x.Stretch.GhsB = v, 0, 15, 2, "0.0", help: "Local intensity: how concentrated the stretch is around the symmetry point (0 = exponential).", labelWidth: lw));
                p.Children.Add(S("SP", x => x.Stretch.GhsSPPercentile, (x, v) => x.Stretch.GhsSPPercentile = v, 0, 100, 0, "0.0",
                    help: "Symmetry point, as the share of the image's pixels darker than it: 0 = the black point, ≈ 50 = the sky background, 90+ = nebula. The contrast is greatest around it.", note: "%", labelWidth: lw, curve: 2));
                p.Children.Add(S("LP", x => x.Stretch.GhsLPPercentile, (x, v) => x.Stretch.GhsLPPercentile = v, 0, 100, 0, "0.0",
                    help: "Shadow protection: below this percentile the curve continues as a straight line (never above SP).", note: "%", labelWidth: lw, curve: 2));
                p.Children.Add(S("HP", x => x.Stretch.GhsHPPercentile, (x, v) => x.Stretch.GhsHPPercentile = v, 0, 100, 100, "0.00",
                    help: "Highlight protection: above this percentile the curve continues as a straight line, keeping star cores from saturating (100 = none; never below SP).", note: "%", labelWidth: lw, curve: 0.5f));
                break;
            case StretchMethod.Log:
                p.Children.Add(S("Strength", x => x.Stretch.LogStrength, (x, v) => x.Stretch.LogStrength = v, 0.01f, 100, 1, "0.00", log: true,
                    help: "k relative to the noise: 1 puts the sky near 0.17 on most linear stacks. The absolute k is shown below.", labelWidth: lw));
                break;
        }
        // Highlight clipping shown as the share of pixels clipped to white (0…2 %).
        if (st.UsesClipping)
            p.Children.Add(S("Highlights", x => (1 - x.Stretch.HighlightsPercentile) * 100, (x, v) => x.Stretch.HighlightsPercentile = 1 - Math.Min(Math.Max(v, 0), 100) / 100, 0, 2, 0, "0.000",
                help: "Share of the pixels clipped to white (the white point). Auto STF keeps the background on target; the other methods keep their strength relative to the noise, so the picture brightens moderately.", note: "%", labelWidth: lw, curve: 3));
        p.Children.Add(S("Gamma", x => x.Stretch.Gamma, (x, v) => x.Stretch.Gamma = v, 0.3f, 3, 1, log: true, labelWidth: lw));
        stretchNote = new NoteRow(ResolvedStretchNote, 9, null, MonoFont);
        p.Children.Add(stretchNote);
        if (!st.Linked && st.Method != StretchMethod.None && s.StretchStage == StretchStage.PreCombine)
            p.Children.Add(Ui.Caption("Unlinked stretches equalise every channel: normalisation and gain have little visible effect."));
    }

    NoteRow? stretchNote;

    /// <summary>The absolute values the manual stretch resolved to (PixInsight-style numbers), per channel when
    /// they differ; null for Auto STF and None.</summary>
    string? ResolvedStretchNote()
    {
        var method = model.Settings.Stretch.Method;
        if (method is not (StretchMethod.Mtf or StretchMethod.Arcsinh or StretchMethod.Ghs or StretchMethod.Log)) return null;
        if (model.Frame is not { } f || f.Settings.Stretch.Method != method) return null;
        List<(string, ResolvedStretch)> entries = model.Settings.StretchStage == StretchStage.PreCombine
            ? ChannelKinds.All.Where(f.ChannelStretch.ContainsKey).Select(k => (k.ShortName(), f.ChannelStretch[k])).ToList()
            : new[] { "R", "G", "B", "L" }.Zip(f.PostStretch ?? []).Select(t => (t.First, t.Second)).ToList();
        if (entries.Count == 0) return null;
        string Value(ResolvedStretch r) => method switch
        {
            StretchMethod.Mtf => string.Create(CultureInfo.InvariantCulture, $"m {r.Midtones:0.00000}"),
            StretchMethod.Arcsinh => string.Create(CultureInfo.InvariantCulture, $"factor {r.ArcsinhStretch:0}"),
            StretchMethod.Log => string.Create(CultureInfo.InvariantCulture, $"k {r.LogK:0}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"ln(D+1) {Math.Log(r.GhsD + 1):0.00}, SP {r.GhsSP:0.00000}"),
        };
        var values = entries.Select(e => (e.Item1, Value(e.Item2))).ToList();
        if (values.Select(v => v.Item2).Distinct().Count() == 1) return "Absolute: " + values[0].Item2;
        return "Absolute: " + string.Join(" · ", values.Select(v => $"{v.Item1} {v.Item2}"));
    }

    // Normalisation

    void NormalizationSection(StackPanel p)
    {
        var s = model.Settings;
        p.Children.Add(M("Method", x => x.Normalization.Method, (x, v) => x.Normalization.Method = v, NormalizationMethods.All, m => m.DisplayName()));
        p.Children.Add(Ui.Caption(s.Normalization.Method.Summary()));
        var options = new List<ChannelKind?> { null };
        options.AddRange(ChannelKinds.All.Where(k => model.Sources.ContainsKey(k)).Select(k => (ChannelKind?)k));
        var refName = model.Context?.Reference?.DisplayName() ?? "–";
        p.Children.Add(new MenuPicker<object>("Reference", () => (object?)s.Normalization.Reference ?? "auto", v => model.UpdateSettings(x => x.Normalization.Reference = v as ChannelKind?),
            options.Select(o => (object?)o ?? "auto").ToList(), o => o is ChannelKind k ? k.DisplayName() : $"Automatic ({refName})"));
        p.Children.Add(C("Use star mask for statistics", x => x.Normalization.UseStarMask, (x, v) => x.Normalization.UseStarMask = v));
        if (s.Normalization.Method == NormalizationMethod.BackgroundSignal)
            p.Children.Add(S("Signal pct", x => x.Normalization.SignalPercentile, (x, v) => x.Normalization.SignalPercentile = v, 0.9f, 0.9999f, 0.995f, "0.0000", help: "Percentile of the star-masked image taken as the emission signal level."));
        if (s.Normalization.Method == NormalizationMethod.LinearFit)
            p.Children.Add(S("Reject σ", x => x.Normalization.FitRejectSigma, (x, v) => x.Normalization.FitRejectSigma = v, 1, 5, 2.5f, "0.0"));
        if (model.Context is RenderContext ctx)
        {
            foreach (var k in ChannelKinds.All.Where(k => ctx.Transforms.ContainsKey(k)))
            {
                var row = new DockPanel();
                row.Children.Add(new TextBlock { Text = k.ShortName(), Width = 36, FontSize = 9, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Gray });
                string stats = ctx.NormalizedMaskedStats.TryGetValue(k, out var st) ? $"bg {Ui.G4(st.Background)}  sig {Ui.G4(st.SignalStrength)}σ" : "";
                var right = new TextBlock { Text = stats, FontSize = 9, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Right };
                DockPanel.SetDock(right, Dock.Right);
                row.Children.Add(right);
                row.Children.Add(new TextBlock { Text = ctx.Transforms[k].Description, FontSize = 9, FontFamily = new FontFamily("Consolas"), Foreground = Brushes.Gray });
                p.Children.Add(row);
            }
        }
    }

    // Luminance

    void LuminanceSection(StackPanel p)
    {
        var lum = model.Settings.Luminance;
        var avail = model.AvailableKinds;
        p.Children.Add(C("Inject luminance", x => x.Luminance.Enabled, (x, v) => x.Luminance.Enabled = v));
        if (lum.Enabled)
        {
            p.Children.Add(M("Source", x => x.Luminance.Preset, (x, v) => x.Luminance.Preset = v, LuminancePresets.All,
                pr => pr.DisplayName() + (pr.Expression(avail, lum.CustomExpression) == null && pr != LuminancePreset.Custom ? " (unavailable)" : "")));
            if (lum.Preset == LuminancePreset.Custom)
            {
                var tb = new TextBox { Text = lum.CustomExpression, FontSize = 11, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 2, 0, 2), ToolTip = "e.g. 0.8*Ha + 0.2*max(OIII, SII)" };
                tb.TextChanged += (_, _) => model.UpdateSettings(x => x.Luminance.CustomExpression = tb.Text);
                p.Children.Add(tb);
            }
            p.Children.Add(new NoteRow(() =>
            {
                var l = model.Settings.Luminance; var av = model.AvailableKinds;
                if (l.ResolvedExpression(av) is not string e) return "No usable channel loaded for this source.";
                bool ok = PixelMath.TryParse(e, out _);
                var missing = l.RequiredChannels(av).Where(k => !av.Contains(k)).ToList();
                return ok ? (missing.Count == 0 ? "Using: " + e : "Missing channels: " + string.Join(", ", missing.Select(k => k.DisplayName()))) : "Expression does not parse";
            }, 9, () =>
            {
                var l = model.Settings.Luminance; var av = model.AvailableKinds;
                if (l.ResolvedExpression(av) is not string e) return Brushes.DarkOrange;
                return PixelMath.TryParse(e, out _) && l.RequiredChannels(av).All(av.Contains) ? Brushes.Gray : Brushes.Red;
            }, new FontFamily("Consolas")));
            p.Children.Add(S("Amount", x => x.Luminance.Amount, (x, v) => x.Luminance.Amount = v, 0, 1, 1));
            p.Children.Add(M("Method", x => x.Luminance.Method, (x, v) => x.Luminance.Method = v, LuminanceMethods.All, m => m.DisplayName()));
        }
        if (model.Settings.Mode == CombineMode.PixelMath && model.Settings.PixelMath.HasLuminance)
            p.Children.Add(Ui.Caption("The Pixel Math luminance expression overrides this source."));
    }

    // Colour bands

    void ColorBandsSection(StackPanel p)
    {
        var head = new DockPanel();
        var seg = Ui.Segmented(new[] { ColorBandComponent.Hue, ColorBandComponent.Saturation, ColorBandComponent.Lightness }, c => c switch { ColorBandComponent.Hue => "Hue", ColorBandComponent.Saturation => "Sat", _ => "Light" },
            () => bandComponent, v => { bandComponent = v; model.Preferences.Set("colorBandComponent", v.ToString()); Rebuild(); }, out _, 10);
        head.Children.Add(seg);
        p.Children.Add(head);
        foreach (var band in ColorBands.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var label = Ui.Row(Ui.Dot(new SolidColorBrush(HsvColor(band.Hue(), 0.85, 0.95))), new TextBlock { Text = band.DisplayName(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            label.Width = 62; label.ToolTip = $"Double-click to reset {band.DisplayName()}";
            var b = band;
            label.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) model.UpdateSettings(x => x.ColorBands.Set(b, new ColorBandAdjust())); };
            row.Children.Add(label);
            MiniSlider slider = bandComponent switch
            {
                ColorBandComponent.Hue => new MiniSlider(() => model.Settings.ColorBands.Adjust(b).Hue, v => model.UpdateSettings(x => { var a = x.ColorBands.Adjust(b).Clone(); a.Hue = v; x.ColorBands.Set(b, a); }), -60, 60, "{0:+0;-0}°"),
                ColorBandComponent.Saturation => new MiniSlider(() => model.Settings.ColorBands.Adjust(b).Saturation, v => model.UpdateSettings(x => { var a = x.ColorBands.Adjust(b).Clone(); a.Saturation = v; x.ColorBands.Set(b, a); }), -1, 1, "{0:+0.00;-0.00}"),
                _ => new MiniSlider(() => model.Settings.ColorBands.Adjust(b).Lightness, v => model.UpdateSettings(x => { var a = x.ColorBands.Adjust(b).Clone(); a.Lightness = v; x.ColorBands.Set(b, a); }), -1, 1, "{0:+0.00;-0.00}"),
            };
            row.Children.Add(slider);
            p.Children.Add(row);
        }
        p.Children.Add(S("Softness", x => x.ColorBands.Softness, (x, v) => x.ColorBands.Softness = v, 0, 1, 0,
            help: "Feathers the bands: each one reaches further into its neighbours (up to twice as far) with a smooth fall-off, and fades in more gradually from grey, so an edit blends across colour edges instead of stopping at them."));
    }

    static Color HsvColor(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch { < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x), < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    // Colour

    void ColourSection(StackPanel p)
    {
        p.Children.Add(S("Saturation", x => x.Post.Saturation, (x, v) => x.Post.Saturation = v, 0, 3, 1));
        p.Children.Add(S("Vibrance", x => x.Post.Vibrance, (x, v) => x.Post.Vibrance = v, -1, 2, 0, help: "Saturation boost that acts mostly on the less saturated colours."));
        p.Children.Add(S("SCNR green", x => x.Post.ScnrGreen, (x, v) => x.Post.ScnrGreen = v, 0, 1, 0, help: "Average-neutral green removal."));
        p.Children.Add(S("Hue shift", x => x.Post.HueShift, (x, v) => x.Post.HueShift = v, -180, 180, 0, "0"));
    }

    // Contrast (tone, after Balance)

    void ContrastSection(StackPanel p)
    {
        const double lw = 76;
        p.Children.Add(S("Exposure", x => x.Tone.Exposure, (x, v) => x.Tone.Exposure = v, -3, 3, 0, help: "Gain in stops on the stretched image (clipped at white).", note: "EV", labelWidth: lw));
        p.Children.Add(S("Brightness", x => x.Tone.Brightness, (x, v) => x.Tone.Brightness = v, -1, 1, 0, help: "Lifts (positive) or lowers the mid-tones; black and white stay.", labelWidth: lw));
        p.Children.Add(S("Contrast", x => x.Tone.Contrast, (x, v) => x.Tone.Contrast = v, -1, 1, 0, help: "S-curve around the mid-tone: darker darks and brighter brights; negative flattens.", labelWidth: lw));
        p.Children.Add(S("Highlights", x => x.Tone.Highlights, (x, v) => x.Tone.Highlights = v, -1, 1, 0, help: "Brightens (positive) or recovers (negative) the bright tones; white and the mid-tone stay.", labelWidth: lw));
        p.Children.Add(S("Shadows", x => x.Tone.Shadows, (x, v) => x.Tone.Shadows = v, -1, 1, 0, help: "Opens up (positive) or deepens (negative) the dark tones; black and the mid-tone stay.", labelWidth: lw));
        p.Children.Add(S("Black point", x => x.Post.BlackPoint, (x, v) => x.Post.BlackPoint = v, 0, 0.5f, 0, "0.000", labelWidth: lw));
        p.Children.Add(S("White point", x => x.Post.WhitePoint, (x, v) => x.Post.WhitePoint = v, 0.5f, 1, 1, "0.000", labelWidth: lw));
        p.Children.Add(S("Gamma", x => x.Post.Gamma, (x, v) => x.Post.Gamma = v, 0.3f, 3, 1, log: true, labelWidth: lw));
    }

    // Balance (Color Balance + Selective Color)

    /// <summary>A slider over a −1…1 field of the selected tone range, shown in percent.</summary>
    SliderRow Balance(string title, ToneRange tone, Func<ToneBalance, float> get, Action<ToneBalance, float> set, string help, double labelWidth)
        => S(title, x => MathF.Round(get(x.Grading.GetBalance(tone)) * 100, MidpointRounding.AwayFromZero), (x, v) => { var a = x.Grading.GetBalance(tone).Clone(); set(a, v / 100); x.Grading.SetBalance(tone, a); }, -50, 50, 0, "0", help: help, labelWidth: labelWidth);

    SliderRow Selective(string title, SelectiveRange range, Func<SelectiveAdjust, float> get, Action<SelectiveAdjust, float> set, string help, double labelWidth)
        => S(title, x => MathF.Round(get(x.Grading.GetSelective(range)) * 100, MidpointRounding.AwayFromZero), (x, v) => { var a = x.Grading.GetSelective(range).Clone(); set(a, v / 100); x.Grading.SetSelective(range, a); }, -100, 100, 0, "0", help: help, labelWidth: labelWidth);

    void BalanceSection(StackPanel p)
    {
        const double lw = 92;
        var g = model.Settings.Grading;
        var tone = balanceTone; var range = selectiveRange;
        // One of the two tools at a time (• = adjusted).
        var modeSeg = Ui.Segmented(new[] { "balance", "selective" }, m => m == "balance" ? "Color Balance" + (g.BalanceIsNeutral ? "" : " •") : "Selective Color" + (g.SelectiveIsNeutral ? "" : " •"),
            () => balanceMode, v => { balanceMode = v; model.Preferences.Set("balanceMode", v); Rebuild(); }, out _, 11);
        modeSeg.HorizontalAlignment = HorizontalAlignment.Center; modeSeg.Margin = new Thickness(0, 0, 0, 4);
        p.Children.Add(modeSeg);
        if (balanceMode != "selective")
        {
            var toneSeg = Ui.Segmented(ToneRanges.All, t => t.DisplayName() + (g.GetBalance(t).IsNeutral ? "" : " •"), () => balanceTone, v => { balanceTone = v; model.Preferences.Set("balanceTone", EnumRaw.Name(v)); Rebuild(); }, out _, 10);
            toneSeg.ToolTip = "Tone range the three sliders act on (• = adjusted); the ranges overlap smoothly by lightness, as in Photoshop";
            toneSeg.HorizontalAlignment = HorizontalAlignment.Center; toneSeg.Margin = new Thickness(0, 2, 0, 4);
            p.Children.Add(toneSeg);
            p.Children.Add(Balance("Cyan – Red", tone, a => a.CyanRed, (a, v) => a.CyanRed = v, $"Negative towards cyan, positive towards red, in the {tone.DisplayName().ToLowerInvariant()}", lw));
            p.Children.Add(Balance("Magenta – Green", tone, a => a.MagentaGreen, (a, v) => a.MagentaGreen = v, "Negative towards magenta, positive towards green", lw));
            p.Children.Add(Balance("Yellow – Blue", tone, a => a.YellowBlue, (a, v) => a.YellowBlue = v, "Negative towards yellow, positive towards blue", lw));
            p.Children.Add(C("Preserve luminosity", x => x.Grading.PreserveLuminosity, (x, v) => x.Grading.PreserveLuminosity = v, "Keep each pixel's lightness: only hue and saturation move (Photoshop's default)"));
        }
        else
        {
            var rangeSeg = Ui.Segmented(SelectiveRanges.All, r => r.DisplayName() + (g.GetSelective(r).IsNeutral ? "" : " •"), () => selectiveRange, v => { selectiveRange = v; model.Preferences.Set("selectiveRange", EnumRaw.Name(v)); Rebuild(); }, out _, 10);
            rangeSeg.ToolTip = "Whites: the brightest nearly-grey pixels; Neutrals: greys around mid-tone; Blacks: the darkest. The hue ranges are the Colour bands block";
            rangeSeg.HorizontalAlignment = HorizontalAlignment.Center; rangeSeg.Margin = new Thickness(0, 2, 0, 4);
            p.Children.Add(rangeSeg);
            p.Children.Add(Selective("Cyan", range, a => a.Cyan, (a, v) => a.Cyan = v, $"More cyan ink (less red) in the {range.DisplayName().ToLowerInvariant()}; negative removes it", lw));
            p.Children.Add(Selective("Magenta", range, a => a.Magenta, (a, v) => a.Magenta = v, "More magenta ink (less green); negative removes it", lw));
            p.Children.Add(Selective("Yellow", range, a => a.Yellow, (a, v) => a.Yellow = v, "More yellow ink (less blue); negative removes it", lw));
            p.Children.Add(Selective("Black", range, a => a.Black, (a, v) => a.Black = v, "Darkens (positive) or lightens (negative) the range", lw));
            var methodRow = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            var methodSeg = Ui.Segmented(new[] { true, false }, b => b ? "Relative" : "Absolute", () => model.Settings.Grading.Relative, v => model.UpdateSettings(x => x.Grading.Relative = v), out _, 10);
            methodSeg.ToolTip = "Relative changes the ink by a fraction of what is there (pure white cannot be touched); Absolute adds the amount outright";
            DockPanel.SetDock(methodSeg, Dock.Right);
            methodRow.Children.Add(methodSeg);
            methodRow.Children.Add(new TextBlock { Text = "Method", FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
            p.Children.Add(methodRow);
        }
    }

    // Denoise

    void DenoiseSection(StackPanel p)
    {
        var d = model.Settings.Denoise;
        p.Children.Add(M("Method", x => x.Denoise.Method, (x, v) => x.Denoise.Method = v, DenoiseMethods.All, m => m.DisplayName(),
            "Wavelets: starlet k·σ thresholding, the classic astronomical method, fast. Non-local means: patch averaging, best on fine texture, slower. Guided filter: edge-preserving smoothing, fastest. BM3D: block matching + collaborative 3-D filtering, strongest on fine noise, slowest."));
        p.Children.Add(S("Luminance", x => x.Denoise.Amount, (x, v) => x.Denoise.Amount = v, 0, 1, 0, help: "Strength of the luminance noise reduction, relative to the measured noise level (so it adapts to the preview level and the export)."));
        switch (d.Method)
        {
            case DenoiseMethod.Wavelets: p.Children.Add(I("Layers", x => x.Denoise.Layers, (x, v) => x.Denoise.Layers = v, 1, 5, 3, "Number of wavelet scales denoised (1 = finest only).")); break;
            case DenoiseMethod.NonLocalMeans: p.Children.Add(I("Search", x => x.Denoise.Search, (x, v) => x.Denoise.Search = v, 3, 10, 5, "Search window radius in pixels; cost grows with its square.")); break;
            case DenoiseMethod.Guided: p.Children.Add(I("Radius", x => x.Denoise.Radius, (x, v) => x.Denoise.Radius = v, 1, 8, 3, "Window radius in pixels.")); break;
            case DenoiseMethod.Bm3d: p.Children.Add(I("Search", x => x.Denoise.BlockSearch, (x, v) => x.Denoise.BlockSearch = v, 6, 24, 16, "Block-matching window radius in pixels; larger windows average more blocks (smoother sky), cost grows with the window.")); break;
        }
        p.Children.Add(S("Protect", x => x.Denoise.ProtectBright, (x, v) => x.Denoise.ProtectBright = v, 0, 1, 0.5f, help: "Keeps bright structures (stars, nebula cores) untouched."));
        p.Children.Add(S("Colour", x => x.Denoise.Color, (x, v) => x.Denoise.Color = v, 0, 1, 0, help: "Smooths colour mottling (chroma only, luminance untouched)."));
        p.Children.Add(S("Hot pixels", x => x.Denoise.HotPixels, (x, v) => x.Denoise.HotPixels = v, 0, 1, 0, help: "Replaces isolated single-pixel spikes (often brought out by strong colour edits) by their neighbours' median, channel by channel; stars, which are wider than one pixel, are kept. Higher catches fainter spikes. Works at full resolution: visible at 1:1 and in the export."));
        p.Children.Add(Ui.Caption(d.Method.Summary(), 9));
    }

    // Detail

    void DetailSection(StackPanel p)
    {
        const double lw = 84;
        // One column, scrolled: local contrast by scale (fine → coarse), then the sharpening.
        var left = p;
        left.Children.Add(S("Micro", x => x.Detail.Micro, (x, v) => x.Detail.Micro = v, -1, 1, 0, help: "Micro-structure local contrast, the finest scale (radius ≈ 0.2 % of the width); stars are excluded from the blur to avoid dark halos.", note: "0.2%", labelWidth: lw));
        left.Children.Add(S("Texture", x => x.Detail.Texture, (x, v) => x.Detail.Texture = v, -1, 1, 0, help: "Fine-detail local contrast (radius ≈ 0.6 % of the width); stars are excluded from the blur to avoid dark halos.", note: "0.6%", labelWidth: lw));
        left.Children.Add(S("Clarity", x => x.Detail.Clarity, (x, v) => x.Detail.Clarity = v, -0.5f, 2, 0, help: "Mid-tone local contrast at a broader scale (radius ≈ 6 % of the width).", note: "6%", labelWidth: lw));
        left.Children.Add(S("Dehaze", x => x.Detail.Dehaze, (x, v) => x.Detail.Dehaze = v, -1, 1, 0, help: "Subtracts the large-scale haze component (radius ≈ 25 % of the width); negative adds haze.", note: "25%", labelWidth: lw));
        left.Children.Add(S("Low freq.", x => x.Detail.LowFrequency, (x, v) => x.Detail.LowFrequency = v, -0.5f, 2, 0, help: "Local contrast of the largest structures (radius ≈ 50 % of the width): separates big nebula regions from the sky without touching fine detail.", note: "50%", labelWidth: lw));
        left.Children.Add(Ui.Separator());
        var right = p;
        right.Children.Add(M("Method", x => x.Detail.SharpenMethod, (x, v) => x.Detail.SharpenMethod = v, SharpenMethods.All, m => m.DisplayName(),
            "Deconvolution: Richardson–Lucy against a Gaussian blur of the radius — edges get steeper and small stars tighter, as if the blur were undone.\nUnsharp mask: adds the difference to a blurred copy — boosts fine contrast (and noise) rather than undoing a blur.\nBoth are held within the local range of the picture, so no dark rings form around stars."));
        right.Children.Add(S("Sharpen", x => x.Detail.Sharpen, (x, v) => x.Detail.Sharpen = v, 0, 2, 0,
            help: model.Settings.Detail.SharpenMethod == SharpenMethod.Deconvolution
                ? "Deconvolution strength (0.1 = one Richardson–Lucy iteration, 2 = twenty). Works in full-resolution pixels: judge it at 1:1 with HQ on; it runs after Denoise since it also sharpens grain."
                : "Unsharp-mask amount on the luminance. Works in full-resolution pixels: judge it at 1:1 with HQ on.", labelWidth: lw));
        right.Children.Add(S("Radius", x => x.Detail.SharpenRadius, (x, v) => x.Detail.SharpenRadius = v, 0.5f, 5, 1.5f, "0.0", help: "Size of the blur to undo (Gaussian σ) in full-resolution pixels: about the star FWHM / 2.35.", note: "px", labelWidth: lw));
        right.Children.Add(S("Masking", x => x.Detail.SharpenMasking, (x, v) => x.Detail.SharpenMasking = v, 0, 1, 0, help: "Protects flat, noisy areas: only detail above this fraction of the noise level is sharpened (like Camera Raw's Masking). Deconvolution always damps a little.", labelWidth: lw));
    }

    // Combine stars (the Background tab's blend settings, also editable from the Stars tab)

    SliderRow SS(string title, Func<StarsBlendSettings, float> get, Action<StarsBlendSettings, float> set, float lo, float hi, float def, string format = "0.00", string? help = null, string? note = null, double labelWidth = 70)
        => new(title, () => get(model.CombineStars), v => model.UpdateCombineStars(s => set(s, v)), lo, hi, def, format, false, help, note, labelWidth);

    CheckRow CS(string title, Func<StarsBlendSettings, bool> get, Action<StarsBlendSettings, bool> set, string? help = null)
        => new(title, () => get(model.CombineStars), v => model.UpdateCombineStars(s => set(s, v)), help);

    MenuPicker<T> MS<T>(string title, Func<StarsBlendSettings, T> get, Action<StarsBlendSettings, T> set, IReadOnlyList<T> options, Func<T, string> label, string? help = null) where T : notnull
        => new(title, () => get(model.CombineStars), v => model.UpdateCombineStars(s => set(s, v)), options, label, help);

    void StarsSection(StackPanel p)
    {
        var st = model.CombineStars;
        const double lw = 70;
        bool onBackground = model.ActiveLayer == ImageLayer.Background;
        if (!onBackground) p.Children.Add(Ui.Caption("Stars tab: with Combine on, the preview shows these stars over the Background tab.", 9));
        var stars = model.LayerSources(ImageLayer.Stars);
        if (stars.Count > 0 && onBackground)
        {
            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            var remove = Ui.IconButton("✕", model.RemoveStarsLayer, "Empty the Stars tab"); remove.Margin = new Thickness(0);
            DockPanel.SetDock(remove, Dock.Right); top.Children.Add(remove);
            var show = Ui.IconButton("➜", () => model.SwitchLayer(ImageLayer.Stars), "Show the Stars tab (Ctrl+2)"); show.IsEnabled = model.CanSwitchLayer;
            DockPanel.SetDock(show, Dock.Right); top.Children.Add(show);
            var name = new TextBlock
            {
                Text = "✦ Stars tab: " + string.Join(", ", ChannelKinds.All.Where(stars.ContainsKey).Select(k => k.ShortName())), FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = string.Join("\n", stars.Values.Select(v => v.FileName).Distinct().OrderBy(x => x)),
            };
            top.Children.Add(name);
            p.Children.Add(top);
        }
        if (onBackground)
        {
            var load = Ui.SmallButton("Load RGB stars…", () => { var paths = Panels.OpenImages(Window.GetWindow(this)); if (paths.Count > 0) model.LoadStarsLayer(paths[0]); },
                "Puts an already processed stars image on the Stars tab (as R, G, B; a grey image as L), replacing what is there, and switches the combination on. The tab shown does not change.");
            load.HorizontalAlignment = HorizontalAlignment.Left; load.Margin = new Thickness(0, 0, 0, 2);
            p.Children.Add(load);
        }
        var enable = CS("Combine BG and Stars", x => x.Enabled, (x, v) => x.Enabled = v);
        enable.IsEnabled = model.HasStars && model.LayerSources(ImageLayer.Background).Count > 0;
        p.Children.Add(enable);
        if (!model.HasStars) p.Children.Add(Ui.Caption("Fill the Stars tab first: drop star channels on it, use Remove Stars…, or Load RGB stars…."));
        if (model.StarsCompositeBusy || model.BackgroundCompositeBusy)
            p.Children.Add(Ui.Caption(onBackground ? "Rendering the Stars tab…" : "Rendering the Background tab…", 9));
        if (st.Enabled && model.HasStars)
        {
            // One column, scrolled: the layer's look, then the blend.
            p.Children.Add(SS("Amount", x => x.Amount, (x, v) => x.Amount = v, 0, 1, 1, labelWidth: lw));
            p.Children.Add(SS("Brightness", x => x.Brightness, (x, v) => x.Brightness = v, -2, 2, 0, help: "Gain of the star layer in stops", note: "EV", labelWidth: lw));
            p.Children.Add(SS("Contrast", x => x.Contrast, (x, v) => x.Contrast = v, -1, 1, 0, help: "Spreads the star layer around the mid-tone: positive darkens faint halos and keeps the cores, negative softens", labelWidth: lw));
            p.Children.Add(SS("Star sat.", x => x.Saturation, (x, v) => x.Saturation = v, 0, 3, 1.3f, labelWidth: lw));
            p.Children.Add(CS("Brightest stars to white", x => x.Normalize, (x, v) => x.Normalize = v, "Scale the layer so its brightest stars reach white — the brightest values of the result (whole-image peak, so preview and export agree). With a lowered Amount the cores still stay white: the amount then fades the faint stars instead of greying everything"));
            p.Children.Add(Ui.Separator());
            p.Children.Add(MS("Blend", x => x.Mode, (x, v) => x.Mode = v, StarsBlendModes.All, m => m.DisplayName(), "Screen: adds without clipping, soft on bright nebulae (default).\nLighten: the brighter of the two.\nAdd: sum, clipped."));
            p.Children.Add(MS("Blend at", x => x.Stage, (x, v) => x.Stage = v, StarsBlendStages.All, m => m.DisplayName(), "After denoise & detail: the stars are not denoised, sharpened or haloed (the starless workflow), and a star slider re-blends only.\nBefore: the stars go through denoise and detail with the nebula."));
            p.Children.Add(CS("Mask by luminance", x => x.UseMask, (x, v) => x.UseMask = v, "Only the parts of the Stars tab brighter than the threshold are blended — for a full RGB frame on the Stars tab. Off for stars-only images."));
            if (st.UseMask)
            {
                p.Children.Add(SS("Threshold", x => x.Threshold, (x, v) => x.Threshold = v, 0, 1, 0.15f, labelWidth: lw));
                p.Children.Add(SS("Softness", x => x.Softness, (x, v) => x.Softness = v, 0.01f, 1, 0.25f, labelWidth: lw));
            }
        }
    }
}

static class DictionaryExtensions
{
    public static bool ContainsKind(this IReadOnlyDictionary<ChannelKind, ChannelSource> d, ChannelKind k) => d.ContainsKey(k);
}
