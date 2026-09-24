namespace BootVideoManager.Core.Api;

/// <summary>Connection settings for steamdeckrepo.com.</summary>
public sealed record RepoApiOptions
{
    /// <summary>Site root; every endpoint is resolved relative to it.</summary>
    public Uri BaseUri { get; init; } = new("https://steamdeckrepo.com/");

    /// <summary>
    /// Identifiable User-Agent, as asked by good API etiquette: product, version and a link to the project.
    /// </summary>
    public string UserAgent { get; init; } =
        $"BootVideoManager/{typeof(RepoApiOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"} (+https://github.com/Robocnop/BootVideoManager)";

    /// <summary>Per-request timeout. The full catalog is ~2 MB compressed, so keep this generous.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Retry behaviour for transient failures and HTTP 429 / 503.</summary>
    public RetryPolicyOptions Retry { get; init; } = new();
}

/// <summary>Bounded retry policy: never loops, always honours the server's <c>Retry-After</c>.</summary>
public sealed record RetryPolicyOptions
{
    /// <summary>Total attempts including the first one.</summary>
    public int MaxAttempts { get; init; } = 3;

    /// <summary>Back-off before the 2nd attempt; doubled for each following attempt.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>If the server asks us to wait longer than this, give up instead of blocking the user.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromSeconds(30);
}
