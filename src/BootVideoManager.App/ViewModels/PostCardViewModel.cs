using System.Globalization;
using Avalonia.Media.Imaging;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Caching;
using BootVideoManager.Core.Install;
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
    public string PreviewAutomationName => $"Aperçu : {Post.Title}";

    /// <summary>Authors are always credited.</summary>
    public string AuthorText => string.IsNullOrWhiteSpace(Post.Author.Name) ? "auteur inconnu" : $"par {Post.Author.Name}";

    public string DurationText => Post.Duration is { } duration
        ? duration.TotalSeconds < 60
            ? $"{Math.Round(duration.TotalSeconds)} s"
            : $"{(int)duration.TotalMinutes}:{duration.Seconds:00}"
        : "? s";

    public string StatsText => string.Create(
        CultureInfo.CurrentCulture,
        $"{Post.Likes:N0} j'aime · {Post.Downloads:N0} {(Post.Downloads > 1 ? "téléchargements" : "téléchargement")}");

    public string TagsText => string.Join(
        " · ",
        new[] { Post.Type == VideoType.SuspendVideo ? "Veille" : "Démarrage" }
            .Concat(Post.Devices.Select(DeviceLabel).OfType<string>()));

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsInstalled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    public partial bool IsInstalling { get; set; }

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
        var bitmap = await BitmapLoader.LoadAsync(_thumbnails, Post.ThumbnailUri, ThumbnailDecodeWidth);
        if (_thumbnailRequested)
        {
            Thumbnail = bitmap;
        }
        else
        {
            bitmap?.Dispose(); // Card was recycled while loading.
        }
    }

    /// <summary>Frees the decoded image once the card is no longer displayed.</summary>
    public void ReleaseThumbnail()
    {
        _thumbnailRequested = false;
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
            await _installs.InstallAsync(Post, progress, cancellation.Token);
        }
        finally
        {
            _installCancellation = null;
            IsInstalling = false;
            SyncInstalledState();
        }
    }

    [RelayCommand]
    private void CancelInstall() => _installCancellation?.Cancel();

    [RelayCommand]
    private Task RemoveAsync() => _installs.RemovePostAsync(Post.Id);
}
