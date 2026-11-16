using System.Net;
using System.Net.Http.Headers;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Tests;

[TestFixture]
public sealed class HttpRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private static readonly HttpRetryOptions ThreeAttempts = new()
    {
        MaxAttempts = 3,
        BaseDelay = TimeSpan.FromSeconds(1),
        MaxDelay = TimeSpan.FromSeconds(30),
    };

    [TestCase(HttpStatusCode.OK)]
    [TestCase(HttpStatusCode.BadRequest)]
    public async Task Returns_a_response_it_does_not_retry(HttpStatusCode status)
    {
        var handler = new StubHandler(() => Reply(status));

        using var response = await Send(handler, new RecordingTimeProvider(Now));

        response.StatusCode.ShouldBe(status);
        handler.Calls.ShouldBe(1);
    }

    [Test]
    public async Task Retries_a_server_error_until_it_succeeds()
    {
        var handler = new StubHandler(
            () => Reply(HttpStatusCode.ServiceUnavailable),
            () => Reply(HttpStatusCode.BadGateway),
            () => Reply(HttpStatusCode.OK));

        using var response = await Send(handler, new RecordingTimeProvider(Now));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Calls.ShouldBe(3);
    }

    [Test]
    public async Task Retries_a_network_failure()
    {
        var handler = new StubHandler(
            () => throw new HttpRequestException("Connection refused."),
            () => Reply(HttpStatusCode.OK));

        using var response = await Send(handler, new RecordingTimeProvider(Now));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        handler.Calls.ShouldBe(2);
    }

    [Test]
    public async Task Throws_Transient_after_the_last_server_error()
    {
        var handler = new StubHandler(
            () => Reply(HttpStatusCode.InternalServerError),
            () => Reply(HttpStatusCode.InternalServerError),
            () => Reply(HttpStatusCode.InternalServerError));

        var ex = await Should.ThrowAsync<ConnectorException>(() => Send(handler, new RecordingTimeProvider(Now)));

        ex.Kind.ShouldBe(ConnectorErrorKind.Transient);
        ex.RetryAfter.ShouldBeNull();
        handler.Calls.ShouldBe(3);
    }

    [Test]
    public async Task Throws_Transient_when_the_network_never_answers()
    {
        var handler = new StubHandler(
            () => throw new HttpRequestException("Connection refused."),
            () => throw new HttpRequestException("Connection refused."),
            () => throw new HttpRequestException("Connection refused."));

        var ex = await Should.ThrowAsync<ConnectorException>(() => Send(handler, new RecordingTimeProvider(Now)));

        ex.Kind.ShouldBe(ConnectorErrorKind.Transient);
        ex.InnerException.ShouldBeOfType<HttpRequestException>();
        handler.Calls.ShouldBe(3);
    }

    [Test]
    public async Task Waits_as_long_as_Retry_After_asks()
    {
        var time = new RecordingTimeProvider(Now);
        var handler = new StubHandler(
            () => Reply(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(2)),
            () => Reply(HttpStatusCode.OK));

        using var response = await Send(handler, time);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        time.Delays.ShouldBe([TimeSpan.FromSeconds(2)]);
    }

    [Test]
    public async Task Reads_a_Retry_After_date_against_the_time_provider()
    {
        var time = new RecordingTimeProvider(Now);
        var handler = new StubHandler(
            () => Reply(HttpStatusCode.ServiceUnavailable, retryAt: Now.AddSeconds(5)),
            () => Reply(HttpStatusCode.OK));

        using var response = await Send(handler, time);

        time.Delays.ShouldBe([TimeSpan.FromSeconds(5)]);
    }

    [Test]
    public async Task Throws_RateLimited_at_once_when_Retry_After_exceeds_the_maximum()
    {
        var time = new RecordingTimeProvider(Now);
        var handler = new StubHandler(
            () => Reply(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromMinutes(2)));

        var ex = await Should.ThrowAsync<ConnectorException>(() => Send(handler, time));

        ex.Kind.ShouldBe(ConnectorErrorKind.RateLimited);
        ex.RetryAfter.ShouldBe(TimeSpan.FromMinutes(2));
        handler.Calls.ShouldBe(1);
        time.Delays.ShouldBeEmpty();
    }

    [Test]
    public async Task Doubles_the_backoff_after_each_attempt()
    {
        var time = new RecordingTimeProvider(Now);
        var handler = new StubHandler(
            () => Reply(HttpStatusCode.InternalServerError),
            () => Reply(HttpStatusCode.InternalServerError),
            () => Reply(HttpStatusCode.InternalServerError),
            () => Reply(HttpStatusCode.OK));

        using var response = await Send(handler, time, ThreeAttempts with { MaxAttempts = 4 });

        time.Delays.Count.ShouldBe(3);
        for (var i = 0; i < 3; i++)
        {
            var ceiling = TimeSpan.FromSeconds(Math.Pow(2, i));
            time.Delays[i].ShouldBeInRange(ceiling / 2, ceiling);
        }
    }

    [Test]
    public void Does_not_retry_when_the_caller_cancels()
    {
        var handler = new StubHandler(() => Reply(HttpStatusCode.OK));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.CatchAsync<OperationCanceledException>(() => Send(handler, new RecordingTimeProvider(Now), ct: cancelled.Token));

        handler.Calls.ShouldBe(0);
    }

    private static async Task<HttpResponseMessage> Send(
        StubHandler handler,
        TimeProvider time,
        HttpRetryOptions? options = null,
        CancellationToken ct = default)
    {
        using var client = new HttpClient(handler, disposeHandler: false);
        var retry = new HttpRetry(client, options ?? ThreeAttempts, time);
        return await retry.SendAsync(() => new HttpRequestMessage(HttpMethod.Get, "https://example.test/"), ct);
    }

    private static HttpResponseMessage Reply(
        HttpStatusCode status,
        TimeSpan? retryAfter = null,
        DateTimeOffset? retryAt = null)
    {
        var response = new HttpResponseMessage(status);
        if (retryAfter is { } delta)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(delta);
        }

        if (retryAt is { } date)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(date);
        }

        return response;
    }
}
