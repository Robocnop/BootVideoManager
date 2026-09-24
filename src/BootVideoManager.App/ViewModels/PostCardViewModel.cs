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

/// <summary>One catalog entry: display data, thumbnail and install state.</summary>
public sealed partial class PostCardViewModel : ViewModelBase
{
    private const int ThumbnailDecodeWidth = 320;

    private readonly InstallCoordinator _installs;
    private readonly ThumbnailCache _thumbnails;
    private readonly Action<PostCardViewModel> _openDetail;
    private CancellationTokenSource? _installCancellation;
    private bool _thumbnailRequested;
    private int _thumbnailGeneration;

    public PostCardViewModel(Post post, InstallCoordinator installs, ThumbnailCache thumbnails, Action<PostCardViewModel> openDetail)
    {
        Post = post;
        _installs = installs;
        _thumbnails = thumbnails;
        _openDetail = openDetail;
        IsInstalled = installs.IsInstalled(post.Id);
    }

    public Post Post { get; }

    public string Title => Post.Title;

    /// <summary>Screen-reader / UI automation name of the thumbnail button.</summary>
    public string PreviewAutomationName => $"{Loc.T("Aperçu", "Preview")} : {Post.Title}";

    /// <summary>Authors are always credited.</summary>
    public string AuthorText => string.IsNullOrWhiteSpace(Post.Author.Name)
        ? Loc.T("auteur inconnu", "unknown author")
        : $"{Loc.T("par", "by")} {Post.Author.Name}";

    public string DurationText => Post.Duration is { } duration
        ? duration.TotalSeconds < 60
            ? $"{Math.Round(duration.TotalSeconds)} s"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}"
        : "? s";

    public string StatsText => string.Create(
        CultureInfo.CurrentCulture,
        $"{Post.Likes:N0} {Loc.T("j'aime", Post.Likes == 1 ? "like" : "likes")} · {Post.Downloads:N0} {(Post.Downloads > 1 ? Loc.T("téléchargements", "downloads") : Loc.T("téléchargement", "download"))}");

    public string TagsText => string.Join(
        " · ",
        new[] { Post.Type == VideoType.SuspendVideo ? Loc.T("Veille", "Suspend") : Loc.T("Démarrage", "Startup") }
            .Concat(Post.Devices.Select(DeviceLabel).OfType<string>()));

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsInstalling { get; set; }

    /// <summary>Waiting for a free download slot (see <see cref="InstallCoordinator.MaxConcurrentDownloads"/>).</summary>
    [ObservableProperty]
    public partial bool IsQueued { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    public bool CanInstall => !IsInstalled && !IsInstalling;

    public static string? DeviceLabel(DeviceTag device) => device switch
    {
        DeviceTag.SteamDeck => "Steam Deck",
        DeviceTag.SteamMachine => "Steam Machine",
        DeviceTag.SteamFrame => "Steam Frame",
        _ => null,
    };

    /// <summary>Loads the thumbnail once, when the card is first shown.</summary>
    public async Task EnsureThumbnailAsync()
    {
        if (_thumbnailRequested)
        {
            return;
        }

        _thumbnailRequested = true;
        var generation = _thumbnailGeneration;
        var bitmap = await BitmapLoader.LoadAsync(_thumbnails, Post.ThumbnailUri, ThumbnailDecodeWidth);
        if (generation == _thumbnailGeneration && Thumbnail is null)
        {
            Thumbnail = bitmap;
        }
        else
        {
            bitmap?.Dispose(); // Card was recycled (and maybe shown again) while loading.
        }
    }

    /// <summary>Frees the decoded image once the card is no longer displayed.</summary>
    public void ReleaseThumbnail()
    {
        _thumbnailRequested = false;
        _thumbnailGeneration++;
        var bitmap = Thumbnail;
        Thumbnail = null;
        bitmap?.Dispose();
    }

    public void SyncInstalledState() => IsInstalled = _installs.IsInstalled(Post.Id);

    [RelayCommand]
    private void OpenDetail() => _openDetail(this);

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (!CanInstall)
        {
            return;
        }

        IsInstalling = true;
        IsQueued = true;
        IsProgressIndeterminate = true;
        Progress = 0;
        using var cancellation = new CancellationTokenSource();
        _installCancellation = cancellation;

        // Progress<T> captures the UI synchronization context: updates arrive on the UI thread.
        var progress = new Progress<DownloadProgress>(p =>
        {
            if (p.Fraction is { } fraction)
            {
                IsProgressIndeterminate = false;
                Progress = fraction * 100;
            }
        });

        try
        {
            await _installs.InstallAsync(Post, progress, cancellation.Token, started: () => IsQueued = false);
        }
        finally
        {
            _installCancellation = null;
            IsQueued = false;
            IsInstalling = false;
            SyncInstalledState();
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCancellation?.Cancel();

    [RelayCommand]
    private Task RemoveAsync() => _installs.RemovePostAsync(Post.Id);
}
