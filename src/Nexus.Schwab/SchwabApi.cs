using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Nexus.Schwab;

internal sealed record SchwabAccount(string Number, string Hash);

/// <summary>Calls Schwab for one sign-in: refreshes the access token, looks up account hashes, and classifies failures.</summary>
internal sealed class SchwabApi
{
    public static readonly Uri AuthorizeUri = new("https://api.schwabapi.com/v1/oauth/authorize");
    public static readonly Uri TokenUri = new("https://api.schwabapi.com/v1/oauth/token");

    private static readonly Uri TraderUri = new("https://api.schwabapi.com/trader/v1/");
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(5);

    private readonly HttpClient _client;
    private readonly HttpRetry _retry;
    private readonly TimeProvider _time;
    private readonly OAuthClient _app;
    private OAuthTokens _tokens;
    private bool _unreported;
    private List<SchwabAccount>? _accounts;

    public SchwabApi(HttpClient client, TimeProvider time, ConnectorConfig config)
    {
        _client = client;
        _time = time;
        _retry = new HttpRetry(client, timeProvider: time);
        _app = config.Client
            ?? throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, "The host passed no Schwab app key and secret.");
        _tokens = config.Tokens ?? throw SignIn("Sign in to Schwab.");
    }

    /// <summary>The newest tokens this instance refreshed, or null when it refreshed none.</summary>
    public OAuthTokens? Refreshed { get; private set; }

    /// <summary>The newest tokens not yet taken, or null.</summary>
    public OAuthTokens? TakeRefreshed()
    {
        var tokens = _unreported ? Refreshed : null;
        _unreported = false;
        return tokens;
    }

    public async Task<IReadOnlyList<SchwabAccount>> AccountsAsync(CancellationToken ct)
    {
        if (_accounts is null)
        {
            using var document = await GetAsync("accounts/accountNumbers", ct).ConfigureAwait(false);
            _accounts = SchwabJson.Items(document.RootElement)
                .Select(account => new SchwabAccount(
                    SchwabJson.RequiredString(account, "accountNumber"),
                    SchwabJson.RequiredString(account, "hashValue")))
                .ToList();
        }

        return _accounts;
    }

    public async Task<string> HashForAsync(string accountNumber, CancellationToken ct) =>
        (await AccountsAsync(ct).ConfigureAwait(false)).FirstOrDefault(account => account.Number == accountNumber)?.Hash
        ?? throw new ConnectorException(
            ConnectorErrorKind.ConfigInvalid,
            $"Account {accountNumber} is not linked to this Schwab sign-in.");

    /// <summary>Reads JSON, retrying network failures, 429, and 5xx responses.</summary>
    public async Task<JsonDocument> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        var uri = new Uri(TraderUri, pathAndQuery);
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uri), once: false, ct)
            .ConfigureAwait(false);

        return await ReadJsonAsync(response, ct).ConfigureAwait(false)
            ?? throw new ConnectorException(ConnectorErrorKind.SourceError, "Schwab sent a response that is not JSON.");
    }

    /// <summary>Sends an action once, never retrying it after it may have reached Schwab.</summary>
    public Task<HttpResponseMessage> SendActionAsync(HttpMethod method, string path, SchwabOrder? order, CancellationToken ct)
    {
        var uri = new Uri(TraderUri, path);
        var body = order?.ToJson().ToJsonString();
        return SendAsync(
            () => new HttpRequestMessage(method, uri)
            {
                Content = body is null ? null : new StringContent(body, Encoding.UTF8, "application/json"),
            },
            once: true,
            ct);
    }

    private static ConnectorException SignIn(string message) => new(ConnectorErrorKind.AuthExpired, message);

    private static ConnectorException UnknownOutcome(Exception? inner) => new(
        ConnectorErrorKind.OutcomeUnknown,
        "Schwab did not answer after the request was sent, so it may have taken effect. "
        + "Check the account's orders before sending it again.",
        inner);

    private static async Task<JsonDocument?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (body.ConfigureAwait(false))
            {
                return await JsonDocument.ParseAsync(body, cancellationToken: ct).ConfigureAwait(false);
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException ex)
        {
            throw new ConnectorException(ConnectorErrorKind.Transient, "The response from Schwab was cut off. Try again later.", ex);
        }
    }

    private static async Task<ConnectorException> FailureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        using var document = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        var detail = document is null ? null : Detail(document.RootElement);

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => SignIn("Schwab refused the sign-in. Sign in to Schwab again."),
            HttpStatusCode.Forbidden => new(ConnectorErrorKind.ConfigInvalid, Join("Schwab refused access.", detail)),
            HttpStatusCode.TooManyRequests => new(
                ConnectorErrorKind.RateLimited,
                "Schwab is limiting requests, so the request was not carried out.",
                retryAfter: response.Headers.RetryAfter?.Delta),

            // Reads retry 5xx in HttpRetry, so only an action reaches here with one.
            _ when status >= 500 => UnknownOutcome(null),
            _ => new(
                ConnectorErrorKind.SourceError,
                Join(string.Create(CultureInfo.InvariantCulture, $"Schwab rejected the request ({status})."), detail)),
        };
    }

    private static string? Detail(JsonElement root)
    {
        var parts = new List<string>();
        if (SchwabJson.String(root, "message") is { } message)
        {
            parts.Add(message);
        }

        foreach (var error in SchwabJson.Items(root, "errors"))
        {
            var text = error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : SchwabJson.String(error, "detail") ?? SchwabJson.String(error, "title") ?? SchwabJson.String(error, "message");
            if (text is not null)
            {
                parts.Add(text);
            }
        }

        return parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    private static string Join(string summary, string? detail) => detail is null ? summary : $"{summary} {detail}";

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> createRequest, bool once, CancellationToken ct)
    {
        await EnsureFreshAsync(ct).ConfigureAwait(false);

        var response = await SendSignedAsync(createRequest, once, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Schwab can revoke an access token before it expires. A 401 carried nothing out, so resending is safe.
            response.Dispose();
            await RefreshAsync(ct).ConfigureAwait(false);
            response = await SendSignedAsync(createRequest, once, ct).ConfigureAwait(false);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            throw await FailureAsync(response, ct).ConfigureAwait(false);
        }
    }

    private Task<HttpResponseMessage> SendSignedAsync(Func<HttpRequestMessage> createRequest, bool once, CancellationToken ct)
    {
        var accessToken = _tokens.AccessToken;

        HttpRequestMessage Signed()
        {
            var request = createRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            return request;
        }

        return once ? SendOnceAsync(Signed, ct) : _retry.SendAsync(Signed, ct);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(Func<HttpRequestMessage> createRequest, CancellationToken ct)
    {
        using var request = createRequest();
        try
        {
            return await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (
            ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.SecureConnectionError)
        {
            throw new ConnectorException(
                ConnectorErrorKind.Transient,
                "Schwab could not be reached, so nothing was sent. Try again later.",
                ex);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw UnknownOutcome(ex);
        }
    }

    private async Task EnsureFreshAsync(CancellationToken ct)
    {
        if (_tokens.ExpiresAt is not { } expiresAt || expiresAt - _time.GetUtcNow() <= RefreshMargin)
        {
            await RefreshAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task RefreshAsync(CancellationToken ct)
    {
        var refreshToken = _tokens.RefreshToken ?? throw SignIn("Sign in to Schwab again.");
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            $"{Uri.EscapeDataString(_app.ClientId)}:{Uri.EscapeDataString(_app.ClientSecret)}"));

        using var response = await _retry.SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, TokenUri)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Basic", basic) },
                Content = new FormUrlEncodedContent(
                [
                    new("grant_type", "refresh_token"),
                    new("refresh_token", refreshToken),
                ]),
            },
            ct).ConfigureAwait(false);

        using var document = await ReadJsonAsync(response, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw document is not null && SchwabJson.String(document.RootElement, "error") == "invalid_client"
                ? new ConnectorException(ConnectorErrorKind.ConfigInvalid, "Schwab refused the app key and secret.")
                : response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                    ? SignIn("Schwab ended the sign-in, which it does every 7 days. Sign in to Schwab again.")
                    : new ConnectorException(
                        ConnectorErrorKind.SourceError,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Schwab refused to refresh the sign-in ({(int)response.StatusCode})."));
        }

        var root = document?.RootElement
            ?? throw new ConnectorException(ConnectorErrorKind.SourceError, "Schwab sent a token response that is not JSON.");
        var lifetime = SchwabJson.Int64(root, "expires_in")
            ?? throw new ConnectorException(ConnectorErrorKind.SourceError, "Schwab sent no 'expires_in'.");

        _tokens = new OAuthTokens(
            SchwabJson.RequiredString(root, "access_token"),
            SchwabJson.String(root, "refresh_token") ?? refreshToken,
            _time.GetUtcNow().AddSeconds(lifetime));
        Refreshed = _tokens;
        _unreported = true;
    }
}
