using System.Net;
using System.Net.Http.Headers;
using BootVideoManager.Core.Api;
using Microsoft.Extensions.Time.Testing;

namespace BootVideoManager.Core.Tests.Api;

public class PoliteRetryHandlerTests
{
    private static readonly RetryPolicyOptions Policy = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromSeconds(1),
        MaxRetryAfter = TimeSpan.FromSeconds(30),
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly List<TimeSpan> _delays = [];
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero));

    private async Task<(HttpResponseMessage Response, StubHttpHandler Stub)> SendAsync(
        Func<HttpRequestMessage, int, HttpResponseMessage> respond,
        CancellationToken cancellationToken)
    {
        var stub = new StubHttpHandler(respond);
        var handler = new PoliteRetryHandler(Policy, _time, (delay, _) =>
        {
            _delays.Add(delay);
            return Task.CompletedTask;
        })
        {
            InnerHandler = stub,
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://steamdeckrepo.com/api/posts/all");
        var response = await invoker.SendAsync(request, cancellationToken);
        return (response, stub);
    }

    [Fact]
    public async Task Success_IsNotRetried()
    {
        var (response, stub) = await SendAsync((_, _) => StubHttpHandler.Status(HttpStatusCode.OK), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(stub.Requests);
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task TooManyRequests_WaitsForRetryAfter_ThenRetries()
    {
        var (response, stub) = await SendAsync(
            (_, call) => call == 1
                ? StubHttpHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(5))
                : StubHttpHandler.Status(HttpStatusCode.OK),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(5)], _delays);
    }

    [Fact]
    public async Task RetryAfterAsAbsoluteDate_IsConvertedToADelay()
    {
        var (response, _) = await SendAsync(
            (_, call) =>
            {
                if (call > 1)
                {
                    return StubHttpHandler.Status(HttpStatusCode.OK);
                }

                var throttled = StubHttpHandler.Status(HttpStatusCode.ServiceUnavailable);
                throttled.Headers.RetryAfter = new RetryConditionHeaderValue(_time.GetUtcNow().AddSeconds(8));
                return throttled;
            },
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([TimeSpan.FromSeconds(8)], _delays);
    }

    [Fact]
    public async Task PersistentFailure_StopsAfterMaxAttempts_WithExponentialBackoff()
    {
        var (response, stub) = await SendAsync((_, _) => StubHttpHandler.Status(HttpStatusCode.BadGateway), Ct);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(3, stub.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], _delays);
    }

    [Fact]
    public async Task RetryAfterLongerThanAllowed_IsReturnedImmediately()
    {
        var (response, stub) = await SendAsync(
            (_, _) => StubHttpHandler.Status(HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(10)),
            Ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Single(stub.Requests);
        Assert.Empty(_delays);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotModified)]
    public async Task NonTransientStatus_IsNotRetried(HttpStatusCode status)
    {
        var (response, stub) = await SendAsync((_, _) => StubHttpHandler.Status(status), Ct);

        Assert.Equal(status, response.StatusCode);
        Assert.Single(stub.Requests);
    }

    [Fact]
    public async Task ConnectionFailure_IsRetried_ThenSucceeds()
    {
        var (response, stub) = await SendAsync(
            (_, call) => call == 1 ? throw new HttpRequestException("reset") : StubHttpHandler.Status(HttpStatusCode.OK),
            Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, stub.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(1)], _delays);
    }

    [Fact]
    public async Task ConnectionFailure_OnLastAttempt_Propagates()
    {
        await Assert.ThrowsAsync<HttpRequestException>(
            () => SendAsync((_, _) => throw new HttpRequestException("offline"), Ct));

        Assert.Equal(2, _delays.Count);
    }
}
