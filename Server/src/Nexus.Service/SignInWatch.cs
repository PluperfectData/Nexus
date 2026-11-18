using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Controllers;
using Nexus.Service.Storage;
using Pluperfect.Mail;

namespace Nexus.Service;

/// <summary>What a workspace is mailed about one of its connections' sign-ins.</summary>
public enum SignInNews
{
    EndingSoon,
    Ended,
}

/// <summary>
/// A connection's sign-in: when it signed in, when its provider ends it, the warning before then, and its end. A warning
/// or an end queues mail on <see cref="SignInMailer"/>, which the caller sends once it has saved.
/// </summary>
public sealed class SignInState(SourceConnectors connectors, OperatorLog log, SignInMailer mail, TimeProvider time)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<SignInAccount> AccountsOf(Connection connection) =>
        connection.Accounts is { } json ? JsonSerializer.Deserialize<List<SignInAccount>>(json, Json) ?? [] : [];

    /// <summary>Starts the connection's sign-in over: its clock, its warning, and the accounts it reaches.</summary>
    public void Record(Connection connection, IReadOnlyList<SignInAccount>? accounts)
    {
        var now = time.GetUtcNow();
        var life = LifeOf(connection);
        connection.NeedsSignIn = false;
        connection.SignedInAt = now.UtcDateTime;
        connection.SignInEndsAt = life?.EndsAt(now)?.UtcDateTime;
        connection.SignInWarnedAt = null;
        connection.SignInEndedAt = null;
        connection.KeptAliveAt = life?.KeepAliveEvery is null ? null : now.UtcDateTime;
        connection.Accounts = accounts is null ? null : JsonSerializer.Serialize(accounts, Json);
    }

    /// <summary>Says, in the log and by mail, that the connection's sign-in ends in <paramref name="left"/>.</summary>
    public void Warn(Connection connection, TimeSpan left)
    {
        connection.SignInWarnedAt = time.GetUtcNow().UtcDateTime;
        log.Warn(
            $"Connection {connection.Name}'s {ProviderOf(connection)} sign-in ends {LogWords.After(left)}. Sign in again before then to keep its syncs running.",
            LogPlace.On(connection));
        mail.Queue(connection, SignInNews.EndingSoon);
    }

    /// <summary>Ends the connection's sign-in as of <paramref name="at"/>, so its syncs stop until someone signs in again.</summary>
    public void End(Connection connection, DateTime at, string? why = null)
    {
        connection.NeedsSignIn = true;
        connection.SignInEndedAt = at;
        var reason = why is null ? "." : $": {why}";
        log.Warn(
            $"{LogWords.Connection(connection, connectors)} needs a new sign-in{reason} Its syncs stop until someone signs in again from the workspace page.",
            LogPlace.On(connection));
        mail.Queue(connection, SignInNews.Ended);
    }

    /// <summary>The connection's sign-in as the pages show it, or null for a connector that does not sign in.</summary>
    public SignInView? ViewOf(Connection connection)
    {
        if (LifeOf(connection) is not { } life)
        {
            return null;
        }

        var endingSoon = !connection.NeedsSignIn
            && connection.SignInEndsAt is { } ends
            && time.GetUtcNow().UtcDateTime >= ends - life.WarnBefore;
        return new SignInView(
            life.Kind,
            connection.SignedInAt,
            connection.SignInEndsAt,
            endingSoon,
            connection.NeedsSignIn,
            connection.SignInEndedAt,
            connection.KeptAliveAt,
            [.. AccountsOf(connection).Select(account => account.Label)]);
    }

    private SignInLife? LifeOf(Connection connection) => connectors.Find(connection.Connector)?.Spec.SignIn;

    private string ProviderOf(Connection connection) => connectors.Find(connection.Connector)?.Spec.DisplayName ?? connection.Connector;
}

/// <summary>Mails the people a workspace chose about its connections' sign-ins, once the caller has saved the change.</summary>
public sealed class SignInMailer(NexusDbContext db, IMailer mailer, Instance instance, SourceConnectors connectors, OperatorLog log)
{
    private const string WhyMailed = "You get this mail because the workspace's session expiry reminder email names you. Its members change that on the workspace page.";

    private readonly List<(Connection Connection, SignInNews News)> _queued = [];

    public void Queue(Connection connection, SignInNews news) => _queued.Add((connection, news));

    /// <summary>The addresses the workspace's setting names now, in order.</summary>
    public async Task<List<string>> RecipientsAsync(int workspaceId, CancellationToken ct)
    {
        var choice = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.SignInMail).FirstAsync(ct);
        var members = db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).Select(m => m.UserId);
        var named = db.SignInMailPeople.Where(p => p.WorkspaceId == workspaceId).Select(p => p.UserId);
        var people = choice switch
        {
            SignInMail.Everyone => db.Users.Where(u => u.IsAdmin || members.Contains(u.Id)),
            SignInMail.Admins => db.Users.Where(u => u.IsAdmin),
            SignInMail.Named => db.Users.Where(u => (u.IsAdmin || members.Contains(u.Id)) && named.Contains(u.Id)),
            _ => db.Users.Where(u => false),
        };
        return await people.OrderBy(u => u.Email).Select(u => u.Email).ToListAsync(ct);
    }

    /// <summary>Sends what was queued. An instance without mail sends nothing.</summary>
    public async Task SendQueuedAsync(CancellationToken ct)
    {
        var queued = _queued.ToList();
        _queued.Clear();
        if (queued.Count == 0 || !instance.MailReady)
        {
            return;
        }

        foreach (var (connection, news) in queued)
        {
            var workspace = await db.Workspaces.FirstAsync(w => w.Id == connection.WorkspaceId, ct);
            foreach (var email in await RecipientsAsync(workspace.Id, ct))
            {
                try
                {
                    await mailer.SendAsync(Message(connection, workspace, news, email), ct);
                }
                catch (MailDeliveryException ex)
                {
                    log.Warn($"Mail about connection {connection.Name}'s sign-in could not be sent to {email}: {ex.Message}", LogPlace.On(connection));
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    private MailRequest Message(Connection connection, Workspace workspace, SignInNews news, string email)
    {
        var provider = connectors.Find(connection.Connector)?.Spec.DisplayName ?? connection.Connector;
        var (subject, lead) = news == SignInNews.EndingSoon
            ? ($"{connection.Name}'s {provider} sign-in ends soon",
                $"The connection {connection.Name}, in the Nexus workspace {workspace.Name}, signs in to {provider}, and {provider} ends that sign-in {When(connection.SignInEndsAt)}. Sign in again before then to keep its syncs running.")
            : ($"{connection.Name}'s {provider} sign-in ended",
                $"The connection {connection.Name}, in the Nexus workspace {workspace.Name}, needs a new sign-in to {provider}. Its syncs stop until someone signs in again.");
        var link = $"{instance.Client}/workspaces/{workspace.Id}";
        return new MailRequest
        {
            From = new MailAddress(instance.MailFrom!, instance.MailFromName),
            To = new MailAddress(email),
            Subject = subject,
            HtmlBody = $"<p>{WebUtility.HtmlEncode(lead)}</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Sign in again on the workspace page</a></p>"
                + $"<p>{WebUtility.HtmlEncode(WhyMailed)}</p>",
            TextBody = $"{lead}\n\nSign in again on the workspace page: {link}\n\n{WhyMailed}",
        };
    }

    private static string When(DateTime? at) =>
        at is { } end ? "at " + end.ToString("h:mm tt 'UTC on' MMM d, yyyy", CultureInfo.InvariantCulture) : "soon";
}

/// <summary>
/// One pass over the signed-in connections: it warns before a fixed clock ends, ends a sign-in at its time, and keeps
/// sessions that need calls alive.
/// </summary>
public sealed partial class SignInChecks(
    NexusDbContext db,
    SourceConnectors connectors,
    ConnectionMaker maker,
    SignInState state,
    SignInMailer mail,
    TimeProvider time,
    ILogger<SignInChecks> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var live = await db.Connections.Where(c => c.SignedInAt != null && !c.NeedsSignIn).ToListAsync(ct);
        foreach (var connection in live)
        {
            if (connectors.Find(connection.Connector) is not { Spec.SignIn: { } life } connector)
            {
                continue;
            }

            if (connection.SignInEndsAt is { } ends)
            {
                if (now >= ends)
                {
                    state.End(connection, ends, $"{connector.Spec.DisplayName} ended it at the time it sets for each sign-in.");
                    continue;
                }

                if (connection.SignInWarnedAt is null && now >= ends - life.WarnBefore)
                {
                    state.Warn(connection, ends - now);
                }
            }

            if (life.KeepAliveEvery is { } every && connector is IKeepsSignInAlive keeper && now - LastAlive(connection) >= every)
            {
                await KeepAliveAsync(connection, connector, keeper, life, now, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        await mail.SendQueuedAsync(ct);
    }

    private async Task KeepAliveAsync(Connection connection, ISourceConnector connector, IKeepsSignInAlive keeper, SignInLife life, DateTime now, CancellationToken ct)
    {
        try
        {
            await keeper.KeepAliveAsync(await maker.ConfigOfAsync(connection, ct), ct);
            connection.KeptAliveAt = now;
        }
        catch (ConnectorException ex) when (ex.Kind == ConnectorErrorKind.AuthExpired)
        {
            state.End(connection, now, $"its {connector.Spec.DisplayName} session ended. {ex.Message}");
        }
        catch (ConnectorException ex)
        {
            if (now - LastAlive(connection) >= life.IdleTimeout)
            {
                state.End(connection, now, $"its {connector.Spec.DisplayName} session could not be kept alive. {ex.Message}");
            }
            else
            {
                LogKeepAliveFailed(logger, ex, connection.Id);
            }
        }
    }

    private static DateTime LastAlive(Connection connection) => connection.KeptAliveAt ?? connection.SignedInAt!.Value;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Connection {ConnectionId} could not be kept alive, and is tried again in a minute.")]
    private static partial void LogKeepAliveFailed(ILogger logger, Exception ex, int connectionId);
}

/// <summary>Runs <see cref="SignInChecks"/> every minute while the service has a database.</summary>
public sealed partial class SignInWatcher(Installation installation, IServiceScopeFactory scopes, TimeProvider time, ILogger<SignInWatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every, time);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!installation.DatabaseReady)
            {
                continue;
            }

            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<SignInChecks>().RunAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailure(logger, ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "The connections' sign-ins could not be checked.")]
    private static partial void LogFailure(ILogger logger, Exception ex);
}
