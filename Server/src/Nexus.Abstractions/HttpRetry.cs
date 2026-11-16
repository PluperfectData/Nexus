using System.Net;

namespace Nexus;

/// <summary>Sends HTTP requests, retrying network failures, 429, and 5xx responses with backoff.</summary>
public sealed class HttpRetry(HttpClient client, HttpRetryOptions? options = null, TimeProvider? timeProvider = null)
{
    private readonly HttpRetryOptions _options = options ?? HttpRetryOptions.Default;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Sends a fresh request from <paramref name="createRequest"/> on each attempt.</summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(createRequest);

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = createRequest();
                response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct))
            {
                if (attempt >= _options.MaxAttempts)
                {
                    throw new ConnectorException(ConnectorErrorKind.Transient, "The service could not be reached.", ex);
                }

                await Task.Delay(Backoff(attempt), _time, ct).ConfigureAwait(false);
                continue;
            }

            if (!IsRetried(response.StatusCode))
            {
                return response;
            }

            var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests;
            var retryAfter = RetryAfter(response);
            response.Dispose();

            if (attempt >= _options.MaxAttempts || retryAfter > _options.MaxDelay)
            {
                throw rateLimited
                    ? new ConnectorException(ConnectorErrorKind.RateLimited, "The service is limiting requests.", retryAfter: retryAfter)
                    : new ConnectorException(ConnectorErrorKind.Transient, "The service failed to respond.", retryAfter: retryAfter);
            }

            await Task.Delay(retryAfter ?? Backoff(attempt), _time, ct).ConfigureAwait(false);
        }
    }

    // HttpClient.Timeout surfaces as a cancellation the caller did not ask for.
    private static bool IsNetworkFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    private static bool IsRetried(HttpStatusCode status) =>
        status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private TimeSpan Backoff(int attempt)
    {
        var ceiling = Math.Min(
            _options.MaxDelay.TotalMilliseconds,
            _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt - 1));

        // Half fixed and half random, so many clients do not retry in step.
        return TimeSpan.FromMilliseconds(ceiling * (0.5 + (Random.Shared.NextDouble() / 2)));
    }
}
