using System.Globalization;
using System.Runtime.InteropServices;

namespace CombineColors;

/// <summary>
/// Picks a UI scale for X11 desktops that do not announce one. Avalonia scales from
/// <c>Xft.dpi</c> or the AVALONIA_* / QT_* scale variables; without any of them a 4K monitor
/// gets scale 1 and the whole interface is illegible. Before the platform starts, this reads the
/// screen's pixel and physical size from the X server and sets AVALONIA_GLOBAL_SCALE_FACTOR
/// when the display is clearly high-density. <c>--scale 1.5</c> or COMBINECOLORS_SCALE=1.5 force a value.
/// </summary>
static class LinuxScaling
{
    public const double Minimum = 1, Maximum = 3;

    public static void Apply(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return;
        double? forced = Forced(args);
        if (forced is double f) { Set(f); return; }
        foreach (var v in new[] { "AVALONIA_GLOBAL_SCALE_FACTOR", "AVALONIA_SCREEN_SCALE_FACTORS", "AVALONIA_USE_PHYSICAL_DPI", "QT_SCALE_FACTOR", "QT_SCREEN_SCALE_FACTORS", "QT_AUTO_SCREEN_SCALE_FACTOR" })
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))) return;
        if (Parse(Environment.GetEnvironmentVariable("GDK_SCALE")) is double gdk && gdk > 1) { Set(gdk); return; }
        var screen = X11Screen.Read();
        if (screen == null) return;
        if (screen.Value.xftDpi is double xft && xft > 0) return;   // Avalonia honours Xft.dpi itself
        var (w, h, wmm, hmm, _) = screen.Value;
        var s = Choose(w, h, wmm, hmm);
        if (s > 1)
        {
            Console.Error.WriteLine($"CombineColors: UI scale {s.ToString("0.##", CultureInfo.InvariantCulture)} for a {w}\u00d7{h} px screen of {wmm}\u00d7{hmm} mm (override with --scale or COMBINECOLORS_SCALE)");
            Set(s);
        }
    }

    /// <summary>The scale for a screen: from the physical density when the server reports a plausible size, else from the pixel count.</summary>
    public static double Choose(int widthPx, int heightPx, int widthMm, int heightMm)
    {
        double scale = 1;
        if (widthMm > 0 && heightMm > 0 && widthPx > 0)
        {
            double dpi = widthPx / (widthMm / 25.4);
            // Projectors and TVs report nonsense sizes (a 20 dpi wall, a 500 dpi watch), and XWayland
            // under GNOME announces whatever size makes 96 dpi (a 4K screen "one metre wide"): trust the
            // physical size only in the normal range and under 90 cm, else go by the pixel count.
            if (dpi >= 50 && dpi <= 400 && widthMm < 900) scale = Math.Round(dpi / 96 * 4) / 4;
            else scale = FromPixels(widthPx, heightPx);
        }
        else scale = FromPixels(widthPx, heightPx);
        if (scale < 1.5) scale = 1;   // 27" QHD (109 dpi) and the like are meant for 1×; 4K from 32" down starts at 1.5
        return Math.Clamp(scale, Minimum, Maximum);
    }

    static double FromPixels(int w, int h) => Math.Min(w, h) >= 2000 ? 2 : 1;   // 4K / 5K without a usable physical size

    static double? Forced(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "--scale") return Parse(args[i + 1]);
        return Parse(Environment.GetEnvironmentVariable("COMBINECOLORS_SCALE"));
    }

    static double? Parse(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 ? Math.Clamp(v, Minimum, Maximum) : null;

    static void Set(double scale) =>
        Environment.SetEnvironmentVariable("AVALONIA_GLOBAL_SCALE_FACTOR", scale.ToString("0.##", CultureInfo.InvariantCulture));

    /// <summary>The default screen's geometry and the Xft.dpi resource, straight from libX11 (null without a display).</summary>
    static class X11Screen
    {
        [DllImport("libX11.so.6")] static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport("libX11.so.6")] static extern int XCloseDisplay(IntPtr display);
        [DllImport("libX11.so.6")] static extern int XDefaultScreen(IntPtr display);
        [DllImport("libX11.so.6")] static extern int XDisplayWidth(IntPtr display, int screen);
        [DllImport("libX11.so.6")] static extern int XDisplayHeight(IntPtr display, int screen);
        [DllImport("libX11.so.6")] static extern int XDisplayWidthMM(IntPtr display, int screen);
        [DllImport("libX11.so.6")] static extern int XDisplayHeightMM(IntPtr display, int screen);
        [DllImport("libX11.so.6")] static extern IntPtr XResourceManagerString(IntPtr display);

        public static (int widthPx, int heightPx, int widthMm, int heightMm, double? xftDpi)? Read()
        {
            try
            {
                var d = XOpenDisplay(IntPtr.Zero);
                if (d == IntPtr.Zero) return null;
                try
                {
                    int s = XDefaultScreen(d);
                    double? xft = null;
                    var res = XResourceManagerString(d);
                    if (res != IntPtr.Zero)
                        foreach (var line in (Marshal.PtrToStringAnsi(res) ?? "").Split('\n'))
                        {
                            var t = line.Trim();
                            if (!t.StartsWith("Xft.dpi:", StringComparison.Ordinal)) continue;
                            if (double.TryParse(t[8..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) xft = v;
                        }
                    return (XDisplayWidth(d, s), XDisplayHeight(d, s), XDisplayWidthMM(d, s), XDisplayHeightMM(d, s), xft);
                }
                finally { XCloseDisplay(d); }
            }
            catch (Exception) { return null; }   // no libX11 (Wayland-only box): leave the platform's own choice
        }
    }
}
