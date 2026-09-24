using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Caching;
using BootVideoManager.Core.Install;
using BootVideoManager.Core.Localization;
using BootVideoManager.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.ViewModels;

/// <summary>"Installed" tab: every video Steam can use — installed, disabled, or shipped with Steam.</summary>
public sealed partial class InstalledViewModel : ViewModelBase
{
    private readonly InstallCoordinator _installs;
    private readonly ThumbnailCache _thumbnails;
    private readonly IPlatformServices _platform;

    public InstalledViewModel(InstallCoordinator installs, ThumbnailCache thumbnails, IPlatformServices platform)
    {
        _installs = installs;
        _thumbnails = thumbnails;
        _platform = platform;

        _installs.InstalledChanged += (_, _) => Rebuild();
        _installs.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallCoordinator.Steam))
            {
                OnPropertyChanged(nameof(MoviesDirectory));
                OnPropertyChanged(nameof(HasSteam));
            }
        };
    }

    public ObservableCollection<InstalledItemViewModel> Items { get; } = [];

    public string MoviesDirectory => _installs.Steam?.MoviesDirectory ?? Loc.T("Aucun dossier Steam n'est sélectionné", "No Steam folder is selected");

    public bool HasSteam => _installs.Steam is not null;

    public bool IsEmpty => Items.Count == 0;

    public string Summary => Items.Count switch
    {
        0 => Loc.T("Le dossier ne contient aucune vidéo.", "The folder contains no video."),
        1 => $"{Loc.T("1 vidéo", "1 video")} · {EnabledText}",
        _ => string.Create(CultureInfo.CurrentCulture, $"{Items.Count} {Loc.T("vidéos", "videos")} · {EnabledText}"),
    };

    private string EnabledText => Items.Count(item => item.IsEnabled) switch
    {
        0 => Loc.T("aucune activée", "none enabled"),
        1 => Loc.T("1 activée", "1 enabled"),
        var count => string.Create(CultureInfo.CurrentCulture, $"{count} {Loc.T("activées", "enabled")}"),
    };

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [RelayCommand]
    private Task RefreshAsync() => RunBusyAsync(_installs.RefreshAsync);

    [RelayCommand]
    private Task RemoveAllAsync() => RunBusyAsync(_installs.RemoveAllAsync);

    [RelayCommand]
    private async Task ImportAsync()
    {
        if (await _platform.PickWebmFileAsync() is { } path)
        {
            await RunBusyAsync(() => _installs.ImportAsync(path));
        }
    }

    [RelayCommand]
    private Task OpenFolderAsync() =>
        _installs.Steam is { } steam ? _platform.OpenFolderAsync(steam.MoviesDirectory) : Task.CompletedTask;

    private async Task RunBusyAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Rebuild()
    {
        foreach (var item in Items)
        {
            item.ReleaseThumbnail();
        }

        Items.Clear();
        foreach (var video in _installs.Installed)
        {
            var item = new InstalledItemViewModel(video, _installs, _platform);
            Items.Add(item);
            _ = item.LoadThumbnailAsync(_thumbnails);
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }
}

/// <summary>A row of the installed list.</summary>
public sealed partial class InstalledItemViewModel(InstalledVideo video, InstallCoordinator installs, IPlatformServices platform) : ViewModelBase
{
    public InstalledVideo Video { get; } = video;

    public string Title => Video.DisplayTitle;

    public string FileName => Video.FileName;

    public string Subtitle => Video switch
    {
        { IsBuiltIn: true } => Loc.T(
            "Fournie avec Steam (steamui/movies) · le fichier d'origine n'est jamais modifié",
            "Shipped with Steam (steamui/movies) · the original file is never modified"),
        { IsSteamShopItem: true } => Loc.T("Objet de la Boutique des points Steam · se gère depuis Steam", "Steam Points Shop item · managed in Steam"),
        { IsInSteamCache: true } => Loc.T("Cache de Steam (config/communityitemscache/startupmovies)", "Steam cache (config/communityitemscache/startupmovies)"),
        { Entry.Source: InstalledVideoSource.LocalImport } => Loc.T("Importée depuis un fichier local", "Imported from a local file"),
        { Entry.Author: { Length: > 0 } author } => $"{Loc.T("par", "by")} {author} · steamdeckrepo.com",
        _ => Loc.T("Origine inconnue", "Unknown origin"),
    };

    public string TypeText => Video.Type switch
    {
        VideoType.SuspendVideo => Loc.T("Veille", "Suspend"),
        VideoType.BootVideo => Loc.T("Démarrage", "Startup"),
        _ => Loc.T("Vidéo", "Video"),
    };

    public string SizeText => Video.SizeBytes < 1024 * 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, Video.SizeBytes / 1024)} {Loc.T("Ko", "KB")}")
        : string.Create(CultureInfo.CurrentCulture, $"{Video.SizeBytes / 1024d / 1024d:0.0} {Loc.T("Mo", "MB")}");

    public string StatusText => Video.Status switch
    {
        InstalledVideoStatus.Tracked => Loc.T("Installée par l'application", "Installed by the app"),
        InstalledVideoStatus.Modified => Loc.T("Modifiée depuis l'installation", "Modified since installed"),
        InstalledVideoStatus.BuiltIn => Loc.T("Vidéo d'origine de Steam", "Steam stock video"),
        _ when Video.IsSteamShopItem => Loc.T("Gérée par Steam", "Managed by Steam"),
        _ when Video.IsInSteamCache => Loc.T("Présente dans le cache de Steam", "In Steam's cache"),
        _ => Loc.T("Ajoutée en dehors de l'application", "Added outside the app"),
    };

    public bool IsTracked => Video.Status is InstalledVideoStatus.Tracked or InstalledVideoStatus.BuiltIn;

    /// <summary>Stock Steam animations cannot be deleted, only disabled.</summary>
    public bool CanDelete => !Video.IsBuiltIn && !Video.IsSteamShopItem;

    /// <summary>Points Shop items are re-downloaded by Steam if moved: they are only shown.</summary>
    public bool CanToggle => !Video.IsSteamShopItem;

    public bool IsEnabled => Video.IsEnabled;

    public bool HasPage => Video.Entry?.PageUri is not null;

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    private bool _released;

    public async Task LoadThumbnailAsync(ThumbnailCache cache)
    {
        var bitmap = await BitmapLoader.LoadAsync(cache, Video.Entry?.ThumbnailUri, 200);
        if (_released)
        {
            bitmap?.Dispose(); // The list was rebuilt while loading: this row is no longer shown.
            return;
        }

        Thumbnail = bitmap;
    }

    public void ReleaseThumbnail()
    {
        _released = true;
        var bitmap = Thumbnail;
        Thumbnail = null;
        bitmap?.Dispose();
    }

    [RelayCommand]
    private Task RemoveAsync() => installs.RemoveAsync(Video);

    /// <summary>The list is rebuilt from disk afterwards, so the switch always reflects the real state.</summary>
    [RelayCommand]
    private Task ToggleEnabledAsync() => installs.SetEnabledAsync(Video, !Video.IsEnabled);

    [RelayCommand]
    private Task OpenPageAsync() =>
        Video.Entry?.PageUri is { } uri ? platform.OpenUriAsync(uri) : Task.CompletedTask;
}
