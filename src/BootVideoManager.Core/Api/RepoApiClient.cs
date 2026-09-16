using System.Net;
using System.Net.Http.Headers;

namespace BootVideoManager.Core.Api;

/// <summary>HTTP client for steamdeckrepo.com; see <c>docs/research.md</c> for the endpoints.</summary>
public sealed class RepoApiClient : IRepoApiClient
{
    /// <summary>Largest <c>per_page</c> the server honours.</summary>
    public const int MaxPageSize = 100;

    private readonly HttpClient _http;
    private readonly RepoApiOptions _options;

    /// <param name="http">
    /// Shared client, ideally built with <see cref="CreateHttpClient"/>. It is not disposed by this class.
    /// </param>
    /// <param name="options">Base address and User-Agent.</param>
    public RepoApiClient(HttpClient http, RepoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        _http = http;
        _options = options;
    }

    /// <summary>
    /// Builds the recommended <see cref="HttpClient"/>: compression, connection recycling (DNS changes),
    /// polite retries and timeout. Create one per application and share it.
    /// </summary>
    public static HttpClient CreateHttpClient(RepoApiOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var transport = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
        };

        return new HttpClient(new PoliteRetryHandler(options.Retry) { InnerHandler = transport })
        {
            Timeout = options.Timeout,
        };
    }

    public Task<CatalogDownload> GetCatalogAsync(DateTimeOffset? ifModifiedSince, CancellationToken cancellationToken = default) =>
        GuardAsync(
            async () =>
            {
                using var request = CreateRequest("api/posts/all");
                request.Headers.IfModifiedSince = ifModifiedSince;

                using var response = await _http
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return new CatalogDownload(true, [], ifModifiedSince);
                }

                EnsureSuccess(response);
                var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return new CatalogDownload(false, content, response.Content.Headers.LastModified);
            },
            cancellationToken);

    public Task<IReadOnlyList<string>> GetTrendingPostIdsAsync(CancellationToken cancellationToken = default) =>
        GuardAsync<IReadOnlyList<string>>(
            async () =>
            {
                using var request = CreateRequest($"api/posts?sort=trending&per_page={MaxPageSize}&page=1");
                using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

                EnsureSuccess(response);
                var content = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                return PostsParser.ParsePage(content).Select(post => post.Id).Distinct(StringComparer.Ordinal).ToArray();
            },
            cancellationToken);

    public Uri GetDownloadUri(string postId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);
        return new Uri(_options.BaseUri, $"post/download/{Uri.EscapeDataString(postId)}");
    }

    private HttpRequestMessage CreateRequest(string relativeUri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_options.BaseUri, relativeUri));
        request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = response.StatusCode;
        if (status == HttpStatusCode.TooManyRequests)
        {
            throw new RepoApiException(RepoApiErrorKind.RateLimited, "steamdeckrepo.com is rate limiting requests.")
            {
                StatusCode = status,
                RetryAfter = response.Headers.RetryAfter?.Delta,
            };
        }

        throw new RepoApiException(RepoApiErrorKind.HttpError, $"steamdeckrepo.com answered HTTP {(int)status}.")
        {
            StatusCode = status,
        };
    }

    /// <summary>Translates transport exceptions into <see cref="RepoApiException"/>; user cancellation passes through.</summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // HttpClient.Timeout surfaces as a cancellation the caller did not request.
            throw new RepoApiException(RepoApiErrorKind.Timeout, "steamdeckrepo.com did not answer in time.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new RepoApiException(RepoApiErrorKind.Network, "Could not reach steamdeckrepo.com.", ex);
        }
        catch (IOException ex)
        {
            throw new RepoApiException(RepoApiErrorKind.Network, "The connection to steamdeckrepo.com was interrupted.", ex);
        }
    }
}
