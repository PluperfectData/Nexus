using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Nexus.Service.Auth;

namespace Nexus.Service.Controllers;

public sealed record FieldView(string Name, string Label, SettingKind Kind, bool Required, IReadOnlyList<string>? Choices, string? Default);

public sealed record ConnectorView(string Id, string Name, string Description, AuthKind Auth, IReadOnlyList<FieldView> Fields)
{
    public static ConnectorView Of(ISourceConnector connector) =>
        new(
            connector.Spec.Id,
            connector.Spec.DisplayName,
            connector.Spec.Description,
            connector.Spec.Auth,
            [.. SourceConnectors.FieldsOf(connector).Select(f => new FieldView(f.Name, f.Label, f.Kind, f.Required, f.Choices, f.Default))]);
}

/// <summary>The connectors the service reads through, with the settings each asks for, for signed-in users and apps.</summary>
[ApiController]
[Route("connectors")]
[Authorize(Policy = AppKeyHandler.UserOrAppPolicy)]
public sealed class ConnectorsController(SourceConnectors connectors) : ControllerBase
{
    [HttpGet]
    public IEnumerable<ConnectorView> List() =>
        connectors.All.OrderBy(c => c.Spec.DisplayName, StringComparer.Ordinal).Select(ConnectorView.Of);
}
