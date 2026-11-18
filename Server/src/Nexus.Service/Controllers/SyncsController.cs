using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

public sealed record RunView(long Id, int Attempt, RunStatus Status, DateTime StartedAt, DateTime? EndedAt, long Rows, ConnectorErrorKind? ErrorKind, string? Error);

/// <summary>
/// A sync, where it reads and where it writes, and how its last run went. <paramref name="SignIn"/> is its connection's
/// sign-in, null for a connector that does not sign in. The destination's fields are null once its destination is
/// removed, and <paramref name="DestinationKeyName"/> and <paramref name="DestinationKeyPrefix"/> name the push key as
/// the push API last reported it.
/// </summary>
public sealed record SyncView(
    int Id,
    int ConnectionId,
    string ConnectionName,
    string ConnectorName,
    SignInView? SignIn,
    int? DestinationId,
    string? DestinationUrl,
    string? AppName,
    string? DestinationKeyName,
    string? DestinationKeyPrefix,
    string Stream,
    string Table,
    SyncMode Mode,
    string? CursorField,
    SyncStatus Status,
    RunView? LastRun);

public sealed record SyncStreamRequest(string? Stream, string? Table, SyncMode Mode, string? CursorField);

public sealed record SyncsRequest(int DestinationId, IReadOnlyList<SyncStreamRequest>? Streams);

public sealed record SyncEditRequest(int DestinationId, string? Table, SyncMode Mode, string? CursorField);

/// <summary>
/// Syncs. A workspace's members add them from a connection's streams, change where one writes and how it reads, run
/// one now, see its runs, and remove it. An app, by its key, lists the syncs into its destinations, runs one now, and
/// removes one.
/// </summary>
[ApiController]
public sealed class SyncsController(
    NexusDbContext db,
    Access access,
    SyncMaker maker,
    SourceConnectors connectors,
    SignInState signIns,
    PendingRuns queue,
    OperatorLog log) : ControllerBase
{
    private const int MaxRuns = 20;

    private const string OtherWorkspace = "Pick a destination of an app in this workspace.";

    [HttpGet("workspaces/{workspaceId:int}/syncs")]
    public async Task<ActionResult<IEnumerable<SyncView>>> List(int workspaceId, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct))
        {
            return NotFound();
        }

        return await ViewsAsync(Loaded().Where(s => s.Connection!.WorkspaceId == workspaceId), ct);
    }

    /// <summary>The calling app's syncs, into any of its destinations, each with how its last run went.</summary>
    [HttpGet("syncs")]
    [Authorize(Policy = AppKeyHandler.AppPolicy)]
    public async Task<IEnumerable<SyncView>> ListForApp(CancellationToken ct)
    {
        var appId = User.AppId();
        return await ViewsAsync(Loaded().Where(s => s.Destination!.AppId == appId), ct);
    }

    /// <summary>Adds a sync for each picked stream, and queues the first run of each.</summary>
    [HttpPost("connections/{connectionId:int}/syncs")]
    public async Task<ActionResult<IEnumerable<SyncView>>> Add(int connectionId, SyncsRequest request, CancellationToken ct)
    {
        var connection = await db.Connections.FirstOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null || !await access.CanSeeAsync(User, connection.WorkspaceId, ct))
        {
            return NotFound();
        }

        if (await InWorkspaceAsync(request.DestinationId, connection.WorkspaceId, ct) is not { } destination)
        {
            return Problem(OtherWorkspace, statusCode: StatusCodes.Status400BadRequest);
        }

        var (syncs, problem) = await maker.AddAsync(connection, destination, request.Streams, null, ct);
        return syncs is null
            ? Problem(problem, statusCode: StatusCodes.Status400BadRequest)
            : StatusCode(StatusCodes.Status201Created, syncs.Select(s => View(s, null)).ToList());
    }

    [HttpPost("syncs/{id:int}/run")]
    [Authorize(Policy = AppKeyHandler.UserOrAppPolicy)]
    public async Task<ActionResult<SyncView>> Run(int id, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } sync)
        {
            return NotFound();
        }

        if (sync.DestinationId is null)
        {
            return Problem("Pick a destination for this sync first.", statusCode: StatusCodes.Status409Conflict);
        }

        if (sync.Status is SyncStatus.Queued or SyncStatus.Running)
        {
            return Problem("This sync is already queued or running.", statusCode: StatusCodes.Status409Conflict);
        }

        if (sync.Connection!.NeedsSignIn)
        {
            return Problem(SyncRunner.SignInEnded, statusCode: StatusCodes.Status409Conflict);
        }

        sync.Status = SyncStatus.Queued;
        log.Info($"Sync {sync.Table} was asked to run now.", LogPlace.On(sync, sync.Connection!));
        await db.SaveChangesAsync(ct);
        queue.Enqueue(new PendingRun(sync.Id, 1));
        return StatusCode(StatusCodes.Status202Accepted, View(sync, await LastRunAsync(sync.Id, ct)));
    }

    [HttpGet("syncs/{id:int}/runs")]
    public async Task<ActionResult<IEnumerable<RunView>>> Runs(int id, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is null)
        {
            return NotFound();
        }

        var runs = await db.Runs.Where(r => r.SyncId == id).OrderByDescending(r => r.Id).Take(MaxRuns).ToListAsync(ct);
        return runs.Select(RunOf).ToList();
    }

    /// <summary>
    /// Changes the sync's destination, table name, or read mode, and queues a run that reads from the start. The table
    /// it wrote before stays where it is.
    /// </summary>
    [HttpPatch("syncs/{id:int}")]
    public async Task<ActionResult<SyncView>> Edit(int id, SyncEditRequest request, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } sync)
        {
            return NotFound();
        }

        if (sync.Status is SyncStatus.Queued or SyncStatus.Running)
        {
            return Problem("This sync is queued or running. Wait for it to end, then change it.", statusCode: StatusCodes.Status409Conflict);
        }

        if (await InWorkspaceAsync(request.DestinationId, sync.Connection!.WorkspaceId, ct) is not { } destination)
        {
            return Problem(OtherWorkspace, statusCode: StatusCodes.Status400BadRequest);
        }

        var edit = new SyncStreamRequest(sync.Stream, request.Table, request.Mode, request.CursorField);
        return await maker.EditAsync(sync, destination, edit, ct) is { } problem
            ? Problem(problem, statusCode: StatusCodes.Status400BadRequest)
            : View(sync, await LastRunAsync(sync.Id, ct));
    }

    /// <summary>Removes the sync and its runs. The table at the destination stays.</summary>
    [HttpDelete("syncs/{id:int}")]
    [Authorize(Policy = AppKeyHandler.UserOrAppPolicy)]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        if (await FindAsync(id, ct) is not { } sync)
        {
            return NotFound();
        }

        if (sync.Status == SyncStatus.Running)
        {
            return Problem("This sync is running. Wait for it to end, then remove it.", statusCode: StatusCodes.Status409Conflict);
        }

        db.Syncs.Remove(sync);
        log.Info(
            sync.Destination is { } destination
                ? $"Sync {sync.Table} was removed. Its table stays at {LogWords.Destination(destination, destination.App!.Name)}."
                : $"Sync {sync.Table} was removed.",
            LogPlace.On(sync.Connection!));
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private async Task<Destination?> InWorkspaceAsync(int destinationId, int workspaceId, CancellationToken ct) =>
        await db.Destinations.Include(d => d.App).FirstOrDefaultAsync(d => d.Id == destinationId && d.App!.WorkspaceId == workspaceId, ct);

    private IQueryable<Sync> Loaded() =>
        db.Syncs.Include(s => s.Connection).Include(s => s.Destination).ThenInclude(d => d!.App);

    // An app reaches only the syncs into its own destinations; a user, those in workspaces they may see.
    private async Task<Sync?> FindAsync(int id, CancellationToken ct)
    {
        var sync = await Loaded().FirstOrDefaultAsync(s => s.Id == id, ct);
        if (sync is null)
        {
            return null;
        }

        var allowed = User.AppId() is { } appId ? sync.Destination?.AppId == appId : await access.CanSeeAsync(User, sync.Connection!.WorkspaceId, ct);
        return allowed ? sync : null;
    }

    private async Task<List<SyncView>> ViewsAsync(IQueryable<Sync> query, CancellationToken ct)
    {
        var syncs = await query.OrderBy(s => s.Table).ToListAsync(ct);
        var ids = syncs.Select(s => s.Id).ToList();
        var lastRuns = (await db.Runs.Where(r => ids.Contains(r.SyncId)).ToListAsync(ct))
            .GroupBy(r => r.SyncId)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.Id)!);
        return [.. syncs.Select(s => View(s, lastRuns.TryGetValue(s.Id, out var run) ? RunOf(run) : null))];
    }

    private async Task<RunView?> LastRunAsync(int syncId, CancellationToken ct) =>
        await db.Runs.Where(r => r.SyncId == syncId).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct) is { } run ? RunOf(run) : null;

    private static RunView RunOf(Run run) => new(run.Id, run.Attempt, run.Status, run.StartedAt, run.EndedAt, run.Rows, run.ErrorKind, run.Error);

    private SyncView View(Sync sync, RunView? lastRun)
    {
        var connection = sync.Connection!;
        var destination = sync.Destination;
        return new(
            sync.Id,
            sync.ConnectionId,
            connection.Name,
            connectors.Find(connection.Connector)?.Spec.DisplayName ?? connection.Connector,
            signIns.ViewOf(connection),
            sync.DestinationId,
            destination?.Url,
            destination?.App?.Name,
            destination?.KeyName,
            destination?.KeyPrefix,
            sync.Stream,
            sync.Table,
            sync.Mode,
            sync.CursorField,
            sync.Status,
            lastRun);
    }
}
