using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Storage;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class SignInClockTests
{
    private const string AdminEmail = "admin@acme.test";

    private static readonly TimeSpan Close = TimeSpan.FromMinutes(1);

    private static readonly Dictionary<string, string> Settings = new() { ["startDate"] = "2026-01-01" };

    private static readonly string[] Returns = ["http://localhost:6160/sources"];

    private readonly StandInSignIn _lasting = new("lasting", SignInLife.Lasting);
    private readonly StandInSignIn _kept = new("kept", SignInLife.KeptAliveFor(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)));

    private ServiceFactory _factory = null!;
    private Browser _admin = null!;
    private HttpClient _browser = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = ServiceFactory.WithPostmark();
        _factory.MoreConnectors.AddRange([_lasting, _kept]);
        _admin = await _factory.SignedInAsync(AdminEmail, admin: true);
        _browser = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [TearDown]
    public void TearDown()
    {
        _browser.Dispose();
        _admin.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task A_sign_in_records_its_clock_and_accounts_for_members_and_apps()
    {
        var workspace = await MakeWorkspaceAsync();
        var id = await SignInAsync(workspace);
        var (key, destination) = await MakeAppAsync(workspace);
        (await _admin.SendAsync(HttpMethod.Post, $"/connections/{id}/syncs", new { destinationId = destination, streams = new[] { new { stream = "balances", table = "balances", mode = "Incremental" } } }))
            .EnsureSuccessStatusCode();

        var card = (await _admin.GetAsync<JsonElement>($"/workspaces/{workspace}/connections")).EnumerateArray().Single().GetProperty("signIn");
        using var app = Bearer(key);
        var synced = (await app.GetFromJsonAsync<JsonElement>("/syncs")).EnumerateArray().Single().GetProperty("signIn");

        card.GetProperty("kind").GetString().ShouldBe("FixedClock");
        var signedIn = card.GetProperty("signedInAt").GetDateTime();
        signedIn.ShouldBe(DateTime.UtcNow, Close);
        card.GetProperty("endsAt").GetDateTime().ShouldBe(signedIn.AddDays(7), TimeSpan.FromSeconds(1));
        card.GetProperty("endingSoon").GetBoolean().ShouldBeFalse();
        card.GetProperty("ended").GetBoolean().ShouldBeFalse();
        card.GetProperty("accounts").EnumerateArray().Select(a => a.GetString()).ShouldBe(["…1234"]);
        synced.GetProperty("kind").GetString().ShouldBe("FixedClock");
        (await EntriesAsync()).ShouldContain(e => e.Text == "Connection Brokerage (Charles Schwab) was signed in and added, reading account …1234.");
    }

    [Test]
    public async Task Signing_in_again_early_starts_the_clock_over_and_clears_the_warning()
    {
        var workspace = await MakeWorkspaceAsync();
        var id = await SignInAsync(workspace);
        await EditAsync(id, c =>
        {
            c.SignedInAt = DateTime.UtcNow.AddDays(-6.5);
            c.SignInEndsAt = DateTime.UtcNow.AddHours(12);
            c.SignInWarnedAt = DateTime.UtcNow.AddHours(-1);
        });

        using var renewed = await RenewAsync(id);

        renewed.Headers.Location!.OriginalString.ShouldBe($"{ServiceFactory.ClientOrigin}/workspaces/{workspace}?signedIn={id}");
        var connection = await ConnectionAsync(id);
        connection.SignedInAt!.Value.ShouldBe(DateTime.UtcNow, Close);
        connection.SignInEndsAt!.Value.ShouldBe(DateTime.UtcNow.AddDays(7), Close);
        connection.SignInWarnedAt.ShouldBeNull();
    }

    [Test]
    public async Task A_renewal_into_other_accounts_is_refused_and_one_with_an_account_added_is_logged()
    {
        var workspace = await MakeWorkspaceAsync();
        var id = await SignInAsync(workspace);
        _factory.Schwab.Accounts.Clear();
        _factory.Schwab.Accounts.Add("99999999");

        using var refused = await RenewAsync(id);

        ErrorOf(refused).ShouldBe(
            "This sign-in reached account …9999, and this connection reads account …1234. Sign in to the same Charles Schwab login, or add a new connection.");
        SignInState.AccountsOf(await ConnectionAsync(id)).Select(a => a.Label).ShouldBe(["…1234"]);

        _factory.Schwab.Accounts.Clear();
        _factory.Schwab.Accounts.AddRange(["12341234", "55556666"]);
        using var accepted = await RenewAsync(id);

        ErrorOf(accepted).ShouldBeEmpty();
        SignInState.AccountsOf(await ConnectionAsync(id)).Select(a => a.Label).ShouldBe(["…1234", "…6666"]);
        (await EntriesAsync()).ShouldContain(e => e.Text == "Connection Brokerage (Charles Schwab) was signed in again. It now reads account …6666 too.");
    }

    [Test]
    public async Task The_checks_warn_once_before_a_fixed_clock_ends_and_mail_the_people_named()
    {
        var workspace = await MakeWorkspaceAsync();
        var id = await SignInAsync(workspace);
        var member = await AddMemberAsync(workspace, "member@acme.test");
        (await _admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/reminder-email", new { choice = "Named", people = new[] { member } }))
            .EnsureSuccessStatusCode();
        await EditAsync(id, c => c.SignInEndsAt = DateTime.UtcNow.AddHours(3));

        await ChecksAsync();
        await ChecksAsync();

        var warned = (await EntriesAsync()).Where(e => e.Level == EntryLevel.Warning).ToList();
        warned.Single().Text.ShouldBe("Connection Brokerage's Charles Schwab sign-in ends in 3 hours. Sign in again before then to keep its syncs running.");
        var mail = Browser.PostmarkTo(_factory.Postmark, "member@acme.test").Single();
        mail.GetProperty("Subject").GetString().ShouldBe("Brokerage's Charles Schwab sign-in ends soon");
        mail.GetProperty("TextBody").GetString()!.ShouldContain($"{ServiceFactory.ClientOrigin}/workspaces/{workspace}");
        Browser.PostmarkTo(_factory.Postmark, AdminEmail).ShouldBeEmpty();
        var card = (await _admin.GetAsync<JsonElement>($"/workspaces/{workspace}/connections")).EnumerateArray().Single().GetProperty("signIn");
        card.GetProperty("endingSoon").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task The_checks_end_a_fixed_clock_at_its_time_and_mail_every_member_and_admin()
    {
        var workspace = await MakeWorkspaceAsync();
        var id = await SignInAsync(workspace);
        await AddMemberAsync(workspace, "member@acme.test");
        (await _admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/reminder-email", new { choice = "Everyone" })).EnsureSuccessStatusCode();
        var ends = DateTime.UtcNow.AddMinutes(-1);
        await EditAsync(id, c => c.SignInEndsAt = ends);

        await ChecksAsync();

        var connection = await ConnectionAsync(id);
        connection.NeedsSignIn.ShouldBeTrue();
        connection.SignInEndedAt!.Value.ShouldBe(ends, TimeSpan.FromSeconds(1));
        (await EntriesAsync()).ShouldContain(e => e.Text ==
            "Brokerage (Charles Schwab) needs a new sign-in: Charles Schwab ended it at the time it sets for each sign-in. Its syncs stop until someone signs in again from the workspace page.");
        foreach (var email in new[] { "member@acme.test", AdminEmail, ServiceFactory.OwnerEmail })
        {
            Browser.PostmarkTo(_factory.Postmark, email).Single().GetProperty("Subject").GetString().ShouldBe("Brokerage's Charles Schwab sign-in ended");
        }
    }

    [Test]
    public async Task A_kept_alive_session_is_called_until_its_call_is_refused_or_it_idles()
    {
        var workspace = await MakeWorkspaceAsync();
        var id = await SeedAsync(workspace, _kept.Spec.Id, keptAliveAt: DateTime.UtcNow.AddMinutes(-2));

        await ChecksAsync();

        _kept.KeepAlives.ShouldBe(1);
        (await ConnectionAsync(id)).KeptAliveAt!.Value.ShouldBe(DateTime.UtcNow, Close);

        await EditAsync(id, c => c.KeptAliveAt = DateTime.UtcNow.AddMinutes(-2));
        _kept.KeepAliveFailures.Enqueue(new ConnectorException(ConnectorErrorKind.Transient, "No answer."));
        await ChecksAsync();
        (await ConnectionAsync(id)).NeedsSignIn.ShouldBeFalse();

        await EditAsync(id, c => c.KeptAliveAt = DateTime.UtcNow.AddMinutes(-6));
        _kept.KeepAliveFailures.Enqueue(new ConnectorException(ConnectorErrorKind.Transient, "No answer."));
        await ChecksAsync();
        (await ConnectionAsync(id)).NeedsSignIn.ShouldBeTrue();
        (await EntriesAsync()).ShouldContain(e => e.Text.Contains("session could not be kept alive. No answer.", StringComparison.Ordinal));

        var second = await SeedAsync(workspace, _kept.Spec.Id, keptAliveAt: DateTime.UtcNow.AddMinutes(-2), name: "Second");
        _kept.KeepAliveFailures.Enqueue(new ConnectorException(ConnectorErrorKind.AuthExpired, "Log in again."));
        await ChecksAsync();
        (await ConnectionAsync(second)).NeedsSignIn.ShouldBeTrue();
        (await EntriesAsync()).ShouldContain(e => e.Text.Contains("session ended. Log in again.", StringComparison.Ordinal));
    }

    [Test]
    public async Task A_lasting_sign_in_stays_signed_in_and_a_database_has_no_sign_in_at_all()
    {
        var workspace = await MakeWorkspaceAsync();
        await SeedAsync(workspace, _lasting.Spec.Id, keptAliveAt: null);
        await SeedAsync(workspace, FakeSource.Id, keptAliveAt: null, name: "Shop");

        await ChecksAsync();

        var cards = (await _admin.GetAsync<JsonElement>($"/workspaces/{workspace}/connections")).EnumerateArray().ToList();
        var lasting = cards.Single(c => c.GetProperty("name").GetString() == "Stand-in").GetProperty("signIn");
        lasting.GetProperty("kind").GetString().ShouldBe("Lasting");
        lasting.GetProperty("endsAt").ValueKind.ShouldBe(JsonValueKind.Null);
        lasting.GetProperty("ended").GetBoolean().ShouldBeFalse();
        cards.Single(c => c.GetProperty("name").GetString() == "Shop").GetProperty("signIn").ValueKind.ShouldBe(JsonValueKind.Null);
        (await EntriesAsync()).ShouldNotContain(e => e.Level == EntryLevel.Warning);
    }

    [Test]
    public async Task Runs_wait_for_a_new_sign_in_and_a_renewal_runs_the_syncs_that_stopped_for_it()
    {
        var workspace = await MakeWorkspaceAsync();
        var id = await SignInAsync(workspace);
        var (_, destination) = await MakeAppAsync(workspace);
        var sync = await StoppedSyncAsync(id, destination);

        using var refused = await _admin.SendAsync(HttpMethod.Post, $"/syncs/{sync}/run");
        _factory.Services.GetRequiredService<PendingRuns>().Enqueue(new PendingRun(sync, 1));
        var waited = await WaitForAsync(async db => await db.Runs.Where(r => r.SyncId == sync && r.EndedAt != null).OrderByDescending(r => r.Id).FirstOrDefaultAsync() is { Error: SyncRunner.SignInEnded } run ? run : null);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Browser.DetailAsync(refused)).ShouldBe(SyncRunner.SignInEnded);
        waited.ErrorKind.ShouldBe(ConnectorErrorKind.AuthExpired);

        using var renewed = await RenewAsync(id);

        (await EntriesAsync()).ShouldContain(e => e.Text == "Connection Brokerage (Charles Schwab) was signed in again. 1 sync that stopped for it runs again.");
        await WaitForAsync(async db => await db.Runs.CountAsync(r => r.SyncId == sync && r.Error != SyncRunner.SignInEnded) > 0 ? true : (bool?)null);
    }

    [Test]
    public async Task Sign_in_mail_is_off_until_set_and_names_only_members_and_admins()
    {
        var workspace = await MakeWorkspaceAsync();
        var member = await AddMemberAsync(workspace, "member@acme.test");
        var outsider = await _factory.AddUserAsync("outsider@acme.test", Browser.Password);
        using var outsiderBrowser = _factory.NewBrowser();
        (await outsiderBrowser.SignInAsync("outsider@acme.test", Browser.Password)).EnsureSuccessStatusCode();

        var first = await _admin.GetAsync<JsonElement>($"/workspaces/{workspace}/reminder-email");
        using var nobody = await _admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/reminder-email", new { choice = "Named", people = Array.Empty<int>() });
        using var stranger = await _admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/reminder-email", new { choice = "Named", people = new[] { outsider.Id } });
        var admins = await _admin.SendAsync<JsonElement>(HttpMethod.Put, $"/workspaces/{workspace}/reminder-email", new { choice = "Admins" });
        using var hidden = await outsiderBrowser.GetAsync($"/workspaces/{workspace}/reminder-email");

        first.GetProperty("choice").GetString().ShouldBe("Off");
        first.GetProperty("mailReady").GetBoolean().ShouldBeTrue();
        first.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ShouldContain(member);
        first.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetInt32()).ShouldNotContain(outsider.Id);
        (await Browser.DetailAsync(nobody)).ShouldBe("Pick at least one person to mail.");
        (await Browser.DetailAsync(stranger)).ShouldBe("Pick people among the workspace's members and the admins.");
        admins.GetProperty("choice").GetString().ShouldBe("Admins");
        hidden.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await EntriesAsync()).ShouldContain(e => e.Text == "Mail about this workspace's sign-ins now goes to the admins.");
    }

    [Test]
    public async Task An_instance_without_mail_ends_a_sign_in_and_sends_nothing()
    {
        using var factory = new ServiceFactory(withMailpit: false);
        using var admin = await factory.SignedInAsync(AdminEmail, admin: true);
        var workspace = (await admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name = "Acme" })).GetProperty("id").GetInt32();
        (await admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/reminder-email", new { choice = "Everyone" })).EnsureSuccessStatusCode();
        int id;
        await using (var db = factory.NewContext())
        {
            var connection = NewConnection(workspace, FakeSource.Id, "Shop", factory.NewSecrets());
            connection.Connector = "schwab";
            connection.SignInEndsAt = DateTime.UtcNow.AddMinutes(-1);
            db.Connections.Add(connection);
            await db.SaveChangesAsync();
            id = connection.Id;
        }

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<SignInChecks>().RunAsync(CancellationToken.None);
        }

        var mail = await admin.GetAsync<JsonElement>($"/workspaces/{workspace}/reminder-email");
        mail.GetProperty("mailReady").GetBoolean().ShouldBeFalse();
        factory.Postmark.Sent.ShouldBeEmpty();
        await using var check = factory.NewContext();
        (await check.Connections.SingleAsync(c => c.Id == id)).NeedsSignIn.ShouldBeTrue();
    }

    private static string ErrorOf(HttpResponseMessage response) =>
        QueryHelpers.ParseQuery(new Uri(response.Headers.Location!.OriginalString).Query).TryGetValue("error", out var error) ? error.ToString() : string.Empty;

    private static string StateOf(string url) => QueryHelpers.ParseQuery(new Uri(url).Query)["state"].ToString();

    private static Connection NewConnection(int workspace, string connector, string name, Secrets secrets) => new()
    {
        WorkspaceId = workspace,
        Name = name,
        Connector = connector,
        Settings = secrets.Protect(new Dictionary<string, string?> { ["host"] = "db.test" }),
        SignedInAt = DateTime.UtcNow.AddMinutes(-10),
        CreatedAt = DateTime.UtcNow,
    };

    private Task<HttpResponseMessage> CallbackAsync(string signInPage) =>
        _browser.GetAsync(new Uri($"/oauth/callback?code={FakeSchwab.GoodCode}&state={StateOf(signInPage)}", UriKind.Relative));

    private async Task<int> SignInAsync(int workspace)
    {
        var page = (await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/sign-ins", new { name = "Brokerage", connector = "schwab", settings = Settings }))
            .GetProperty("url").GetString()!;
        using var back = await CallbackAsync(page);
        await using var db = _factory.NewContext();
        return await db.Connections.Where(c => c.Name == "Brokerage").Select(c => c.Id).SingleAsync();
    }

    private async Task<HttpResponseMessage> RenewAsync(int id)
    {
        var renewal = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/connections/{id}/sign-in");
        return await CallbackAsync(renewal.GetProperty("url").GetString()!);
    }

    private async Task ChecksAsync()
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SignInChecks>().RunAsync(CancellationToken.None);
    }

    private async Task<int> SeedAsync(int workspace, string connector, DateTime? keptAliveAt, string name = "Stand-in")
    {
        await using var db = _factory.NewContext();
        var connection = NewConnection(workspace, connector, name, _factory.NewSecrets());
        connection.KeptAliveAt = keptAliveAt;
        connection.SignedInAt = connector == FakeSource.Id ? null : connection.SignedInAt;
        db.Connections.Add(connection);
        await db.SaveChangesAsync();
        return connection.Id;
    }

    // A sync of the connection whose last run failed because the sign-in ended, with the sign-in ended since.
    private async Task<int> StoppedSyncAsync(int connection, int destination)
    {
        await using var db = _factory.NewContext();
        var sync = new Sync
        {
            ConnectionId = connection,
            DestinationId = destination,
            Stream = "balances",
            Table = "balances",
            Mode = SyncMode.Incremental,
            Status = SyncStatus.Failed,
            CreatedAt = DateTime.UtcNow,
        };
        db.Syncs.Add(sync);
        await db.SaveChangesAsync();
        db.Runs.Add(new Run
        {
            SyncId = sync.Id,
            Attempt = 1,
            Status = RunStatus.Failed,
            ErrorKind = ConnectorErrorKind.AuthExpired,
            Error = "Sign in to Schwab again.",
            StartedAt = DateTime.UtcNow,
            EndedAt = DateTime.UtcNow,
        });
        await db.Connections.Where(c => c.Id == connection).ExecuteUpdateAsync(set => set.SetProperty(c => c.NeedsSignIn, true));
        await db.SaveChangesAsync();
        return sync.Id;
    }

    private async Task EditAsync(int id, Action<Connection> edit)
    {
        await using var db = _factory.NewContext();
        edit(await db.Connections.SingleAsync(c => c.Id == id));
        await db.SaveChangesAsync();
    }

    private async Task<Connection> ConnectionAsync(int id)
    {
        await using var db = _factory.NewContext();
        return await db.Connections.SingleAsync(c => c.Id == id);
    }

    private async Task<List<LogEntry>> EntriesAsync()
    {
        await using var db = _factory.NewContext();
        return await db.LogEntries.OrderBy(e => e.Id).ToListAsync();
    }

    private async Task<T> WaitForAsync<T>(Func<NexusDbContext, Task<T?>> found)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            await using (var db = _factory.NewContext())
            {
                if (await found(db) is { } value)
                {
                    return value;
                }
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("What the test waited for never came.");
            }

            await Task.Delay(50);
        }
    }

    private async Task<int> AddMemberAsync(int workspace, string email)
    {
        var user = await _factory.AddUserAsync(email);
        (await _admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/members/{user.Id}")).EnsureSuccessStatusCode();
        return user.Id;
    }

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> MakeWorkspaceAsync()
    {
        var workspace = (await _admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name = "Acme" })).GetProperty("id").GetInt32();
        (await _admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/providers/schwab", new { clientId = "app-key", clientSecret = "app-secret" }))
            .EnsureSuccessStatusCode();
        return workspace;
    }

    private async Task<(string Key, int Destination)> MakeAppAsync(int workspace)
    {
        var made = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/apps", new { name = "Scry", returnUrls = Returns });
        var id = made.GetProperty("app").GetProperty("id").GetInt32();
        var destination = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{id}/destinations", new { url = "http://scry.test/ingest", key = FakePush.Key });
        return (made.GetProperty("key").GetString()!, destination.GetProperty("id").GetInt32());
    }
}
