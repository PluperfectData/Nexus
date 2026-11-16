using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using Pluperfect.Mail;

namespace Nexus.Service.Controllers;

public sealed record UserView(int Id, string Email, bool IsAdmin, bool HasPassword, DateTime CreatedAt, DateTime? SignedInAt, IReadOnlyList<int> Workspaces);

public sealed record InviteRequest(string? Email, bool IsAdmin);

public sealed record UserEdit(bool IsAdmin);

/// <summary>
/// An invite, mailed or not. <c>Link</c> is the invite link for the admin to pass on, given when it was not mailed:
/// the instance has no mail set up, or <c>MailProblem</c> says why sending failed.
/// </summary>
public sealed record InviteView(UserView User, string? Link, string? MailProblem);

/// <summary>A new invite link, given when it was not mailed, with the reason.</summary>
public sealed record InviteLinkView(string? Link, string? MailProblem);

/// <summary>The instance's users, for admins: invite one, make or unmake an admin, and remove one.</summary>
[ApiController]
[Route("users")]
[Authorize(Policy = NexusClaims.AdminPolicy)]
public sealed partial class UsersController(NexusDbContext db, LinkMail mail, Sessions sessions, Instance instance, TimeProvider time, ILogger<UsersController> logger)
    : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<UserView>> List(CancellationToken ct)
    {
        var users = await db.Users.OrderBy(u => u.Email).ToListAsync(ct);
        var members = await db.WorkspaceMembers.ToListAsync(ct);
        return users.Select(user => View(user, members));
    }

    /// <summary>Makes a user and mails them an invite link, or answers the link when it cannot be mailed.</summary>
    [HttpPost]
    public async Task<ActionResult<InviteView>> Invite(InviteRequest request, CancellationToken ct)
    {
        if (Emails.Normalize(request.Email) is not { } email)
        {
            return Problem("Enter an email address.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return Problem($"{email} already has an account.", statusCode: StatusCodes.Status409Conflict);
        }

        var user = new User { Email = email, IsAdmin = request.IsAdmin, LinkStamp = SignInLinks.NewStamp(), CreatedAt = time.GetUtcNow().UtcDateTime };
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        var (link, problem) = await DeliverAsync(user, ct);
        return StatusCode(StatusCodes.Status201Created, new InviteView(View(user, []), link, problem));
    }

    /// <summary>Mails the user a new invite link, for one who lost theirs, or answers the link when it cannot be mailed.</summary>
    [HttpPost("{id:int}/invite")]
    public async Task<ActionResult<InviteLinkView>> Reinvite(int id, CancellationToken ct)
    {
        if (await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) is not { } user)
        {
            return NotFound();
        }

        var (link, problem) = await DeliverAsync(user, ct);
        return new InviteLinkView(link, problem);
    }

    // Answers the link when it was not mailed, and why sending failed when it was tried.
    private async Task<(string? Link, string? MailProblem)> DeliverAsync(User user, CancellationToken ct)
    {
        var link = mail.InviteFor(user);
        if (!instance.MailReady)
        {
            return (link, null);
        }

        try
        {
            await mail.SendInviteAsync(user, link, ct);
            return (null, null);
        }
        catch (MailDeliveryException ex)
        {
            LogUndelivered(logger, ex, user.Email);
            return (link, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not mail an invite to {Email}.")]
    private static partial void LogUndelivered(ILogger logger, Exception ex, string email);

    [HttpPatch("{id:int}")]
    public async Task<ActionResult<UserView>> Edit(int id, UserEdit edit, CancellationToken ct)
    {
        if (await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) is not { } user)
        {
            return NotFound();
        }

        if (user.IsAdmin && !edit.IsAdmin && await IsLastAdminAsync(user, ct))
        {
            return Problem("An instance keeps at least one admin. Make someone else an admin first.", statusCode: StatusCodes.Status409Conflict);
        }

        user.IsAdmin = edit.IsAdmin;
        await db.SaveChangesAsync(ct);
        return View(user, await db.WorkspaceMembers.Where(m => m.UserId == id).ToListAsync(ct));
    }

    /// <summary>Removes the user and ends their sessions.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Remove(int id, CancellationToken ct)
    {
        if (await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) is not { } user)
        {
            return NotFound();
        }

        if (user.Id == User.UserId())
        {
            return Problem("You can't remove yourself.", statusCode: StatusCodes.Status409Conflict);
        }

        if (user.IsAdmin && await IsLastAdminAsync(user, ct))
        {
            return Problem("An instance keeps at least one admin.", statusCode: StatusCodes.Status409Conflict);
        }

        await sessions.EndAllAsync(user, "The user was removed.", ct);
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Task<bool> IsLastAdminAsync(User user, CancellationToken ct) => db.Users.AllAsync(u => !u.IsAdmin || u.Id == user.Id, ct);

    private static UserView View(User user, IEnumerable<WorkspaceMember> members) =>
        new(user.Id, user.Email, user.IsAdmin, user.PasswordHash is not null, user.CreatedAt, user.SignedInAt,
            [.. members.Where(m => m.UserId == user.Id).Select(m => m.WorkspaceId).Order()]);
}
