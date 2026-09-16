namespace BootVideoManager.Core.Api;

/// <summary>Raw full-catalog download.</summary>
/// <param name="IsNotModified">True when the server answered 304: the cached copy is still current.</param>
/// <param name="Content">JSON body (empty when <paramref name="IsNotModified"/>).</param>
/// <param name="LastModified">Server <c>Last-Modified</c>, to send back on the next conditional request.</param>
public sealed record CatalogDownload(bool IsNotModified, byte[] Content, DateTimeOffset? LastModified);

/// <summary>Read-only access to steamdeckrepo.com.</summary>
public interface IRepoApiClient
{
    /// <summary>
    /// Downloads <c>/api/posts/all</c>. When <paramref name="ifModifiedSince"/> is given the request is
    /// conditional and usually costs a bodiless 304.
    /// </summary>
    /// <exception cref="RepoApiException">On any network, HTTP or timeout failure.</exception>
    Task<CatalogDownload> GetCatalogAsync(DateTimeOffset? ifModifiedSince, CancellationToken cancellationToken = default);

    /// <summary>Ids of the currently trending posts, best first (the server caps the list at 100).</summary>
    /// <exception cref="RepoApiException">On any network, HTTP, timeout or format failure.</exception>
    Task<IReadOnlyList<string>> GetTrendingPostIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Official download link of a post. It redirects to a short-lived signed file URL, so it must be
    /// requested right before downloading.
    /// </summary>
    Uri GetDownloadUri(string postId);
}
