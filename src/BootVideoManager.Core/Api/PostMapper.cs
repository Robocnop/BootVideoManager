using System.Globalization;
using BootVideoManager.Core.Api.Dto;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Api;

/// <summary>Validates a raw <see cref="PostDto"/> and turns it into a <see cref="Post"/>.</summary>
internal static class PostMapper
{
    internal static readonly Uri SiteRoot = new("https://steamdeckrepo.com/");

    /// <summary>
    /// Maps a DTO. Returns <c>false</c> only when the entry is unusable (no id or no valid video link);
    /// every other missing field gets a safe default.
    /// </summary>
    public static bool TryMap(PostDto dto, out Post post)
    {
        post = null!;

        var id = dto.Id?.Trim();
        if (string.IsNullOrEmpty(id) || TryParseWebUri(dto.Video) is not { } videoUri)
        {
            return false;
        }

        var slug = string.IsNullOrWhiteSpace(dto.Slug) ? id : dto.Slug.Trim();
        var title = string.IsNullOrWhiteSpace(dto.Title) ? slug : dto.Title.Trim();

        post = new Post(
            Id: id,
            Slug: slug,
            Title: title,
            Description: dto.Content?.Trim() ?? string.Empty,
            Author: new PostAuthor(
                dto.User?.Id ?? 0,
                dto.User?.SteamName?.Trim() ?? string.Empty,
                TryParseWebUri(dto.User?.SteamAvatar)),
            ThumbnailUri: TryParseWebUri(dto.Thumbnail),
            VideoUri: videoUri,
            PreviewUri: TryParseWebUri(dto.VideoPreview),
            Duration: dto.VideoDuration is > 0 and < 86_400 ? TimeSpan.FromSeconds(dto.VideoDuration.Value) : null,
            CreatedAt: ParseDate(dto.CreatedAt),
            UpdatedAt: ParseDate(dto.UpdatedAt ?? dto.CreatedAt),
            PageUri: ParsePageUri(dto.Url, id, slug),
            Likes: ClampCounter(dto.Likes),
            Downloads: ClampCounter(dto.Downloads),
            Type: ParseType(dto.Type),
            Devices: ParseDevices(dto.Devices));
        return true;
    }

    internal static VideoType ParseType(string? value) => value switch
    {
        "boot_video" => VideoType.BootVideo,
        "suspend_video" => VideoType.SuspendVideo,
        "boot_video_removed" or "suspend_video_removed" => VideoType.Removed,
        _ => VideoType.Unknown,
    };

    internal static DeviceTag ParseDevice(string? value) => value switch
    {
        "steam_deck" => DeviceTag.SteamDeck,
        "steam_machine" => DeviceTag.SteamMachine,
        "steam_frame" => DeviceTag.SteamFrame,
        _ => DeviceTag.Unknown,
    };

    private static DeviceTag[] ParseDevices(List<string?>? values) =>
        values is null ? [] : values.Select(ParseDevice).Distinct().ToArray();

    /// <summary>Accepts only absolute http(s) links: the catalog is untrusted input.</summary>
    private static Uri? TryParseWebUri(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri
            : null;

    /// <summary>The API returns <c>http://</c> page links; upgrade them and rebuild the link when absent.</summary>
    private static Uri ParsePageUri(string? value, string id, string slug)
    {
        if (TryParseWebUri(value) is { } uri)
        {
            return uri.Scheme == Uri.UriSchemeHttp && uri.Host.EndsWith("steamdeckrepo.com", StringComparison.OrdinalIgnoreCase)
                ? new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri
                : uri;
        }

        return new Uri(SiteRoot, $"post/{Uri.EscapeDataString(id)}/{Uri.EscapeDataString(slug)}");
    }

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var date)
            ? date
            : DateTimeOffset.UnixEpoch;

    private static int ClampCounter(long? value) => (int)Math.Clamp(value ?? 0, 0, int.MaxValue);
}
