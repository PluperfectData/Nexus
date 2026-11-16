using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using Pluperfect.Mail;

namespace Nexus.Service.Controllers;

public sealed record SignInRequest(string? Email, string? Password, bool Remember);

public sealed record LinkRequest(string? Email);

public sealed record RedeemRequest(string? Token, bool Remember);

public sealed record PasswordRequest(string? Password);

/// <summary>Who is signed in, for the client.</summary>
public sealed record MeView(bool SignedIn, int? Id, string? Email, bool IsAdmin, bool HasPassword);

/// <summary>Signing in with a password or a mailed link, refreshing the session, signing out, and setting a password.</summary>
[ApiController]
[Route("auth")]
public sealed partial class AuthController(
    NexusDbContext db,
    Sessions sessions,
    SignInLinks links,
    LinkMail mail,
    Instance instance,
    SignInThrottle throttle,
    IAntiforgery antiforgery,
    ILogger<AuthController> logger) : ControllerBase
{
    private const string Refused = "That email and password do not match.";

    // The field is `token`, the name Pluperfect.Jwt's client reads.
    [HttpGet("csrf")]
    [AllowAnonymous]
    public IActionResult Csrf() => Ok(new { token = CsrfToken() });

    [HttpGet("me")]
    [AllowAnonymous]
    public async Task<MeView> Me(CancellationToken ct)
    {
        if (User.Identity?.IsAuthenticated != true || await db.Users.FirstOrDefaultAsync(u => u.Id == User.UserId(), ct) is not { } user)
        {
            return new MeView(false, null, null, false, false);
        }

        return View(user);
    }

    [HttpPost("sign-in")]
    [AllowAnonymous]
    public async Task<ActionResult<MeView>> SignIn(SignInRequest request, CancellationToken ct)
    {
        var email = Emails.Normalize(request.Email);
        if (email is null || string.IsNullOrEmpty(request.Password))
        {
            return Problem(Refused, statusCode: StatusCodes.Status401Unauthorized);
        }

        if (throttle.IsLockedOut(email))
        {
            return Problem(
                "Too many tries for this address. Wait 15 minutes, or sign in with a mailed link.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var (matches, rehash) = user is null ? (false, false) : Passwords.Check(user, request.Password);
        if (!matches)
        {
            throttle.RecordFailure(email);
            return Problem(Refused, statusCode: StatusCodes.Status401Unauthorized);
        }

        throttle.Reset(email);
        if (rehash)
        {
            user!.PasswordHash = Passwords.Hash(user, request.Password);
        }

        await sessions.SignInAsync(user!, request.Remember, ct);
        return View(user!);
    }

    /// <summary>Mails a sign-in link to the address if it belongs to a user. The answer is the same either way.</summary>
    [HttpPost("link")]
    [AllowAnonymous]
    public async Task<IActionResult> Link(LinkRequest request, CancellationToken ct)
    {
        var email = Emails.Normalize(request.Email);
        if (email is null)
        {
            return Problem("Enter an email address.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (!instance.MailReady)
        {
            return Problem("This instance has no mail set up, so it cannot mail links. Sign in with your password.", statusCode: StatusCodes.Status409Conflict);
        }

        if (throttle.MayMailLink(email) && await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct) is { } user)
        {
            // Answered the same either way, so the answer never says whether the address has an account.
            try
            {
                await mail.SendSignInAsync(user, ct);
            }
            catch (MailDeliveryException ex)
            {
                LogUndelivered(logger, ex, email);
            }
        }

        return Accepted();
    }

    [HttpPost("link/redeem")]
    [AllowAnonymous]
    public async Task<ActionResult<MeView>> Redeem(RedeemRequest request, CancellationToken ct)
    {
        var claim = links.Read(request.Token);
        var user = claim is null ? null : await db.Users.FirstOrDefaultAsync(u => u.Id == claim.UserId, ct);
        if (user is null || !SignInLinks.IsCurrent(claim!, user))
        {
            return Problem("This link has expired or was already used. Ask for a new one.", statusCode: StatusCodes.Status400BadRequest);
        }

        user.LinkStamp = SignInLinks.NewStamp();
        await sessions.SignInAsync(user, request.Remember, ct);
        return View(user);
    }

    // Anonymous, since an expired access token is the reason to call it; the refresh cookie is the credential.
    [HttpPost("refresh")]
    [AllowAnonymous]
    public async Task<ActionResult<MeView>> Refresh(CancellationToken ct) =>
        await sessions.RefreshAsync(ct) is { } user ? View(user) : Problem("Sign in again.", statusCode: StatusCodes.Status401Unauthorized);

    [HttpPost("sign-out")]
    [AllowAnonymous]
    public async Task<IActionResult> SignOut(CancellationToken ct)
    {
        await sessions.SignOutAsync(ct);
        return NoContent();
    }

    /// <summary>Sets the signed-in user's password, which ends their other sessions. A mailed link is how a forgotten one is replaced.</summary>
    [HttpPut("password")]
    public async Task<ActionResult<MeView>> SetPassword(PasswordRequest request, CancellationToken ct)
    {
        if (Passwords.Problem(request.Password) is { } problem)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await db.Users.FirstAsync(u => u.Id == User.UserId(), ct);
        user.PasswordHash = Passwords.Hash(user, request.Password!);
        await db.SaveChangesAsync(ct);
        await sessions.EndAllAsync(user, "The password changed.", ct);
        await sessions.SignInAsync(user, persistent: false, ct);
        return View(user);
    }

    private static MeView View(User user) => new(true, user.Id, user.Email, user.IsAdmin, user.PasswordHash is not null);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not mail a sign-in link to {Email}.")]
    private static partial void LogUndelivered(ILogger logger, Exception ex, string email);

    private string CsrfToken() => antiforgery.GetAndStoreTokens(HttpContext).RequestToken!;
}
