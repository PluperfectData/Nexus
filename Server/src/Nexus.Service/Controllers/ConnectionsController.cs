using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

/// <summary>
/// A connection with the settings it is safe to show: every one but its secrets. <paramref name="FileName"/> names the
/// file a connector such as CSV reads, and <paramref name="SignIn"/> is its sign-in, null for a connector that does not
/// sign in.
/// </summary>
public sealed record ConnectionView(
    int Id,
    string Name,
    string Connector,
    IReadOnlyDictionary<string, string?> Settings,
    string? FileName,
    SignInView? SignIn,
    DateTime CreatedAt);

/// <summary>
/// A connection's sign-in to its provider. <paramref name="EndsAt"/> is when a fixed clock ends it, and
/// <paramref name="EndingSoon"/> whether that is within the connector's warning. <paramref name="Ended"/> means its
/// syncs stop until someone signs in again. <paramref name="KeptAliveAt"/> is when a session that ends without calls
/// last answered one, and <paramref name="Accounts"/> labels the accounts it reaches.
/// </summary>
public sealed record SignInView(
    SignInKind Kind,
    DateTime? SignedInAt,
    DateTime? EndsAt,
    bool EndingSoon,
    bool Ended,
    DateTime? EndedAt,
    DateTime? KeptAliveAt,
    IReadOnlyList<string> Accounts);

public sealed record ConnectionRequest(string? Name, string? Connector, IReadOnlyDictionary<string, string?>? Settings);

/// <summary>A connection made from an upload: its name, its connector, its other settings as a JSON object, and the file.</summary>
public sealed class FileConnectionForm
{
    public string? Name { get; set; }

    public string? Connector { get; set; }

    public string? Settings { get; set; }

    public IFormFile? File { get; set; }

    /// <summary>The settings sent as JSON, or null when they are not a JSON object of text.</summary>
    public Dictionary<string, string?>? ParsedSettings()
    {
        try
        {
            return string.IsNullOrWhiteSpace(Settings) ? [] : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string?>>(Settings);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}

public sealed class FileForm
{
    public IFormFile? File { get; set; }
}

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

/// <summary>
/// A workspace's connections, for its members: add one once its settings connect, list its streams, replace the file
/// one reads, and remove it.
/// </summary>
[ApiController]
public sealed class ConnectionsController(
    NexusDbContext db,
    Access access,
    ConnectionMaker maker,
    ConnectionFiles files,
    PendingRuns queue,
    SourceConnectors connectors,
    OperatorLog log) : ControllerBase
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
    [Consumes("application/json")]
    public async Task<ActionResult<ConnectionView>> Add(int workspaceId, ConnectionRequest request, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        return Made(await maker.AddAsync(workspaceId, request.Name, request.Connector, request.Settings, null, null, ct));
    }

    /// <summary>Adds a connection that reads a file, such as CSV, once the connector reads the file uploaded with it.</summary>
    [HttpPost("workspaces/{workspaceId:int}/connections")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(ConnectionFiles.MaxBytes + ConnectionFiles.FormOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ConnectionFiles.MaxBytes + ConnectionFiles.FormOverheadBytes)]
    public async Task<ActionResult<ConnectionView>> AddWithFile(int workspaceId, [FromForm] FileConnectionForm form, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        if (form.ParsedSettings() is not { } settings)
        {
            return Problem("Send the settings as a JSON object of text.", statusCode: StatusCodes.Status400BadRequest);
        }

        var (file, refused) = await files.SaveAsync(form.File, ct);
        return file is null
            ? Problem(refused, statusCode: StatusCodes.Status400BadRequest)
            : Made(await maker.AddAsync(workspaceId, form.Name, form.Connector, settings, file, null, ct));
    }

    /// <summary>
    /// Replaces the file the connection reads once the connector reads the new copy, and queues a run of each of its
    /// syncs that has a destination.
    /// </summary>
    [HttpPut("connections/{id:int}/file")]
    [RequestSizeLimit(ConnectionFiles.MaxBytes + ConnectionFiles.FormOverheadBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = ConnectionFiles.MaxBytes + ConnectionFiles.FormOverheadBytes)]
    public async Task<ActionResult<ConnectionView>> ReplaceFile(int id, [FromForm] FileForm form, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } connection)
        {
            return NotFound();
        }

        var syncs = await db.Syncs.Where(s => s.ConnectionId == id).ToListAsync(ct);
        if (syncs.Any(s => s.Status == SyncStatus.Running))
        {
            return Problem("A sync from this connection is running. Wait for it to end, then replace its file.", statusCode: StatusCodes.Status409Conflict);
        }

        var (file, refused) = await files.SaveAsync(form.File, ct);
        if (file is null)
        {
            return Problem(refused, statusCode: StatusCodes.Status400BadRequest);
        }

        if (await maker.ReplaceFileAsync(connection, file, ct) is { } problem)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var queued = syncs.Where(s => s.DestinationId is not null && s.Status != SyncStatus.Queued).ToList();
        foreach (var sync in queued)
        {
            sync.Status = SyncStatus.Queued;
        }

        await db.SaveChangesAsync(ct);
        foreach (var sync in queued)
        {
            queue.Enqueue(new PendingRun(sync.Id, 1));
        }

        return maker.ViewOf(connection);
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

    /// <summary>Removes the connection with its syncs and its file. The tables at each destination stay.</summary>
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

        var syncs = await db.Syncs.CountAsync(s => s.ConnectionId == id, ct);
        db.Connections.Remove(connection);
        log.Info(
            syncs switch
            {
                0 => $"Connection {LogWords.Connection(connection, connectors)} was removed.",
                1 => $"Connection {LogWords.Connection(connection, connectors)} was removed, with its sync. The table it wrote stays at its destination.",
                _ => $"Connection {LogWords.Connection(connection, connectors)} was removed, with its {syncs} syncs. The tables they wrote stay at their destinations.",
            },
            LogPlace.In(connection.WorkspaceId));
        await db.SaveChangesAsync(ct);
        files.Delete(connection.StoredFile);
        return NoContent();
    }

    private ActionResult<ConnectionView> Made((Connection? Connection, string? Problem) made) =>
        made.Connection is null
            ? Problem(made.Problem, statusCode: StatusCodes.Status400BadRequest)
            : StatusCode(StatusCodes.Status201Created, maker.ViewOf(made.Connection));

    private async Task<Connection?> FindAsync(int id, CancellationToken ct) =>
        await db.Connections.FirstOrDefaultAsync(c => c.Id == id, ct) is { } connection && await access.CanSeeAsync(User, connection.WorkspaceId, ct)
            ? connection
            : null;
}
