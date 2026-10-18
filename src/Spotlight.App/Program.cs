using Avalonia;
using Avalonia.Media;

namespace Spotlight.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Second launch (e.g. double-clicking the exe again): wake the running copy and exit.
        if (!SingleInstance.TryAcquire())
        {
            SingleInstance.SignalExisting();
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        // Software rendering: a small, mostly hidden window doesn't need a GPU context, and skipping
        // ANGLE/Direct3D saves tens of MB of resident memory.
        .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.Software } })
        .With(new FontManagerOptions
        {
            DefaultFamilyName = OperatingSystem.IsWindows() ? "Microsoft YaHei UI" : null,
            FontFallbacks = new[] { new FontFallback { FontFamily = new FontFamily("Microsoft YaHei UI") } },
        });
}
