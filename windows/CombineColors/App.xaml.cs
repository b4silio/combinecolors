using System.IO;
using System.Windows;
using System.Windows.Threading;
using CombineColors.Model;

namespace CombineColors;

public partial class App : Application
{
    public ProjectModel Model { get; private set; } = null!;
    public Preferences Prefs { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // `--prefs <file>` keeps a test instance's recent sets (with their saved presets and dust spots) apart from the user's.
        string? prefsPath = null, screenshot = null;
        var files = new List<string>();
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--prefs" && i + 1 < e.Args.Length) { prefsPath = e.Args[++i]; continue; }
            // `--screenshot <png>` renders the window to a file 12 s after launch and quits (UI checks without a display).
            if (e.Args[i] == "--screenshot" && i + 1 < e.Args.Length) { screenshot = e.Args[++i]; continue; }
            if (!e.Args[i].StartsWith("-")) files.Add(e.Args[i]);
        }
        Prefs = new Preferences(prefsPath);
        Model = new ProjectModel(new WpfDispatcher(Dispatcher), Prefs);
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.Message, "CombineColors", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        var window = new MainWindow(Model);
        MainWindow = window;
        window.Show();
        var existing = files.Where(File.Exists).ToList();
        if (existing.Count > 0) window.OpenPaths(existing);
        if (screenshot != null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    var root = (FrameworkElement)window.Content;
                    var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    rtb.Render(root);
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                    using var fs = File.Create(screenshot);
                    enc.Save(fs);
                }
                finally { Shutdown(); }
            };
            timer.Start();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Model?.SaveRecentState();
        Prefs?.Flush();
        base.OnExit(e);
    }
}
