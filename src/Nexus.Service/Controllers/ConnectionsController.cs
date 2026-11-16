using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

/// <summary>A connection with the settings it is safe to show: every one but its secrets.</summary>
public sealed record ConnectionView(
    int Id,
    string Name,
    string Connector,
    IReadOnlyDictionary<string, string?> Settings,
    bool NeedsSignIn,
    DateTime CreatedAt);

public sealed record ConnectionRequest(string? Name, string? Connector, IReadOnlyDictionary<string, string?>? Settings);

public sealed record ColumnView(string Name, ColumnType Type);

public sealed record StreamView(
    string Name,
    IReadOnlyList<ColumnView> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<string> CursorFields,
    IReadOnlyList<SyncMode> SyncModes)
{
    public static IEnumerable<StreamView> From(SourceCatalog catalog) =>
        catalog.Streams
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .Select(s => new StreamView(s.Name, [.. s.Columns.Select(c => new ColumnView(c.Name, c.Type))], s.PrimaryKey, s.CursorFields, s.SyncModes));
}

/// <summary>A workspace's connections, for its members: add one once its settings connect, list its streams, and remove it.</summary>
[ApiController]
public sealed class ConnectionsController(NexusDbContext db, Access access, ConnectionMaker maker) : ControllerBase
{
    [HttpGet("workspaces/{workspaceId:int}/connections")]
    public async Task<ActionResult<IEnumerable<ConnectionView>>> List(int workspaceId, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        var connections = await db.Connections.Where(c => c.WorkspaceId == workspaceId).OrderBy(c => c.Name).ToListAsync(ct);
        return connections.Select(maker.ViewOf).ToList();
    }

    /// <summary>Adds a connection once its settings connect.</summary>
    [HttpPost("workspaces/{workspaceId:int}/connections")]
    public async Task<ActionResult<ConnectionView>> Add(int workspaceId, ConnectionRequest request, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        var (connection, problem) = await maker.AddAsync(workspaceId, request.Name, request.Connector, request.Settings, ct);
        return connection is null
            ? Problem(problem, statusCode: StatusCodes.Status400BadRequest)
            : StatusCode(StatusCodes.Status201Created, maker.ViewOf(connection));
    }

    /// <summary>The streams the connection offers, read from its source now.</summary>
    [HttpGet("connections/{id:int}/streams")]
    public async Task<ActionResult<IEnumerable<StreamView>>> Streams(int id, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } connection)
        {
            return NotFound();
        }

        var (catalog, problem) = await maker.DiscoverAsync(connection, ct);
        return catalog is null ? Problem(problem, statusCode: StatusCodes.Status400BadRequest) : StreamView.From(catalog).ToList();
    }

    /// <summary>Removes the connection with its syncs. The tables at each destination stay.</summary>
    [HttpDelete("connections/{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } connection)
        {
            return NotFound();
        }

        if (await db.Syncs.AnyAsync(s => s.ConnectionId == id && s.Status == SyncStatus.Running, ct))
        {
            return Problem("A sync from this connection is running. Wait for it to end, then remove it.", statusCode: StatusCodes.Status409Conflict);
        }

        db.Connections.Remove(connection);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<Connection?> FindAsync(int id, CancellationToken ct) =>
        await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct) is { } connection && await access.CanSeeAsync(User, connection.WorkspaceId, ct)
            ? connection
            : null;
}
