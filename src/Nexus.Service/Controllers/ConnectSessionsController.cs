using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

public sealed record ConnectSessionRequest(string? Connector, int DestinationId, string? ReturnUrl);

/// <summary>A connect page's address, with its token after the <c>#</c>, and when it expires.</summary>
public sealed record ConnectSessionView(string Url, DateTime ExpiresAt);

/// <summary>Opens a connect page for an app, by its key: one connector, into one of its destinations, returning to one of its addresses.</summary>
[ApiController]
[Route("connect-sessions")]
[Authorize(Policy = AppKeyHandler.AppPolicy)]
public sealed class ConnectSessionsController(NexusDbContext db, SourceConnectors connectors, ServiceSettings settings, TimeProvider time) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ConnectSessionView>> Open(ConnectSessionRequest request, CancellationToken ct)
    {
        var app = await db.Apps.SingleAsync(a => a.Id == User.AppId(), ct);
        if (connectors.Find(request.Connector ?? string.Empty) is null)
        {
            return Problem($"'{request.Connector}' is not a connector the service reads.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!await db.Destinations.AnyAsync(d => d.Id == request.DestinationId && d.AppId == app.Id, ct))
        {
            return Problem("Pick one of this app's destinations.", statusCode: StatusCodes.Status400BadRequest);
        }

        var returns = JsonSerializer.Deserialize<List<string>>(app.ReturnUrls) ?? [];
        if (request.ReturnUrl is null || !returns.Contains(request.ReturnUrl, StringComparer.Ordinal))
        {
            return Problem(
                $"'{request.ReturnUrl}' is not one of this app's return addresses. An admin adds it on the app in Nexus.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var (token, hash) = AppKeys.MakeConnectToken();
        var now = time.GetUtcNow().UtcDateTime;
        var session = new ConnectSession
        {
            TokenHash = hash,
            AppId = app.Id,
            DestinationId = request.DestinationId,
            Connector = request.Connector!,
            ReturnUrl = request.ReturnUrl,
            CreatedAt = now,
            ExpiresAt = now + ConnectSession.Lifetime,
        };
        db.ConnectSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return StatusCode(
            StatusCodes.Status201Created,
            new ConnectSessionView($"{settings.ClientOrigin.TrimEnd('/')}/connect#{token}", session.ExpiresAt));
    }
}
