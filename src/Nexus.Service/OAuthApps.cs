using Nexus.Schwab;

namespace Nexus.Service;

/// <summary>The <c>Nexus:Schwab</c> configuration section: the Schwab app the service signs in through.</summary>
public sealed class SchwabApp
{
    public const string Section = "Nexus:Schwab";

    public string? AppKey { get; set; }

    public string? AppSecret { get; set; }

    /// <summary>The app's callback URL, exactly as registered with Schwab. The service serves it.</summary>
    public string CallbackUrl { get; set; } = "https://127.0.0.1:6164/oauth/callback";

    public OAuthClient? Client =>
        string.IsNullOrWhiteSpace(AppKey) || string.IsNullOrWhiteSpace(AppSecret) ? null : new OAuthClient(AppKey.Trim(), AppSecret.Trim());
}

/// <summary>The OAuth apps the service signs in through, by connector: Schwab's, the only one so far.</summary>
public sealed class OAuthApps(SchwabApp schwab, IHttpClientFactory http, TimeProvider time)
{
    public const string SchwabId = "schwab";

    /// <summary>The HTTP client Schwab's connector and sign-in go through.</summary>
    public const string SchwabHttpClient = "schwab";

    /// <summary>The app a connector refreshes its tokens with, or null for one the service signs in to through no app.</summary>
    public OAuthClient? ClientFor(string connector) => connector == SchwabId ? schwab.Client : null;

    /// <summary>Why the service cannot sign in to the connector's provider, or null when it can.</summary>
    public string? ProblemFor(string connector) =>
        connector != SchwabId
            ? $"The service has no sign-in for '{connector}'."
            : schwab.Client is null
                ? "Set Nexus:Schwab:AppKey and Nexus:Schwab:AppSecret to sign in to Schwab."
                : null;

    /// <summary>The provider's sign-in page, which returns to the callback with <paramref name="state"/>.</summary>
    public Uri SignInPage(string connector, string state) => SchwabSignIn.AuthorizeUrl(Ready(connector), schwab.CallbackUrl, state);

    /// <summary>Exchanges the code the provider's redirect carried for tokens.</summary>
    /// <exception cref="ConnectorException">The provider refused the code or the app.</exception>
    public Task<OAuthTokens> ExchangeAsync(string connector, string code, CancellationToken ct) =>
        SchwabSignIn.ExchangeAsync(http.CreateClient(SchwabHttpClient), Ready(connector), code, schwab.CallbackUrl, time, ct);

    private OAuthClient Ready(string connector) =>
        ProblemFor(connector) is { } problem ? throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, problem) : schwab.Client!;
}
