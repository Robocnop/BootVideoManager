using System.Collections.ObjectModel;
using BootVideoManager.App.Services;
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

/// <summary>Settings tab: Steam folder selection, how-to and credits.</summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    public static readonly Uri SiteUri = new("https://steamdeckrepo.com/");

    private readonly SteamLocator _locator;
    private readonly SettingsStore _settingsStore;
    private readonly InstallCoordinator _installs;
    private readonly IPlatformServices _platform;
    private readonly INotifier _notifier;

    public SettingsViewModel(SteamLocator locator, SettingsStore settingsStore, InstallCoordinator installs, IPlatformServices platform, INotifier notifier)
    {
        _locator = locator;
        _settingsStore = settingsStore;
        _installs = installs;
        _platform = platform;
        _notifier = notifier;

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

    public string CurrentRoot => _installs.Steam?.RootPath ?? "Aucun dossier Steam sélectionné";

    public string CurrentMoviesDirectory => _installs.Steam?.MoviesDirectory ?? "—";

    public bool IsManual => _installs.Steam?.Kind == SteamInstallKind.Manual;

    public string Version { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "?";

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

            _notifier.ShowError($"--steam-root ignoré : « {commandLineRoot} » n'est pas un dossier Steam.");
        }

        var saved = _settingsStore.Load().SteamRootOverride;

        if (saved is not null)
        {
            if (_locator.TryCreateManual(saved) is { } manual)
            {
                _installs.Steam = manual with { Kind = SteamInstallKind.Manual };
                return true;
            }

            _notifier.ShowError($"Le dossier Steam enregistré n'est plus valide : {saved}. Détection automatique utilisée.");
        }

        _installs.Steam = Detected.FirstOrDefault()?.Installation;
        return _installs.Steam is not null;
    }

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
        if (await _platform.PickFolderAsync("Choisir le dossier d'installation de Steam") is not { } path)
        {
            return;
        }

        if (_locator.TryCreateManual(path) is not { } installation)
        {
            ValidationMessage = "Ce dossier ne ressemble pas à une installation Steam (aucun dossier « config » ou « steamapps »).";
            return;
        }

        await ApplyAsync(installation, installation.RootPath);
    }

    [RelayCommand]
    private Task OpenSiteAsync() => _platform.OpenUriAsync(SiteUri);

    private async Task ApplyAsync(SteamInstallation installation, string? overridePath)
    {
        ValidationMessage = null;
        try
        {
            _settingsStore.Save(new AppSettings { SteamRootOverride = overridePath });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notifier.ShowError($"Le choix n'a pas pu être enregistré pour les prochains lancements : {ex.Message}");
        }

        _installs.Steam = installation;
        await _installs.RefreshAsync();
        _notifier.ShowInfo($"Dossier Steam utilisé : {installation.RootPath}");
    }

    private static string KindLabel(SteamInstallKind kind) => kind switch
    {
        SteamInstallKind.Registry => "Détecté via le registre Windows",
        SteamInstallKind.DefaultLocation => "Emplacement par défaut",
        SteamInstallKind.Native => "Installation Linux / Steam Deck",
        SteamInstallKind.Flatpak => "Flatpak",
        SteamInstallKind.Snap => "Snap",
        _ => "Choisi manuellement",
    };
}
