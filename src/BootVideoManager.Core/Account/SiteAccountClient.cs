using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BootVideoManager.Core.Api;

namespace BootVideoManager.Core.Account;

/// <summary>New like state of a post, as the site reports it after a toggle.</summary>
public sealed record LikeState(bool Liked, int Likes);

/// <summary>
/// Account features of steamdeckrepo.com (likes). The site has no public API for them: it is a Laravel + Inertia
/// application authenticated by a session cookie, so this client talks to it like the browser does. Pages are read
/// through the JSON embedded in their <c>data-page</c> attribute; the like button posts to <c>/post/{id}/like</c>
/// with the anti-CSRF token Laravel keeps in the <c>XSRF-TOKEN</c> cookie.
/// </summary>
public sealed partial class SiteAccountClient : IDisposable
{
    /// <summary>Safety net for accounts with a huge number of likes (12 per page on the site).</summary>
    public const int MaxLikedPages = 100;

    private const string XsrfCookieName = "XSRF-TOKEN";

    private readonly HttpClient _http;
    private readonly RepoApiOptions _options;
    private readonly Lock _gate = new();
    private CookieContainer _cookies = new();

    /// <param name="transport">Innermost handler; tests pass a stub. Cookies are handled here, not by the handler.</param>
    public SiteAccountClient(RepoApiOptions options, HttpMessageHandler? transport = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        transport ??= new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            UseCookies = false,
            AllowAutoRedirect = false,
        };
        _http = new HttpClient(new PoliteRetryHandler(options.Retry) { InnerHandler = transport }) { Timeout = options.Timeout };
    }

    /// <summary>Raised when the site renewed its cookies, so the stored session can be refreshed.</summary>
    public event EventHandler? CookiesChanged;

    /// <summary>Where the sign-in browser should start: the site redirects it to Steam's OpenID page.</summary>
    public Uri LoginUri => new(_options.BaseUri, "login");

    /// <summary>Whether a cookie belongs to the site (and not to Steam, also visited while signing in).</summary>
    public bool IsSiteCookieDomain(string domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var host = _options.BaseUri.Host;
        var bare = domain.TrimStart('.');
        return bare.Equals(host, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + bare, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether the sign-in browser is back on the site after Steam (any page but the login redirects).</summary>
    public bool IsBackOnSite(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.Host.Equals(_options.BaseUri.Host, StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            && !uri.AbsolutePath.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Replaces the session cookies (after sign-in, or when restoring a saved session).</summary>
    public void UseCookies(IEnumerable<SiteCookie> cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        var container = new CookieContainer();
        foreach (var cookie in cookies.Where(c => IsSiteCookieDomain(c.Domain)))
        {
            try
            {
                container.Add(new Cookie(cookie.Name, cookie.Value, string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path, cookie.Domain)
                {
                    Expires = cookie.Expires?.UtcDateTime ?? DateTime.MinValue,
                });
            }
            catch (CookieException)
            {
                // A cookie the site does not need for the session (odd characters): skip it.
            }
        }

        lock (_gate)
        {
            _cookies = container;
        }
    }

    /// <summary>Current cookies, to be saved.</summary>
    public IReadOnlyList<SiteCookie> ExportCookies()
    {
        lock (_gate)
        {
            return _cookies.GetAllCookies()
                .Where(c => !c.Expired)
                .Select(c => new SiteCookie
                {
                    Name = c.Name,
                    Value = c.Value,
                    Domain = c.Domain,
                    Path = c.Path,
                    Expires = c.Expires == DateTime.MinValue ? null : new DateTimeOffset(c.Expires.ToUniversalTime(), TimeSpan.Zero),
                })
                .ToArray();
        }
    }

    public void ClearCookies() => UseCookies([]);

    /// <returns>The signed-in user, or <c>null</c> when the cookies do not (or no longer) open a session.</returns>
    public Task<SiteUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default) =>
        RepoApiClient.GuardAsync(
            async () =>
            {
                using var page = await GetPageAsync("help", cancellationToken).ConfigureAwait(false);
                return ParseUser(page.RootElement);
            },
            cancellationToken);

    /// <summary>Ids of every post the user liked, newest like first.</summary>
    /// <exception cref="RepoApiException"><see cref="RepoApiErrorKind.SignedOut"/> when the session expired.</exception>
    public Task<IReadOnlyList<string>> GetLikedPostIdsAsync(long userId, CancellationToken cancellationToken = default) =>
        RepoApiClient.GuardAsync<IReadOnlyList<string>>(
            async () =>
            {
                var ids = new List<string>();
                for (var page = 1; page <= MaxLikedPages; page++)
                {
                    using var document = await GetPageAsync(
                        string.Create(CultureInfo.InvariantCulture, $"user/{userId}/liked?page={page}"),
                        cancellationToken).ConfigureAwait(false);
                    var (pageIds, lastPage) = ParseLikedPage(document.RootElement);
                    ids.AddRange(pageIds);
                    if (page >= lastPage || pageIds.Count == 0)
                    {
                        break;
                    }
                }

                return ids.Distinct(StringComparer.Ordinal).ToArray();
            },
            cancellationToken);

    /// <summary>Likes the post, or removes the like (the site toggles), then reads back the real state.</summary>
    /// <exception cref="RepoApiException"><see cref="RepoApiErrorKind.SignedOut"/> when the session expired.</exception>
    public Task<LikeState> ToggleLikeAsync(string postId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        return RepoApiClient.GuardAsync(
            async () =>
            {
                var postPath = $"post/{Uri.EscapeDataString(postId)}";
                using (var request = CreateRequest(HttpMethod.Post, $"{postPath}/like"))
                {
                    request.Headers.Referrer = new Uri(_options.BaseUri, postPath);
                    request.Content = new StringContent(string.Empty);
                    using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
                    EnsureStillSignedIn(response);
                }

                using var page = await GetPageAsync(postPath, cancellationToken).ConfigureAwait(false);
                return ParseLikeState(page.RootElement);
            },
            cancellationToken);
    }

    /// <summary>Ends the session on the site too (best effort) and forgets the cookies.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Post, "logout");
            request.Content = new StringContent(string.Empty);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            // Offline: the local session is dropped anyway.
        }
        finally
        {
            ClearCookies();
        }
    }

    public void Dispose() => _http.Dispose();

    /// <summary>Extracts and parses the Inertia page object embedded in the HTML.</summary>
    internal static JsonDocument ParseInertiaPage(string html)
    {
        var match = DataPageRegex().Match(html);
        if (!match.Success)
        {
            throw new RepoApiException(RepoApiErrorKind.InvalidResponse, "The steamdeckrepo.com page has no Inertia data.");
        }

        try
        {
            return JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups["json"].Value));
        }
        catch (JsonException ex)
        {
            throw new RepoApiException(RepoApiErrorKind.InvalidResponse, "The steamdeckrepo.com page data is not valid JSON.", ex);
        }
    }

    internal static SiteUser? ParseUser(JsonElement page)
    {
        if (!TryGetPath(page, out var user, "props", "user") || user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("id", out var idElement) || !TryGetInt64(idElement, out var id))
        {
            return null;
        }

        var name = GetString(user, "steam_name") ?? GetString(user, "name") ?? string.Empty;
        var avatar = GetString(user, "steam_avatar") ?? GetString(user, "profile_photo_url");
        return new SiteUser(id, name, Uri.TryCreate(avatar, UriKind.Absolute, out var avatarUri) ? avatarUri : null);
    }

    internal static (IReadOnlyList<string> Ids, int LastPage) ParseLikedPage(JsonElement page)
    {
        if (!TryGetPath(page, out var posts, "props", "data", "posts", "data") || posts.ValueKind != JsonValueKind.Array)
        {
            throw new RepoApiException(RepoApiErrorKind.InvalidResponse, "The liked posts page has an unexpected layout.");
        }

        var ids = posts.EnumerateArray().Select(p => GetString(p, "id")).OfType<string>().ToArray();
        var lastPage = TryGetPath(page, out var last, "props", "data", "posts", "meta", "last_page") && TryGetInt64(last, out var value)
            ? (int)Math.Clamp(value, 1, MaxLikedPages)
            : 1;
        return (ids, lastPage);
    }

    internal static LikeState ParseLikeState(JsonElement page)
    {
        if (!TryGetPath(page, out var post, "props", "post", "data") || post.ValueKind != JsonValueKind.Object
            || !post.TryGetProperty("liked", out var liked) || liked.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new RepoApiException(RepoApiErrorKind.InvalidResponse, "The post page does not say whether it is liked.");
        }

        var likes = post.TryGetProperty("likes", out var likesElement) && TryGetInt64(likesElement, out var count) ? (int)count : 0;
        return new LikeState(liked.GetBoolean(), likes);
    }

    /// <summary>
    /// Reads a page, following the site's own redirects (e.g. <c>/post/{id}</c> → <c>/post/{id}/{slug}</c>); a
    /// redirect to the login page or to another host ends as <see cref="RepoApiErrorKind.SignedOut"/> / an HTTP error.
    /// </summary>
    private async Task<JsonDocument> GetPageAsync(string relativeUri, CancellationToken cancellationToken)
    {
        const int MaxRedirects = 3;
        var uri = new Uri(_options.BaseUri, relativeUri);
        for (var redirects = 0; ; redirects++)
        {
            using var request = CreateRequest(HttpMethod.Get, uri);
            using var response = await SendAsync(request, cancellationToken).ConfigureAwait(false);
            EnsureStillSignedIn(response);
            if ((int)response.StatusCode is >= 300 and < 400
                && response.Headers.Location is { } location
                && redirects < MaxRedirects)
            {
                var target = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (target.Host.Equals(_options.BaseUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    uri = target;
                    continue;
                }
            }

            RepoApiClient.EnsureSuccess(response);
            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseInertiaPage(html);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativeUri) =>
        CreateRequest(method, new Uri(_options.BaseUri, relativeUri));

    private HttpRequestMessage CreateRequest(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        lock (_gate)
        {
            var cookieHeader = _cookies.GetCookieHeader(uri);
            if (cookieHeader.Length > 0)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            if (method != HttpMethod.Get && _cookies.GetCookies(uri)[XsrfCookieName] is { } xsrf)
            {
                // Laravel's VerifyCsrfToken accepts the (URL-decoded) XSRF-TOKEN cookie echoed in this header.
                request.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", Uri.UnescapeDataString(xsrf.Value));
                request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            }
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies) && request.RequestUri is { } uri)
        {
            lock (_gate)
            {
                foreach (var header in setCookies)
                {
                    try
                    {
                        _cookies.SetCookies(uri, header);
                    }
                    catch (CookieException)
                    {
                        // Ignore a malformed cookie rather than failing the request.
                    }
                }
            }

            CookiesChanged?.Invoke(this, EventArgs.Empty);
        }

        return response;
    }

    /// <summary>401, 419 (CSRF token of a dead session) or a redirect to the login page mean "sign in again".</summary>
    private static void EnsureStillSignedIn(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        var redirectsToLogin = status is >= 300 and < 400
            && response.Headers.Location is { } location
            && location.OriginalString.Contains("/login", StringComparison.OrdinalIgnoreCase);
        if (status is 401 or 419 || redirectsToLogin)
        {
            throw new RepoApiException(RepoApiErrorKind.SignedOut, "The steamdeckrepo.com session is no longer valid.")
            {
                StatusCode = response.StatusCode,
            };
        }

        if (status is >= 300 and < 400)
        {
            return; // The like endpoint answers with a redirect back to the post.
        }

        RepoApiClient.EnsureSuccess(response);
    }

    private static bool TryGetPath(JsonElement element, out JsonElement result, params string[] path)
    {
        result = element;
        foreach (var name in path)
        {
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty(name, out result))
            {
                return false;
            }
        }

        return true;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            }
            : null;

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        value = 0;
        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value),
            _ => false,
        };
    }

    [GeneratedRegex("""data-page="(?<json>[^"]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex DataPageRegex();
}
