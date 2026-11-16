using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

/// <summary>
/// A destination, with its key as the push API named it at the last check. <paramref name="KeyProblem"/> says why the
/// push API refused the key then, if it did.
/// </summary>
public sealed record DestinationView(int Id, string Url, DateTime CreatedAt, string? KeyName, string? KeyPrefix, DateTime? KeyCheckedAt, string? KeyProblem)
{
    public static DestinationView Of(Destination d) => new(d.Id, d.Url, d.CreatedAt, d.KeyName, d.KeyPrefix, d.KeyCheckedAt, d.KeyProblem);
}

/// <summary>An app with its destinations. <paramref name="KeyPrefix"/> and <paramref name="KeyCreatedAt"/> are null once its key is revoked.</summary>
public sealed record AppView(
    int Id,
    string Name,
    string? KeyPrefix,
    DateTime? KeyCreatedAt,
    IReadOnlyList<string> ReturnUrls,
    DateTime CreatedAt,
    IReadOnlyList<DestinationView> Destinations);

/// <summary>An app with its key, which the service shows only this once.</summary>
public sealed record AppWithKey(AppView App, string Key);

public sealed record AppRequest(string? Name, IReadOnlyList<string>? ReturnUrls);

/// <summary>A workspace's apps. Members see them, to choose a destination for a sync; admins make and change them.</summary>
[ApiController]
public sealed class AppsController(NexusDbContext db, Access access, OperatorLog log, TimeProvider time) : ControllerBase
{
    private const int MaxReturnUrls = 10;

    private const int MaxUrlLength = 2000;

    [HttpGet("workspaces/{workspaceId:int}/apps")]
    public async Task<ActionResult<IEnumerable<AppView>>> List(int workspaceId, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        var apps = await db.Apps.Where(a => a.WorkspaceId == workspaceId).OrderBy(a => a.Name).ToListAsync(ct);
        var ids = apps.Select(a => a.Id).ToList();
        var destinations = await db.Destinations.Where(d => ids.Contains(d.AppId)).OrderBy(d => d.Id).ToListAsync(ct);
        return apps.Select(app => View(app, destinations)).ToList();
    }

    [HttpPost("workspaces/{workspaceId:int}/apps")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<AppWithKey>> Make(int workspaceId, AppRequest request, CancellationToken ct)
    {
        if (!await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct))
        {
            return NotFound();
        }

        if (Check(request, out var name, out var urls) is { } problem)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var (key, hash, prefix) = AppKeys.Make();
        var now = time.GetUtcNow().UtcDateTime;
        var app = new App
        {
            WorkspaceId = workspaceId,
            Name = name,
            KeyHash = hash,
            KeyPrefix = prefix,
            KeyCreatedAt = now,
            ReturnUrls = JsonSerializer.Serialize(urls),
            CreatedAt = now,
        };
        db.Apps.Add(app);
        log.Info($"App {name} was made, with key {prefix}….", LogPlace.In(workspaceId));
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, new AppWithKey(View(app, []), key));
    }

    [HttpPatch("apps/{id:int}")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<AppView>> Edit(int id, AppRequest request, CancellationToken ct)
    {
        if (await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct) is not { } app)
        {
            return NotFound();
        }

        if (Check(request, out var name, out var urls) is { } problem)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        log.Info(
            app.Name == name ? $"App {name}'s return addresses were changed." : $"App {app.Name} was renamed {name}, and its return addresses saved.",
            LogPlace.In(app.WorkspaceId));
        app.Name = name;
        app.ReturnUrls = JsonSerializer.Serialize(urls);
        await db.SaveChangesAsync(ct);
        return View(app, await db.Destinations.Where(d => d.AppId == id).ToListAsync(ct));
    }

    /// <summary>Gives the app a new key, which ends the old one.</summary>
    [HttpPost("apps/{id:int}/key")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<AppWithKey>> NewKey(int id, CancellationToken ct)
    {
        if (await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct) is not { } app)
        {
            return NotFound();
        }

        var (key, hash, prefix) = AppKeys.Make();
        log.Info(
            app.KeyPrefix is { } old ? $"App {app.Name} was given key {prefix}…, which ends key {old}…." : $"App {app.Name} was given key {prefix}….",
            LogPlace.In(app.WorkspaceId));
        app.KeyHash = hash;
        app.KeyPrefix = prefix;
        app.KeyCreatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return new AppWithKey(View(app, await db.Destinations.Where(d => d.AppId == id).ToListAsync(ct)), key);
    }

    [HttpDelete("apps/{id:int}/key")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<AppView>> RevokeKey(int id, CancellationToken ct)
    {
        if (await db.Apps.FirstOrDefaultAsync(a => a.Id == id, ct) is not { } app)
        {
            return NotFound();
        }

        if (app.KeyPrefix is { } revoked)
        {
            log.Info($"App {app.Name}'s key {revoked}… was revoked.", LogPlace.In(app.WorkspaceId));
        }

        app.KeyHash = null;
        app.KeyPrefix = null;
        app.KeyCreatedAt = null;
        await db.SaveChangesAsync(ct);
        return View(app, await db.Destinations.Where(d => d.AppId == id).ToListAsync(ct));
    }

    /// <summary>
    /// Removes the app with its destinations. Their syncs stay, each waiting for a new destination, and the tables at
    /// each destination stay.
    /// </summary>
    [HttpDelete("apps/{id:int}")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        if (await db.Syncs.AnyAsync(s => s.Destination!.AppId == id && s.Status == SyncStatus.Running, ct))
        {
            return Problem("A sync to this app is running. Wait for it to end, then remove the app.", statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) is not { } app)
        {
            return NotFound();
        }

        var orphaned = await db.Syncs.CountAsync(s => s.Destination!.AppId == id, ct);
        if (await db.Apps.Where(a => a.Id == id).ExecuteDeleteAsync(ct) == 0)
        {
            return NotFound();
        }

        log.Info(
            orphaned == 0
                ? $"App {app.Name} was removed, with its destinations."
                : $"App {app.Name} was removed, with its destinations. {Syncs(orphaned)} into them now wait for a new destination.",
            LogPlace.In(app.WorkspaceId));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    internal static string Syncs(int count) => count == 1 ? "1 sync" : $"{count} syncs";

    private static string? Check(AppRequest request, out string name, out List<string> urls)
    {
        name = request.Name?.Trim() ?? string.Empty;
        urls = [.. (request.ReturnUrls ?? []).Select(url => url.Trim()).Where(url => url.Length > 0).Distinct(StringComparer.Ordinal)];
        if (name.Length is 0 or > App.MaxNameLength)
        {
            return $"An app needs a name of {App.MaxNameLength} characters or fewer.";
        }

        if (urls.Count > MaxReturnUrls)
        {
            return $"An app has at most {MaxReturnUrls} return addresses.";
        }

        return urls.FirstOrDefault(url => !IsWebAddress(url)) is { } bad
            ? $"'{bad}' is not an http or https address of {MaxUrlLength} characters or fewer."
            : null;
    }

    internal static bool IsWebAddress(string url) =>
        url.Length <= MaxUrlLength
        && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static AppView View(App app, IEnumerable<Destination> destinations) =>
        new(app.Id, app.Name, app.KeyPrefix, app.KeyCreatedAt, JsonSerializer.Deserialize<List<string>>(app.ReturnUrls) ?? [], app.CreatedAt,
            [.. destinations.Where(d => d.AppId == app.Id).Select(DestinationView.Of)]);
}
