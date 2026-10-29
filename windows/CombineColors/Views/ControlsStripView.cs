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
    Point? dragStart; double dragOffset;

    enum ColorBandComponent { Hue, Saturation, Lightness }

    public ControlsStripView(ProjectModel model)
    {
        this.model = model;
        Height = StripHeight;
        Background = (Brush)Application.Current.Resources["PanelBackground"];
        collapsed = model.Preferences.GetString("collapsedBlocks")?.Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet() ?? new HashSet<string>();
        bandComponent = Enum.TryParse<ColorBandComponent>(model.Preferences.GetString("colorBandComponent") ?? "", true, out var bc) ? bc : ColorBandComponent.Saturation;
        scroller = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = blocks, Background = Brushes.Transparent };
        scroller.PreviewMouseWheel += (_, e) => { if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 || e.OriginalSource is not Slider) { } };
        scroller.PreviewMouseLeftButtonDown += OnDragStart;
        scroller.PreviewMouseMove += OnDragMove;
        scroller.PreviewMouseLeftButtonUp += (_, _) => { dragStart = null; if (scroller.IsMouseCaptured) scroller.ReleaseMouseCapture(); };
        Child = scroller;
        Add("channels", "Channels", 250); Add("stretch", "Stretch", 262); Add("normalisation", "Normalisation", 290); Add("luminance", "Luminance", 250);
        Add("bands", "Colour bands", 226); Add("colour", "Colour", 250); Add("denoise", "Denoise", 330); Add("detail", "Detail", 266); Add("stars", "RGB stars", 262);
        model.Changed += OnChanged;
        Rebuild();
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
            case Change.Sources: case Change.Context: Rebuild(); break;
        }
    }

    bool HasRgb => model.Sources.ContainsKey(ChannelKind.Red) && model.Sources.ContainsKey(ChannelKind.Green) && model.Sources.ContainsKind(ChannelKind.Blue);

    string Signature()
    {
        var s = model.Settings;
        var avail = model.AvailableKinds;
        var parts = new List<string>
        {
            string.Join(",", avail.SortedByRaw().Select(k => k.RawValue())), s.Stretch.Method.ToString(), s.StretchStage.ToString(), s.Stretch.Linked.ToString(),
            s.Normalization.Method.ToString(), s.Luminance.Enabled.ToString(), s.Luminance.Preset.ToString(), s.Luminance.ResolvedExpression(avail) ?? "-",
            s.Denoise.Method.ToString(), s.Stars.Enabled.ToString(), s.Stars.UseMask.ToString(), s.Mode.ToString(), s.PixelMath.HasLuminance.ToString(),
            model.Context?.Reference?.ToString() ?? "-", s.ColorBands.IsNeutral.ToString(), s.Detail.Equals(new DetailSettings()).ToString(), s.Denoise.Equals(new DenoiseSettings()).ToString(),
            bandComponent.ToString(),
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
        Build("bands", ColorBandsSection); Build("colour", ColourSection); Build("denoise", DenoiseSection); Build("detail", DetailSection); Build("stars", StarsSection);
        // Stays collapsed while no R, G and B channels are loaded (nothing to blend).
        blockById["stars"].Expanded = HasRgb && !collapsed.Contains("stars");
    }

    void Build(string id, Action<StackPanel> builder)
    {
        var panel = blockById[id].Content;
        panel.Children.Clear();
        builder(panel);
    }

    // Helpers binding into the settings.

    SliderRow S(string title, Func<CombineSettings, float> get, Action<CombineSettings, float> set, float lo, float hi, float def, string format = "0.00", bool log = false, string? help = null, string? note = null, double labelWidth = 70)
        => new(title, () => get(model.Settings), v => model.UpdateSettings(s => set(s, v)), lo, hi, def, format, log, help, note, labelWidth);

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
        var s = model.Settings;
        p.Children.Add(M("Method", x => x.Stretch.Method, (x, v) => x.Stretch.Method = v, StretchMethods.All, m => m.DisplayName()));
        p.Children.Add(M("Order", x => x.StretchStage, (x, v) => x.StretchStage = v, StretchStages.All, m => m.ShortName(), "Stretch first: each channel is stretched, then combined. Combine first: the linear channels are combined, then the colour image is stretched."));
        p.Children.Add(C("Linked channels", x => x.Stretch.Linked, (x, v) => x.Stretch.Linked = v, "Same stretch for all channels"));
        if (s.Stretch.Method != StretchMethod.None)
        {
            p.Children.Add(S("Target bg", x => x.Stretch.TargetBackground, (x, v) => x.Stretch.TargetBackground = v, 0.02f, 0.6f, 0.2f, help: "Where the median background lands after the stretch (PixInsight STF default 0.25)."));
            p.Children.Add(S("Shadow clip", x => x.Stretch.ShadowsClipping, (x, v) => x.Stretch.ShadowsClipping = v, -6, 0, -2.8f, "0.0", help: "Black point in MADN units below the median (STF default −2.8)."));
            p.Children.Add(S("Highlights", x => x.Stretch.HighlightsPercentile, (x, v) => x.Stretch.HighlightsPercentile = v, 0.9f, 1, 1, "0.0000", help: "Percentile mapped to white (1 = no highlight clipping)."));
        }
        switch (s.Stretch.Method)
        {
            case StretchMethod.Mtf:
                p.Children.Add(S("Shadows", x => x.Stretch.MtfShadows, (x, v) => x.Stretch.MtfShadows = v, 0, 0.5f, 0, "0.000"));
                p.Children.Add(S("Midtones", x => x.Stretch.MtfMidtones, (x, v) => x.Stretch.MtfMidtones = v, 0.001f, 0.999f, 0.5f, "0.000", log: true));
                p.Children.Add(S("Highlights", x => x.Stretch.MtfHighlights, (x, v) => x.Stretch.MtfHighlights = v, 0.5f, 1, 1, "0.000"));
                break;
            case StretchMethod.Arcsinh:
                p.Children.Add(S("Stretch", x => x.Stretch.ArcsinhStretch, (x, v) => x.Stretch.ArcsinhStretch = v, 1, 5000, 200, "0", log: true));
                break;
            case StretchMethod.Ghs:
                p.Children.Add(S("ln(D+1)", x => x.Stretch.GhsLogD, (x, v) => x.Stretch.GhsLogD = v, 0, 12, 5, help: "Stretch factor (logarithmic)."));
                p.Children.Add(S("b", x => x.Stretch.GhsB, (x, v) => x.Stretch.GhsB = v, 0, 15, 2, help: "Local intensity: how concentrated the stretch is around the symmetry point."));
                p.Children.Add(S("SP", x => x.Stretch.GhsSP, (x, v) => x.Stretch.GhsSP = v, 0, 1, 0, "0.000", help: "Symmetry point."));
                p.Children.Add(S("LP", x => x.Stretch.GhsLP, (x, v) => x.Stretch.GhsLP = v, 0, 1, 0, "0.000", help: "Shadow protection point."));
                p.Children.Add(S("HP", x => x.Stretch.GhsHP, (x, v) => x.Stretch.GhsHP = v, 0, 1, 1, "0.000", help: "Highlight protection point."));
                break;
            case StretchMethod.Log:
                p.Children.Add(S("k", x => x.Stretch.LogK, (x, v) => x.Stretch.LogK = v, 1, 100000, 2000, "0", log: true));
                break;
        }
        p.Children.Add(S("Gamma", x => x.Stretch.Gamma, (x, v) => x.Stretch.Gamma = v, 0.3f, 3, 1, log: true));
        if (!s.Stretch.Linked && s.StretchStage == StretchStage.PreCombine && s.Stretch.Method == StretchMethod.AutoSTF)
            p.Children.Add(Ui.Caption("Unlinked auto-stretch equalises every channel: normalisation and gain have no visible effect."));
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
        var reset = Ui.IconButton("↺", () => model.UpdateSettings(x => x.ColorBands = new ColorBandsSettings()), "Reset all bands");
        reset.IsEnabled = !model.Settings.ColorBands.IsNeutral; reset.HorizontalAlignment = HorizontalAlignment.Right; reset.Margin = new Thickness(0);
        head.Children.Add(reset);
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
        p.Children.Add(S("Vibrance", x => x.Post.Vibrance, (x, v) => x.Post.Vibrance = v, -1, 2, 0));
        p.Children.Add(S("SCNR green", x => x.Post.ScnrGreen, (x, v) => x.Post.ScnrGreen = v, 0, 1, 0, help: "Average-neutral green removal."));
        p.Children.Add(S("Hue shift", x => x.Post.HueShift, (x, v) => x.Post.HueShift = v, -180, 180, 0, "0"));
        p.Children.Add(S("Black point", x => x.Post.BlackPoint, (x, v) => x.Post.BlackPoint = v, 0, 0.5f, 0, "0.000"));
        p.Children.Add(S("White point", x => x.Post.WhitePoint, (x, v) => x.Post.WhitePoint = v, 0.5f, 1, 1, "0.000"));
        p.Children.Add(S("Gamma", x => x.Post.Gamma, (x, v) => x.Post.Gamma = v, 0.3f, 3, 1, log: true));
        p.Children.Add(Ui.Row(
            Ui.SmallButton("Reset colour", () => model.UpdateSettings(x => x.Post = new PostSettings())),
            Ui.SmallButton("Classic SHO look", () => model.UpdateSettings(x => { x.Post.ScnrGreen = 0.8f; x.Post.Saturation = 1.3f; x.Post.HueShift = -10; }), "SCNR 0.8, saturation 1.3, hue −10°")));
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
        var foot = new DockPanel();
        var reset = Ui.IconButton("↺", () => model.UpdateSettings(x => x.Denoise = new DenoiseSettings()), "Reset the denoise settings");
        reset.IsEnabled = !d.Equals(new DenoiseSettings()); reset.VerticalAlignment = VerticalAlignment.Bottom; reset.Margin = new Thickness(4, 0, 0, 0);
        DockPanel.SetDock(reset, Dock.Right);
        foot.Children.Add(reset);
        foot.Children.Add(Ui.Caption(d.Method.Summary(), 9));
        p.Children.Add(foot);
    }

    // Detail

    void DetailSection(StackPanel p)
    {
        const double lw = 84;
        p.Children.Add(S("Texture", x => x.Detail.Texture, (x, v) => x.Detail.Texture = v, -1, 1, 0, help: "Fine-detail local contrast (radius ≈ 0.6 % of the width); stars are excluded from the blur to avoid dark halos.", note: "0.6%", labelWidth: lw));
        p.Children.Add(S("Clarity", x => x.Detail.Clarity, (x, v) => x.Detail.Clarity = v, -0.5f, 2, 0, help: "Mid-tone local contrast at a broader scale (radius ≈ 6 % of the width).", note: "6%", labelWidth: lw));
        p.Children.Add(S("Dehaze", x => x.Detail.Dehaze, (x, v) => x.Detail.Dehaze = v, -1, 1, 0, help: "Subtracts the large-scale haze component (radius ≈ 25 % of the width); negative adds haze.", note: "25%", labelWidth: lw));
        p.Children.Add(new Separator { Margin = new Thickness(0, 3, 0, 3) });
        p.Children.Add(S("Sharpen", x => x.Detail.Sharpen, (x, v) => x.Detail.Sharpen = v, 0, 2, 0, help: "Unsharp mask on the luminance (stars excluded from the blur). The radius is in full-resolution pixels, so judge it at 1:1 with HQ on; a fitted preview shows little of it.", labelWidth: lw));
        p.Children.Add(S("Radius", x => x.Detail.SharpenRadius, (x, v) => x.Detail.SharpenRadius = v, 0.5f, 5, 1.5f, "0.0", help: "Sharpening radius in full-resolution pixels.", note: "px", labelWidth: lw));
        p.Children.Add(S("Masking", x => x.Detail.SharpenMasking, (x, v) => x.Detail.SharpenMasking = v, 0, 1, 0, help: "Protects flat, noisy areas: only detail above this fraction of the noise level is sharpened (like Camera Raw's Masking).", labelWidth: lw));
        var reset = Ui.SmallButton("Reset", () => model.UpdateSettings(x => x.Detail = new DetailSettings()));
        reset.HorizontalAlignment = HorizontalAlignment.Right; reset.IsEnabled = !model.Settings.Detail.Equals(new DetailSettings());
        p.Children.Add(reset);
    }

    // Stars

    void StarsSection(StackPanel p)
    {
        var st = model.Settings.Stars;
        var enable = C("Blend RGB stars into the result", x => x.Stars.Enabled, (x, v) => x.Stars.Enabled = v);
        enable.IsEnabled = HasRgb;
        p.Children.Add(enable);
        if (!HasRgb) p.Children.Add(Ui.Caption("Load R, G and B channels (or a colour stars image) to enable."));
        if (st.Enabled)
        {
            p.Children.Add(S("Amount", x => x.Stars.Amount, (x, v) => x.Stars.Amount = v, 0, 1, 1));
            p.Children.Add(M("Star data", x => x.Stars.Stretch, (x, v) => x.Stars.Stretch = v, StarsStretchModes.All, m => m.DisplayName()));
            p.Children.Add(C("Mask stars by luminance", x => x.Stars.UseMask, (x, v) => x.Stars.UseMask = v, "Off for star-only images from StarNet/StarXTerminator."));
            if (st.UseMask)
            {
                p.Children.Add(S("Threshold", x => x.Stars.Threshold, (x, v) => x.Stars.Threshold = v, 0, 1, 0.15f));
                p.Children.Add(S("Softness", x => x.Stars.Softness, (x, v) => x.Stars.Softness = v, 0.01f, 1, 0.25f));
            }
            p.Children.Add(S("Star sat.", x => x.Stars.Saturation, (x, v) => x.Stars.Saturation = v, 0, 3, 1.3f));
            p.Children.Add(M("Blend", x => x.Stars.Mode, (x, v) => x.Stars.Mode = v, StarsBlendModes.All, m => m.DisplayName()));
        }
    }
}

static class DictionaryExtensions
{
    public static bool ContainsKind(this IReadOnlyDictionary<ChannelKind, ChannelSource> d, ChannelKind k) => d.ContainsKey(k);
}
