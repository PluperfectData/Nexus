using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

public sealed record WorkspaceView(int Id, string Name, DateTime CreatedAt);

public sealed record MemberView(int Id, string Email);

public sealed record WorkspaceDetail(int Id, string Name, DateTime CreatedAt, IReadOnlyList<MemberView> Members);

public sealed record WorkspaceRequest(string? Name);

/// <summary>Workspaces: every one for an admin, and a member's own for a member. Admins make, rename, and remove them, and choose their members.</summary>
[ApiController]
[Route("workspaces")]
public sealed class WorkspacesController(NexusDbContext db, Access access, ConnectionFiles files, OperatorLog log, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<WorkspaceView>> List(CancellationToken ct)
    {
        var id = User.UserId();
        var visible = User.IsAdmin()
            ? db.Workspaces
            : db.Workspaces.Where(w => db.WorkspaceMembers.Any(m => m.WorkspaceId == w.Id && m.UserId == id));
        return await visible.OrderBy(w => w.Name).Select(w => new WorkspaceView(w.Id, w.Name, w.CreatedAt)).ToListAsync(ct);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<WorkspaceDetail>> Get(int id, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, id, ct))
        {
            return NotFound();
        }

        return await DetailAsync(id, ct);
    }

    [HttpPost]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<WorkspaceDetail>> Make(WorkspaceRequest request, CancellationToken ct)
    {
        if (NameOf(request) is not { } name)
        {
            return Problem($"A workspace needs a name of {Workspace.MaxNameLength} characters or fewer.", statusCode: StatusCodes.Status400BadRequest);
        }

        var workspace = new Workspace { Name = name, CreatedAt = time.GetUtcNow().UtcDateTime };
        db.Workspaces.Add(workspace);
        log.Info($"Workspace {name} was made.", LogPlace.In(workspace));
        await db.SaveChangesAsync(ct);
        return StatusCode(StatusCodes.Status201Created, await DetailAsync(workspace.Id, ct));
    }

    [HttpPatch("{id:int}")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<WorkspaceDetail>> Rename(int id, WorkspaceRequest request, CancellationToken ct)
    {
        if (await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct) is not { } workspace)
        {
            return NotFound();
        }

        if (NameOf(request) is not { } name)
        {
            return Problem($"A workspace needs a name of {Workspace.MaxNameLength} characters or fewer.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (workspace.Name != name)
        {
            log.Info($"Workspace {workspace.Name} was renamed {name}.", LogPlace.In(workspace.Id));
        }

        workspace.Name = name;
        await db.SaveChangesAsync(ct);
        return await DetailAsync(id, ct);
    }

    /// <summary>Removes the workspace with its apps, connections, and syncs. The tables at each destination stay.</summary>
    [HttpDelete("{id:int}")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        if (await db.Syncs.AnyAsync(s => s.Connection!.WorkspaceId == id && s.Status == SyncStatus.Running, ct))
        {
            return Problem("A sync in this workspace is running. Wait for it to end, then remove the workspace.", statusCode: StatusCodes.Status409Conflict);
        }

        if (await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct) is not { } workspace)
        {
            return NotFound();
        }

        var stored = await db.Connections.Where(c => c.WorkspaceId == id && c.StoredFile != null).Select(c => c.StoredFile).ToListAsync(ct);
        if (await db.Workspaces.Where(w => w.Id == id).ExecuteDeleteAsync(ct) == 0)
        {
            return NotFound();
        }

        stored.ForEach(files.Delete);
        log.Info($"Workspace {workspace.Name} was removed, with its apps, connections, and syncs. The tables at each destination stay.", LogPlace.Instance);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPut("{id:int}/members/{userId:int}")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<WorkspaceDetail>> AddMember(int id, int userId, CancellationToken ct)
    {
        var workspace = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (workspace is null || user is null)
        {
            return NotFound();
        }

        if (!await db.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == id && m.UserId == userId, ct))
        {
            db.WorkspaceMembers.Add(new WorkspaceMember { WorkspaceId = id, UserId = userId });
            log.Info($"{user.Email} was added to workspace {workspace.Name}.", LogPlace.In(id));
            await db.SaveChangesAsync(ct);
        }

        return await DetailAsync(id, ct);
    }

    [HttpDelete("{id:int}/members/{userId:int}")]
    [Authorize(Policy = NexusClaims.AdminPolicy)]
    public async Task<ActionResult<WorkspaceDetail>> RemoveMember(int id, int userId, CancellationToken ct)
    {
        if (await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct) is not { } workspace)
        {
            return NotFound();
        }

        var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        if (await db.WorkspaceMembers.Where(m => m.WorkspaceId == id && m.UserId == userId).ExecuteDeleteAsync(ct) > 0)
        {
            log.Info($"{email} was taken out of workspace {workspace.Name}.", LogPlace.In(id));
            await db.SaveChangesAsync(ct);
        }

        return await DetailAsync(id, ct);
    }

    private async Task<WorkspaceDetail> DetailAsync(int id, CancellationToken ct)
    {
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == id, ct);
        var members = await db.WorkspaceMembers
            .Where(m => m.WorkspaceId == id)
            .Select(m => new MemberView(m.UserId, m.User!.Email))
            .ToListAsync(ct);
        return new WorkspaceDetail(workspace.Id, workspace.Name, workspace.CreatedAt, [.. members.OrderBy(m => m.Email, StringComparer.Ordinal)]);
    }

    private static string? NameOf(WorkspaceRequest request) =>
        request.Name?.Trim() is { Length: > 0 and <= Workspace.MaxNameLength } name ? name : null;
}
