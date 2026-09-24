using System.Globalization;
using BootVideoManager.App.Services;
using BootVideoManager.Core.Localization;
using CommunityToolkit.Mvvm.Input;

namespace BootVideoManager.App.ViewModels;

/// <summary>Side panel with the video preview and full information about a post.</summary>
public sealed partial class PostDetailViewModel(PostCardViewModel card, IPlatformServices platform, PreviewSoundViewModel sound, Action close) : ViewModelBase
{
    public PostCardViewModel Card { get; } = card;

    /// <summary>Preview volume and mute (remembered between sessions).</summary>
    public PreviewSoundViewModel Sound { get; } = sound;

    /// <summary>The exact file that would be installed, so the preview matches the result.</summary>
    public Uri PreviewUri => Card.Post.VideoUri;

    public string Description => string.IsNullOrWhiteSpace(Card.Post.Description)
        ? Loc.T("Aucune description.", "No description.")
        : Card.Post.Description;

    public string UploadedText => string.Create(
        CultureInfo.CurrentCulture,
        $"{Loc.T("Publiée le", "Published on")} {Card.Post.CreatedAt.LocalDateTime:d}");

    [RelayCommand]
    private Task OpenPageAsync() => platform.OpenUriAsync(Card.Post.PageUri);

    [RelayCommand]
    private void Close() => close();
}
