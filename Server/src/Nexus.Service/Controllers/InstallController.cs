using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using Pluperfect.Jwt;

namespace Nexus.Service.Controllers;

public sealed record DatabaseRequest(string? Host, int? Port, string? Name, string? User, string? Password);

public sealed record InstallAddressRequest(string? ClientOrigin);

public sealed record InstallAdminRequest(string? Email, string? Password);

/// <summary>A database's place, without its password.</summary>
public sealed record DatabaseView(string Host, int Port, string Name, string User);

/// <summary>
/// Where the instance stands, for the installer page. <c>Local</c> says the page is on the machine the service runs
/// on, which needs no code; <c>Allowed</c> says this request may use the installer, and only then is the database shown.
/// </summary>
public sealed record InstallView(
    bool Installed,
    InstallStage Stage,
    bool Local,
    bool Allowed,
    bool DatabaseConfigured,
    DatabaseView? Database,
    string? DatabaseProblem,
    bool ClientOriginConfigured,
    bool Mail);

/// <summary>
/// The first-run installer: the database, then the client's address, then the first admin. On an instance already set
/// up, it makes or restores an admin for someone locked out. A page on the machine the service runs on may use it; any
/// other page needs the install code from the service's log.
/// </summary>
[ApiController]
[Route("install")]
[AllowAnonymous]
[SkipAntiforgery]
[AnyOrigin]
public sealed class InstallController(Installation installation, Instance instance, InstallAccess access, TimeProvider time) : ControllerBase
{
    [HttpGet]
    public async Task<InstallView> Get(CancellationToken ct) => await ViewAsync(access.Check(HttpContext) == InstallAccessResult.Allowed, ct);

    [HttpPost("database")]
    public async Task<ActionResult<InstallView>> Database(DatabaseRequest request, CancellationToken ct)
    {
        if (Refusal() is { } refused)
        {
            return refused;
        }

        if (instance.ConfiguredConnectionString is not null)
        {
            return Problem(DatabaseForm.Configured, statusCode: StatusCodes.Status409Conflict);
        }

        if (installation.DatabaseReady)
        {
            return Problem("The database is set up. An admin changes it on the Settings page.", statusCode: StatusCodes.Status409Conflict);
        }

        var (settings, problem) = DatabaseForm.Read(request, kept: null);
        if (problem is not null)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        if (await installation.UseDatabaseAsync(settings!, ct) is { } failed)
        {
            return Problem(failed, statusCode: StatusCodes.Status400BadRequest);
        }

        return await ViewAsync(allowed: true, ct);
    }

    /// <summary>Sets the client's address. It also repairs a wrong one, which leaves the client unable to reach the service.</summary>
    [HttpPost("address")]
    public async Task<ActionResult<InstallView>> Address(InstallAddressRequest request, CancellationToken ct)
    {
        if (Refusal() is { } refused)
        {
            return refused;
        }

        if (instance.ClientOriginConfigured)
        {
            return Problem("The client's address is set by the service's configuration, in Nexus:ClientOrigin.", statusCode: StatusCodes.Status409Conflict);
        }

        if (Origins.Parse(request.ClientOrigin) is not { } origin)
        {
            return Problem(AddressRules.Client, statusCode: StatusCodes.Status400BadRequest);
        }

        instance.File.SaveClientOrigin(origin);
        return await ViewAsync(allowed: true, ct);
    }

    /// <summary>Makes the first admin, or makes an existing user an admin with a new password, and signs them in.</summary>
    [HttpPost("admin")]
    public async Task<ActionResult<MeView>> Admin(
        InstallAdminRequest request,
        [FromServices] IServiceProvider services,
        CancellationToken ct)
    {
        if (Refusal() is { } refused)
        {
            return refused;
        }

        if (await installation.StageAsync(ct) is InstallStage.Database or InstallStage.Address)
        {
            return Problem("Set up the database and the client's address first.", statusCode: StatusCodes.Status409Conflict);
        }

        if (Emails.Normalize(request.Email) is not { } email)
        {
            return Problem("Enter an email address.", statusCode: StatusCodes.Status400BadRequest);
        }

        if (Passwords.Problem(request.Password) is { } problem)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var db = services.GetRequiredService<NexusDbContext>();
        var sessions = services.GetRequiredService<Sessions>();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var existing = user is not null;
        if (user is null)
        {
            user = new User { Email = email, LinkStamp = SignInLinks.NewStamp(), CreatedAt = time.GetUtcNow().UtcDateTime };
            db.Users.Add(user);
        }

        // A new stamp ends the user's mailed links, since the installer sets their password.
        user.IsAdmin = true;
        user.LinkStamp = SignInLinks.NewStamp();
        user.PasswordHash = Passwords.Hash(user, request.Password!);
        await db.SaveChangesAsync(ct);
        if (existing)
        {
            await sessions.EndAllAsync(user, "An admin was made from the installer.", ct);
        }

        await installation.AdminMadeAsync(ct);
        await sessions.SignInAsync(user, persistent: false, ct);
        return new MeView(true, user.Id, user.Email, true, true);
    }

    /// <summary>Writes the install code to the service's log, for a page on another machine. It is written at most once a minute.</summary>
    [HttpPost("code")]
    public async Task<IActionResult> Code(CancellationToken ct)
    {
        await installation.AnnounceAsync(ct);
        return Accepted();
    }

    private ObjectResult? Refusal() => access.Check(HttpContext) switch
    {
        InstallAccessResult.Allowed => null,
        InstallAccessResult.LockedOut => Problem("Too many wrong codes. Wait 15 minutes, then try again.", statusCode: StatusCodes.Status429TooManyRequests),
        InstallAccessResult.WrongCode => Problem(
            "That is not the install code. Ask for one, then copy it from the service's log.",
            statusCode: StatusCodes.Status401Unauthorized),
        _ => Problem(
            "This page is not on the machine Nexus runs on, so it needs the install code from the service's log.",
            statusCode: StatusCodes.Status401Unauthorized),
    };

    private async Task<InstallView> ViewAsync(bool allowed, CancellationToken ct)
    {
        var stage = await installation.StageAsync(ct);
        var configured = instance.ConfiguredConnectionString;
        var place = configured is not null ? DatabaseSettings.Describe(configured) : instance.File.DatabasePlace;
        return new InstallView(
            stage == InstallStage.Done,
            stage,
            InstallAccess.IsLocal(HttpContext),
            allowed,
            configured is not null,
            allowed && place is not null ? new DatabaseView(place.Host, place.Port, place.Name, place.User) : null,
            allowed ? installation.DatabaseProblem : null,
            instance.ClientOriginConfigured,
            instance.MailReady);
    }
}

/// <summary>Reads a database from a form, for the installer and the Settings page alike.</summary>
public static partial class DatabaseForm
{
    public const string Configured = "The database is set by the service's configuration, in ConnectionStrings:Nexus.";

    /// <summary>
    /// The database the form names, or why it names none. A blank password keeps <paramref name="kept"/>'s, when the
    /// form names the same server, user, and database.
    /// </summary>
    public static (DatabaseSettings? Settings, string? Problem) Read(DatabaseRequest request, DatabaseSettings? kept)
    {
        var host = request.Host?.Trim();
        var name = request.Name?.Trim();
        var user = request.User?.Trim();
        var port = request.Port ?? DatabaseSettings.DefaultPort;
        if (string.IsNullOrEmpty(host))
        {
            return (null, "Enter the MySQL server's host, such as localhost.");
        }

        if (port is < 1 or > 65535)
        {
            return (null, "A port is a number from 1 to 65535.");
        }

        if (name is null || !NamePattern().IsMatch(name))
        {
            return (null, "Enter the database's name: up to 64 letters, digits, dashes, and underscores.");
        }

        if (string.IsNullOrEmpty(user))
        {
            return (null, "Enter the MySQL user Nexus signs in as.");
        }

        var password = request.Password;
        if (string.IsNullOrEmpty(password)
            && kept is not null
            && kept.SamePlace(new DatabaseSettings(host, port, name, user, string.Empty))
            && kept.User == user)
        {
            password = kept.Password;
        }

        return (new DatabaseSettings(host, port, name, user, password ?? string.Empty), null);
    }

    [GeneratedRegex("^[A-Za-z0-9_$-]{1,64}$")]
    private static partial Regex NamePattern();
}

public static class AddressRules
{
    public const string Client = "Enter the client's address, such as https://nexus.example.com, with nothing after its host.";

    public const string Callback = "Enter the https address providers send people back to, such as https://api.nexus.example.com, with nothing after its host.";
}
