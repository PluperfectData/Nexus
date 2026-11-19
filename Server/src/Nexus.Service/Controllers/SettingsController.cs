using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc;
using Nexus.Service.Auth;
using Pluperfect.Mail;

namespace Nexus.Service.Controllers;

public sealed record AddressesRequest(string? ClientOrigin, string? CallbackOrigin);

public sealed record MailSettingsRequest(MailChoice? Transport, string? ServerToken, string? From, string? FromName, string? MailpitUrl);

public sealed record LogSettingsRequest(int? Days);

/// <summary>
/// How the instance sends mail, and what the pages chose. The server token is never answered, only whether one is set.
/// A field set by the service's configuration is shown, and the page cannot change it.
/// </summary>
public sealed record MailView(
    MailTransport Transport,
    MailChoice? Choice,
    bool TransportConfigured,
    bool TokenSet,
    bool TokenConfigured,
    string? From,
    bool FromConfigured,
    string FromName,
    bool FromNameConfigured,
    string? MailpitUrl,
    bool MailpitConfigured,
    string? MailpitInbox);

public sealed record SettingsView(
    string? ClientOrigin,
    bool ClientOriginConfigured,
    string? CallbackOrigin,
    bool CallbackOriginConfigured,
    string? CallbackUrl,
    string CallbackSuggestion,
    MailView Mail,
    DatabaseView? Database,
    bool DatabaseConfigured,
    int LogDays,
    bool LogDaysConfigured);

public sealed record TestMailView(string SentTo, MailTransport Transport, string? Inbox);

/// <summary>The instance's own settings, for admins: its addresses, its mail, and its database.</summary>
[ApiController]
[Route("settings")]
[Authorize(Policy = NexusClaims.AdminPolicy)]
public sealed class SettingsController(Instance instance, Installation installation, IMailer mailer, IServer server, IServiceScopeFactory scopes) : ControllerBase
{
    [HttpGet]
    public SettingsView Get() => View();

    [HttpPut("addresses")]
    public async Task<ActionResult<SettingsView>> Addresses(AddressesRequest request, CancellationToken ct)
    {
        var client = Origins.Parse(request.ClientOrigin);
        if (client is null)
        {
            return Problem(AddressRules.Client, statusCode: StatusCodes.Status400BadRequest);
        }

        string? callback = null;
        if (!string.IsNullOrWhiteSpace(request.CallbackOrigin) && (callback = Origins.Parse(request.CallbackOrigin, httpsOnly: true)) is null)
        {
            return Problem(AddressRules.Callback, statusCode: StatusCodes.Status400BadRequest);
        }

        if (instance.ClientOriginConfigured && client != instance.ClientOrigin)
        {
            return Problem("The client's address is set by the service's configuration, in Nexus:ClientOrigin.", statusCode: StatusCodes.Status409Conflict);
        }

        if (instance.CallbackOriginConfigured && callback != instance.CallbackOrigin)
        {
            return Problem("The callback address is set by the service's configuration, in Nexus:CallbackOrigin.", statusCode: StatusCodes.Status409Conflict);
        }

        var changed = client != instance.ClientOrigin || callback != instance.CallbackOrigin;
        if (!instance.ClientOriginConfigured)
        {
            instance.File.SaveClientOrigin(client);
        }

        if (!instance.CallbackOriginConfigured)
        {
            instance.File.SaveCallbackOrigin(callback);
        }

        if (changed)
        {
            var comesBack = instance.CallbackOrigin is { } origin ? $"sign-ins come back to {origin}" : "no callback address is set, so no sign-in can come back";
            await NoteAsync(log => log.Info($"The addresses were changed: the client is at {instance.ClientOrigin}, and {comesBack}.", LogPlace.Instance), ct);
        }

        return View();
    }

    /// <summary>Sends mail through Postmark or to Mailpit. A blank token or address keeps the one stored.</summary>
    [HttpPut("mail")]
    public async Task<ActionResult<SettingsView>> Mail(MailSettingsRequest request, CancellationToken ct)
    {
        if (MailForm.Save(request, instance) is { } failed)
        {
            return Problem(failed.Problem, statusCode: failed.Status);
        }

        await NoteAsync(log => log.Info($"Mail was set to {MailForm.Describe(request, instance)}.", LogPlace.Instance), ct);
        return View();
    }

    /// <summary>Sends no mail, keeping each transport's settings for choosing it again.</summary>
    [HttpDelete("mail")]
    public async Task<ActionResult<SettingsView>> TurnOffMail(CancellationToken ct)
    {
        if (instance.TransportConfigured)
        {
            return Problem(MailForm.Configured, statusCode: StatusCodes.Status409Conflict);
        }

        instance.File.TurnMailOff();
        await NoteAsync(log => log.Info("Mail was turned off.", LogPlace.Instance), ct);
        return View();
    }

    /// <summary>Sets how many days the operator log keeps an entry.</summary>
    [HttpPut("log")]
    public async Task<ActionResult<SettingsView>> Log(LogSettingsRequest request, CancellationToken ct)
    {
        if (instance.LogDaysConfigured)
        {
            return Problem("How long the log keeps entries is set by the service's configuration, in Nexus:LogDays.", statusCode: StatusCodes.Status409Conflict);
        }

        if (request.Days is not { } days || days < Instance.MinLogDays || days > Instance.MaxLogDays)
        {
            return Problem(
                $"The log keeps entries for {Instance.MinLogDays} to {Instance.MaxLogDays} days.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (days != instance.LogDays)
        {
            instance.File.SaveLogDays(days);
            await NoteAsync(log => log.Info($"The log now keeps entries for {Days(days)}.", LogPlace.Instance), ct);
        }

        return View();
    }

    /// <summary>Mails the signed-in admin, to show the settings work.</summary>
    [HttpPost("mail/test")]
    public async Task<ActionResult<TestMailView>> TestMail(CancellationToken ct)
    {
        if (!instance.MailReady)
        {
            return Problem(InstanceMailer.NotSetUp, statusCode: StatusCodes.Status409Conflict);
        }

        var to = User.FindFirstValue(ClaimTypes.Name)!;
        if (await MailForm.TestAsync(mailer, instance, to, ct) is { } failed)
        {
            await NoteAsync(log => log.Warn($"A test mail to {to} failed: {failed}", LogPlace.Instance), ct);
            return Problem(failed, statusCode: StatusCodes.Status502BadGateway);
        }

        await NoteAsync(log => log.Info($"A test mail was sent to {to}.", LogPlace.Instance), ct);
        return MailForm.Sent(to, instance);
    }

    /// <summary>Moves the service to another database, or changes how it signs in to this one, once the new one opens and is brought up to date.</summary>
    [HttpPut("database")]
    public async Task<ActionResult<SettingsView>> Database(DatabaseRequest request, CancellationToken ct)
    {
        if (instance.ConfiguredConnectionString is not null)
        {
            return Problem(DatabaseForm.Configured, statusCode: StatusCodes.Status409Conflict);
        }

        var (settings, problem) = DatabaseForm.Read(request, instance.File.Database);
        if (problem is not null)
        {
            return Problem(problem, statusCode: StatusCodes.Status400BadRequest);
        }

        var before = instance.File.DatabasePlace;
        if (await installation.UseDatabaseAsync(settings!, ct) is { } failed)
        {
            return Problem(failed, statusCode: StatusCodes.Status400BadRequest);
        }

        // Written through a new scope, whose context opens the database just switched to.
        var said = before is not null && before.SamePlace(settings!)
            ? $"The service now signs in to its database, {settings!.Name} on {settings.Host}:{settings.Port}, as {settings.User}."
            : $"The service moved to database {settings!.Name} on {settings.Host}:{settings.Port}, signing in as {settings.User}.";
        await NoteAsync(log => log.Info(said, LogPlace.Instance), ct);
        return View();
    }

    private static string Days(int days) => days == 1 ? "1 day" : $"{days} days";

    // Settings live outside the database, so each entry is saved on its own, through a context of its own.
    private async Task NoteAsync(Action<OperatorLog> write, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        write(scope.ServiceProvider.GetRequiredService<OperatorLog>());
        await scope.ServiceProvider.GetRequiredService<Storage.NexusDbContext>().SaveChangesAsync(ct);
    }

    private SettingsView View()
    {
        var configured = instance.ConfiguredConnectionString;
        var place = configured is not null ? DatabaseSettings.Describe(configured) : instance.File.DatabasePlace;
        return new SettingsView(
            instance.ClientOrigin,
            instance.ClientOriginConfigured,
            instance.CallbackOrigin,
            instance.CallbackOriginConfigured,
            instance.CallbackUrl,
            CallbackSuggestion(),
            MailForm.View(instance),
            place is null ? null : new DatabaseView(place.Host, place.Port, place.Name, place.User),
            configured is not null,
            instance.LogDays,
            instance.LogDaysConfigured);
    }

    // The service's own https address, where it listens on one, and otherwise the https form of the address this request came to.
    private string CallbackSuggestion() =>
        server.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(address => Origins.Parse(address, httpsOnly: true))
            .FirstOrDefault(origin => origin is not null)
        ?? $"https://{Request.Host}";
}

/// <summary>Keeps and tests the instance's mail, for the installer and the Settings page alike.</summary>
public static class MailForm
{
    public const int MaxNameLength = 100;

    public const string Configured = "Mail goes through Postmark, as the service's configuration sets it, in Mail:ServerToken and Nexus:MailFrom.";

    /// <summary>
    /// Keeps the transport the form chose, with its settings, or answers why it cannot, with the status to answer it
    /// with. A blank token or Mailpit address keeps the one stored, and a value set by configuration is left as it is.
    /// </summary>
    public static (string Problem, int Status)? Save(MailSettingsRequest request, Instance instance)
    {
        if (instance.TransportConfigured)
        {
            return (Configured, StatusCodes.Status409Conflict);
        }

        var name = string.IsNullOrWhiteSpace(request.FromName) ? Instance.DefaultSenderName : request.FromName.Trim();
        if (name.Length > MaxNameLength || name.Any(char.IsControl))
        {
            return ($"A sender's name is one line of up to {MaxNameLength} characters.", StatusCodes.Status400BadRequest);
        }

        return request.Transport switch
        {
            MailChoice.Postmark => SavePostmark(request, instance, name),
            MailChoice.Mailpit => SaveMailpit(request, instance, name),
            _ => ("Choose how mail goes: through Postmark, or to Mailpit.", StatusCodes.Status400BadRequest),
        };
    }

    /// <summary>What the saved settings do, for the Log page.</summary>
    public static string Describe(MailSettingsRequest request, Instance instance) =>
        instance.Transport == MailTransport.Mailpit
            ? $"go to Mailpit at {instance.MailpitUrl}"
            : $"go through Postmark, from {instance.MailFromName} at {instance.Sender}"
                + (string.IsNullOrWhiteSpace(request.ServerToken) ? string.Empty : ", with a new server token");

    private static (string Problem, int Status)? SavePostmark(MailSettingsRequest request, Instance instance, string name)
    {
        var token = string.IsNullOrWhiteSpace(request.ServerToken) ? null : request.ServerToken.Trim();
        if (instance.ServerTokenConfigured && token is not null)
        {
            return ("Postmark's server token is set by the service's configuration, in Mail:ServerToken.", StatusCodes.Status409Conflict);
        }

        if (token is null && !instance.ServerTokenConfigured && !instance.File.HasServerToken)
        {
            return ("Enter Postmark's server token. Postmark shows it under the server's API Tokens.", StatusCodes.Status400BadRequest);
        }

        string? from = null;
        if (!instance.MailFromConfigured && (from = Emails.Normalize(request.From)) is null)
        {
            return ("Enter the address mail comes from, on a domain Postmark may send as.", StatusCodes.Status400BadRequest);
        }

        instance.File.SavePostmark(token, from, name);
        return null;
    }

    private static (string Problem, int Status)? SaveMailpit(MailSettingsRequest request, Instance instance, string name)
    {
        var given = string.IsNullOrWhiteSpace(request.MailpitUrl) ? null : request.MailpitUrl.Trim().TrimEnd('/');
        if (instance.MailpitConfigured && given is not null && given != instance.MailpitUrl)
        {
            return ("Mailpit's address is set by the service's configuration, in Mail:MailpitUrl.", StatusCodes.Status409Conflict);
        }

        if (!instance.MailpitConfigured)
        {
            if ((given is null && instance.File.MailpitUrl is null) || (given is not null && !PluperfectMailOptionsValidator.IsWebAddress(given)))
            {
                return ("Enter Mailpit's address, such as http://localhost:8025.", StatusCodes.Status400BadRequest);
            }
        }

        string? from = null;
        if (!instance.MailFromConfigured && !string.IsNullOrWhiteSpace(request.From) && (from = Emails.Normalize(request.From)) is null)
        {
            return ("Enter the address mail comes from, or leave it blank.", StatusCodes.Status400BadRequest);
        }

        instance.File.SaveMailpit(instance.MailpitConfigured ? null : given, from, name);
        return null;
    }

    /// <summary>Mails <paramref name="to"/> to show the settings work. Answers why the transport refused, or null once sent.</summary>
    public static async Task<string?> TestAsync(IMailer mailer, Instance instance, string to, CancellationToken ct)
    {
        try
        {
            await mailer.SendAsync(
                new MailRequest
                {
                    From = new MailAddress(instance.MailFrom!, instance.MailFromName),
                    To = new MailAddress(to),
                    Subject = "Nexus can send mail",
                    HtmlBody = "<p>This instance of Nexus sends mail with these settings. Sign-in links and invites go out the same way.</p>",
                    TextBody = "This instance of Nexus sends mail with these settings. Sign-in links and invites go out the same way.",
                },
                ct);
            return null;
        }
        catch (MailDeliveryException ex)
        {
            return ex.Message;
        }
    }

    public static TestMailView Sent(string to, Instance instance) =>
        new(to, instance.Transport, instance.Transport == MailTransport.Mailpit ? instance.MailpitInbox : null);

    public static MailView View(Instance instance) =>
        new(
            instance.Transport,
            instance.MailChoice,
            instance.TransportConfigured,
            instance.ServerToken is not null,
            instance.ServerTokenConfigured,
            instance.Sender,
            instance.MailFromConfigured,
            instance.MailFromName,
            instance.MailFromNameConfigured,
            instance.MailpitUrl,
            instance.MailpitConfigured,
            instance.MailpitInbox);
}
