using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nexus.Service.Auth;
using Nexus.Service.Storage;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class OperatorLogTests
{
    private const string Admin = "admin@acme.test";

    private const string Member = "member@acme.test";

    private const string Into = "into Scry at http://scry.test/ingest";

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    private ServiceFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new ServiceFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task A_run_that_succeeds_says_what_it_sent_where_on_its_sync_and_workspace()
    {
        _factory.Source.Rows.AddRange([[1L, "pen"], [2L, "ink"]]);
        var seeded = await SeedAsync();

        await RunAsync(seeded.SyncId);

        var entry = (await EntriesAsync()).Single();
        entry.Text.ShouldBe($"Sync orders sent 2 rows from Shop (Fake), stream shop.orders, {Into}.");
        entry.Level.ShouldBe(EntryLevel.Information);
        entry.Actor.ShouldBeNull();
        entry.SyncId.ShouldBe(seeded.SyncId);
        entry.ConnectionId.ShouldBe(seeded.ConnectionId);
        entry.WorkspaceId.ShouldBe(seeded.WorkspaceId);
    }

    [Test]
    public async Task A_run_that_will_try_again_warns_and_says_when()
    {
        _factory.Source.Rows.Add([1L, "pen"]);
        _factory.Source.Failures.Enqueue(new ConnectorException(ConnectorErrorKind.Transient, "The database is busy."));
        var seeded = await SeedAsync();

        await RunAsync(seeded.SyncId, expectedRuns: 2);

        var entries = await EntriesAsync();
        entries[0].Level.ShouldBe(EntryLevel.Warning);
        entries[0].Text.ShouldBe("Sync orders failed: The database is busy. It tries again now (try 2 of 4).");
        entries[1].Text.ShouldStartWith("Sync orders sent 1 row from");
    }

    [Test]
    public async Task A_run_that_fails_for_good_is_an_error_with_the_reason()
    {
        _factory.Source.Failures.Enqueue(new ConnectorException(ConnectorErrorKind.ConfigInvalid, "The password is wrong. Change it on the workspace page."));
        var seeded = await SeedAsync();

        await RunAsync(seeded.SyncId);

        var entry = (await EntriesAsync()).Single();
        entry.Level.ShouldBe(EntryLevel.Error);
        entry.Text.ShouldBe("Sync orders failed: The password is wrong. Change it on the workspace page.");
    }

    [Test]
    public async Task An_unexpected_error_is_named_on_the_log_page_with_no_stack_trace()
    {
        _factory.Source.Failures.Enqueue(new InvalidOperationException("The fake broke."));
        var seeded = await SeedAsync();

        var run = await RunAsync(seeded.SyncId);

        run.Error.ShouldBe(SyncRunner.Unexpected);
        var entry = (await EntriesAsync()).Single();
        entry.Level.ShouldBe(EntryLevel.Error);
        entry.Text.ShouldBe("Sync orders stopped on an error the service did not expect: InvalidOperationException: The fake broke.");
        _factory.Logs.Lines.ShouldContain(line => line.Contains("stopped on an unexpected error", StringComparison.Ordinal));
    }

    [Test]
    public async Task An_ended_sign_in_warns_on_the_connection_once()
    {
        _factory.Source.Failures.Enqueue(new ConnectorException(ConnectorErrorKind.AuthExpired, "Sign in again."));
        var seeded = await SeedAsync();

        await RunAsync(seeded.SyncId);

        var entries = await EntriesAsync();
        var warned = entries.Single(e => e.Level == EntryLevel.Warning);
        warned.Text.ShouldBe("Shop (Fake) needs a new sign-in. Its syncs stop until someone signs in again from the workspace page.");
        warned.ConnectionId.ShouldBe(seeded.ConnectionId);
        warned.SyncId.ShouldBeNull();
        entries.Single(e => e.Level == EntryLevel.Error).Text.ShouldBe("Sync orders failed: Sign in again.");
    }

    [Test]
    public async Task A_connectors_own_lines_join_the_sync_from_Information_up()
    {
        _factory.Source.Notes.AddRange([new LogMessage(LogLevel.Warning, "Two rows were skipped."), new LogMessage(LogLevel.Debug, "Read 2 rows.")]);
        var seeded = await SeedAsync();

        await RunAsync(seeded.SyncId);

        var entries = await EntriesAsync();
        var noted = entries.Single(e => e.Level == EntryLevel.Warning);
        noted.Text.ShouldBe("Sync orders: Two rows were skipped.");
        noted.SyncId.ShouldBe(seeded.SyncId);
        entries.ShouldNotContain(e => e.Text.Contains("Read 2 rows.", StringComparison.Ordinal));
    }

    [Test]
    public async Task The_developer_log_gets_each_saved_entry()
    {
        _factory.Source.Rows.Add([1L, "pen"]);
        var seeded = await SeedAsync();

        await RunAsync(seeded.SyncId);

        _factory.Logs.Written.ShouldContain(line =>
            line.Category == OperatorLogWriter.Category && line.Line == $"Sync orders sent 1 row from Shop (Fake), stream shop.orders, {Into}.");
    }

    [Test]
    public async Task Running_a_sync_by_hand_names_who_asked()
    {
        var seeded = await SeedAsync();
        using var admin = await _factory.SignedInAsync(Admin, admin: true);

        (await admin.SendAsync(HttpMethod.Post, $"/syncs/{seeded.SyncId}/run")).StatusCode.ShouldBe(HttpStatusCode.Accepted);

        var entry = (await EntriesAsync()).First();
        entry.Text.ShouldBe("Sync orders was asked to run now.");
        entry.Actor.ShouldBe(Admin);
        entry.SyncId.ShouldBe(seeded.SyncId);
    }

    [Test]
    public async Task A_change_that_is_refused_leaves_no_entry()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var before = (await EntriesAsync()).Count;

        using var refused = await admin.SendAsync(
            HttpMethod.Post,
            $"/workspaces/{workspace}/connections",
            new { name = "Shop", connector = FakeSource.Id, settings = new { host = FakeSource.Unreachable } });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await EntriesAsync()).Count.ShouldBe(before);
    }

    [Test]
    public async Task A_member_reads_their_own_workspaces_entries_and_an_admin_reads_every_one()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        using var member = await _factory.SignedInAsync(Member);
        var acme = await MakeWorkspaceAsync(admin, "Acme");
        var other = await MakeWorkspaceAsync(admin, "Other");
        var memberId = await IdOfAsync(Member);
        (await admin.SendAsync(HttpMethod.Put, $"/workspaces/{acme}/members/{memberId}")).EnsureSuccessStatusCode();
        (await admin.SendAsync(HttpMethod.Post, "/users", new { email = "new@acme.test" })).EnsureSuccessStatusCode();

        var mine = await TextsAsync(member, "/log");
        var all = await TextsAsync(admin, "/log");
        var instance = await TextsAsync(admin, "/log?instance=true");

        mine.ShouldBe([$"{Member} was added to workspace Acme.", "Workspace Acme was made."]);
        all.ShouldBe(["new@acme.test was invited.", $"{Member} was added to workspace Acme.", "Workspace Other was made.", "Workspace Acme was made."]);
        instance.ShouldBe(["new@acme.test was invited."]);
        (await member.GetAsync($"/log?workspace={other}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await member.GetAsync("/log?instance=true")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var page = await admin.GetAsync<JsonElement>($"/log?workspace={acme}");
        page.GetProperty("entries")[0].GetProperty("workspaceName").GetString().ShouldBe("Acme");
        page.GetProperty("entries")[0].GetProperty("actor").GetString().ShouldBe(Admin);
    }

    [Test]
    public async Task Filters_by_level_and_sync_and_reads_older_entries_a_page_at_a_time()
    {
        var seeded = await SeedAsync();
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        await using (var db = _factory.NewContext())
        {
            var at = DateTime.UtcNow;
            for (var i = 0; i < Controllers.LogController.PageSize + 5; i++)
            {
                db.LogEntries.Add(new LogEntry { At = at, Level = EntryLevel.Information, Text = $"Entry {i}.", WorkspaceId = seeded.WorkspaceId });
            }

            db.LogEntries.Add(new LogEntry { At = at, Level = EntryLevel.Warning, Text = "A warning.", WorkspaceId = seeded.WorkspaceId, SyncId = seeded.SyncId });
            db.LogEntries.Add(new LogEntry { At = at, Level = EntryLevel.Error, Text = "An error.", WorkspaceId = seeded.WorkspaceId });
            await db.SaveChangesAsync();
        }

        (await TextsAsync(admin, "/log?level=Warning")).ShouldBe(["An error.", "A warning."]);
        (await TextsAsync(admin, "/log?level=Error")).ShouldBe(["An error."]);
        var bySync = await admin.GetAsync<JsonElement>($"/log?sync={seeded.SyncId}");
        bySync.GetProperty("subject").GetString().ShouldBe("Sync orders");
        bySync.GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("text").GetString()).ShouldBe(["A warning."]);

        var first = await admin.GetAsync<JsonElement>("/log");
        first.GetProperty("entries").GetArrayLength().ShouldBe(Controllers.LogController.PageSize);
        var next = first.GetProperty("next").GetInt64();
        var rest = await admin.GetAsync<JsonElement>($"/log?before={next}");
        rest.GetProperty("entries").GetArrayLength().ShouldBe(7);
        rest.GetProperty("next").ValueKind.ShouldBe(JsonValueKind.Null);
        rest.GetProperty("entries")[6].GetProperty("text").GetString().ShouldBe("Entry 0.");
    }

    [Test]
    public async Task Removing_a_workspace_keeps_its_entries_for_admins()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var acme = await MakeWorkspaceAsync(admin, "Acme");

        (await admin.SendAsync(HttpMethod.Delete, $"/workspaces/{acme}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await TextsAsync(admin, "/log?instance=true")).ShouldBe(
        [
            "Workspace Acme was removed, with its apps, connections, and syncs. The tables at each destination stay.",
            "Workspace Acme was made.",
        ]);
    }

    [Test]
    public async Task Prunes_entries_older_than_the_days_kept_which_an_admin_changes()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        await AddEntriesAgedAsync(31, 1);

        await PruneAsync(_factory);
        (await AgesAsync()).ShouldBe([1]);

        (await admin.SendAsync<JsonElement>(HttpMethod.Put, "/settings/log", new { days = 60 })).GetProperty("logDays").GetInt32().ShouldBe(60);
        await AddEntriesAgedAsync(45);
        await PruneAsync(_factory);

        (await AgesAsync()).ShouldBe([45, 1]);
        (await TextsAsync(admin, "/log?instance=true")).ShouldContain("The log now keeps entries for 60 days.");
        (await admin.SendAsync(HttpMethod.Put, "/settings/log", new { days = 0 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.SendAsync(HttpMethod.Put, "/settings/log", new { days = 366 })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task A_configured_number_of_days_wins_and_the_page_cannot_change_it()
    {
        using var configured = _factory.WithWebHostBuilder(builder => builder.UseSetting("Nexus:LogDays", "7"));
        await _factory.AddUserAsync(Admin, Browser.Password, admin: true);
        var cookies = new CookieContainer();
        var client = configured.CreateDefaultClient(new Uri("http://localhost/"), new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(cookies));
        using var browser = new Browser(client, cookies);
        (await browser.SignInAsync(Admin, Browser.Password)).EnsureSuccessStatusCode();

        var settings = await browser.GetAsync<JsonElement>("/settings");
        settings.GetProperty("logDays").GetInt32().ShouldBe(7);
        settings.GetProperty("logDaysConfigured").GetBoolean().ShouldBeTrue();
        (await browser.SendAsync(HttpMethod.Put, "/settings/log", new { days = 30 })).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        await AddEntriesAgedAsync(8, 6);
        await PruneAsync(configured);
        (await AgesAsync()).ShouldBe([6]);
    }

    [Test]
    public async Task Each_kind_of_change_writes_its_entry()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var app = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/apps", new { name = "Scry" });
        var appId = app.GetProperty("app").GetProperty("id").GetInt32();
        var appKey = app.GetProperty("key").GetString()!;
        var destination = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{appId}/destinations", new { url = "http://scry.test/ingest", key = FakePush.Key });
        var connection = await admin.SendAsync<JsonElement>(
            HttpMethod.Post, $"/workspaces/{workspace}/connections", new { name = "Shop", connector = FakeSource.Id, settings = new { host = "db.test" } });
        (await admin.SendAsync(
            HttpMethod.Post,
            $"/connections/{connection.GetProperty("id").GetInt32()}/syncs",
            new { destinationId = destination.GetProperty("id").GetInt32(), streams = new[] { new { stream = FakeSource.StreamName, table = "orders", mode = "FullRefresh" } } }))
            .EnsureSuccessStatusCode();
        (await admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/sign-in-apps/{OAuthApps.SchwabId}", new { clientId = "abcd1234", clientSecret = "s3cret" }))
            .EnsureSuccessStatusCode();
        await using (var db = _factory.NewContext())
        {
            await db.Destinations.ExecuteUpdateAsync(set => set.SetProperty(d => d.Key, _factory.NewSecrets().ProtectText("scry_revoked")));
        }

        (await admin.SendAsync(HttpMethod.Post, $"/destinations/{destination.GetProperty("id").GetInt32()}/check")).EnsureSuccessStatusCode();
        using (var asApp = _factory.CreateClient())
        {
            asApp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", appKey);
            (await asApp.DeleteAsync("/app/key")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var anonymous = _factory.CreateClient())
        {
            var opened = await anonymous.PostAsJsonAsync("/pairings", new
            {
                appName = "Scry at localhost:6161",
                ingestUrl = "http://localhost:6161/ingest",
                callbackUrl = "http://localhost:6161/nexus/callback",
                connectReturnUrl = "http://localhost:6160/sources",
                verifierHash = new string('a', 64),
            });
            var pairing = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            (await admin.SendAsync(HttpMethod.Post, $"/pairings/{pairing}/decline")).EnsureSuccessStatusCode();
        }

        await _factory.AddUserAsync("locked@acme.test", Browser.Password);
        using (var guesser = _factory.NewBrowser())
        {
            for (var i = 0; i < SignInThrottle.MaxFailures; i++)
            {
                await guesser.SignInAsync("locked@acme.test", "not the password");
            }
        }

        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<Admins>().AddAsync("ops@acme.test", CancellationToken.None);
        }

        var texts = (await EntriesAsync()).Select(e => e.Text).ToList();
        texts.ShouldContain("Workspace Acme was made.");
        texts.ShouldContain(t => t.StartsWith("App Scry was made, with key nexus_", StringComparison.Ordinal));
        texts.ShouldContain("Destination http://scry.test/ingest was added to app Scry, sending key scry_good…, named Nexus.");
        texts.ShouldContain("Connection Shop (Fake) was added.");
        texts.ShouldContain($"Sync orders was added. It reads stream shop.orders of Shop (Fake) in full, {Into}.");
        texts.ShouldContain("The Charles Schwab sign-in app was set, with a key ending 1234.");
        texts.ShouldContain("Destination http://scry.test/ingest of app Scry refused its key: The push API refused the key. Send a push API key.");
        texts.ShouldContain(t => t.StartsWith("App Scry revoked its own key nexus_", StringComparison.Ordinal));
        texts.ShouldContain("Scry at localhost:6161 asked to link, and was declined.");
        texts.ShouldContain("Signing in with a password as locked@acme.test is locked for 15 minutes, after 5 wrong passwords in a row. A mailed link still works.");
        texts.ShouldContain("The add-admin command made ops@acme.test an admin, with a sign-in link good for an hour.");
        (await EntriesAsync()).Single(e => e.Text.Contains("revoked its own key", StringComparison.Ordinal)).Actor.ShouldBe("app Scry");
    }

    private sealed record Seeded(int WorkspaceId, int ConnectionId, int SyncId);

    private async Task<Seeded> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NexusDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<Secrets>();
        var now = DateTime.UtcNow;
        var workspace = new Workspace { Name = "Acme", CreatedAt = now };
        var app = new App { Workspace = workspace, Name = "Scry", CreatedAt = now };
        var destination = new Destination { App = app, Url = "http://scry.test/ingest", Key = secrets.ProtectText(FakePush.Key), CreatedAt = now };
        var connection = new Connection
        {
            Workspace = workspace,
            Name = "Shop",
            Connector = FakeSource.Id,
            Settings = secrets.Protect(new Dictionary<string, string?> { ["host"] = "db.test" }),
            CreatedAt = now,
        };
        var sync = new Sync
        {
            Connection = connection,
            Destination = destination,
            Stream = FakeSource.StreamName,
            Table = "orders",
            Mode = SyncMode.FullRefresh,
            Status = SyncStatus.Ready,
            CreatedAt = now,
        };
        db.Syncs.Add(sync);
        await db.SaveChangesAsync();
        return new Seeded(workspace.Id, connection.Id, sync.Id);
    }

    // Queues the sync, then waits until it has ended this many runs in all, and answers the last.
    private async Task<Run> RunAsync(int syncId, int expectedRuns = 1)
    {
        _factory.Services.GetRequiredService<PendingRuns>().Enqueue(new PendingRun(syncId, 1));
        var deadline = DateTime.UtcNow + Patience;
        while (true)
        {
            await using var db = _factory.NewContext();
            var ended = await db.Runs.Where(r => r.SyncId == syncId && r.EndedAt != null).OrderBy(r => r.Id).ToListAsync();
            var status = await db.Syncs.Where(s => s.Id == syncId).Select(s => s.Status).SingleAsync();
            if (ended.Count >= expectedRuns && status is SyncStatus.Ready or SyncStatus.Failed)
            {
                return ended[^1];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Sync {syncId} ended {ended.Count} of {expectedRuns} runs and is {status}.");
            }

            await Task.Delay(50);
        }
    }

    // Oldest first.
    private async Task<List<LogEntry>> EntriesAsync()
    {
        await using var db = _factory.NewContext();
        return await db.LogEntries.OrderBy(e => e.Id).ToListAsync();
    }

    private static async Task<List<string?>> TextsAsync(Browser browser, string path) =>
        [.. (await browser.GetAsync<JsonElement>(path)).GetProperty("entries").EnumerateArray().Select(e => e.GetProperty("text").GetString())];

    private async Task AddEntriesAgedAsync(params int[] days)
    {
        await using var db = _factory.NewContext();
        foreach (var age in days)
        {
            db.LogEntries.Add(new LogEntry { At = DateTime.UtcNow.AddDays(-age), Level = EntryLevel.Information, Text = $"{age} days old." });
        }

        await db.SaveChangesAsync();
    }

    // The days old of each seeded entry still kept, oldest first.
    private async Task<List<int>> AgesAsync()
    {
        await using var db = _factory.NewContext();
        return [.. (await db.LogEntries.Where(e => e.Text.EndsWith(" days old.")).OrderBy(e => e.At).Select(e => e.Text).ToListAsync())
            .Select(text => int.Parse(text.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture))];
    }

    private static async Task PruneAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<LogPruning>().PruneAsync(CancellationToken.None);
    }

    private async Task<int> IdOfAsync(string email)
    {
        await using var db = _factory.NewContext();
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
    }

    private static async Task<int> MakeWorkspaceAsync(Browser admin, string name) =>
        (await admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name })).GetProperty("id").GetInt32();
}
