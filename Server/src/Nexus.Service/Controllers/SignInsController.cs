using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

public sealed record ProviderSignInRequest(string? Name, string? Connector, IReadOnlyDictionary<string, string?>? Settings);

/// <summary>
/// Signing in to a provider, such as Schwab: a workspace's members start one for a new connection or to renew one, and
/// the provider sends the browser back to <c>/oauth/callback</c>, which keeps the tokens and sends it on to the page
/// the sign-in started from. Connect pages start theirs at <c>POST /connect/sign-in</c>.
/// </summary>
[ApiController]
public sealed class SignInsController(
    NexusDbContext db,
    Access access,
    ConnectionMaker maker,
    OAuthApps apps,
    SignIns signIns,
    SourceConnectors connectors,
    Instance instance,
    TimeProvider time) : ControllerBase
{
    private const string Lost = "This sign-in ran out of time, or did not start in Nexus. Start it again.";

    [HttpPost("workspaces/{workspaceId:int}/sign-ins")]
    public async Task<ActionResult<SignInPage>> Start(int workspaceId, ProviderSignInRequest request, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        var (name, given, problem) = await maker.CheckSignInAsync(workspaceId, request.Name, request.Connector, request.Settings, ct);
        return problem is not null
            ? Problem(problem, statusCode: StatusCodes.Status400BadRequest)
            : await PageForAsync(new PendingSignIn(request.Connector!, workspaceId, name, given, null, null, null), ct);
    }

    /// <summary>Signs the connection in again, keeping its settings and syncs.</summary>
    [HttpPost("connections/{id:int}/sign-in")]
    public async Task<ActionResult<SignInPage>> Renew(int id, CancellationToken ct)
    {
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (connection is null || !await access.CanSeeAsync(User, connection.WorkspaceId, ct))
        {
            return NotFound();
        }

        if (connectors.Find(connection.Connector)?.Spec.Auth != AuthKind.OAuth)
        {
            return Problem("This connection takes no sign-in.", statusCode: StatusCodes.Status400BadRequest);
        }

        return await apps.ProblemForAsync(connection.Connector, connection.WorkspaceId, ct) is { } problem
            ? Problem(problem, statusCode: StatusCodes.Status400BadRequest)
            : await PageForAsync(new PendingSignIn(connection.Connector, connection.WorkspaceId, null, null, connection.Id, null, null), ct);
    }

    /// <summary>Where the provider sends the browser back, with a code to exchange for tokens or the reason it has none.</summary>
    [HttpGet("oauth/callback")]
    [AllowAnonymous]
    public async Task<IActionResult> Callback(string? code, string? state, string? error, CancellationToken ct)
    {
        if (await signIns.TakeAsync(state, ct) is not { } pending)
        {
            return Redirect($"{Client}/?error={Uri.EscapeDataString(Lost)}");
        }

        var now = time.GetUtcNow().UtcDateTime;
        ConnectSession? session = null;
        if (pending.SessionId is { } sessionId)
        {
            session = await db.ConnectSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.EndedAt == null && s.ExpiresAt > now, ct);
            if (session is null)
            {
                return Failed(pending, "This connect page expired while you signed in. Go back to the app and start again.");
            }
        }

        var provider = connectors.Find(pending.Connector)?.Spec.DisplayName ?? pending.Connector;
        if (string.IsNullOrEmpty(code))
        {
            return Failed(pending, $"{provider} did not sign you in: {error ?? "no reason given"}.");
        }

        OAuthTokens tokens;
        try
        {
            tokens = await apps.ExchangeAsync(pending.Connector, pending.WorkspaceId, code, ct);
        }
        catch (ConnectorException ex)
        {
            return Failed(pending, ex.Message);
        }

        Connection connection;
        if (pending.ConnectionId is { } connectionId)
        {
            if (await db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct) is not { } renewed)
            {
                return Failed(pending, "The connection this sign-in was for has been removed.");
            }

            await maker.RenewAsync(renewed, tokens, ct);
            connection = renewed;
        }
        else
        {
            connection = await maker.AddSignedInAsync(pending.WorkspaceId, pending.Name!, pending.Connector, pending.Settings!, tokens, ct);
        }

        if (session is null)
        {
            return Redirect($"{Client}/workspaces/{pending.WorkspaceId}?signedIn={connection.Id}");
        }

        if (session.Renews)
        {
            session.EndedAt = now;
            await db.SaveChangesAsync(ct);
            return Redirect(ConnectController.ReturnTo(session, "connection", connection.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        session.ConnectionId = connection.Id;
        await db.SaveChangesAsync(ct);
        return Redirect($"{Client}/connect#{pending.SessionToken}");
    }

    private string Client => instance.Client;

    private async Task<ActionResult<SignInPage>> PageForAsync(PendingSignIn pending, CancellationToken ct) =>
        new SignInPage((await apps.SignInPageAsync(pending.Connector, pending.WorkspaceId, await signIns.StartAsync(pending, ct), ct)).ToString());

    // Back to the page the sign-in started from, with the reason it failed.
    private RedirectResult Failed(PendingSignIn pending, string reason) =>
        Redirect(pending.SessionToken is { } token
            ? $"{Client}/connect?error={Uri.EscapeDataString(reason)}#{token}"
            : $"{Client}/workspaces/{pending.WorkspaceId}?error={Uri.EscapeDataString(reason)}");
}
