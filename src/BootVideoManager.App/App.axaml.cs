using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BootVideoManager.App.Controls;
using BootVideoManager.App.Services;
using BootVideoManager.App.ViewModels;
using BootVideoManager.App.Views;

namespace BootVideoManager.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: every service is created once here.
            var services = AppServices.Create();
            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(services, new AvaloniaPlatformServices(window));
            window.DataContext = viewModel;
            var steamRoot = GetOptionValue(desktop.Args, "--steam-root");
            window.Opened += async (_, _) => await viewModel.InitializeAsync(steamRoot);
            desktop.MainWindow = window;

            // Last line of defence: report instead of crashing (expected errors are handled closer to the source).
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine(e.Exception);
                viewModel.ShowError($"Erreur inattendue : {e.Exception.Message}");
                e.Handled = true;
            };

            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                services.Dispose();
                VlcRuntime.Shutdown();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Reads <c>--name value</c> or <c>--name=value</c>.</summary>
    private static string? GetOptionValue(string[]? args, string name)
    {
        if (args is null)
        {
            return null;
        }

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == name && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.Ordinal))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }
}
