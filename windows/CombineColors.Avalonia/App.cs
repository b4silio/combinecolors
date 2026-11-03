using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using CombineColors.Model;
using CombineColors.Views;

namespace CombineColors;

public sealed class App : Application
{
    public ProjectModel Model { get; private set; } = null!;
    public Preferences Prefs { get; private set; } = null!;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Dark;
        Resources["PreviewBackground"] = AppTheme.PreviewBackground;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) { base.OnFrameworkInitializationCompleted(); return; }
        // `--prefs <file>` keeps a test instance's recent sets (with their saved presets and dust spots) apart from the user's.
        string? prefsPath = null, screenshot = null, openEditor = null, editorAction = null;
        double screenshotDelay = 12;
        var files = new List<string>();
        var args = desktop.Args ?? [];
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--prefs" && i + 1 < args.Length) { prefsPath = args[++i]; continue; }
            // `--screenshot <png>` renders the window to a file after a delay (default 12 s) and quits (UI checks without a display).
            if (args[i] == "--screenshot" && i + 1 < args.Length) { screenshot = args[++i]; continue; }
            if (args[i] == "--screenshot-delay" && i + 1 < args.Length) { double.TryParse(args[++i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out screenshotDelay); continue; }
            // `--open-editor gradient:Ha[:background]` (or `dust:Ha`) opens that editor once the channel is loaded (screenshots).
            if (args[i] == "--open-editor" && i + 1 < args.Length) { openEditor = args[++i]; continue; }
            if (args[i] == "--editor-action" && i + 1 < args.Length) { editorAction = args[++i]; continue; }
            if (args[i] == "--scale" && i + 1 < args.Length) { i++; continue; }   // consumed by LinuxScaling before the platform starts
            if (!args[i].StartsWith('-')) files.Add(args[i]);
        }
        Prefs = new Preferences(prefsPath);
        Model = new ProjectModel(new AvaloniaDispatcher(Dispatcher.UIThread), Prefs) { LaunchEditor = openEditor, LaunchEditorAction = editorAction };
        var window = new MainWindow(Model);
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.Exit += (_, _) => { Model.SaveRecentState(); Prefs.Flush(); };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            System.Diagnostics.Trace.WriteLine(e.Exception);
            Model.ErrorMessage = e.Exception.Message;
        };
        window.Opened += (_, _) =>
        {
            var existing = files.Where(File.Exists).ToList();
            if (existing.Count > 0) window.OpenPaths(existing);
            if (screenshot != null)
                DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        double scale = window.RenderScaling;
                        var root = (Control)window.Content!;
                        var size = new PixelSize((int)(root.Bounds.Width * scale), (int)(root.Bounds.Height * scale));
                        using var rtb = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
                        rtb.Render(root);
                        rtb.Save(screenshot);
                    }
                    catch (Exception e) { Console.Error.WriteLine($"screenshot failed: {e}"); }
                    finally { desktop.Shutdown(); }
                }, TimeSpan.FromSeconds(screenshotDelay));
        };
        base.OnFrameworkInitializationCompleted();
    }
}
