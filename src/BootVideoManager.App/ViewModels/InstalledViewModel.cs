using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media.Imaging;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Caching;
using BootVideoManager.Core.Install;
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

    public string MoviesDirectory => _installs.Steam?.MoviesDirectory ?? "Aucun dossier Steam n'est sélectionné";

    public bool HasSteam => _installs.Steam is not null;

    public bool IsEmpty => Items.Count == 0;

    public string Summary => Items.Count switch
    {
        0 => "Le dossier ne contient aucune vidéo.",
        1 => $"1 vidéo · {EnabledText}",
        _ => string.Create(CultureInfo.CurrentCulture, $"{Items.Count} vidéos · {EnabledText}"),
    };

    private string EnabledText => Items.Count(item => item.IsEnabled) switch
    {
        0 => "aucune activée",
        1 => "1 activée",
        var count => string.Create(CultureInfo.CurrentCulture, $"{count} activées"),
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
        { IsBuiltIn: true } => "Fournie avec Steam (steamui/movies) · le fichier d'origine n'est jamais modifié",
        { IsSteamShopItem: true } => "Objet de la Boutique des points Steam · se gère depuis Steam",
        { IsInSteamCache: true } => "Cache de Steam (config/communityitemscache/startupmovies)",
        { Entry.Source: InstalledVideoSource.LocalImport } => "Importée depuis un fichier local",
        { Entry.Author: { Length: > 0 } author } => $"par {author} · steamdeckrepo.com",
        _ => "Origine inconnue",
    };

    public string TypeText => Video.Type switch
    {
        VideoType.SuspendVideo => "Veille",
        VideoType.BootVideo => "Démarrage",
        _ => "Vidéo",
    };

    public string SizeText => Video.SizeBytes < 1024 * 1024
        ? string.Create(CultureInfo.CurrentCulture, $"{Math.Max(1, Video.SizeBytes / 1024)} Ko")
        : string.Create(CultureInfo.CurrentCulture, $"{Video.SizeBytes / 1024d / 1024d:0.0} Mo");

    public string StatusText => Video.Status switch
    {
        InstalledVideoStatus.Tracked => "Installée par l'application",
        InstalledVideoStatus.Modified => "Modifiée depuis l'installation",
        InstalledVideoStatus.BuiltIn => "Vidéo d'origine de Steam",
        _ when Video.IsSteamShopItem => "Gérée par Steam",
        _ when Video.IsInSteamCache => "Présente dans le cache de Steam",
        _ => "Ajoutée en dehors de l'application",
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

    public async Task LoadThumbnailAsync(ThumbnailCache cache) =>
        Thumbnail = await BitmapLoader.LoadAsync(cache, Video.Entry?.ThumbnailUri, 200);

    public void ReleaseThumbnail()
    {
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
