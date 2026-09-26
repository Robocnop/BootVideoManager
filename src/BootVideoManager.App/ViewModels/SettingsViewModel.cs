using System.Collections.ObjectModel;
using System.Globalization;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Platform;
using BootVideoManager.Core.Steam;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.ViewModels;

/// <summary>Detected Steam installation shown in the settings list.</summary>
public sealed record SteamChoice(SteamInstallation Installation, string KindLabel)
{
    public string RootPath => Installation.RootPath;
}

/// <summary>Settings tab: Steam folder, updates, language, how-to, logs and credits.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public static readonly Uri SiteUri = new("https://steamdeckrepo.com/");

    private readonly SteamLocator _locator;
    private readonly SettingsStore _settingsStore;
    private readonly InstallCoordinator _installs;
    private readonly IPlatformServices _platform;
    private readonly INotifier _notifier;
    private readonly bool _languageLoaded;

    public SettingsViewModel(
        SteamLocator locator,
        SettingsStore settingsStore,
        InstallCoordinator installs,
        AccountCoordinator account,
        IPlatformServices platform,
        INotifier notifier,
        UpdateViewModel updates)
    {
        Account = account;
        account.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AccountCoordinator.User) or nameof(AccountCoordinator.LikedCount) or nameof(AccountCoordinator.LikesLoaded))
            {
                OnPropertyChanged(nameof(AccountStatusText));
            }
        };
        _locator = locator;
        _settingsStore = settingsStore;
        _installs = installs;
        _platform = platform;
        _notifier = notifier;
        Updates = updates;

        LanguageOptions =
        [
            new(Loc.T("Automatique (langue du système)", "Automatic (system language)"), null),
            new("Français", Loc.French),
            new("English", Loc.English),
        ];
        var savedLanguage = settingsStore.Load().Language;
        SelectedLanguage = LanguageOptions.FirstOrDefault(o => o.Value == savedLanguage) ?? LanguageOptions[0];
        _languageLoaded = true;

        _installs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallCoordinator.Steam))
            {
                OnPropertyChanged(nameof(CurrentRoot));
                OnPropertyChanged(nameof(CurrentMoviesDirectory));
                OnPropertyChanged(nameof(IsManual));
            }
        };
    }

    public ObservableCollection<SteamChoice> Detected { get; } = [];

    public UpdateViewModel Updates { get; }

    public AccountCoordinator Account { get; }

    public string AccountStatusText => Account.User is { } user
        ? Account.LikesLoaded
            ? string.Create(CultureInfo.CurrentCulture, $"{Loc.T("Connecté en tant que", "Signed in as")} {user.Name} · {Account.LikedCount:N0} {Loc.T("j'aime", Account.LikedCount == 1 ? "like" : "likes")}")
            : $"{Loc.T("Connecté en tant que", "Signed in as")} {user.Name}"
        : Loc.T("Non connecté", "Not signed in");

    public IReadOnlyList<Choice<string?>> LanguageOptions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRestartRequired))]
    public partial Choice<string?> SelectedLanguage { get; set; }

    /// <summary>The chosen language differs from the one on screen: texts change after a restart.</summary>
    public bool IsRestartRequired =>
        SelectedLanguage is not null && Loc.Resolve(SelectedLanguage.Value, CultureInfo.CurrentUICulture) != Loc.Current;

    public string RestartHint { get; } = Loc.T(
        "La nouvelle langue s'appliquera au prochain démarrage.",
        "The new language will apply at the next start.");

    public string CurrentRoot => _installs.Steam?.RootPath ?? Loc.T("Aucun dossier Steam n'est sélectionné", "No Steam folder is selected");

    public string CurrentMoviesDirectory => _installs.Steam?.MoviesDirectory ?? "—";

    public bool IsManual => _installs.Steam?.Kind == SteamInstallKind.Manual;

    public string Version { get; } = AppRuntime.VersionText;

    [ObservableProperty]
    public partial string? ValidationMessage { get; set; }

    /// <summary>
    /// Picks, in order: the <c>--steam-root</c> command-line folder (not persisted, handy for testing on a copy),
    /// the saved folder if still valid, then the first detected installation.
    /// </summary>
    /// <returns><c>false</c> when no Steam installation is available.</returns>
    public bool ResolveInitialSteam(string? commandLineRoot)
    {
        DetectInstallations();

        if (commandLineRoot is not null)
        {
            if (_locator.TryCreateManual(commandLineRoot) is { } fromCommandLine)
            {
                _installs.Steam = fromCommandLine;
                return true;
            }

            _notifier.ShowError(Loc.T(
                $"Option --steam-root ignorée : « {commandLineRoot} » n'est pas un dossier Steam.",
                $"--steam-root option ignored: “{commandLineRoot}” is not a Steam folder."));
        }

        var saved = _settingsStore.Load().SteamRootOverride;

        if (saved is not null)
        {
            if (_locator.TryCreateManual(saved) is { } manual)
            {
                _installs.Steam = manual with { Kind = SteamInstallKind.Manual };
                return true;
            }

            _notifier.ShowError(Loc.T(
                $"Le dossier Steam enregistré n'est plus valide ({saved}) : la détection automatique a pris le relais.",
                $"The saved Steam folder is no longer valid ({saved}): automatic detection took over."));
        }

        _installs.Steam = Detected.FirstOrDefault()?.Installation;
        AppLog.Info(_installs.Steam is { } steam ? $"Steam folder: {steam.RootPath} ({steam.Kind})." : "No Steam installation found.");
        return _installs.Steam is not null;
    }

    partial void OnSelectedLanguageChanged(Choice<string?> value)
    {
        if (!_languageLoaded)
        {
            return;
        }

        try
        {
            _settingsStore.Update(s => s with { Language = value.Value });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not save the language.", ex);
            _notifier.ShowError(Loc.T($"Impossible d'enregistrer la langue : {ex.Message}", $"Could not save the language: {ex.Message}"));
        }
    }

    [RelayCommand]
    private void Restart() => _platform.CloseApplication(restart: true);

    [RelayCommand]
    private Task OpenLogsAsync() =>
        AppLog.Directory is { } directory ? _platform.OpenFolderAsync(directory) : Task.CompletedTask;

    [RelayCommand]
    private void DetectInstallations()
    {
        Detected.Clear();
        foreach (var installation in _locator.FindInstallations())
        {
            Detected.Add(new SteamChoice(installation, KindLabel(installation.Kind)));
        }
    }

    [RelayCommand]
    private Task UseDetectedAsync(SteamChoice choice)
    {
        // The best automatic candidate needs no override; any other one is remembered explicitly.
        var isDefault = ReferenceEquals(choice, Detected.FirstOrDefault());
        return ApplyAsync(choice.Installation, isDefault ? null : choice.RootPath);
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _platform.PickFolderAsync(Loc.T("Choisir le dossier d'installation de Steam", "Choose Steam's installation folder")) is not { } path)
        {
            return;
        }

        if (_locator.TryCreateManual(path) is not { } installation)
        {
            ValidationMessage = Loc.T(
                "Ce dossier ne semble pas contenir d'installation Steam (ni dossier « config » ni dossier « steamapps »).",
                "This folder does not look like a Steam installation (no “config” nor “steamapps” folder).");
            return;
        }

        await ApplyAsync(installation, installation.RootPath);
    }

    [RelayCommand]
    private Task SignInAsync() => Account.SignInAsync();

    [RelayCommand]
    private Task SignOutAsync() => Account.SignOutAsync();

    [RelayCommand]
    private Task RefreshLikesAsync() => Account.RefreshLikesAsync(quiet: false);

    [RelayCommand]
    private Task OpenSiteAsync() => _platform.OpenUriAsync(SiteUri);

    private async Task ApplyAsync(SteamInstallation installation, string? overridePath)
    {
        ValidationMessage = null;
        string? saveError = null;
        try
        {
            // Only the Steam folder changes: the other preferences are kept.
            _settingsStore.Update(s => s with { SteamRootOverride = overridePath });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warn("Could not save the Steam folder.", ex);
            saveError = ex.Message;
        }

        _installs.Steam = installation;
        AppLog.Info($"Steam folder changed: {installation.RootPath}.");
        await _installs.RefreshAsync();

        // Shown last so the confirmation never hides the save failure.
        if (saveError is null)
        {
            _notifier.ShowInfo(Loc.T($"Dossier Steam utilisé : {installation.RootPath}", $"Steam folder in use: {installation.RootPath}"));
        }
        else
        {
            _notifier.ShowError(Loc.T(
                $"Dossier Steam utilisé : {installation.RootPath}, mais ce choix n'a pas pu être enregistré pour les prochains lancements : {saveError}",
                $"Steam folder in use: {installation.RootPath}, but this choice could not be saved for the next launches: {saveError}"));
        }
    }

    private static string KindLabel(SteamInstallKind kind) => kind switch
    {
        SteamInstallKind.Registry => Loc.T("Détectée dans le registre Windows", "Found in the Windows registry"),
        SteamInstallKind.DefaultLocation => Loc.T("Emplacement par défaut", "Default location"),
        SteamInstallKind.Native => Loc.T("Installation Linux / Steam Deck", "Linux / Steam Deck installation"),
        SteamInstallKind.Flatpak => "Flatpak",
        SteamInstallKind.Snap => "Snap",
        _ => Loc.T("Choisie manuellement", "Chosen manually"),
    };
}
