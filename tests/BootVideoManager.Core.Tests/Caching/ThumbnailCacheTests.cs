using System.IO.Abstractions.TestingHelpers;
using System.Net;
using BootVideoManager.Core.Api;
using BootVideoManager.Core.Caching;
using Microsoft.Extensions.Time.Testing;

namespace BootVideoManager.Core.Tests.Caching;

public sealed class ThumbnailCacheTests : IDisposable
{
    private static readonly string Directory = MockUnixSupport.Path(@"C:\cache\thumbnails");
    private static readonly Uri Thumbnail = new("https://cdn.steamdeckrepo.com/thumbnails/abc.png");

    private readonly MockFileSystem _fileSystem = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));
    private readonly List<ThumbnailCache> _caches = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _caches.ForEach(c => c.Dispose());

    private ThumbnailCache Create(StubHttpHandler handler, long maxBytes = ThumbnailCache.DefaultMaxBytes)
    {
        var cache = new ThumbnailCache(_fileSystem, Directory, new HttpClient(handler), new RepoApiOptions(), _time, maxBytes);
        _caches.Add(cache);
        return cache;
    }

    [Fact]
    public async Task DownloadsOnce_ThenServesFromDisk()
    {
        var handler = new StubHttpHandler((_, _) => StubHttpHandler.Json([1, 2, 3]));
        var cache = Create(handler);

        var first = await cache.GetAsync(Thumbnail, Ct);
        var second = await cache.GetAsync(Thumbnail, Ct);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.EndsWith(".png", first, StringComparison.Ordinal);
        Assert.Equal([1, 2, 3], _fileSystem.File.ReadAllBytes(first));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ConcurrentRequestsForSameImage_ShareOneDownload()
    {
        var release = new TaskCompletionSource();
        var handler = new StubHttpHandler(async (_, _, _) =>
        {
            await release.Task;
            return StubHttpHandler.Json([9]);
        });
        var cache = Create(handler);

        var a = cache.GetAsync(Thumbnail, Ct);
        var b = cache.GetAsync(Thumbnail, Ct);
        release.SetResult();

        Assert.Equal(await a, await b);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ServerError_ReturnsNull_AndCachesNothing(HttpStatusCode status)
    {
        var cache = Create(new StubHttpHandler((_, _) => StubHttpHandler.Status(status)));

        Assert.Null(await cache.GetAsync(Thumbnail, Ct));
        Assert.False(_fileSystem.Directory.Exists(Directory) && _fileSystem.Directory.EnumerateFiles(Directory).Any());
    }

    [Fact]
    public async Task NetworkFailure_ReturnsNull()
    {
        var cache = Create(new StubHttpHandler((_, _) => throw new HttpRequestException("offline")));

        Assert.Null(await cache.GetAsync(Thumbnail, Ct));
    }

    [Fact]
    public void UnknownExtensions_AreNotTrusted()
    {
        var cache = Create(new StubHttpHandler((_, _) => StubHttpHandler.Status(HttpStatusCode.OK)));

        Assert.EndsWith(".img", cache.PathFor(new Uri("https://cdn.example/thumb.exe")), StringComparison.Ordinal);
        Assert.EndsWith(".jpg", cache.PathFor(new Uri("https://cdn.example/thumb.JPG?size=1")), StringComparison.Ordinal);
    }

    [Fact]
    public void Trim_EvictsLeastRecentlyUsedFilesBeyondBudget()
    {
        var cache = Create(new StubHttpHandler((_, _) => StubHttpHandler.Status(HttpStatusCode.OK)), maxBytes: 100);
        var old = _fileSystem.Path.Combine(Directory, "old.png");
        var recent = _fileSystem.Path.Combine(Directory, "recent.png");
        _fileSystem.AddFile(old, new MockFileData(new byte[60]) { LastAccessTime = new DateTime(2020, 1, 1), LastWriteTime = new DateTime(2020, 1, 1) });
        _fileSystem.AddFile(recent, new MockFileData(new byte[60]) { LastAccessTime = new DateTime(2026, 1, 1), LastWriteTime = new DateTime(2026, 1, 1) });

        cache.Trim();

        Assert.False(_fileSystem.File.Exists(old));
        Assert.True(_fileSystem.File.Exists(recent));
    }
}
