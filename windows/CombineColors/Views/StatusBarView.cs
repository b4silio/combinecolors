using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Orientation = System.Windows.Controls.Orientation;
using Stretch = System.Windows.Media.Stretch;
using System.Windows.Media;
using AstroCore;
using CombineColors.Model;

namespace CombineColors.Views;

/// <summary>Single fixed-height status line: loading / status text, render error, pixel read-out, render timing, zoom.</summary>
public sealed class StatusBarView : Grid
{
    readonly ProjectModel model;
    readonly ProgressBar spinner = new() { IsIndeterminate = true, Width = 60, Height = 8, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly ProgressBar exportBar = new() { Width = 120, Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    readonly TextBlock error = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11, Foreground = Brushes.DarkOrange, Margin = new Thickness(12, 0, 0, 0) };
    readonly TextBlock readout = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 10, FontFamily = new FontFamily("Consolas"), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Right };
    readonly TextBlock timing = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = Brushes.Gray, Width = 90, TextAlignment = TextAlignment.Right };
    readonly TextBlock zoom = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 11, Foreground = Brushes.Gray, Width = 48, TextAlignment = TextAlignment.Right };
    public double ZoomPercent { set => zoom.Text = value.ToString("0", CultureInfo.InvariantCulture) + "%"; }

    public StatusBarView(ProjectModel model)
    {
        this.model = model;
        Height = 24;
        Background = (Brush)Application.Current.Resources["PanelBackground"];
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0) };
        left.Children.Add(spinner); left.Children.Add(exportBar); left.Children.Add(status);
        SetColumn(left, 0); Children.Add(left);
        SetColumn(error, 1); Children.Add(error);
        SetColumn(readout, 3); Children.Add(readout);
        SetColumn(timing, 4); Children.Add(timing);
        SetColumn(zoom, 5); zoom.Margin = new Thickness(0, 0, 10, 0); Children.Add(zoom);
        model.Changed += what => { if (what is Change.Status or Change.Frame or Change.Hover or Change.Sources) Refresh(); };
        Refresh();
    }

    void Refresh()
    {
        spinner.Visibility = model.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        exportBar.Visibility = model.ExportProgress is double ? Visibility.Visible : Visibility.Collapsed;
        if (model.ExportProgress is double ep) exportBar.Value = ep;
        status.Text = model.IsLoading ? model.LoadingMessage : (model.ExportProgress is double ? "Exporting…" : model.StatusText);
        error.Text = model.RenderError ?? "";
        error.Visibility = model.RenderError == null ? Visibility.Collapsed : Visibility.Visible;
        readout.Text = Readout();
        timing.Text = model.Frame != null ? $"L{model.LastRenderLevel} · {(int)model.LastRenderMillis} ms" + (model.IsRendering ? " ⟳" : "") : "";
    }

    /// <summary>Pixel read-out as one string (fixed format so the width barely changes).</summary>
    string Readout()
    {
        if (model.Hover is not HoverInfo h) return "";
        var parts = new List<string> { $"({h.X,5}, {h.Y,5})" };
        parts.AddRange(h.Values.Select(v => $"{v.kind.ShortName()} {v.value.ToString("0.00000", CultureInfo.InvariantCulture)}"));
        if (h.Rgb is var (r, g, b) && h.Rgb != null) parts.Add($"RGB {r.ToString("0.000", CultureInfo.InvariantCulture)} {g.ToString("0.000", CultureInfo.InvariantCulture)} {b.ToString("0.000", CultureInfo.InvariantCulture)}");
        return string.Join("  ", parts);
    }
}
