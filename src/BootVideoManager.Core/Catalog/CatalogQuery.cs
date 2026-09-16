using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Catalog;

/// <summary>Sort orders offered by the site.</summary>
public enum CatalogSort
{
    /// <summary>Server trending order first, then most downloaded.</summary>
    Trending,
    MostDownloaded,
    MostLiked,
    Newest,
    Oldest,
}

/// <summary>Search, filter and sort criteria applied locally to the catalog.</summary>
public sealed record CatalogQuery
{
    /// <summary>Space-separated terms; every term must appear in the title or the author name (case and accent insensitive).</summary>
    public string? SearchText { get; init; }

    /// <summary>Restrict to boot or suspend videos; <c>null</c> for both.</summary>
    public VideoType? Type { get; init; }

    /// <summary>Restrict to videos tagged for this device; <c>null</c> for all.</summary>
    public DeviceTag? Device { get; init; }

    /// <summary>Inclusive lower bound; posts of unknown duration are excluded when a bound is set.</summary>
    public TimeSpan? MinDuration { get; init; }

    /// <summary>Inclusive upper bound; posts of unknown duration are excluded when a bound is set.</summary>
    public TimeSpan? MaxDuration { get; init; }

    public CatalogSort Sort { get; init; } = CatalogSort.Trending;
}
