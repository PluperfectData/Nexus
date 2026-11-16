using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

/// <summary>The calling app as Nexus knows it, with the destinations and return addresses its connect pages may use.</summary>
public sealed record CallerAppView(int Id, string Name, string WorkspaceName, IReadOnlyList<DestinationView> Destinations, IReadOnlyList<string> ReturnUrls);

/// <summary>Tells an app, by its key, about itself: chiefly the destination ids that opening a connect page takes.</summary>
[ApiController]
[Route("app")]
[Authorize(Policy = AppKeyHandler.AppPolicy)]
public sealed class AppController(NexusDbContext db, OperatorLog log) : ControllerBase
{
    [HttpGet]
    public async Task<CallerAppView> Get(CancellationToken ct)
    {
        var app = await db.Apps.Include(a => a.Workspace).SingleAsync(a => a.Id == User.AppId(), ct);
        var destinations = (await db.Destinations.Where(d => d.AppId == app.Id).OrderBy(d => d.Id).ToListAsync(ct))
            .Select(DestinationView.Of)
            .ToList();
        return new CallerAppView(app.Id, app.Name, app.Workspace!.Name, destinations, JsonSerializer.Deserialize<List<string>>(app.ReturnUrls) ?? []);
    }

    /// <summary>Revokes the calling app's own key, as an app does when it unlinks. The app, its destinations, and its syncs stay.</summary>
    [HttpDelete("key")]
    public async Task<IActionResult> RevokeKey(CancellationToken ct)
    {
        var app = await db.Apps.SingleAsync(a => a.Id == User.AppId(), ct);
        log.Info($"App {app.Name} revoked its own key {app.KeyPrefix}….", LogPlace.In(app.WorkspaceId));
        app.KeyHash = null;
        app.KeyPrefix = null;
        app.KeyCreatedAt = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}
