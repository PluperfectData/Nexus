using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Push;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

public sealed record DestinationRequest(string? Url, string? Key);

/// <summary>Where an app's rows go, for admins: its push API's address and key, checked before they are kept.</summary>
[ApiController]
[Authorize(Policy = NexusClaims.AdminPolicy)]
public sealed class DestinationsController(NexusDbContext db, PushDestination push, Secrets secrets, TimeProvider time) : ControllerBase
{
    [HttpPost("apps/{appId:int}/destinations")]
    public async Task<ActionResult<DestinationView>> Add(int appId, DestinationRequest request, CancellationToken ct)
    {
        if (!await db.Apps.AnyAsync(a => a.Id == appId, ct))
        {
            return NotFound();
        }

        var url = request.Url?.Trim() ?? string.Empty;
        var key = request.Key?.Trim() ?? string.Empty;
        if (!AppsController.IsWebAddress(url) || key.Length == 0)
        {
            return Problem("A destination needs its push API's http or https address and a push API key.", statusCode: StatusCodes.Status400BadRequest);
        }

        PushKey named;
        try
        {
            named = await push.IdentifyAsync(Config(url, key), ct);
        }
        catch (ConnectorException ex)
        {
            return Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        var now = time.GetUtcNow().UtcDateTime;
        var destination = new Destination { AppId = appId, Url = url, Key = secrets.ProtectText(key), CreatedAt = now };
        destination.Checked(named, null, now);
        db.Destinations.Add(destination);
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, DestinationView.Of(destination));
    }

    /// <summary>Asks the push API whether it still takes the destination's key, and records its answer.</summary>
    [HttpPost("destinations/{id:int}/check")]
    public async Task<ActionResult<DestinationView>> Check(int id, CancellationToken ct)
    {
        if (await db.Destinations.FirstOrDefaultAsync(d => d.Id == id, ct) is not { } destination)
        {
            return NotFound();
        }

        var now = time.GetUtcNow().UtcDateTime;
        try
        {
            destination.Checked(await push.IdentifyAsync(Config(destination.Url, secrets.UnprotectText(destination.Key)), ct), null, now);
        }
        catch (ConnectorException ex)
        {
            destination.Checked(null, ex.Message, now);
        }

        await db.SaveChangesAsync(ct);
        return DestinationView.Of(destination);
    }

    /// <summary>Removes the destination. Its syncs stay, each waiting for a new destination, and the tables it holds stay.</summary>
    [HttpDelete("destinations/{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        if (await db.Syncs.AnyAsync(s => s.DestinationId == id && s.Status == SyncStatus.Running, ct))
        {
            return Problem("A sync to this destination is running. Wait for it to end, then remove it.", statusCode: StatusCodes.Status409Conflict);
        }

        return await db.Destinations.Where(d => d.Id == id).ExecuteDeleteAsync(ct) == 0 ? NotFound() : NoContent();
    }

    internal static ConnectorConfig Config(string url, string key) =>
        new(new Dictionary<string, string?> { [PushDestination.UrlSetting] = url, [PushDestination.KeySetting] = key });
}
