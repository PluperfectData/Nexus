using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Nexus.Service.Storage;
using Pluperfect.Jwt;

namespace Nexus.Service.Auth;

/// <summary>An app's key, sent as <c>Authorization: Bearer nexus_...</c>, as an authentication scheme of its own.</summary>
public sealed class AppKeyHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, NexusDbContext db)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "AppKey";

    public const string AppClaim = "nexus:app";

    /// <summary>Signed-in users and apps both reach these routes, each by its own credential.</summary>
    public const string UserOrAppPolicy = "user-or-app";

    public const string AppPolicy = "app";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer " + AppKeys.Lead, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }

        var hash = AppKeys.HashOf(header["Bearer ".Length..].Trim());
        if (await db.Apps.FirstOrDefaultAsync(a => a.KeyHash == hash, Context.RequestAborted) is not { } app)
        {
            return AuthenticateResult.Fail("The app key is not one this service made, or it was revoked.");
        }

        var identity = new ClaimsIdentity([new Claim(AppClaim, app.Id.ToString(CultureInfo.InvariantCulture))], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class AppClaims
{
    /// <summary>The calling app's id, when an app key authenticated the request.</summary>
    public static int? AppId(this ClaimsPrincipal user) =>
        user.FindFirstValue(AppKeyHandler.AppClaim) is { } id ? int.Parse(id, CultureInfo.InvariantCulture) : null;
}

/// <summary>
/// Pluperfect.Jwt's antiforgery check, except on a request that carries an <c>Authorization</c> header. A browser sends
/// that header cross-site only after a CORS preflight, which the service answers for the client's origin alone, so such
/// a request cannot be forged by another site.
/// </summary>
public sealed class BearerAwareAntiforgery(IAntiforgery antiforgery) : IAsyncAuthorizationFilter
{
    private readonly AntiforgeryFilter _inner = new(antiforgery, "/auth/csrf");

    public Task OnAuthorizationAsync(AuthorizationFilterContext context) =>
        context.HttpContext.Request.Headers.Authorization.Count > 0 ? Task.CompletedTask : _inner.OnAuthorizationAsync(context);
}
