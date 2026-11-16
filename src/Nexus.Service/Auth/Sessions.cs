using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Service.Storage;
using Pluperfect.Jwt;

namespace Nexus.Service.Auth;

/// <summary>The claims a signed-in user carries.</summary>
public static class NexusClaims
{
    public const string Admin = "nexus:admin";

    public const string True = "true";

    public const string AdminPolicy = "admin";

    /// <summary>The signed-in user's id.</summary>
    public static int UserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("No user is signed in."), CultureInfo.InvariantCulture);

    public static bool IsAdmin(this ClaimsPrincipal user) => user.HasClaim(Admin, True);
}

/// <summary>
/// Pluperfect.Jwt's cookie session for a user: a short access token, and a refresh token that rotates, scoped to the
/// auth routes. Modeled on PokéStory Site's sign-in service.
/// </summary>
public sealed class Sessions(
    NexusDbContext db,
    JwtIssuer jwt,
    IRefreshTokenStore refreshTokens,
    IOptions<PluperfectJwtOptions> options,
    IHttpContextAccessor context,
    IWebHostEnvironment environment,
    TimeProvider time)
{
    private PluperfectJwtOptions Options => options.Value;

    // The same answer Program gives AddPluperfectJwt, so the cookie written is the one the library reads.
    private bool SecureOrigin => !environment.IsDevelopment();

    private HttpContext Http => context.HttpContext ?? throw new InvalidOperationException("No request to sign in against.");

    public async Task SignInAsync(User user, bool persistent, CancellationToken ct)
    {
        user.SignedInAt = time.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        WriteAccessCookie(user);
        var token = RefreshTokens.Create();
        await refreshTokens.CreateAsync(
            Subject(user),
            user.Email,
            RefreshTokens.Hash(token),
            time.GetUtcNow().AddDays(Options.RefreshTokenDays),
            Http.Connection.RemoteIpAddress?.ToString(),
            persistent,
            ct);
        WriteRefreshCookie(token, persistent);
    }

    /// <summary>
    /// Trades the refresh cookie for a new pair, reading the user again so a removed user or a changed role takes
    /// effect. Answers the user, or null when the session is gone.
    /// </summary>
    public async Task<User?> RefreshAsync(CancellationToken ct)
    {
        var presented = Http.Request.Cookies[Options.RefreshCookieName];
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        var replacement = RefreshTokens.Create();
        var result = await refreshTokens.RedeemAsync(
            RefreshTokens.Hash(presented),
            RefreshTokens.Hash(replacement),
            time.GetUtcNow().AddDays(Options.RefreshTokenDays),
            Http.Connection.RemoteIpAddress?.ToString(),
            ct);
        if (result.Outcome != RefreshOutcome.Ok)
        {
            ClearCookies();
            return null;
        }

        var id = int.Parse(result.SubjectId!, CultureInfo.InvariantCulture);
        if (await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) is not { } user)
        {
            await refreshTokens.RevokeAllAsync(result.SubjectId!, "The user was removed.", ct);
            ClearCookies();
            return null;
        }

        WriteAccessCookie(user);
        WriteRefreshCookie(replacement, result.Persistent);
        return user;
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        var presented = Http.Request.Cookies[Options.RefreshCookieName];
        if (!string.IsNullOrEmpty(presented))
        {
            await refreshTokens.RevokeAsync(RefreshTokens.Hash(presented), "Signed out.", ct);
        }

        ClearCookies();
    }

    /// <summary>Ends every session the user has, everywhere.</summary>
    public Task EndAllAsync(User user, string reason, CancellationToken ct) => refreshTokens.RevokeAllAsync(Subject(user), reason, ct);

    private static string Subject(User user) => user.Id.ToString(CultureInfo.InvariantCulture);

    private void WriteAccessCookie(User user)
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, Subject(user)),
            new(ClaimTypes.Name, user.Email),
            new(NexusClaims.Admin, user.IsAdmin ? NexusClaims.True : "false"),
        ];
        Http.Response.Cookies.Append(
            CookieTransport.NameFor(Options.AccessCookieName, SecureOrigin),
            jwt.Issue(claims, time.GetUtcNow().AddMinutes(Options.AccessTokenMinutes)),
            CookieTransport.Access(SecureOrigin));
    }

    // A session that is not kept past the browser's window gets a refresh cookie with no expiry.
    private void WriteRefreshCookie(string token, bool persistent) =>
        Http.Response.Cookies.Append(
            Options.RefreshCookieName,
            token,
            CookieTransport.Refresh(SecureOrigin, Options.RefreshCookiePath, persistent ? time.GetUtcNow().AddDays(Options.RefreshTokenDays) : null));

    // Each cookie is cleared with the path it was written with; a mismatched path leaves the original in place.
    private void ClearCookies()
    {
        Http.Response.Cookies.Delete(CookieTransport.NameFor(Options.AccessCookieName, SecureOrigin), CookieTransport.Clearing(SecureOrigin, "/"));
        Http.Response.Cookies.Delete(Options.RefreshCookieName, CookieTransport.Clearing(SecureOrigin, Options.RefreshCookiePath));
    }
}
