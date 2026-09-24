using System.Diagnostics;
using Avalonia;
using BootVideoManager.App.Services;

namespace BootVideoManager.App;

internal static class Program
{
    // Don't use Avalonia or any SynchronizationContext-reliant code before AppMain is called.
    [STAThread]
    public static int Main(string[] args)
    {
        int exitCode;
        using (var instance = SingleInstance.Acquire())
        {
            if (!instance.IsPrimary)
            {
                // Already running: show that window instead of opening a second one.
                instance.SignalPrimary();
                return 0;
            }

            instance.StartListening();
            exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        // The mutex is released at this point, so the new copy becomes the primary instance.
        if (App.RestartRequested && Environment.ProcessPath is { } executable)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start);
        }

        return exitCode;
    }

    // Also used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
