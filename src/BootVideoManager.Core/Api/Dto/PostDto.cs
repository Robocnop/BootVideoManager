using System.Text.Json.Serialization;

namespace BootVideoManager.Core.Api.Dto;

// Raw wire shapes of steamdeckrepo.com. Everything is nullable and loosely typed on purpose:
// validation and normalisation happen in PostMapper so a single odd entry never breaks the catalog.

internal sealed class PostDto
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("slug")] public string? Slug { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("user")] public UserDto? User { get; set; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; set; }
    [JsonPropertyName("video")] public string? Video { get; set; }
    [JsonPropertyName("video_duration")] public double? VideoDuration { get; set; }
    [JsonPropertyName("video_preview")] public string? VideoPreview { get; set; }
    [JsonPropertyName("created_at")] public string? CreatedAt { get; set; }
    [JsonPropertyName("updated_at")] public string? UpdatedAt { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("likes")] public long? Likes { get; set; }
    [JsonPropertyName("downloads")] public long? Downloads { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("devices")] public List<string?>? Devices { get; set; }
}

internal sealed class UserDto
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("steam_name")] public string? SteamName { get; set; }
    [JsonPropertyName("steam_avatar")] public string? SteamAvatar { get; set; }
}
