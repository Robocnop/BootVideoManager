using BootVideoManager.App.Services;
using BootVideoManager.Core.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.ViewModels;

/// <summary>Shell: tabs, Steam status, update banner, notifications and confirmation dialogs.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase, IDialogService, INotifier, IDisposable
{
    public const int SettingsTabIndex = 2;
    private static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(6);

    private readonly AppServices _services;
    private readonly InstallCoordinator _installs;

    /// <param name="canSelfUpdate">Whether updates can be installed in place (defaults to how this copy was installed).</param>
    public MainWindowViewModel(AppServices services, IPlatformServices platform, bool? canSelfUpdate = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _installs = new InstallCoordinator(services.Install, this, this);

        Updates = new UpdateViewModel(services.Updates, services.UpdateOptions, services.Settings, this, platform, canSelfUpdate ?? AppRuntime.CanSelfUpdate);
        Catalog = new CatalogViewModel(services.Catalog, services.Thumbnails, _installs, platform, services.Settings, new PreviewSoundViewModel(services.Settings));
        Installed = new InstalledViewModel(_installs, services.Thumbnails, platform);
        Settings = new SettingsViewModel(services.SteamLocator, services.Settings, _installs, platform, this, Updates);

        _installs.InstalledChanged += (_, _) => OnPropertyChanged(nameof(InstalledCount));
        _installs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallCoordinator.Steam))
            {
                OnPropertyChanged(nameof(SteamText));
            }
        };
    }

    public CatalogViewModel Catalog { get; }

    public InstalledViewModel Installed { get; }

    public SettingsViewModel Settings { get; }

    public UpdateViewModel Updates { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCatalogTab), nameof(IsInstalledTab), nameof(IsSettingsTab))]
    public partial int SelectedTabIndex { get; set; }

    public bool IsCatalogTab => SelectedTabIndex == 0;

    public bool IsInstalledTab => SelectedTabIndex == 1;

    public bool IsSettingsTab => SelectedTabIndex == SettingsTabIndex;

    [ObservableProperty]
    public partial ConfirmDialogViewModel? Dialog { get; set; }

    [ObservableProperty]
    public partial NotificationViewModel? Notification { get; set; }

    public string SteamText => _installs.Steam is { } steam
        ? $"Steam : {steam.RootPath}"
        : Loc.T("Steam introuvable : indiquez son dossier dans l'onglet Réglages", "Steam not found: choose its folder in the Settings tab");

    public int InstalledCount => _installs.Installed.Count;

    /// <summary>Resolves the Steam folder, then loads the installed videos and the catalog in parallel.</summary>
    /// <param name="steamRootOverride">Optional <c>--steam-root</c> value.</param>
    public async Task InitializeAsync(string? steamRootOverride)
    {
        if (!Settings.ResolveInitialSteam(steamRootOverride))
        {
            SelectedTabIndex = SettingsTabIndex;
        }

        await Task.WhenAll(_installs.RefreshAsync(), Catalog.LoadAsync(forceRefresh: false), Updates.CheckAtStartupAsync());
        _ = Task.Run(_services.Thumbnails.Trim);
    }

    public Task<bool> ConfirmAsync(string title, string message, string confirmText, bool destructive)
    {
        Dialog?.CancelCommand.Execute(null);

        var completion = new TaskCompletionSource<bool>();
        Dialog = new ConfirmDialogViewModel(title, message, confirmText, destructive, result =>
        {
            Dialog = null;
            completion.TrySetResult(result);
        });
        return completion.Task;
    }

    public void ShowInfo(string message)
    {
        var notification = Show(message, isError: false);
        _ = DismissLaterAsync(notification);
    }

    public void ShowError(string message)
    {
        AppLog.Warn($"Error shown to the user: {message}");
        Show(message, isError: true);
    }

    /// <summary>Escape closes the dialog first, then the detail panel.</summary>
    [RelayCommand]
    private void Escape()
    {
        if (Dialog is not null)
        {
            Dialog.CancelCommand.Execute(null);
        }
        else if (Catalog.SelectedPost is not null)
        {
            Catalog.CloseDetailCommand.Execute(null);
        }
    }

    public void Dispose()
    {
        Catalog.Dispose();
        _installs.Dispose();
    }

    private NotificationViewModel Show(string message, bool isError) =>
        Notification = new NotificationViewModel(message, isError, n =>
        {
            if (ReferenceEquals(Notification, n))
            {
                Notification = null;
            }
        });

    private static async Task DismissLaterAsync(NotificationViewModel notification)
    {
        await Task.Delay(InfoDuration);
        notification.DismissCommand.Execute(null);
    }
}
