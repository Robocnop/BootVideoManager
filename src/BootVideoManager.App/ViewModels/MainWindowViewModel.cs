using Avalonia.Media.Imaging;
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
        Account = new AccountCoordinator(services.Account, services.AccountSessions, platform, this, services.Paths.WebViewDataDirectory);

        Updates = new UpdateViewModel(services.Updates, services.UpdateOptions, services.Settings, this, platform, canSelfUpdate ?? AppRuntime.CanSelfUpdate, AppRuntime.IsFlatpak);
        Catalog = new CatalogViewModel(services.Catalog, services.Thumbnails, _installs, Account, platform, services.Settings, new PreviewSoundViewModel(services.Settings));
        StartupMovie = new StartupMovieCoordinator(_installs, platform, this, this);
        Installed = new InstalledViewModel(_installs, StartupMovie, services.Catalog, services.Thumbnails, platform, this);
        Settings = new SettingsViewModel(services.SteamLocator, services.Settings, _installs, Account, platform, this, Updates);

        _installs.InstalledChanged += (_, _) => OnPropertyChanged(nameof(InstalledCount));
        Account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AccountCoordinator.User))
            {
                OnPropertyChanged(nameof(AccountName));
                _ = LoadAccountAvatarAsync();
            }
        };
        _installs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallCoordinator.Steam))
            {
                OnPropertyChanged(nameof(SteamText));
            }
        };
    }

    public CatalogViewModel Catalog { get; }

    /// <summary>steamdeckrepo.com account (sign-in and likes).</summary>
    public AccountCoordinator Account { get; }

    public InstalledViewModel Installed { get; }

    public StartupMovieCoordinator StartupMovie { get; }

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

    /// <summary>Top bar: the signed-in steamdeckrepo.com account.</summary>
    public string AccountName => Account.User?.Name ?? string.Empty;

    [ObservableProperty]
    public partial Bitmap? AccountAvatar { get; private set; }

    /// <summary>Resolves the Steam folder, then loads the installed videos and the catalog in parallel.</summary>
    /// <param name="steamRootOverride">Optional <c>--steam-root</c> value.</param>
    public async Task InitializeAsync(string? steamRootOverride)
    {
        if (!Settings.ResolveInitialSteam(steamRootOverride))
        {
            SelectedTabIndex = SettingsTabIndex;
        }

        await Task.WhenAll(_installs.RefreshAsync(), Catalog.LoadAsync(forceRefresh: false), Updates.CheckAtStartupAsync(), Account.InitializeAsync());
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

    /// <summary>Top bar button shown while signed out: makes the feature discoverable without reading the docs.</summary>
    [RelayCommand]
    private Task SignInAsync() => Account.SignInAsync();

    /// <summary>Top bar account chip: its settings (reload likes, sign out) live in the Settings tab.</summary>
    [RelayCommand]
    private void OpenAccountSettings() => SelectedTabIndex = SettingsTabIndex;

    private async Task LoadAccountAvatarAsync()
    {
        var avatarUri = Account.User?.AvatarUri;
        var bitmap = await BitmapLoader.LoadAsync(_services.Thumbnails, avatarUri, 64);
        if (Account.User?.AvatarUri != avatarUri)
        {
            bitmap?.Dispose(); // Signed out (or in as someone else) meanwhile.
            return;
        }

        var previous = AccountAvatar;
        AccountAvatar = bitmap;
        previous?.Dispose();
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
