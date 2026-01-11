using System;
using Avalonia;

namespace Mostlylucid.ImageSummarizer.Desktop;

internal class Program
{
    public static string[] Args { get; private set; } = Array.Empty<string>();

    [STAThread]
    public static void Main(string[] args)
    {
        Args = args;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}