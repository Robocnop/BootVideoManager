using BootVideoManager.Core.Api;

namespace BootVideoManager.Core.Catalog;

/// <summary>Refresh policy of the catalog.</summary>
public sealed record CatalogOptions
{
    /// <summary>
    /// A cached catalog younger than this is used without any request. The server itself only regenerates
    /// the catalog about once an hour, so polling faster is pointless.
    /// </summary>
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>Even a user-forced refresh is skipped if the last one is more recent than this.</summary>
    public TimeSpan MinimumForcedRefreshInterval { get; init; } = TimeSpan.FromMinutes(1);
}

/// <summary>
/// Loads the catalog with a cache-first strategy: disk cache → conditional request → offline fallback.
/// A payload that fails to parse never replaces a good cache.
/// </summary>
public sealed class CatalogService : IDisposable
{
    private readonly IRepoApiClient _api;
    private readonly CatalogCache _cache;
    private readonly TimeProvider _timeProvider;
    private readonly CatalogOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CatalogService(IRepoApiClient api, CatalogCache cache, TimeProvider timeProvider, CatalogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _api = api;
        _cache = cache;
        _timeProvider = timeProvider;
        _options = options ?? new CatalogOptions();
    }

    /// <summary>Last snapshot returned by <see cref="LoadAsync"/>.</summary>
    public CatalogSnapshot? Current { get; private set; }

    /// <summary>Returns the catalog, contacting the server only when the cache is due for a refresh.</summary>
    /// <param name="forceRefresh">User asked for a refresh; still throttled by <see cref="CatalogOptions.MinimumForcedRefreshInterval"/>.</param>
    /// <param name="cancellationToken">Cancels the network part.</param>
    /// <exception cref="RepoApiException">Only when the server fails <b>and</b> no usable cache exists.</exception>
    public async Task<CatalogSnapshot> LoadAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _timeProvider.GetUtcNow();
            var cached = _cache.TryLoad();

            if (cached is not null)
            {
                var maxAge = forceRefresh ? _options.MinimumForcedRefreshInterval : _options.RefreshInterval;
                var age = now - cached.Metadata.FetchedAt;
                if (age >= TimeSpan.Zero && age < maxAge)
                {
                    if (TryParse(cached.Content) is { } fresh)
                    {
                        return Current = new CatalogSnapshot(fresh.Posts, cached.Metadata, CatalogSource.Cache, fresh.SkippedCount, null, null);
                    }

                    cached = null; // Corrupted cache: behave as if there was none.
                }
            }

            return Current = await RefreshAsync(cached, now, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async Task<CatalogSnapshot> RefreshAsync(CachedCatalog? cached, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ParsedCatalog? cachedCatalog = null;
        CatalogDownload download;
        try
        {
            download = await _api.GetCatalogAsync(cached?.Metadata.LastModified, cancellationToken).ConfigureAwait(false);

            if (download.IsNotModified)
            {
                cachedCatalog = cached is null ? null : TryParse(cached.Content);
                if (cached is not null && cachedCatalog is not null)
                {
                    var confirmed = await WithTrendingAsync(cached.Metadata with { FetchedAt = now }, now, cancellationToken).ConfigureAwait(false);
                    var writeError = TryWrite(() => _cache.SaveMetadata(confirmed));
                    return new CatalogSnapshot(cachedCatalog.Posts, confirmed, CatalogSource.Network, cachedCatalog.SkippedCount, null, writeError);
                }

                // 304 but nothing usable locally (should not happen): ask for the full body once.
                download = await _api.GetCatalogAsync(null, cancellationToken).ConfigureAwait(false);
            }

            var parsed = PostsParser.ParseAll(download.Content);
            var metadata = await WithTrendingAsync(
                    new CatalogCacheMetadata
                    {
                        LastModified = download.LastModified,
                        FetchedAt = now,
                        TrendingPostIds = cached?.Metadata.TrendingPostIds ?? [],
                        TrendingFetchedAt = cached?.Metadata.TrendingFetchedAt,
                    },
                    now,
                    cancellationToken)
                .ConfigureAwait(false);

            var saveError = TryWrite(() => _cache.Save(download.Content, metadata));
            return new CatalogSnapshot(parsed.Posts, metadata, CatalogSource.Network, parsed.SkippedCount, null, saveError);
        }
        catch (RepoApiException ex)
        {
            // Offline, rate limited, or the new payload is unreadable: keep serving the last good catalog.
            cachedCatalog ??= cached is null ? null : TryParse(cached.Content);
            if (cached is null || cachedCatalog is null)
            {
                throw;
            }

            return new CatalogSnapshot(cachedCatalog.Posts, cached.Metadata, CatalogSource.StaleCache, cachedCatalog.SkippedCount, ex, null);
        }
    }

    /// <summary>Refreshes the trending order when due. Failures are not fatal: the previous order is kept.</summary>
    private async Task<CatalogCacheMetadata> WithTrendingAsync(CatalogCacheMetadata metadata, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (metadata.TrendingFetchedAt is { } last && now - last < _options.RefreshInterval)
        {
            return metadata;
        }

        try
        {
            var ids = await _api.GetTrendingPostIdsAsync(cancellationToken).ConfigureAwait(false);
            return metadata with { TrendingPostIds = ids, TrendingFetchedAt = now };
        }
        catch (RepoApiException)
        {
            return metadata;
        }
    }

    private static ParsedCatalog? TryParse(byte[] content)
    {
        try
        {
            return PostsParser.ParseAll(content);
        }
        catch (RepoApiException)
        {
            return null;
        }
    }

    private static Exception? TryWrite(Action write)
    {
        try
        {
            write();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex;
        }
    }
}
