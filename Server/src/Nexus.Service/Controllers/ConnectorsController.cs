using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

public sealed record FieldView(string Name, string Label, SettingKind Kind, bool Required, IReadOnlyList<string>? Choices, string? Default);

/// <summary>
/// A connector and the settings it asks for. <paramref name="NotReady"/> says why it cannot be connected in the
/// workspace asking yet, such as a provider the workspace has not set up; it is null when it can, or when no workspace
/// is asking.
/// </summary>
public sealed record ConnectorView(string Id, string Name, string Description, AuthKind Auth, IReadOnlyList<FieldView> Fields, string? NotReady)
{
    public static ConnectorView Of(ISourceConnector connector, string? notReady = null) =>
        new(
            connector.Spec.Id,
            connector.Spec.DisplayName,
            connector.Spec.Description,
            connector.Spec.Auth,
            [.. SourceConnectors.FieldsOf(connector).Select(f => new FieldView(f.Name, f.Label, f.Kind, f.Required, f.Choices, f.Default))],
            notReady);

    /// <summary>The connector as a workspace sees it, with why it cannot be connected there yet.</summary>
    public static async Task<ConnectorView> ForAsync(ISourceConnector connector, OAuthApps apps, int workspaceId, CancellationToken ct) =>
        Of(connector, connector.Spec.Auth == AuthKind.OAuth ? await apps.ProblemForAsync(connector.Spec.Id, workspaceId, ct) : null);
}

/// <summary>
/// The connectors the service reads through, with the settings each asks for, for signed-in users and apps. An app
/// sees whether each is ready in its own workspace.
/// </summary>
[ApiController]
[Route("connectors")]
[Authorize(Policy = AppKeyHandler.UserOrAppPolicy)]
public sealed class ConnectorsController(SourceConnectors connectors, OAuthApps apps, NexusDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<ConnectorView>> List(CancellationToken ct)
    {
        var ordered = connectors.All.OrderBy(c => c.Spec.DisplayName, StringComparer.Ordinal).ToList();
        if (User.AppId() is not { } appId)
        {
            return ordered.Select(c => ConnectorView.Of(c));
        }

        var workspaceId = await db.Apps.Where(a => a.Id == appId).Select(a => a.WorkspaceId).SingleAsync(ct);
        var views = new List<ConnectorView>();
        foreach (var connector in ordered)
        {
            views.Add(await ConnectorView.ForAsync(connector, apps, workspaceId, ct));
        }

        return views;
    }
}
