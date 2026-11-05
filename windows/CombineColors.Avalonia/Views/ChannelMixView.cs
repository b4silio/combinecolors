using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Orientation = Avalonia.Layout.Orientation;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Views;

/// <summary>
/// Channel mix (right pane, between the palettes and the presets): one compact row per loaded channel —
/// on / off, weight slider tinted with the channel colour — with the finer adjustments (gain, pedestal,
/// per-channel stretch tweaks) folded under a chevron. It sits next to the palettes because weights and
/// palette together make the colour combination.
/// </summary>
public sealed class ChannelMixView : Border
{
    readonly ProjectModel model;
    readonly StackPanel panel = new() { Spacing = 0 };
    readonly HashSet<ChannelKind> expanded = new();
    string structure = "";

    public ChannelMixView(ProjectModel model)
    {
        this.model = model;
        Background = AppTheme.PanelBackground;
        Padding = new Thickness(8, 5, 8, 5);
        Child = panel;
        model.Changed += what =>
        {
            switch (what)
            {
                case Change.Settings: if (Signature() != structure) Rebuild(); else Refresh(); break;
                case Change.Sources: case Change.Layer: case Change.Context: Rebuild(); break;
            }
        };
        Rebuild();
    }

    List<ChannelKind> Kinds => ChannelKinds.All.Where(k => model.Sources.ContainsKey(k)).ToList();

    string Signature()
    {
        var s = model.Settings;
        var used = s.AllChannels(model.AvailableKinds);
        return string.Join("|", Kinds.Select(k => $"{k}:{s.Adjust(k).Enabled}:{s.Adjust(k).IsNeutral}:{AdvancedChanged(s.Adjust(k))}:{used.Contains(k)}"))
            + "|" + s.StretchStage + "|" + s.Channels.Values.All(a => a.IsNeutral) + "|" + string.Join(",", expanded.Order());
    }

    /// <summary>Anything but the weight and the checkbox changed (a dot on the closed chevron).</summary>
    static bool AdvancedChanged(ChannelAdjust a)
    {
        var b = a.Clone(); b.Weight = 1; b.Enabled = true;
        return !b.IsNeutral;
    }

    void Refresh()
    {
        foreach (var r in panel.GetLogicalDescendants().OfType<IRefreshable>()) r.Refresh();
    }

    static void Edit(CombineSettings s, ChannelKind k, Action<ChannelAdjust> edit) { var a = s.Adjust(k).Clone(); edit(a); s.SetAdjust(k, a); }

    SliderRow S(ChannelKind k, string title, Func<ChannelAdjust, float> get, Action<ChannelAdjust, float> set, float lo, float hi, float def, string format = "0.00", bool log = false, string? help = null, double labelWidth = 78)
        => new(title, () => get(model.Settings.Adjust(k)), v => model.UpdateSettings(s => Edit(s, k, a => set(a, v))), lo, hi, def, format, log, help, null, labelWidth);

    void Rebuild()
    {
        structure = Signature();
        panel.Children.Clear();
        var kinds = Kinds;
        IsVisible = kinds.Count > 0;
        bool pre = model.Settings.StretchStage == StretchStage.PreCombine;
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        var reset = Ui.IconButton("↺", () => model.UpdateSettings(s => s.Channels.Clear()), "Reset the channel weights and gains");
        reset.IsEnabled = !model.Settings.Channels.Values.All(a => a.IsNeutral); reset.Margin = new Thickness(0);
        DockPanel.SetDock(reset, Dock.Right); head.Children.Add(reset);
        head.Children.Add(Ui.Row(
            new TextBlock { Text = "Channel mix", FontWeight = FontWeight.Bold, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) },
            new TextBlock { Text = pre ? "weights after the stretch" : "linear weights, before the stretch", FontSize = 9, Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center }));
        panel.Children.Add(head);
        var used = model.Settings.AllChannels(model.AvailableKinds);
        foreach (var k in kinds) panel.Children.Add(Row(k, pre, used.Contains(k)));
    }

    Control Row(ChannelKind kind, bool pre, bool used)
    {
        var box = new StackPanel();
        var current = model.Settings.Adjust(kind);
        var row = new DockPanel();
        var enabled = new CheckRow("", () => model.Settings.Adjust(kind).Enabled, v => model.UpdateSettings(s => Edit(s, kind, a => a.Enabled = v)), $"Use {kind.DisplayName()} in the combination");
        enabled.Margin = new Thickness(0, 0, 2, 0); enabled.VerticalAlignment = VerticalAlignment.Center; enabled.MinWidth = 20;
        DockPanel.SetDock(enabled, Dock.Left); row.Children.Add(enabled);
        var dot = Ui.Dot(Ui.Tint(kind)); DockPanel.SetDock(dot, Dock.Left); row.Children.Add(dot);
        bool open = expanded.Contains(kind);
        var chevron = Ui.IconButton(open ? "▴" : (AdvancedChanged(current) ? "▾•" : "▾"), () => { if (!expanded.Remove(kind)) expanded.Add(kind); Rebuild(); },
            open ? "Hide the finer adjustments" : "Gain, pedestal" + (pre ? ", stretch boost, black point and gamma" : "") + (AdvancedChanged(current) ? " (changed)" : ""), 10);
        chevron.Margin = new Thickness(2, 0, 0, 0); chevron.MinWidth = 22;
        DockPanel.SetDock(chevron, Dock.Right); row.Children.Add(chevron);
        var weight = S(kind, kind.ShortName(), a => a.Weight, (a, v) => a.Weight = v, 0, 2, 1,
            help: (pre ? $"Weight of {kind.DisplayName()}, applied after the stretch." : $"Linear multiplier of {kind.DisplayName()} before the combined stretch.")
                + (used ? "" : " Not used by the current palette.") + " Double-click to reset.", labelWidth: 38).Tinted(Ui.Tint(kind));
        weight.Opacity = used && current.Enabled ? 1 : 0.5;
        row.Children.Add(weight);
        box.Children.Add(row);
        if (open)
        {
            var more = new StackPanel { Margin = new Thickness(34, 0, 0, 3) };
            more.Children.Add(S(kind, "Linear gain", a => a.Gain, (a, v) => a.Gain = v, 0.1f, 10, 1, log: true, help: "Multiplier on the normalised linear data (before the stretch)."));
            more.Children.Add(S(kind, "Pedestal σ", a => a.OffsetSigma, (a, v) => a.OffsetSigma = v, -20, 20, 0, "0.0", help: "Adds a constant in units of the reference background sigma."));
            if (pre)
            {
                more.Children.Add(S(kind, "Target boost", a => a.StretchBoost, (a, v) => a.StretchBoost = v, -0.15f, 0.4f, 0, help: "Raises/lowers this channel's stretch target background."));
                more.Children.Add(S(kind, "Black point", a => a.BlackPoint, (a, v) => a.BlackPoint = v, 0, 0.5f, 0, "0.000", help: "Subtracted after the stretch."));
                more.Children.Add(S(kind, "Gamma", a => a.Gamma, (a, v) => a.Gamma = v, 0.3f, 3, 1, log: true));
            }
            if (!current.IsNeutral)
            {
                var r = Ui.SmallButton($"Reset {kind.ShortName()}", () => model.UpdateSettings(s => s.SetAdjust(kind, new ChannelAdjust())));
                r.HorizontalAlignment = HorizontalAlignment.Right;
                more.Children.Add(r);
            }
            box.Children.Add(more);
        }
        return box;
    }
}
