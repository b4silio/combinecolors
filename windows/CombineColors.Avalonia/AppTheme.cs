using Avalonia.Media;

namespace CombineColors;

/// <summary>The dark palette of the app (the WPF build keeps these in App.xaml).</summary>
public static class AppTheme
{
    public static readonly IBrush PreviewBackground = Brush("#FF1C1C1C");
    public static readonly IBrush PanelBackground = Brush("#FF202020");
    public static readonly IBrush PanelBorder = Brush("#FF3A3A3A");
    public static readonly IBrush SubtleText = Brush("#FF9A9A9A");
    public static readonly IBrush TextBrush = Brush("#FFE6E6E6");
    public static readonly IBrush OverlayBackground = Brush("#E6303030");
    public static readonly IBrush AccentBrush = Brush("#FF4A9EF5");
    /// <summary>Monospace family with per-platform fallbacks (Menlo on macOS, Consolas on Windows, DejaVu / Liberation on Linux).</summary>
    public static readonly FontFamily Mono = new("Menlo, Consolas, DejaVu Sans Mono, Liberation Mono, Courier New, monospace");

    static IBrush Brush(string hex) { var b = new SolidColorBrush(Color.Parse(hex)); return b.ToImmutable(); }
}
