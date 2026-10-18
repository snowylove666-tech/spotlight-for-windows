using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using Spotlight.App;
using Spotlight.Core;
using Spotlight.Core.Indexing;
using Spotlight.Core.Search;

// usage: Screenshot <index.db> <query|--settings> <out.png> [light|dark]
var (db, query, output) = (args[0], args[1], args[2]);
bool dark = args.Length > 3 && args[3] == "dark";

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
var settings = new Settings();
if (query == "--settings")
{
    var sw = new SettingsWindow(settings) { Height = 1250 };
    sw.Show();
    for (int i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Thread.Sleep(50); }
    sw.CaptureRenderedFrame()!.Save(output);
    Console.WriteLine($"saved {output}");
    return;
}
using var store = new IndexStore(db, settings);
using var engine = new SearchEngine(db);
var window = new SearchWindow(engine, store, settings) { Background = dark ? Avalonia.Media.Brushes.Black : Avalonia.Media.Brushes.LightSteelBlue };
window.Show();
window.SetIndexStatus(new IndexStatus(IndexPhase.Idle, store.Counts().files, store.Counts().withContent, 0));
window.FindControl<TextBox>("Query")!.Text = query;

void Pump(double seconds)
{
    var until = DateTime.UtcNow.AddSeconds(seconds);
    while (DateTime.UtcNow < until)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Thread.Sleep(50);
    }
}
Pump(5);
// Optional 5th argument: the result whose name contains this text is selected (to show its preview).
if (args.Length > 4)
{
    var list = window.FindControl<ListBox>("Results")!;
    list.SelectedItem = list.Items.OfType<ResultItem>().FirstOrDefault(i => i.Name.Contains(args[4]));
    Pump(8);
}
var frame = window.CaptureRenderedFrame();
frame!.Save(output);
Console.WriteLine($"saved {output}");
