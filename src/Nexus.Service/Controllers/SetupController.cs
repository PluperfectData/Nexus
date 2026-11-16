using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;

namespace Nexus.Service.Controllers;

public sealed record SetupRequest(string? Code, string? Email, string? Password);

public sealed record SetupView(bool Needed);

/// <summary>Claims an instance with no users: the one-time code from the service's log makes the first admin.</summary>
[ApiController]
[Route("setup")]
[AllowAnonymous]
public sealed class SetupController(NexusDbContext db, SetupCode code, Sessions sessions, SignInThrottle throttle, TimeProvider time) : ControllerBase
{
    // One budget for every try, since the code, not an address, is what is being guessed.
    private const string ThrottleKey = "setup";

    [HttpGet]
    public async Task<SetupView> Get(CancellationToken ct) => new(!await db.Users.AnyAsync(ct));

    [HttpPost]
    public async Task<ActionResult<MeView>> Claim(SetupRequest request, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
        {
            return Problem("This instance is set up. Sign in instead.", statusCode: StatusCodes.Status409Conflict);
        }

        if (throttle.IsLockedOut(ThrottleKey))
        {
            return Problem("Too many wrong codes. Wait 15 minutes, then try again.", statusCode: StatusCodes.Status429TooManyRequests);
        }

        if (Emails.Normalize(request.Email) is not { } email)
        {
            return Problem("Enter an email address.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (Passwords.Problem(request.Password) is { } problem)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        if (!code.TrySpend(request.Code))
        {
            throttle.RecordFailure(ThrottleKey);
            return Problem(
                "That is not this instance's setup code. The service writes the code to its log when it starts with no users.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var admin = new User { Email = email, IsAdmin = true, LinkStamp = SignInLinks.NewStamp(), CreatedAt = time.GetUtcNow().UtcDateTime };
        admin.PasswordHash = Passwords.Hash(admin, request.Password!);
        db.Users.Add(admin);
        await db.SaveChangesAsync(ct);
        await sessions.SignInAsync(admin, persistent: false, ct);
        return new MeView(true, admin.Id, admin.Email, true, true);
    }
}
