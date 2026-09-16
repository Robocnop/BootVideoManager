namespace BootVideoManager.Core.Models;

/// <summary>Author of a post (a Steam account on steamdeckrepo.com).</summary>
/// <param name="Id">Numeric user id on the site.</param>
/// <param name="Name">Steam display name, always shown next to the video to credit the author.</param>
/// <param name="AvatarUri">Steam avatar, if any.</param>
public sealed record PostAuthor(long Id, string Name, Uri? AvatarUri);

/// <summary>A video published on steamdeckrepo.com, validated and normalised.</summary>
/// <param name="Id">Short hash id (e.g. <c>MnZgE</c>); unique, used in URLs and file names.</param>
/// <param name="Slug">URL slug; <b>not</b> unique across posts.</param>
/// <param name="Title">Display title.</param>
/// <param name="Description">Free text description, possibly empty.</param>
/// <param name="Author">Uploader, to be credited in the UI.</param>
/// <param name="ThumbnailUri">Still image for the grid, if provided.</param>
/// <param name="VideoUri">Direct CDN link to the <c>.webm</c> file.</param>
/// <param name="PreviewUri">Lightweight preview clip (usually <c>.mp4</c>), if provided.</param>
/// <param name="Duration">Video length, unknown for a few posts.</param>
/// <param name="CreatedAt">Upload date (UTC).</param>
/// <param name="UpdatedAt">Last edit date (UTC).</param>
/// <param name="PageUri">Public page of the post on the site.</param>
/// <param name="Likes">Like counter at catalog fetch time.</param>
/// <param name="Downloads">Download counter at catalog fetch time.</param>
/// <param name="Type">Boot or suspend animation.</param>
/// <param name="Devices">Devices the author targeted.</param>
public sealed record Post(
    string Id,
    string Slug,
    string Title,
    string Description,
    PostAuthor Author,
    Uri? ThumbnailUri,
    Uri VideoUri,
    Uri? PreviewUri,
    TimeSpan? Duration,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Uri PageUri,
    int Likes,
    int Downloads,
    VideoType Type,
    IReadOnlyList<DeviceTag> Devices);
