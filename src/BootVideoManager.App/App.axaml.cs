using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using BootVideoManager.App.Controls;
using BootVideoManager.App.Services;
using BootVideoManager.App.ViewModels;
using BootVideoManager.App.Views;
using BootVideoManager.Core.Localization;

namespace BootVideoManager.App;

public partial class App : Application
{
    /// <summary>Set before shutting down to start a fresh copy (e.g. after a language change).</summary>
    public static bool RestartRequested { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Composition root: every service is created once here.
            var services = AppServices.Create();
            AppLog.Initialize(services.Log);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => AppLog.Error("Unhandled exception.", e.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLog.Warn("Unobserved task exception.", e.Exception);
                e.SetObserved();
            };

            // The language must be chosen before any view is created: their texts are read once.
            Loc.Apply(services.Settings.Load().Language);
            AppLog.Info($"Boot Video Manager {AppRuntime.VersionText} ({AppRuntime.RuntimeIdentifier}) started, language: {Loc.Current}.");

            var window = new MainWindow();
            var viewModel = new MainWindowViewModel(services, new AvaloniaPlatformServices(window));
            window.DataContext = viewModel;
            var steamRoot = GetOptionValue(desktop.Args, "--steam-root");
            window.Opened += async (_, _) => await viewModel.InitializeAsync(steamRoot);
            desktop.MainWindow = window;

            SingleInstance.ActivationRequested += (_, _) => Dispatcher.UIThread.Post(() => BringToFront(window));

            // Last line of defence: report instead of crashing (expected errors are handled closer to the source).
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                AppLog.Error("Unhandled UI exception.", e.Exception);
                viewModel.ShowError(Loc.T($"Erreur inattendue : {e.Exception.Message}", $"Unexpected error: {e.Exception.Message}"));
                e.Handled = true;
            };

            desktop.Exit += (_, _) =>
            {
                viewModel.Dispose();
                services.Dispose();
                VlcRuntime.Shutdown();
                AppLog.Info("Exited.");
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Show();
        window.Activate();
        // Windows refuses focus changes from background processes: a Topmost toggle still raises the window.
        window.Topmost = true;
        window.Topmost = false;
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
