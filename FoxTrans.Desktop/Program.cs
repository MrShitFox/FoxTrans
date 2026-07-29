using Avalonia;
using FoxTrans.Desktop.Diagnostics;

namespace FoxTrans.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupTrace.Mark("main");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseWin32()
            .UseSkia()
            .UseHarfBuzz();
}
