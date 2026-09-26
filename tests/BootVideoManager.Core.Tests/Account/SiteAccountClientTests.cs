using System.IO.Abstractions.TestingHelpers;
using System.Net;
using System.Text;
using BootVideoManager.Core.Account;
using BootVideoManager.Core.Api;

namespace BootVideoManager.Core.Tests.Account;

public sealed class SiteAccountClientTests
{
    private static readonly RepoApiOptions Options = new()
    {
        Retry = new RetryPolicyOptions { MaxAttempts = 1, BaseDelay = TimeSpan.Zero },
    };

    private static readonly SiteCookie[] SignedInCookies =
    [
        new() { Name = "steam_deck_repo_session", Value = "session-value", Domain = "steamdeckrepo.com" },
        new() { Name = "XSRF-TOKEN", Value = "abc%3D%3D", Domain = "steamdeckrepo.com" },
        new() { Name = "steamLoginSecure", Value = "steam-secret", Domain = "steamcommunity.com" },
    ];

    [Fact]
    public async Task GetCurrentUser_ReadsTheInertiaUserAndSendsOnlySiteCookies()
    {
        string? cookieHeader = null;
        using var handler = new StubHttpHandler((request, _) =>
        {
            cookieHeader = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(";", values) : null;
            return Page("""{"component":"Help","props":{"user":{"id":42,"steam_name":"Tom & Co","steam_avatar":"https://avatars.example/a.jpg"}}}""");
        });
        using var client = new SiteAccountClient(Options, handler);
        client.UseCookies(SignedInCookies);

        var user = await client.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new SiteUser(42, "Tom & Co", new Uri("https://avatars.example/a.jpg")), user);
        Assert.NotNull(cookieHeader);
        Assert.Contains("steam_deck_repo_session=session-value", cookieHeader, StringComparison.Ordinal);
        Assert.DoesNotContain("steamLoginSecure", cookieHeader, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetCurrentUser_ReturnsNullForAGuest()
    {
        using var handler = new StubHttpHandler((_, _) => Page("""{"props":{"user":null}}"""));
        using var client = new SiteAccountClient(Options, handler);

        Assert.Null(await client.GetCurrentUserAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetLikedPostIds_FollowsEveryPage()
    {
        using var handler = new StubHttpHandler((request, _) => request.RequestUri!.Query.Contains("page=1", StringComparison.Ordinal)
            ? Page("""{"props":{"data":{"posts":{"data":[{"id":"a1"},{"id":"b2"}],"meta":{"current_page":1,"last_page":2}}}}}""")
            : Page("""{"props":{"data":{"posts":{"data":[{"id":"c3"}],"meta":{"current_page":2,"last_page":2}}}}}"""));
        using var client = new SiteAccountClient(Options, handler);

        var ids = await client.GetLikedPostIdsAsync(7, TestContext.Current.CancellationToken);

        Assert.Equal(["a1", "b2", "c3"], ids);
        Assert.Equal(
            ["https://steamdeckrepo.com/user/7/liked?page=1", "https://steamdeckrepo.com/user/7/liked?page=2"],
            handler.Requests.Select(r => r.Uri!.AbsoluteUri));
    }

    [Fact]
    public async Task ToggleLike_PostsWithTheDecodedXsrfTokenThenReadsTheState()
    {
        string? xsrf = null;
        using var handler = new StubHttpHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                xsrf = request.Headers.GetValues("X-XSRF-TOKEN").Single();
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://steamdeckrepo.com/post/a1");
                return redirect;
            }

            if (request.RequestUri!.AbsolutePath == "/post/a1")
            {
                // The site sends bare post URLs to their canonical /post/{id}/{slug} address.
                var canonical = new HttpResponseMessage(HttpStatusCode.Found);
                canonical.Headers.Location = new Uri("https://steamdeckrepo.com/post/a1/my_video");
                return canonical;
            }

            return Page("""{"props":{"post":{"data":{"id":"a1","liked":true,"likes":"13"}}}}""");
        });
        using var client = new SiteAccountClient(Options, handler);
        client.UseCookies(SignedInCookies);

        var state = await client.ToggleLikeAsync("a1", TestContext.Current.CancellationToken);

        Assert.Equal(new LikeState(true, 13), state);
        Assert.Equal("abc==", xsrf);
        Assert.Equal(
            ["https://steamdeckrepo.com/post/a1/like", "https://steamdeckrepo.com/post/a1", "https://steamdeckrepo.com/post/a1/my_video"],
            handler.Requests.Select(r => r.Uri!.AbsoluteUri));
    }

    [Theory]
    [InlineData(419)]
    [InlineData(401)]
    public async Task ToggleLike_ReportsAnExpiredSession(int status)
    {
        using var handler = new StubHttpHandler((_, _) => new HttpResponseMessage((HttpStatusCode)status));
        using var client = new SiteAccountClient(Options, handler);

        var error = await Assert.ThrowsAsync<RepoApiException>(() => client.ToggleLikeAsync("a1", TestContext.Current.CancellationToken));

        Assert.Equal(RepoApiErrorKind.SignedOut, error.Kind);
    }

    [Fact]
    public async Task RenewedCookiesAreKeptAndReported()
    {
        using var handler = new StubHttpHandler((_, _) =>
        {
            var response = Page("""{"props":{"user":null}}""");
            response.Headers.Add("Set-Cookie", "XSRF-TOKEN=renewed; path=/; secure");
            return response;
        });
        using var client = new SiteAccountClient(Options, handler);
        client.UseCookies(SignedInCookies);
        var changed = 0;
        client.CookiesChanged += (_, _) => changed++;

        await client.GetCurrentUserAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, changed);
        Assert.Equal("renewed", client.ExportCookies().Single(c => c.Name == "XSRF-TOKEN").Value);
        Assert.DoesNotContain(client.ExportCookies(), c => c.Name == "steamLoginSecure");
    }

    [Fact]
    public async Task APageWithoutInertiaDataIsAnInvalidResponse()
    {
        using var handler = new StubHttpHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>maintenance</html>") });
        using var client = new SiteAccountClient(Options, handler);

        var error = await Assert.ThrowsAsync<RepoApiException>(() => client.GetCurrentUserAsync(TestContext.Current.CancellationToken));

        Assert.Equal(RepoApiErrorKind.InvalidResponse, error.Kind);
    }

    [Theory]
    [InlineData("https://steamdeckrepo.com/", true)]
    [InlineData("https://steamdeckrepo.com/user/42/liked", true)]
    [InlineData("https://steamdeckrepo.com/login", false)]
    [InlineData("https://steamdeckrepo.com/auth/steam/handle?openid.mode=id_res", false)]
    [InlineData("https://steamcommunity.com/openid/login", false)]
    public void IsBackOnSite_WaitsForTheSiteAfterSteam(string url, bool expected)
    {
        using var client = new SiteAccountClient(Options, new StubHttpHandler((_, _) => new HttpResponseMessage()));

        Assert.Equal(expected, client.IsBackOnSite(new Uri(url)));
    }

    [Fact]
    public void SessionStore_RoundTripsAndTreatsGarbageAsSignedOut()
    {
        var fileSystem = new MockFileSystem();
        var store = new SiteSessionStore(fileSystem, @"C:\config\account.dat", encrypt: false);
        Assert.Null(store.Load());

        store.Save(new SiteSession { Cookies = [.. SignedInCookies.Take(2)], UserId = 42, UserName = "Tom", AvatarUrl = "https://avatars.example/a.jpg" });
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(new SiteUser(42, "Tom", new Uri("https://avatars.example/a.jpg")), loaded.User);
        Assert.Equal("session-value", loaded.Cookies[0].Value);

        fileSystem.File.WriteAllText(@"C:\config\account.dat", "not json");
        Assert.Null(store.Load());

        store.Delete();
        Assert.False(fileSystem.File.Exists(@"C:\config\account.dat"));
    }

    private static HttpResponseMessage Page(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(
            $"<html><body><div id=\"app\" data-page=\"{WebUtility.HtmlEncode(json)}\"></div></body></html>",
            Encoding.UTF8,
            "text/html"),
    };
}
