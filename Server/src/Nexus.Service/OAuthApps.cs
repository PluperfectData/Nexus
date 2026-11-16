using Microsoft.EntityFrameworkCore;
using Nexus.Schwab;
using Nexus.Service.Storage;

namespace Nexus.Service;

/// <summary>
/// The <c>Nexus:Schwab</c> configuration section: a Schwab app for every workspace, which wins over a workspace's own,
/// and a callback for Schwab alone.
/// </summary>
public sealed class SchwabApp
{
    public const string Section = "Nexus:Schwab";

    public string? AppKey { get; set; }

    public string? AppSecret { get; set; }

    /// <summary>The app's callback URL, exactly as registered with Schwab, when it is not the instance's own.</summary>
    public string? CallbackUrl { get; set; }

    public OAuthClient? Client =>
        string.IsNullOrWhiteSpace(AppKey) || string.IsNullOrWhiteSpace(AppSecret) ? null : new OAuthClient(AppKey.Trim(), AppSecret.Trim());
}

/// <summary>
/// The apps the service signs in through, by connector and workspace: Schwab's, the only provider so far. Each
/// workspace sets its own on its page; one set in configuration serves every workspace instead.
/// </summary>
public sealed class OAuthApps(
    SchwabApp schwab,
    Instance instance,
    NexusDbContext db,
    Secrets secrets,
    SourceConnectors connectors,
    IHttpClientFactory http,
    TimeProvider time)
{
    public const string SchwabId = "schwab";

    /// <summary>The HTTP client Schwab's connector and sign-in go through.</summary>
    public const string SchwabHttpClient = "schwab";

    public const string NoCallback =
        "An admin sets the address sign-ins come back to, on the Settings page in Nexus, before anyone can sign in.";

    /// <summary>The connectors a workspace signs in to through an app of its own.</summary>
    public IEnumerable<ISourceConnector> SignInConnectors =>
        connectors.All.Where(c => c.Spec.Id == SchwabId).OrderBy(c => c.Spec.DisplayName, StringComparer.Ordinal);

    /// <summary>The app configuration sets for every workspace, or null.</summary>
    public OAuthClient? ConfiguredClient(string connector) => connector == SchwabId ? schwab.Client : null;

    /// <summary>The app a connection in the workspace signs in and refreshes its tokens through, or null when it has none.</summary>
    public async Task<OAuthClient?> ClientForAsync(string connector, int workspaceId, CancellationToken ct)
    {
        if (connector != SchwabId)
        {
            return null;
        }

        if (ConfiguredClient(connector) is { } configured)
        {
            return configured;
        }

        var app = await db.ProviderApps.AsNoTracking().FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Connector == connector, ct);
        return app is null ? null : new OAuthClient(app.ClientId, secrets.UnprotectText(app.ClientSecret));
    }

    /// <summary>Where the provider sends the browser back, exactly as it must be registered, or null until it is set.</summary>
    public string? CallbackFor(string connector) =>
        connector == SchwabId && !string.IsNullOrWhiteSpace(schwab.CallbackUrl) ? schwab.CallbackUrl.Trim() : instance.CallbackUrl;

    /// <summary>Why the workspace cannot sign in to the connector's provider yet, or null when it can.</summary>
    public async Task<string?> ProblemForAsync(string connector, int workspaceId, CancellationToken ct) =>
        connector != SchwabId || connectors.Find(connector) is not { } found
            ? $"The service has no sign-in for '{connector}'."
            : CallbackFor(connector) is null
                ? NoCallback
                : await ClientForAsync(connector, workspaceId, ct) is null
                    ? $"This workspace has no {found.Spec.DisplayName} app yet. Add it under Sign-in apps on the workspace page in Nexus."
                    : null;

    /// <summary>The provider's sign-in page, which returns to the callback with <paramref name="state"/>.</summary>
    /// <exception cref="ConnectorException">The workspace cannot sign in to the provider yet.</exception>
    public async Task<Uri> SignInPageAsync(string connector, int workspaceId, string state, CancellationToken ct) =>
        SchwabSignIn.AuthorizeUrl(await ReadyAsync(connector, workspaceId, ct), CallbackFor(connector)!, state);

    /// <summary>Exchanges the code the provider's redirect carried for tokens.</summary>
    /// <exception cref="ConnectorException">The provider refused the code or the app, or the workspace has no app.</exception>
    public async Task<OAuthTokens> ExchangeAsync(string connector, int workspaceId, string code, CancellationToken ct) =>
        await SchwabSignIn.ExchangeAsync(
            http.CreateClient(SchwabHttpClient), await ReadyAsync(connector, workspaceId, ct), code, CallbackFor(connector)!, time, ct);

    private async Task<OAuthClient> ReadyAsync(string connector, int workspaceId, CancellationToken ct) =>
        await ProblemForAsync(connector, workspaceId, ct) is { } problem
            ? throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, problem)
            : (await ClientForAsync(connector, workspaceId, ct))!;
}
