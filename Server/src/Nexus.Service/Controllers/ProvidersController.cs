using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

/// <summary>
/// A workspace's app for a provider it signs in to. Its secret is never answered: <c>KeyEnding</c> is the last four
/// characters of its key, to tell apps apart. <c>CallbackUrl</c> is the address to register on the app, and
/// <c>Problem</c> why the workspace cannot sign in to the provider yet.
/// </summary>
public sealed record ProviderView(
    string Connector,
    string Name,
    bool Set,
    bool Configured,
    string? KeyEnding,
    DateTime? UpdatedAt,
    string? CallbackUrl,
    string? Problem);

/// <summary>An app's key and secret. A blank one keeps the one stored.</summary>
public sealed record ProviderRequest(string? ClientId, string? ClientSecret);

/// <summary>The apps a workspace signs in to providers through, such as its Schwab app, for its members.</summary>
[ApiController]
[Route("workspaces/{workspaceId:int}/providers")]
public sealed class ProvidersController(NexusDbContext db, Access access, OAuthApps apps, Secrets secrets, OperatorLog log, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ProviderView>>> List(int workspaceId, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        var stored = await db.ProviderApps.Where(a => a.WorkspaceId == workspaceId).ToListAsync(ct);
        var views = new List<ProviderView>();
        foreach (var connector in apps.SignInConnectors)
        {
            views.Add(await ViewAsync(connector, workspaceId, stored.FirstOrDefault(a => a.Connector == connector.Spec.Id), ct));
        }

        return views;
    }

    /// <summary>Sets the workspace's app for the connector, or replaces it.</summary>
    [HttpPut("{connector}")]
    public async Task<ActionResult<ProviderView>> Set(int workspaceId, string connector, ProviderRequest request, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct) || apps.SignInConnectors.FirstOrDefault(c => c.Spec.Id == connector) is not { } found)
        {
            return NotFound();
        }

        if (apps.ConfiguredClient(connector) is not null)
        {
            return Problem(Configured(found), statusCode: StatusCodes.Status409Conflict);
        }

        var app = await db.ProviderApps.FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Connector == connector, ct);
        var key = request.ClientId?.Trim() ?? string.Empty;
        var secret = request.ClientSecret?.Trim() ?? string.Empty;
        if ((key.Length == 0 && app is null) || key.Length > ProviderApp.MaxClientIdLength || key.Any(char.IsControl) || key.Any(char.IsWhiteSpace))
        {
            return Problem(
                $"Enter the app's key from the {found.Spec.DisplayName} developer portal, up to {ProviderApp.MaxClientIdLength} characters with no spaces.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (secret.Length > ProviderApp.MaxClientSecretLength || secret.Any(char.IsControl))
        {
            return Problem($"An app's secret is one line of up to {ProviderApp.MaxClientSecretLength} characters.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (app is null && secret.Length == 0)
        {
            return Problem($"Enter the app's secret from the {found.Spec.DisplayName} developer portal.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (app is null)
        {
            app = new ProviderApp { WorkspaceId = workspaceId, Connector = connector, ClientId = key, ClientSecret = secrets.ProtectText(secret) };
            db.ProviderApps.Add(app);
            log.Info($"The {found.Spec.DisplayName} provider was set up, with a key ending {Ending(key)}.", LogPlace.In(workspaceId));
        }
        else
        {
            if (key.Length > 0)
            {
                app.ClientId = key;
            }

            if (secret.Length > 0)
            {
                app.ClientSecret = secrets.ProtectText(secret);
            }

            var changed = (key.Length > 0, secret.Length > 0) switch
            {
                (true, true) => "its key and secret were replaced",
                (true, false) => "its key was replaced",
                _ => "its secret was replaced",
            };
            log.Info($"The {found.Spec.DisplayName} provider was changed: {changed}. Its key ends {Ending(app.ClientId)}.", LogPlace.In(workspaceId));
        }

        app.UpdatedAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return await ViewAsync(found, workspaceId, app, ct);
    }

    /// <summary>Removes the workspace's app. Its connections stop syncing until another is set.</summary>
    [HttpDelete("{connector}")]
    public async Task<IActionResult> Remove(int workspaceId, string connector, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        var removed = await db.ProviderApps.Where(a => a.WorkspaceId == workspaceId && a.Connector == connector).ExecuteDeleteAsync(ct);
        if (removed == 0)
        {
            return NotFound();
        }

        var name = apps.SignInConnectors.FirstOrDefault(c => c.Spec.Id == connector)?.Spec.DisplayName ?? connector;
        log.Info($"The {name} provider was removed. Its connections stop syncing until another is set.", LogPlace.In(workspaceId));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static string Ending(string key) => key[^Math.Min(4, key.Length)..];

    private async Task<ProviderView> ViewAsync(ISourceConnector connector, int workspaceId, ProviderApp? stored, CancellationToken ct)
    {
        var configured = apps.ConfiguredClient(connector.Spec.Id);
        var key = configured?.ClientId ?? stored?.ClientId;
        return new ProviderView(
            connector.Spec.Id,
            connector.Spec.DisplayName,
            key is not null,
            configured is not null,
            key is null ? null : key[^Math.Min(4, key.Length)..],
            configured is null ? stored?.UpdatedAt : null,
            apps.CallbackFor(connector.Spec.Id),
            await apps.ProblemForAsync(connector.Spec.Id, workspaceId, ct));
    }

    private static string Configured(ISourceConnector connector) =>
        $"The service's configuration sets the {connector.Spec.DisplayName} app for every workspace, in {SchwabApp.Section}.";
}
