using System.Net;
using BootVideoManager.Core.Api;

namespace BootVideoManager.Core.Tests.Api;

public class RepoApiClientTests
{
    private static readonly RepoApiOptions Options = new() { UserAgent = "BootVideoManager-Tests/1.0 (+https://example.org)" };
    private static readonly DateTimeOffset LastModified = new(2026, 9, 16, 17, 57, 42, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (RepoApiClient Client, StubHttpHandler Handler) Create(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var handler = new StubHttpHandler(respond);
        return (new RepoApiClient(new HttpClient(handler), Options), handler);
    }

    [Fact]
    public async Task GetCatalog_RequestsAllPostsWithIdentifiableUserAgent()
    {
        var (client, handler) = Create((_, _) => StubHttpHandler.Json(Fixture.Bytes("posts_all.json"), lastModified: LastModified));

        var download = await client.GetCatalogAsync(null, Ct);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri("https://steamdeckrepo.com/api/posts/all"), request.Uri);
        Assert.Equal(Options.UserAgent, request.UserAgent);
        Assert.Contains("application/json", request.Accept, StringComparison.Ordinal);
        Assert.Null(request.IfModifiedSince);
        Assert.False(download.IsNotModified);
        Assert.Equal(Fixture.Bytes("posts_all.json"), download.Content);
        Assert.Equal(LastModified, download.LastModified);
    }

    [Fact]
    public async Task GetCatalog_IsConditional_AndReports304AsNotModified()
    {
        var (client, handler) = Create((_, _) => StubHttpHandler.Status(HttpStatusCode.NotModified));

        var download = await client.GetCatalogAsync(LastModified, Ct);

        Assert.Equal(LastModified, Assert.Single(handler.Requests).IfModifiedSince);
        Assert.True(download.IsNotModified);
        Assert.Empty(download.Content);
        Assert.Equal(LastModified, download.LastModified);
    }

    [Fact]
    public async Task GetCatalog_429_ThrowsRateLimitedWithRetryAfter()
    {
        var (client, _) = Create((_, _) => StubHttpHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(42)));

        var ex = await Assert.ThrowsAsync<RepoApiException>(() => client.GetCatalogAsync(null, Ct));

        Assert.Equal(RepoApiErrorKind.RateLimited, ex.Kind);
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task GetCatalog_ErrorStatus_ThrowsHttpError(HttpStatusCode status)
    {
        var (client, _) = Create((_, _) => StubHttpHandler.Status(status));

        var ex = await Assert.ThrowsAsync<RepoApiException>(() => client.GetCatalogAsync(null, Ct));

        Assert.Equal(RepoApiErrorKind.HttpError, ex.Kind);
        Assert.Equal(status, ex.StatusCode);
    }

    [Fact]
    public async Task GetCatalog_ConnectionFailure_ThrowsNetwork()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("No such host is known."));

        var ex = await Assert.ThrowsAsync<RepoApiException>(() => client.GetCatalogAsync(null, Ct));

        Assert.Equal(RepoApiErrorKind.Network, ex.Kind);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task GetCatalog_ServerTooSlow_ThrowsTimeout()
    {
        var (client, _) = Create((_, _) => throw new TaskCanceledException("timeout", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<RepoApiException>(() => client.GetCatalogAsync(null, Ct));

        Assert.Equal(RepoApiErrorKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task GetCatalog_UserCancellation_IsNotWrapped()
    {
        var (client, _) = Create((_, _) => StubHttpHandler.Json(Fixture.Bytes("posts_all.json")));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetCatalogAsync(null, cancelled.Token));
    }

    [Fact]
    public async Task GetTrendingPostIds_RequestsLargestTrendingPage_AndDeduplicates()
    {
        var (client, handler) = Create((_, _) => StubHttpHandler.Json(Fixture.Bytes("posts_page.json")));

        var ids = await client.GetTrendingPostIdsAsync(Ct);

        Assert.Equal(
            new Uri("https://steamdeckrepo.com/api/posts?sort=trending&per_page=100&page=1"),
            Assert.Single(handler.Requests).Uri);
        Assert.Equal(["BBBBB", "AAAAA"], ids);
    }

    [Fact]
    public async Task GetTrendingPostIds_HtmlInsteadOfJson_ThrowsInvalidResponse()
    {
        var (client, _) = Create((_, _) => StubHttpHandler.Json(Fixture.Utf8("<html>redesigned</html>")));

        var ex = await Assert.ThrowsAsync<RepoApiException>(() => client.GetTrendingPostIdsAsync(Ct));

        Assert.Equal(RepoApiErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public void GetDownloadUri_PointsToOfficialDownloadRoute_AndEscapesId()
    {
        var (client, handler) = Create((_, _) => throw new InvalidOperationException("no request expected"));

        Assert.Equal(new Uri("https://steamdeckrepo.com/post/download/MnZgE"), client.GetDownloadUri("MnZgE"));
        Assert.Equal("https://steamdeckrepo.com/post/download/a%2F..%2Fb", client.GetDownloadUri("a/../b").AbsoluteUri);
        Assert.Empty(handler.Requests);
    }
}
