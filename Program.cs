using Avalonia;
using System;

namespace FgoAuto;

sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        OpenCvSharp.Cv2.SetNumThreads(Math.Min(2, Environment.ProcessorCount));
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
