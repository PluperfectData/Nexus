using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

public sealed record ReminderEmailCandidate(int Id, string Email, bool IsAdmin);

/// <summary>
/// Who a workspace mails when a connection's sign-in is about to end, or has ended. <paramref name="People"/> are those
/// named, chosen among <paramref name="Candidates"/>: its members and the admins. Without <paramref name="MailReady"/>,
/// nothing is sent.
/// </summary>
public sealed record ReminderEmailView(SignInMail Choice, IReadOnlyList<int> People, IReadOnlyList<ReminderEmailCandidate> Candidates, bool MailReady);

public sealed record ReminderEmailRequest(SignInMail Choice, IReadOnlyList<int>? People);

/// <summary>A workspace's session expiry reminder email, which its members set.</summary>
[ApiController]
[Route("workspaces/{workspaceId:int}/reminder-email")]
public sealed class ReminderEmailController(NexusDbContext db, Access access, Instance instance, OperatorLog log) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<ReminderEmailView>> Get(int workspaceId, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct) || await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId, ct) is not { } workspace)
        {
            return NotFound();
        }

        return await ViewAsync(workspace, ct);
    }

    [HttpPut]
    public async Task<ActionResult<ReminderEmailView>> Set(int workspaceId, ReminderEmailRequest request, CancellationToken ct)
    {
        if (!await access.CanSeeAsync(User, workspaceId, ct) || await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspaceId, ct) is not { } workspace)
        {
            return NotFound();
        }

        var candidates = await CandidatesAsync(workspaceId, ct);
        var people = request.Choice == SignInMail.Named ? (request.People ?? []).Distinct().ToList() : [];
        if (request.Choice == SignInMail.Named && people.Count == 0)
        {
            return Problem("Pick at least one person to mail.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (people.Any(id => candidates.All(c => c.Id != id)))
        {
            return Problem("Pick people among the workspace's members and the admins.", statusCode: StatusCodes.Status400BadRequest);
        }

        workspace.SignInMail = request.Choice;
        await db.SignInMailPeople.Where(p => p.WorkspaceId == workspaceId).ExecuteDeleteAsync(ct);
        db.SignInMailPeople.AddRange(people.Select(id => new SignInMailPerson { WorkspaceId = workspaceId, UserId = id }));
        var who = request.Choice switch
        {
            SignInMail.Everyone => "every member and admin",
            SignInMail.Admins => "the admins",
            SignInMail.Named => LogWords.List([.. candidates.Where(c => people.Contains(c.Id)).Select(c => c.Email)]),
            _ => "no one",
        };
        log.Info($"Mail about this workspace's sign-ins now goes to {who}.", LogPlace.In(workspace));
        await db.SaveChangesAsync(ct);
        return await ViewAsync(workspace, ct);
    }

    private async Task<ReminderEmailView> ViewAsync(Workspace workspace, CancellationToken ct) =>
        new(
            workspace.SignInMail,
            await db.SignInMailPeople.Where(p => p.WorkspaceId == workspace.Id).Select(p => p.UserId).OrderBy(id => id).ToListAsync(ct),
            await CandidatesAsync(workspace.Id, ct),
            instance.MailReady);

    private async Task<List<ReminderEmailCandidate>> CandidatesAsync(int workspaceId, CancellationToken ct)
    {
        var members = db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).Select(m => m.UserId);
        return await db.Users
            .Where(u => u.IsAdmin || members.Contains(u.Id))
            .OrderBy(u => u.Email)
            .Select(u => new ReminderEmailCandidate(u.Id, u.Email, u.IsAdmin))
            .ToListAsync(ct);
    }
}
