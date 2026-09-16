using System.Net;

namespace BootVideoManager.Core.Api;

/// <summary>
/// HTTP pipeline stage that retries transient failures a bounded number of times,
/// honouring <c>Retry-After</c> on 429 / 503 so the app never hammers the site.
/// </summary>
public sealed class PoliteRetryHandler : DelegatingHandler
{
    private readonly RetryPolicyOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PoliteRetryHandler(RetryPolicyOptions options)
        : this(options, TimeProvider.System, null)
    {
    }

    /// <param name="options">Retry limits.</param>
    /// <param name="timeProvider">Clock used to interpret an absolute <c>Retry-After</c> date.</param>
    /// <param name="delay">Wait implementation; replaced in tests to avoid real sleeps.</param>
    internal PoliteRetryHandler(
        RetryPolicyOptions options,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task>? delay)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);

        _options = options;
        _timeProvider = timeProvider;
        _delay = delay ?? ((wait, ct) => Task.Delay(wait, timeProvider, ct));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < _options.MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                // Connection-level failure: short exponential back-off, then retry.
                await _delay(Backoff(attempt), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (attempt >= _options.MaxAttempts || !IsRetryable(response.StatusCode))
            {
                return response;
            }

            var wait = GetRetryAfter(response) ?? Backoff(attempt);
            if (wait > _options.MaxRetryAfter)
            {
                // The server wants a long pause: surface it to the caller instead of blocking.
                return response;
            }

            response.Dispose();
            await _delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsRetryable(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout;

    private TimeSpan Backoff(int attempt) => _options.BaseDelay * Math.Pow(2, attempt - 1);

    private TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }

        if (retryAfter?.Date is { } date)
        {
            var wait = date - _timeProvider.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }
}
