using System.IO.Abstractions.TestingHelpers;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Catalog;
using Microsoft.Extensions.Time.Testing;

namespace BootVideoManager.Core.Tests.Catalog;

public sealed class CatalogServiceTests : IDisposable
{
    private static readonly DateTimeOffset ServerLastModified = new(2026, 9, 16, 17, 0, 0, TimeSpan.Zero);
    private static readonly string CacheDirectory = MockUnixSupport.Path(@"C:\cache\BootVideoManager");

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 16, 18, 0, 0, TimeSpan.Zero));
    private readonly FakeRepoApiClient _api = new();
    private readonly CatalogService _service;

    public CatalogServiceTests()
    {
        _service = new CatalogService(_api, new CatalogCache(_fileSystem, CacheDirectory), _time);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string ContentPath => _fileSystem.Path.Combine(CacheDirectory, CatalogCache.ContentFileName);

    public void Dispose() => _service.Dispose();

    private async Task<CatalogSnapshot> LoadInitialCatalogAsync()
    {
        _api.EnqueueCatalog(Fixture.Bytes("posts_all.json"), ServerLastModified);
        return await _service.LoadAsync(cancellationToken: Ct);
    }

    [Fact]
    public async Task FirstLoad_DownloadsParsesAndCaches()
    {
        var snapshot = await LoadInitialCatalogAsync();

        Assert.Equal(CatalogSource.Network, snapshot.Source);
        Assert.Equal(4, snapshot.Posts.Count);
        Assert.Equal(4, snapshot.SkippedEntries);
        Assert.Equal(_time.GetUtcNow(), snapshot.FetchedAt);
        Assert.Null(snapshot.RefreshError);
        Assert.Null(snapshot.CacheWriteError);
        Assert.Equal([null], _api.CatalogRequests);
        Assert.Equal(0, snapshot.TrendingRanks["BBBBB"]);
        Assert.Equal(1, snapshot.TrendingRanks["AAAAA"]);
        Assert.Equal(Fixture.Bytes("posts_all.json"), _fileSystem.File.ReadAllBytes(ContentPath));
        Assert.Same(snapshot, _service.Current);
    }

    [Fact]
    public async Task LoadWithinRefreshInterval_UsesDiskCacheWithoutAnyRequest()
    {
        await LoadInitialCatalogAsync();
        _time.Advance(TimeSpan.FromMinutes(30));

        var snapshot = await _service.LoadAsync(cancellationToken: Ct);

        Assert.Equal(CatalogSource.Cache, snapshot.Source);
        Assert.Equal(4, snapshot.Posts.Count);
        Assert.Equal(0, snapshot.TrendingRanks["BBBBB"]);
        Assert.Single(_api.CatalogRequests);
        Assert.Equal(1, _api.TrendingRequests);
    }

    [Fact]
    public async Task LoadAfterRefreshInterval_IsConditional_And304KeepsCachedCatalog()
    {
        await LoadInitialCatalogAsync();
        _time.Advance(TimeSpan.FromHours(2));
        _api.EnqueueNotModified();

        var snapshot = await _service.LoadAsync(cancellationToken: Ct);

        Assert.Equal(CatalogSource.Network, snapshot.Source);
        Assert.Equal(4, snapshot.Posts.Count);
        Assert.Equal(ServerLastModified, _api.CatalogRequests[1]);
        Assert.Equal(_time.GetUtcNow(), snapshot.FetchedAt);

        // The 304 renewed the cache lifetime: the next load stays offline.
        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(CatalogSource.Cache, (await _service.LoadAsync(cancellationToken: Ct)).Source);
        Assert.Equal(2, _api.CatalogRequests.Count);
    }

    [Fact]
    public async Task ChangedCatalog_ReplacesCache()
    {
        await LoadInitialCatalogAsync();
        _time.Advance(TimeSpan.FromHours(2));
        var updated = Fixture.Utf8("""{"posts":[{"id":"ZZZZZ","video":"https://cdn.steamdeckrepo.com/videos/z.webm","type":"boot_video"}]}""");
        _api.EnqueueCatalog(updated, ServerLastModified.AddHours(1));

        var snapshot = await _service.LoadAsync(cancellationToken: Ct);

        Assert.Equal("ZZZZZ", Assert.Single(snapshot.Posts).Id);
        Assert.Equal(updated, _fileSystem.File.ReadAllBytes(ContentPath));
    }

    [Fact]
    public async Task ForcedRefresh_IsThrottledToProtectTheServer()
    {
        await LoadInitialCatalogAsync();

        _time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(CatalogSource.Cache, (await _service.LoadAsync(forceRefresh: true, Ct)).Source);
        Assert.Single(_api.CatalogRequests);

        _time.Advance(TimeSpan.FromMinutes(2));
        _api.EnqueueNotModified();
        Assert.Equal(CatalogSource.Network, (await _service.LoadAsync(forceRefresh: true, Ct)).Source);
        Assert.Equal(2, _api.CatalogRequests.Count);
    }

    [Theory]
    [InlineData(RepoApiErrorKind.Network)]
    [InlineData(RepoApiErrorKind.Timeout)]
    [InlineData(RepoApiErrorKind.RateLimited)]
    [InlineData(RepoApiErrorKind.HttpError)]
    public async Task ServerUnavailable_WithCache_ServesStaleCatalogAndReportsWhy(RepoApiErrorKind kind)
    {
        await LoadInitialCatalogAsync();
        _time.Advance(TimeSpan.FromHours(2));
        _api.EnqueueFailure(kind);

        var snapshot = await _service.LoadAsync(cancellationToken: Ct);

        Assert.Equal(CatalogSource.StaleCache, snapshot.Source);
        Assert.Equal(kind, snapshot.RefreshError?.Kind);
        Assert.Equal(4, snapshot.Posts.Count);
    }

    [Fact]
    public async Task ServerUnavailable_WithoutCache_Throws()
    {
        _api.EnqueueFailure(RepoApiErrorKind.Network);

        var ex = await Assert.ThrowsAsync<RepoApiException>(() => _service.LoadAsync(cancellationToken: Ct));

        Assert.Equal(RepoApiErrorKind.Network, ex.Kind);
        Assert.False(_fileSystem.File.Exists(ContentPath));
    }

    [Fact]
    public async Task UnreadableNewPayload_NeverOverwritesGoodCache()
    {
        await LoadInitialCatalogAsync();
        _time.Advance(TimeSpan.FromHours(2));
        _api.EnqueueCatalog(Fixture.Utf8("<html>Site redesign</html>"), ServerLastModified.AddHours(1));

        var snapshot = await _service.LoadAsync(cancellationToken: Ct);

        Assert.Equal(CatalogSource.StaleCache, snapshot.Source);
        Assert.Equal(RepoApiErrorKind.InvalidResponse, snapshot.RefreshError?.Kind);
        Assert.Equal(Fixture.Bytes("posts_all.json"), _fileSystem.File.ReadAllBytes(ContentPath));
    }

    [Fact]
    public async Task TrendingFailure_IsNotFatal()
    {
        _api.TrendingResponse = () => throw new RepoApiException(RepoApiErrorKind.RateLimited, "slow down");

        var snapshot = await LoadInitialCatalogAsync();

        Assert.Equal(CatalogSource.Network, snapshot.Source);
        Assert.Empty(snapshot.TrendingRanks);
    }

    [Fact]
    public async Task CorruptedCache_IsIgnoredAndDownloadedAgain()
    {
        await LoadInitialCatalogAsync();
        _fileSystem.File.WriteAllText(ContentPath, "{ truncated");
        _api.EnqueueCatalog(Fixture.Bytes("posts_all.json"), ServerLastModified);

        var snapshot = await _service.LoadAsync(cancellationToken: Ct);

        Assert.Equal(CatalogSource.Network, snapshot.Source);
        Assert.Null(_api.CatalogRequests[1]); // no conditional request against a broken cache
        Assert.Equal(4, snapshot.Posts.Count);
    }
}
