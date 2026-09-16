using System.Globalization;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Catalog;

/// <summary>
/// Pure, in-memory search / filter / sort over the catalog. Kept free of I/O so the UI can call it on
/// every keystroke and tests can exercise it directly.
/// </summary>
public static class CatalogQueryEngine
{
    private static readonly CompareInfo Comparer = CultureInfo.InvariantCulture.CompareInfo;
    private const CompareOptions SearchOptions = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public static IReadOnlyList<Post> Apply(CatalogSnapshot snapshot, CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return Apply(snapshot.Posts, snapshot.TrendingRanks, query);
    }

    public static IReadOnlyList<Post> Apply(
        IEnumerable<Post> posts,
        IReadOnlyDictionary<string, int> trendingRanks,
        CatalogQuery query)
    {
        ArgumentNullException.ThrowIfNull(posts);
        ArgumentNullException.ThrowIfNull(trendingRanks);
        ArgumentNullException.ThrowIfNull(query);

        var terms = (query.SearchText ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var filtered = posts.Where(post =>
            IsListed(post)
            && (query.Type is null || post.Type == query.Type)
            && (query.Device is null || post.Devices.Contains(query.Device.Value))
            && MatchesDuration(post, query)
            && MatchesSearch(post, terms));

        return Sort(filtered, query.Sort, trendingRanks).ToList();
    }

    /// <summary>Only boot and suspend videos are listed; removed or unknown kinds never are.</summary>
    public static bool IsListed(Post post) => post.Type is VideoType.BootVideo or VideoType.SuspendVideo;

    private static bool MatchesDuration(Post post, CatalogQuery query)
    {
        if (query.MinDuration is null && query.MaxDuration is null)
        {
            return true;
        }

        return post.Duration is { } duration
            && (query.MinDuration is null || duration >= query.MinDuration)
            && (query.MaxDuration is null || duration <= query.MaxDuration);
    }

    private static bool MatchesSearch(Post post, string[] terms) =>
        terms.All(term => Contains(post.Title, term) || Contains(post.Author.Name, term));

    private static bool Contains(string text, string term) => Comparer.IndexOf(text, term, SearchOptions) >= 0;

    private static IOrderedEnumerable<Post> Sort(IEnumerable<Post> posts, CatalogSort sort, IReadOnlyDictionary<string, int> trendingRanks)
    {
        var ordered = sort switch
        {
            CatalogSort.Trending => posts
                .OrderBy(post => trendingRanks.TryGetValue(post.Id, out var rank) ? rank : int.MaxValue)
                .ThenByDescending(post => post.Downloads),
            CatalogSort.MostDownloaded => posts.OrderByDescending(post => post.Downloads).ThenByDescending(post => post.Likes),
            CatalogSort.MostLiked => posts.OrderByDescending(post => post.Likes).ThenByDescending(post => post.Downloads),
            CatalogSort.Newest => posts.OrderByDescending(post => post.CreatedAt),
            CatalogSort.Oldest => posts.OrderBy(post => post.CreatedAt),
            _ => throw new ArgumentOutOfRangeException(nameof(sort), sort, "Unknown sort order."),
        };

        // Deterministic tie-breakers keep the grid stable between refreshes.
        return sort == CatalogSort.Oldest
            ? ordered.ThenBy(post => post.Id, StringComparer.Ordinal)
            : ordered.ThenByDescending(post => post.CreatedAt).ThenBy(post => post.Id, StringComparer.Ordinal);
    }
}
