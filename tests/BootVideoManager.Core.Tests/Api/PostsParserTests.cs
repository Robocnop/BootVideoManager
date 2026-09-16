using BootVideoManager.Core.Api;
using BootVideoManager.Core.Models;

namespace BootVideoManager.Core.Tests.Api;

public class PostsParserTests
{
    private static ParsedCatalog ParseFixture() => PostsParser.ParseAll(Fixture.Bytes("posts_all.json"));

    [Fact]
    public void ParseAll_MapsEveryFieldOfACompletePost()
    {
        var post = ParseFixture().Posts.Single(p => p.Id == "AAAAA");

        Assert.Equal("star_wars_intro", post.Slug);
        Assert.Equal("Star Wars Intro", post.Title);
        Assert.Equal("Synthetic description.", post.Description);
        Assert.Equal(new PostAuthor(2, "AuthorOne", new Uri("https://avatars.example.com/one.jpg")), post.Author);
        Assert.Equal(new Uri("https://cdn.steamdeckrepo.com/thumbnails/aaaaa.png"), post.ThumbnailUri);
        Assert.Equal(new Uri("https://cdn.steamdeckrepo.com/videos/aaaaa.webm"), post.VideoUri);
        Assert.Equal(new Uri("https://cdn.steamdeckrepo.com/previews/aaaaa.mp4"), post.PreviewUri);
        Assert.Equal(TimeSpan.FromSeconds(10), post.Duration);
        Assert.Equal(new DateTimeOffset(2022, 10, 6, 16, 34, 50, TimeSpan.Zero), post.CreatedAt);
        Assert.Equal(new DateTimeOffset(2022, 10, 7, 8, 0, 0, TimeSpan.Zero), post.UpdatedAt);
        Assert.Equal(224, post.Likes);
        Assert.Equal(34826, post.Downloads);
        Assert.Equal(VideoType.BootVideo, post.Type);
        Assert.Equal([DeviceTag.SteamDeck], post.Devices);
    }

    [Fact]
    public void ParseAll_UpgradesSitePageLinksToHttps()
    {
        var post = ParseFixture().Posts.Single(p => p.Id == "AAAAA");

        Assert.Equal(new Uri("https://steamdeckrepo.com/post/AAAAA/star_wars_intro"), post.PageUri);
    }

    [Fact]
    public void ParseAll_ToleratesNullsStringNumbersAndDuplicateDevices()
    {
        var post = ParseFixture().Posts.Single(p => p.Id == "BBBBB");

        Assert.Equal("Pokémon Sleep", post.Title);
        Assert.Equal(string.Empty, post.Description);
        Assert.Null(post.Author.AvatarUri);
        Assert.Null(post.Duration);
        Assert.Null(post.PreviewUri);
        Assert.Equal(12, post.Likes);
        Assert.Equal(post.CreatedAt, post.UpdatedAt);
        Assert.Equal(new Uri("https://steamdeckrepo.com/post/BBBBB/pokemon_sleep"), post.PageUri);
        Assert.Equal(VideoType.SuspendVideo, post.Type);
        Assert.Equal([DeviceTag.SteamDeck, DeviceTag.SteamMachine], post.Devices);
    }

    [Fact]
    public void ParseAll_KeepsRemovedAndUnknownKindsForTheQueryLayerToFilter()
    {
        var posts = ParseFixture().Posts;

        Assert.Equal(VideoType.Removed, posts.Single(p => p.Id == "CCCCC").Type);
        Assert.Equal(VideoType.Unknown, posts.Single(p => p.Id == "DDDDD").Type);
    }

    [Fact]
    public void ParseAll_AppliesSafeDefaultsToDegradedPost()
    {
        var post = ParseFixture().Posts.Single(p => p.Id == "DDDDD");

        Assert.Equal("DDDDD", post.Slug);
        Assert.Equal("DDDDD", post.Title);
        Assert.Equal(new PostAuthor(0, string.Empty, null), post.Author);
        Assert.Null(post.ThumbnailUri); // ftp:// is rejected
        Assert.Null(post.PreviewUri); // not a URL
        Assert.Equal(TimeSpan.FromSeconds(7.5), post.Duration);
        Assert.Equal(DateTimeOffset.UnixEpoch, post.CreatedAt);
        Assert.Equal(new Uri("https://example.org/elsewhere"), post.PageUri); // foreign hosts are left untouched
        Assert.Equal(0, post.Likes); // negative counters are clamped
        Assert.Equal([DeviceTag.SteamFrame, DeviceTag.Unknown], post.Devices);
    }

    [Fact]
    public void ParseAll_SkipsUnusableEntriesWithoutFailing()
    {
        var catalog = ParseFixture();

        Assert.Equal(["AAAAA", "BBBBB", "CCCCC", "DDDDD"], catalog.Posts.Select(p => p.Id));
        Assert.Equal(4, catalog.SkippedCount); // no video, bad likes type, non-object, no id
    }

    [Fact]
    public void ParseAll_ReadsServerCacheTimestamp()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1789577865), ParseFixture().ServerCachedAt);
    }

    [Theory]
    [InlineData("<!DOCTYPE html><html><body>Maintenance</body></html>")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"posts\": {}}")]
    [InlineData("{\"data\": []}")]
    public void ParseAll_UnrecognisedEnvelope_ThrowsInvalidResponse(string body)
    {
        var ex = Assert.Throws<RepoApiException>(() => PostsParser.ParseAll(Fixture.Utf8(body)));

        Assert.Equal(RepoApiErrorKind.InvalidResponse, ex.Kind);
    }

    [Fact]
    public void ParseAll_EmptyCatalog_IsValid()
    {
        var catalog = PostsParser.ParseAll(Fixture.Utf8("{\"posts\": []}"));

        Assert.Empty(catalog.Posts);
        Assert.Equal(0, catalog.SkippedCount);
        Assert.Null(catalog.ServerCachedAt);
    }

    [Fact]
    public void ParsePage_ReturnsPostsInServerOrder()
    {
        var posts = PostsParser.ParsePage(Fixture.Bytes("posts_page.json"));

        Assert.Equal(["BBBBB", "AAAAA", "BBBBB"], posts.Select(p => p.Id));
    }
}
