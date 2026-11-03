using Avalonia;

namespace CombineColors;

static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        LinuxScaling.Apply(args);
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
