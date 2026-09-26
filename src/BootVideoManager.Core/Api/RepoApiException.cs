using System.Net;

namespace BootVideoManager.Core.Api;

/// <summary>Category of API failure, used by the UI to pick a clear message.</summary>
public enum RepoApiErrorKind
{
    /// <summary>No connection, DNS failure, connection reset…</summary>
    Network,

    /// <summary>The server did not answer in time.</summary>
    Timeout,

    /// <summary>HTTP 429: we are sending too many requests.</summary>
    RateLimited,

    /// <summary>Any other non-success HTTP status.</summary>
    HttpError,

    /// <summary>The body could not be understood (site redesign, maintenance page…).</summary>
    InvalidResponse,

    /// <summary>The steamdeckrepo.com session expired or was revoked: the user must sign in again.</summary>
    SignedOut,
}

/// <summary>Single exception type surfaced by the API layer; never leaks transport exceptions.</summary>
public sealed class RepoApiException : Exception
{
    public RepoApiException(RepoApiErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public RepoApiErrorKind Kind { get; }

    /// <summary>HTTP status when the server answered.</summary>
    public HttpStatusCode? StatusCode { get; init; }

    /// <summary>Delay requested by the server before retrying, when provided.</summary>
    public TimeSpan? RetryAfter { get; init; }
}
