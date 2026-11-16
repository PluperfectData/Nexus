using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Nexus.Schwab;

/// <summary>
/// The two halves of a Schwab sign-in that a host runs: the page to send the user to, and the exchange of the code
/// Schwab's redirect carries for tokens.
/// </summary>
public static class SchwabSignIn
{
    /// <summary>Schwab's sign-in page for the app, which returns to <paramref name="callback"/> with <paramref name="state"/>.</summary>
    /// <param name="app">The app's key and secret; only the key is sent.</param>
    /// <param name="callback">The app's callback URL, exactly as registered with Schwab.</param>
    /// <param name="state">A value the redirect brings back, so the host can tell its own sign-ins apart.</param>
    public static Uri AuthorizeUrl(OAuthClient app, string callback, string state)
    {
        ArgumentNullException.ThrowIfNull(app);

        return new Uri(string.Create(
            CultureInfo.InvariantCulture,
            $"{SchwabApi.AuthorizeUri}?response_type=code&client_id={Uri.EscapeDataString(app.ClientId)}"
            + $"&redirect_uri={Uri.EscapeDataString(callback)}&state={Uri.EscapeDataString(state)}"));
    }

    /// <summary>
    /// Exchanges the code from Schwab's redirect for tokens. The request is sent once, since a code works only once.
    /// </summary>
    /// <param name="client">The HTTP client to send it with.</param>
    /// <param name="app">The app's key and secret.</param>
    /// <param name="code">The <c>code</c> the redirect carried.</param>
    /// <param name="callback">The same callback URL the sign-in page was given.</param>
    /// <param name="timeProvider">The clock the tokens' expiry is read from; the system clock by default.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="ConnectorException">Schwab refused the code or the app, or sent no tokens.</exception>
    public static async Task<OAuthTokens> ExchangeAsync(
        HttpClient client,
        OAuthClient app,
        string code,
        string callback,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(app);

        using var request = new HttpRequestMessage(HttpMethod.Post, SchwabApi.TokenUri)
        {
            Headers =
            {
                Authorization = new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(
                        $"{Uri.EscapeDataString(app.ClientId)}:{Uri.EscapeDataString(app.ClientSecret)}"))),
            },
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "authorization_code"),
                new("code", code),
                new("redirect_uri", callback),
            ]),
        };

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException(ConnectorErrorKind.Transient, "Schwab could not be reached. Sign in again later.", ex);
        }

        using (response)
        {
            using var body = await ParseOrNullAsync(response, ct).ConfigureAwait(false);
            var root = body?.RootElement ?? default;
            if (!response.IsSuccessStatusCode)
            {
                var reason = SchwabJson.String(root, "error_description") ?? SchwabJson.String(root, "error") ?? "no reason given";
                throw new ConnectorException(
                    SchwabJson.String(root, "error") == "invalid_client" ? ConnectorErrorKind.ConfigInvalid : ConnectorErrorKind.AuthExpired,
                    string.Create(CultureInfo.InvariantCulture, $"Schwab refused the sign-in ({(int)response.StatusCode}): {reason}."));
            }

            if (SchwabJson.String(root, "access_token") is not { } access
                || SchwabJson.String(root, "refresh_token") is not { } refresh
                || SchwabJson.Int64(root, "expires_in") is not { } lifetime)
            {
                throw new ConnectorException(ConnectorErrorKind.SourceError, "Schwab's token response is missing a token or its lifetime.");
            }

            return new OAuthTokens(access, refresh, (timeProvider ?? TimeProvider.System).GetUtcNow().AddSeconds(lifetime));
        }
    }

    private static async Task<JsonDocument?> ParseOrNullAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
