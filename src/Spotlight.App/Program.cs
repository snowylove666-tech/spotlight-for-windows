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
        // A resident app must not vanish without a trace: errors nobody catches are written down
        // (an error on a background task is then ignored; one on any other thread still ends the process).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Spotlight.Core.ErrorLog.Write("unhandled (process ends)", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));
        TaskScheduler.UnobservedTaskException += (_, e) => { Spotlight.Core.ErrorLog.Write("background task", e.Exception); e.SetObserved(); };
        // If it crashes anyway, Windows starts it again (in the background) after a moment.
        Platform.RegisterForRestart("--background");
        Spotlight.Core.ErrorLog.Note($"started {typeof(Program).Assembly.GetName().Version} {string.Join(' ', args)}");
        int code = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        Spotlight.Core.ErrorLog.Note($"exited ({code})");
        return code;
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
