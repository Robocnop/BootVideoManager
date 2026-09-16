using System.Text.Json;
using BootVideoManager.Core.Api.Dto;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Api;

/// <summary>Result of parsing the full catalog (<c>/api/posts/all</c>).</summary>
/// <param name="Posts">Valid posts, in server order.</param>
/// <param name="SkippedCount">Entries ignored because they were malformed.</param>
/// <param name="ServerCachedAt">When the server generated this catalog (<c>last_cached_at</c>).</param>
public sealed record ParsedCatalog(IReadOnlyList<Post> Posts, int SkippedCount, DateTimeOffset? ServerCachedAt);

/// <summary>
/// Parses steamdeckrepo.com JSON payloads. Tolerant per entry (a bad post is skipped),
/// strict on the envelope (a missing <c>posts</c> array means the API changed).
/// </summary>
public static class PostsParser
{
    /// <summary>Parses <c>GET /api/posts/all</c>: <c>{ "posts": [...], "last_cached_at": 1789577865 }</c>.</summary>
    /// <exception cref="RepoApiException">Kind <see cref="RepoApiErrorKind.InvalidResponse"/> if the envelope is not recognised.</exception>
    public static ParsedCatalog ParseAll(ReadOnlyMemory<byte> json)
    {
        using var document = ParseDocument(json);
        var root = document.RootElement;
        var (posts, skipped) = ParsePosts(GetPostsArray(root));

        DateTimeOffset? serverCachedAt = root.TryGetProperty("last_cached_at", out var cachedAt)
            && cachedAt.ValueKind == JsonValueKind.Number
            && cachedAt.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;

        return new ParsedCatalog(posts, skipped, serverCachedAt);
    }

    /// <summary>Parses <c>GET /api/posts?…</c>: <c>{ "posts": [...], "sortOptions": {...}, "currentSort": "…" }</c>.</summary>
    /// <exception cref="RepoApiException">Kind <see cref="RepoApiErrorKind.InvalidResponse"/> if the envelope is not recognised.</exception>
    public static IReadOnlyList<Post> ParsePage(ReadOnlyMemory<byte> json)
    {
        using var document = ParseDocument(json);
        return ParsePosts(GetPostsArray(document.RootElement)).Posts;
    }

    private static JsonDocument ParseDocument(ReadOnlyMemory<byte> json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new RepoApiException(RepoApiErrorKind.InvalidResponse, "The catalog is not valid JSON.", ex);
        }
    }

    private static JsonElement GetPostsArray(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("posts", out var posts)
            || posts.ValueKind != JsonValueKind.Array)
        {
            throw new RepoApiException(
                RepoApiErrorKind.InvalidResponse,
                "Unexpected catalog format: no \"posts\" array. The site API may have changed.");
        }

        return posts;
    }

    private static (List<Post> Posts, int Skipped) ParsePosts(JsonElement array)
    {
        var posts = new List<Post>(array.GetArrayLength());
        var skipped = 0;

        foreach (var element in array.EnumerateArray())
        {
            if (TryDeserialize(element) is { } dto && PostMapper.TryMap(dto, out var post))
            {
                posts.Add(post);
            }
            else
            {
                skipped++;
            }
        }

        return (posts, skipped);
    }

    private static PostDto? TryDeserialize(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            return element.Deserialize(RepoJsonContext.Default.PostDto);
        }
        catch (JsonException)
        {
            // A field has an unexpected type (e.g. "likes": "a lot"): skip this entry only.
            return null;
        }
    }
}
