using BootVideoManager.Core.Api;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Catalog;

/// <summary>Where the data of a <see cref="CatalogSnapshot"/> comes from.</summary>
public enum CatalogSource
{
    /// <summary>Served from disk without contacting the server (cache still fresh).</summary>
    Cache,

    /// <summary>Confirmed (304) or downloaded (200) from the server during this load.</summary>
    Network,

    /// <summary>The server could not be used; last known catalog shown instead.</summary>
    StaleCache,
}

/// <summary>Immutable view of the catalog at a point in time.</summary>
public sealed class CatalogSnapshot
{
    internal CatalogSnapshot(
        IReadOnlyList<Post> posts,
        CatalogCacheMetadata metadata,
        CatalogSource source,
        int skippedEntries,
        RepoApiException? refreshError,
        Exception? cacheWriteError)
    {
        Posts = posts;
        FetchedAt = metadata.FetchedAt;
        Source = source;
        SkippedEntries = skippedEntries;
        RefreshError = refreshError;
        CacheWriteError = cacheWriteError;

        var ranks = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var id in metadata.TrendingPostIds)
        {
            ranks.TryAdd(id, ranks.Count);
        }

        TrendingRanks = ranks;
    }

    /// <summary>All valid posts, including removed/unknown kinds (filtered out by <see cref="CatalogQueryEngine"/>).</summary>
    public IReadOnlyList<Post> Posts { get; }

    /// <summary>Post id → trending position (0 = hottest).</summary>
    public IReadOnlyDictionary<string, int> TrendingRanks { get; }

    /// <summary>Last time the server confirmed this data.</summary>
    public DateTimeOffset FetchedAt { get; }

    public CatalogSource Source { get; }

    /// <summary>Malformed entries ignored while parsing.</summary>
    public int SkippedEntries { get; }

    /// <summary>Why the refresh failed when <see cref="Source"/> is <see cref="CatalogSource.StaleCache"/>.</summary>
    public RepoApiException? RefreshError { get; }

    /// <summary>Set when the data is fine but could not be persisted (next start will download again).</summary>
    public Exception? CacheWriteError { get; }
}
