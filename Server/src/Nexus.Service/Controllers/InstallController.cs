using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using Pluperfect.Jwt;
using Pluperfect.Mail;

namespace Nexus.Service.Controllers;

public sealed record DatabaseRequest(string? Host, int? Port, string? Name, string? User, string? Password);

public sealed record InstallAddressRequest(string? ClientOrigin);

/// <summary>The first admin, or an admin made for someone locked out. The password may be blank once mail works.</summary>
public sealed record InstallAdminRequest(string? Email, string? Password);

public sealed record InstallTestMailRequest(string? To);

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
    bool Mail,
    MailView? MailSettings);

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
public sealed class InstallController(
    Installation installation,
    Instance instance,
    InstallAccess access,
    IMailer mailer,
    IServiceScopeFactory scopes,
    TimeProvider time) : ControllerBase
{
    private const string MailIsSet = "Nexus is set up. An admin changes its mail on the Settings page.";

    private const string NoWayBack = "Set up mail, or choose a password. Without either, there is no way to sign in again.";

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

        await NoteAsync($"The installer set the database: {settings!.Name} on {settings.Host}:{settings.Port}, signing in as {settings.User}.", ct);
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

        var changed = origin != instance.ClientOrigin;
        instance.File.SaveClientOrigin(origin);
        if (changed && installation.DatabaseReady)
        {
            await NoteAsync($"The installer set the client's address to {origin}.", ct);
        }

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

        var password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
        if (password is null && !instance.MailReady)
        {
            return Problem(NoWayBack, statusCode: StatusCodes.Status400BadRequest);
        }

        if (password is null && instance.Transport == MailTransport.Mailpit
            && !await services.GetRequiredService<MailpitProbe>().AnswersAsync(instance.MailpitUrl!, ct))
        {
            return Problem(
                $"Mailpit isn't answering at {instance.MailpitUrl}, so a sign-in link would go nowhere. Start Mailpit, or choose a password.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (password is not null && Passwords.Problem(password) is { } problem)
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

        // A new stamp ends the user's mailed links, since the installer signs them in. A blank password leaves theirs.
        user.IsAdmin = true;
        user.LinkStamp = SignInLinks.NewStamp();
        if (password is not null)
        {
            user.PasswordHash = Passwords.Hash(user, password);
        }

        services.GetRequiredService<OperatorLog>().Info(
            (existing, password is not null) switch
            {
                (true, true) => $"The installer made {email} an admin, with a new password.",
                (true, false) => $"The installer made {email} an admin.",
                (false, true) => $"The installer made {email}, the first admin.",
                (false, false) => $"The installer made {email}, the first admin, who signs in with mailed links.",
            },
            LogPlace.Instance);
        await db.SaveChangesAsync(ct);
        if (existing)
        {
            await sessions.EndAllAsync(user, "An admin was made from the installer.", ct);
        }

        await installation.AdminMadeAsync(ct);
        await sessions.SignInAsync(user, persistent: false, ct);
        return new MeView(true, user.Id, user.Email, true, user.PasswordHash is not null);
    }

    /// <summary>Sends mail through Postmark or to Mailpit before the first admin, as the Settings page does after.</summary>
    [HttpPut("mail")]
    public async Task<ActionResult<InstallView>> Mail(MailSettingsRequest request, CancellationToken ct)
    {
        if (await MailRefusalAsync(ct) is { } refused)
        {
            return refused;
        }

        if (MailForm.Save(request, instance) is { } failed)
        {
            return Problem(failed.Problem, statusCode: failed.Status);
        }

        if (installation.DatabaseReady)
        {
            await NoteAsync($"The installer set mail to {MailForm.Describe(request, instance)}.", ct);
        }

        return await ViewAsync(allowed: true, ct);
    }

    /// <summary>Sends no mail, keeping each transport's settings for choosing it again.</summary>
    [HttpDelete("mail")]
    public async Task<ActionResult<InstallView>> TurnOffMail(CancellationToken ct)
    {
        if (await MailRefusalAsync(ct) is { } refused)
        {
            return refused;
        }

        if (instance.TransportConfigured)
        {
            return Problem(MailForm.Configured, statusCode: StatusCodes.Status409Conflict);
        }

        instance.File.TurnMailOff();
        if (installation.DatabaseReady)
        {
            await NoteAsync("The installer turned mail off.", ct);
        }

        return await ViewAsync(allowed: true, ct);
    }

    /// <summary>Mails the address given, to show the settings work. No admin exists yet to mail instead.</summary>
    [HttpPost("mail/test")]
    public async Task<ActionResult<TestMailView>> TestMail(InstallTestMailRequest request, CancellationToken ct)
    {
        if (await MailRefusalAsync(ct) is { } refused)
        {
            return refused;
        }

        if (!instance.MailReady)
        {
            return Problem(InstanceMailer.NotSetUp, statusCode: StatusCodes.Status409Conflict);
        }

        if (Emails.Normalize(request.To) is not { } to)
        {
            return Problem("Enter the address to send the test mail to.", statusCode: StatusCodes.Status400BadRequest);
        }

        return await MailForm.TestAsync(mailer, instance, to, ct) is { } failed
            ? Problem(failed, statusCode: StatusCodes.Status502BadGateway)
            : MailForm.Sent(to, instance);
    }

    /// <summary>Writes the install code to the service's log, for a page on another machine. It is written at most once a minute.</summary>
    [HttpPost("code")]
    public async Task<IActionResult> Code(CancellationToken ct)
    {
        await installation.AnnounceAsync(ct);
        return Accepted();
    }

    // Mail is the installer's only until the instance is set up; the Settings page changes it from then on.
    private async Task<ObjectResult?> MailRefusalAsync(CancellationToken ct) =>
        Refusal() ?? (await installation.IsInstalledAsync(ct) ? Problem(MailIsSet, statusCode: StatusCodes.Status409Conflict) : null);

    // The installer's routes run before the database is set, so each entry is saved through a context of its own.
    private async Task NoteAsync(string text, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        scope.ServiceProvider.GetRequiredService<OperatorLog>().Info(text, LogPlace.Instance);
        await scope.ServiceProvider.GetRequiredService<NexusDbContext>().SaveChangesAsync(ct);
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
            instance.MailReady,
            allowed ? MailForm.View(instance) : null);
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
