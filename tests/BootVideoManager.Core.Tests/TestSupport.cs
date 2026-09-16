using System.Net;
using System.Net.Http.Headers;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Tests;

/// <summary>Access to JSON samples copied next to the test assembly.</summary>
internal static class Fixture
{
    public static byte[] Bytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    public static byte[] Utf8(string text) => System.Text.Encoding.UTF8.GetBytes(text);
}

/// <summary>What a stubbed HTTP request looked like (captured before the message is disposed).</summary>
internal sealed record RecordedRequest(Uri? Uri, string? UserAgent, DateTimeOffset? IfModifiedSince, string Accept);

/// <summary>Scriptable in-memory HTTP transport.</summary>
internal sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> _respond;

    public StubHttpHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public StubHttpHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
        : this((request, call, _) => Task.FromResult(respond(request, call)))
    {
    }

    public List<RecordedRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(new RecordedRequest(
            request.RequestUri,
            request.Headers.TryGetValues("User-Agent", out var agents) ? string.Join(" ", agents) : null,
            request.Headers.IfModifiedSince,
            request.Headers.Accept.ToString()));
        return _respond(request, Requests.Count, cancellationToken);
    }

    public static HttpResponseMessage Json(byte[] body, HttpStatusCode status = HttpStatusCode.OK, DateTimeOffset? lastModified = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Headers.LastModified = lastModified;
        return new HttpResponseMessage(status) { Content = content };
    }

    public static HttpResponseMessage Status(HttpStatusCode status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delay);
        }

        return response;
    }
}

/// <summary>Builds <see cref="Post"/> instances with sensible defaults for query tests.</summary>
internal static class PostFactory
{
    public static Post Create(
        string id,
        string title = "Title",
        string author = "Author",
        VideoType type = VideoType.BootVideo,
        DeviceTag[]? devices = null,
        int? durationSeconds = 10,
        int likes = 0,
        int downloads = 0,
        DateTimeOffset? createdAt = null) =>
        new(
            Id: id,
            Slug: id.ToLowerInvariant(),
            Title: title,
            Description: string.Empty,
            Author: new PostAuthor(1, author, null),
            ThumbnailUri: null,
            VideoUri: new Uri($"https://cdn.steamdeckrepo.com/videos/{id}.webm"),
            PreviewUri: null,
            Duration: durationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            CreatedAt: createdAt ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            UpdatedAt: createdAt ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            PageUri: new Uri($"https://steamdeckrepo.com/post/{id}"),
            Likes: likes,
            Downloads: downloads,
            Type: type,
            Devices: devices ?? [DeviceTag.SteamDeck]);
}

/// <summary>Scriptable <see cref="IRepoApiClient"/> for service tests.</summary>
internal sealed class FakeRepoApiClient : IRepoApiClient
{
    public Queue<Func<CatalogDownload>> CatalogResponses { get; } = new();

    public Func<IReadOnlyList<string>> TrendingResponse { get; set; } = () => ["BBBBB", "AAAAA"];

    public List<DateTimeOffset?> CatalogRequests { get; } = [];

    public int TrendingRequests { get; private set; }

    public void EnqueueCatalog(byte[] content, DateTimeOffset? lastModified) =>
        CatalogResponses.Enqueue(() => new CatalogDownload(false, content, lastModified));

    public void EnqueueNotModified() =>
        CatalogResponses.Enqueue(() => new CatalogDownload(true, [], null));

    public void EnqueueFailure(RepoApiErrorKind kind) =>
        CatalogResponses.Enqueue(() => throw new RepoApiException(kind, "simulated failure"));

    public Task<CatalogDownload> GetCatalogAsync(DateTimeOffset? ifModifiedSince, CancellationToken cancellationToken = default)
    {
        CatalogRequests.Add(ifModifiedSince);
        if (CatalogResponses.Count == 0)
        {
            throw new InvalidOperationException("Unexpected catalog request.");
        }

        return Task.FromResult(CatalogResponses.Dequeue()());
    }

    public Task<IReadOnlyList<string>> GetTrendingPostIdsAsync(CancellationToken cancellationToken = default)
    {
        TrendingRequests++;
        return Task.FromResult(TrendingResponse());
    }

    public Uri GetDownloadUri(string postId) => new($"https://steamdeckrepo.com/post/download/{postId}");
}
