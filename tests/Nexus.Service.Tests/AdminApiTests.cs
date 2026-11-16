using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Storage;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class AdminApiTests
{
    private const string Admin = "admin@acme.test";

    private static readonly string[] ScryReturns = ["http://localhost:6160/sources"];

    private ServiceFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new ServiceFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Everything_but_signing_in_needs_a_session()
    {
        using var browser = _factory.NewBrowser();

        (await browser.GetAsync("/workspaces")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetAsync("/connectors")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await browser.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_member_sees_only_their_workspaces_and_none_of_the_admin_routes()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var mine = await MakeWorkspaceAsync(admin, "Acme");
        var theirs = await MakeWorkspaceAsync(admin, "Globex");
        using var member = await _factory.SignedInAsync("member@acme.test");
        var memberId = (await member.GetAsync<JsonElement>("/auth/me")).GetProperty("id").GetInt32();
        (await admin.SendAsync(HttpMethod.Put, $"/workspaces/{mine}/members/{memberId}")).EnsureSuccessStatusCode();

        var seen = await member.GetAsync<JsonElement>("/workspaces");

        seen.EnumerateArray().Select(w => w.GetProperty("name").GetString()).ShouldBe(["Acme"]);
        (await member.GetAsync($"/workspaces/{theirs}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await member.GetAsync("/users")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await member.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Mine" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await member.SendAsync(HttpMethod.Post, $"/workspaces/{mine}/apps", new { name = "Scry" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Invites_a_user_by_mail()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);

        var invited = await admin.SendAsync<JsonElement>(HttpMethod.Post, "/users", new { email = "New@Acme.test", isAdmin = false });

        invited.GetProperty("email").GetString().ShouldBe("new@acme.test");
        invited.GetProperty("hasPassword").GetBoolean().ShouldBeFalse();
        var mail = Browser.MailTo(_factory.MailDirectory, "new@acme.test").Single();
        mail.Subject.ShouldBe("You're invited to Nexus");
        using var newcomer = _factory.NewBrowser();
        (await newcomer.SendAsync(HttpMethod.Post, "/auth/link/redeem", new { token = Browser.LinkToken(mail) })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await admin.SendAsync(HttpMethod.Post, "/users", new { email = "new@acme.test" })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task Keeps_at_least_one_admin_and_never_removes_the_one_asking()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var id = (await admin.GetAsync<JsonElement>("/auth/me")).GetProperty("id").GetInt32();

        (await admin.SendAsync(HttpMethod.Patch, $"/users/{id}", new { isAdmin = false })).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await admin.SendAsync(HttpMethod.Delete, $"/users/{id}")).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task An_app_key_shows_once_and_a_destination_is_kept_only_once_its_key_works()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");

        var made = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/apps", new { name = "Scry", returnUrls = ScryReturns });

        var key = made.GetProperty("key").GetString()!;
        key.ShouldStartWith("nexus_");
        var app = made.GetProperty("app");
        app.GetProperty("keyPrefix").GetString().ShouldBe(key[..12]);
        var listed = (await admin.GetAsync<JsonElement>($"/workspaces/{workspace}/apps")).EnumerateArray().Single();
        listed.TryGetProperty("key", out _).ShouldBeFalse();

        var keyMade = app.GetProperty("keyCreatedAt").GetDateTime();
        keyMade.ShouldBe(app.GetProperty("createdAt").GetDateTime());

        var appId = app.GetProperty("id").GetInt32();
        var revoked = await admin.SendAsync<JsonElement>(HttpMethod.Delete, $"/apps/{appId}/key");
        revoked.GetProperty("keyCreatedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        var renewed = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{appId}/key");
        renewed.GetProperty("app").GetProperty("keyCreatedAt").GetDateTime().ShouldBeGreaterThanOrEqualTo(keyMade);

        using var refused = await admin.SendAsync(HttpMethod.Post, $"/apps/{appId}/destinations", new { url = "http://scry.test/ingest", key = "scry_wrong" });
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(refused)).ShouldBe("The push API refused the key. Send a push API key.");
        var destination = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{appId}/destinations", new { url = "http://scry.test/ingest", key = FakePush.Key });
        destination.GetProperty("keyName").GetString().ShouldBe("Nexus");
        destination.GetProperty("keyPrefix").GetString().ShouldBe("scry_good");
        destination.GetProperty("keyCheckedAt").ValueKind.ShouldBe(JsonValueKind.String);
        destination.GetProperty("keyProblem").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public async Task Checks_a_destinations_key_and_records_a_refusal()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var scry = await MakeAppAsync(admin, workspace, "Scry", "http://scry.test/ingest");
        var secrets = _factory.Services.GetRequiredService<Secrets>();
        await using (var db = _factory.NewContext())
        {
            await db.Destinations.ExecuteUpdateAsync(set => set.SetProperty(d => d.Key, secrets.ProtectText("scry_revoked")));
        }

        var checkedKey = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/destinations/{scry.Destination}/check");

        checkedKey.GetProperty("keyProblem").GetString().ShouldBe("The push API refused the key. Send a push API key.");
        checkedKey.GetProperty("keyName").GetString().ShouldBe("Nexus");
        var listed = (await admin.GetAsync<JsonElement>($"/workspaces/{workspace}/apps")).EnumerateArray().Single();
        listed.GetProperty("destinations")[0].GetProperty("keyProblem").GetString().ShouldNotBeNull();
        using var member = await _factory.SignedInAsync("member@acme.test");
        (await member.SendAsync(HttpMethod.Post, $"/destinations/{scry.Destination}/check")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task A_member_connects_a_source_and_syncs_a_stream_into_an_apps_destination()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var destination = await MakeDestinationAsync(admin, workspace);
        using var member = await _factory.SignedInAsync("member@acme.test");
        var memberId = (await member.GetAsync<JsonElement>("/auth/me")).GetProperty("id").GetInt32();
        (await admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/members/{memberId}")).EnsureSuccessStatusCode();
        _factory.Source.Rows.AddRange([[1L, "pen"], [2L, "ink"]]);

        var fake = (await member.GetAsync<JsonElement>("/connectors")).EnumerateArray().Single(c => c.GetProperty("id").GetString() == FakeSource.Id);
        fake.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ShouldBe(["host", "password"]);
        using var unreachable = await member.SendAsync(
            HttpMethod.Post, $"/workspaces/{workspace}/connections", new { name = "Shop", connector = FakeSource.Id, settings = new { host = FakeSource.Unreachable } });
        (await Browser.DetailAsync(unreachable)).ShouldBe("Could not reach nowhere.test.");
        var connection = await member.SendAsync<JsonElement>(
            HttpMethod.Post, $"/workspaces/{workspace}/connections", new { name = "Shop", connector = FakeSource.Id, settings = new { host = "db.test", password = "hunter2" } });
        connection.GetProperty("settings").GetRawText().ShouldBe("""{"host":"db.test"}""");
        var connectionId = connection.GetProperty("id").GetInt32();

        var streams = await member.GetAsync<JsonElement>($"/connections/{connectionId}/streams");
        streams.EnumerateArray().Single().GetProperty("name").GetString().ShouldBe(FakeSource.StreamName);
        var syncs = await member.SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/connections/{connectionId}/syncs",
            new { destinationId = destination, streams = new[] { new { stream = FakeSource.StreamName, table = "orders", mode = "FullRefresh" } } });
        var syncId = syncs.EnumerateArray().Single().GetProperty("id").GetInt32();

        var run = await WaitForRunAsync(member, syncId);
        run.GetProperty("status").GetString().ShouldBe("Succeeded");
        run.GetProperty("rows").GetInt64().ShouldBe(2);
        _factory.Push.Tables["orders"].ShouldBe(["""[1,"pen"]""", """[2,"ink"]"""]);
        var listed = (await member.GetAsync<JsonElement>($"/workspaces/{workspace}/syncs")).EnumerateArray().Single();
        listed.GetProperty("appName").GetString().ShouldBe("Scry");
        listed.GetProperty("lastRun").GetProperty("status").GetString().ShouldBe("Succeeded");

        (await member.SendAsync(HttpMethod.Post, $"/syncs/{syncId}/run")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForRunAsync(member, syncId, runs: 2);
        (await member.SendAsync(HttpMethod.Delete, $"/syncs/{syncId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await member.GetAsync<JsonElement>($"/workspaces/{workspace}/syncs")).GetArrayLength().ShouldBe(0);
    }

    [Test]
    public async Task Refuses_a_table_name_the_destination_cannot_take_or_already_has()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var destination = await MakeDestinationAsync(admin, workspace);
        var connection = await admin.SendAsync<JsonElement>(
            HttpMethod.Post, $"/workspaces/{workspace}/connections", new { name = "Shop", connector = FakeSource.Id, settings = new { host = "db.test" } });
        var path = $"/connections/{connection.GetProperty("id").GetInt32()}/syncs";

        using var bad = await admin.SendAsync(HttpMethod.Post, path, Sync(destination, "Shop.Orders"));
        (await Browser.DetailAsync(bad)).ShouldBe($"'Shop.Orders': {SyncMaker.TableRule}");
        (await admin.SendAsync(HttpMethod.Post, path, Sync(destination, "orders"))).StatusCode.ShouldBe(HttpStatusCode.Created);
        using var twice = await admin.SendAsync(HttpMethod.Post, path, Sync(destination, "orders"));
        (await Browser.DetailAsync(twice)).ShouldBe("The destination already has a sync into 'orders'. Pick another table name.");
    }

    [Test]
    public async Task Moves_a_sync_into_another_apps_destination_and_reads_an_incremental_stream_from_the_start()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var scry = await MakeDestinationAsync(admin, workspace);
        var reports = await MakeAppAsync(admin, workspace, "Reports", "http://reports.test/ingest");
        _factory.Source.Rows.AddRange([[1L, "pen"], [2L, "ink"]]);
        var connection = await AddConnectionAsync(admin, workspace);
        var syncId = await AddSyncAsync(admin, connection, scry, "orders", "Incremental");
        (await WaitForRunAsync(admin, syncId)).GetProperty("rows").GetInt64().ShouldBe(2);

        var moved = await admin.SendAsync<JsonElement>(
            HttpMethod.Patch, $"/syncs/{syncId}", new { destinationId = reports.Destination, table = "orders", mode = "Incremental", cursorField = "id" });

        moved.GetProperty("destinationId").GetInt32().ShouldBe(reports.Destination);
        moved.GetProperty("destinationUrl").GetString().ShouldBe("http://reports.test/ingest");
        moved.GetProperty("appName").GetString().ShouldBe("Reports");
        moved.GetProperty("destinationKeyName").GetString().ShouldBe("Nexus");
        moved.GetProperty("connectorName").GetString().ShouldBe("Fake");
        moved.GetProperty("status").GetString().ShouldBe(nameof(SyncStatus.Queued));
        // A read resumed from the checkpoint would send only row 2 again.
        (await WaitForRunAsync(admin, syncId, runs: 2)).GetProperty("rows").GetInt64().ShouldBe(2);
        _factory.Source.Reads[1].State.ShouldBeNull();
    }

    [Test]
    public async Task Renames_a_table_and_changes_how_it_is_read()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var scry = await MakeDestinationAsync(admin, workspace);
        _factory.Source.Rows.AddRange([[1L, "pen"], [2L, "ink"]]);
        var syncId = await AddSyncAsync(admin, await AddConnectionAsync(admin, workspace), scry, "orders", "FullRefresh");
        await WaitForRunAsync(admin, syncId);

        var edited = await admin.SendAsync<JsonElement>(
            HttpMethod.Patch, $"/syncs/{syncId}", new { destinationId = scry, table = "shop_orders", mode = "Incremental", cursorField = "id" });

        edited.GetProperty("table").GetString().ShouldBe("shop_orders");
        edited.GetProperty("mode").GetString().ShouldBe("Incremental");
        edited.GetProperty("cursorField").GetString().ShouldBe("id");
        (await WaitForRunAsync(admin, syncId, runs: 2)).GetProperty("status").GetString().ShouldBe("Succeeded");
        _factory.Push.Tables["shop_orders"].ShouldBe(["""[1,"pen"]""", """[2,"ink"]"""]);
        _factory.Push.Tables.ContainsKey("orders").ShouldBeTrue();
    }

    [Test]
    public async Task Refuses_an_edit_that_changes_nothing_breaks_a_rule_or_cannot_be_read_and_one_while_running()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var scry = await MakeAppAsync(admin, workspace, "Scry", "http://scry.test/ingest");
        var reports = await MakeAppAsync(admin, workspace, "Reports", "http://reports.test/ingest");
        var elsewhere = await MakeDestinationAsync(admin, await MakeWorkspaceAsync(admin, "Globex"));
        var connection = await AddConnectionAsync(admin, workspace);
        var syncId = await AddSyncAsync(admin, connection, scry.Destination, "orders", "FullRefresh");
        await WaitForRunAsync(admin, syncId);
        await WaitForRunAsync(admin, await AddSyncAsync(admin, connection, reports.Destination, "orders", "FullRefresh"));
        var path = $"/syncs/{syncId}";

        async Task<string?> RefusalAsync(int destination, string table, string mode, string? cursorField = null)
        {
            using var refused = await admin.SendAsync(HttpMethod.Patch, path, new { destinationId = destination, table, mode, cursorField });
            refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            return await Browser.DetailAsync(refused);
        }

        (await RefusalAsync(scry.Destination, "orders", "FullRefresh")).ShouldBe("Nothing changed. Pick another destination, table name, or way to read it.");
        (await RefusalAsync(elsewhere, "orders", "FullRefresh")).ShouldBe("Pick a destination of an app in this workspace.");
        (await RefusalAsync(reports.Destination, "orders", "FullRefresh")).ShouldBe("The destination already has a sync into 'orders'. Pick another table name.");
        (await RefusalAsync(scry.Destination, "Shop.Orders", "FullRefresh")).ShouldBe($"'Shop.Orders': {SyncMaker.TableRule}");
        (await RefusalAsync(scry.Destination, "orders", "Incremental", "nope")).ShouldBe("'shop.orders' cannot track 'nope'. It can track: id.");

        await using (var db = _factory.NewContext())
        {
            await db.Syncs.Where(s => s.Id == syncId).ExecuteUpdateAsync(set => set.SetProperty(s => s.Status, SyncStatus.Running));
        }

        using var running = await admin.SendAsync(HttpMethod.Patch, path, new { destinationId = reports.Destination, table = "items", mode = "FullRefresh" });
        running.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Browser.DetailAsync(running)).ShouldBe("This sync is queued or running. Wait for it to end, then change it.");
    }

    [Test]
    public async Task Removing_a_destination_or_an_app_keeps_its_syncs_waiting_for_another()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var scry = await MakeAppAsync(admin, workspace, "Scry", "http://scry.test/ingest");
        var reports = await MakeAppAsync(admin, workspace, "Reports", "http://reports.test/ingest");
        var spare = await MakeAppAsync(admin, workspace, "Spare", "http://spare.test/ingest");
        var connection = await AddConnectionAsync(admin, workspace);
        var orders = await AddSyncAsync(admin, connection, scry.Destination, "orders", "FullRefresh");
        var items = await AddSyncAsync(admin, connection, reports.Destination, "items", "FullRefresh");
        await WaitForRunAsync(admin, orders);
        await WaitForRunAsync(admin, items);

        (await admin.SendAsync(HttpMethod.Delete, $"/destinations/{scry.Destination}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await admin.SendAsync(HttpMethod.Delete, $"/apps/{reports.Id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var listed = (await admin.GetAsync<JsonElement>($"/workspaces/{workspace}/syncs")).EnumerateArray().ToList();
        listed.Select(s => s.GetProperty("table").GetString()).ShouldBe(["items", "orders"]);
        listed.ShouldAllBe(s => s.GetProperty("destinationId").ValueKind == JsonValueKind.Null && s.GetProperty("appName").ValueKind == JsonValueKind.Null);
        using var unsent = await admin.SendAsync(HttpMethod.Post, $"/syncs/{orders}/run");
        unsent.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Browser.DetailAsync(unsent)).ShouldBe("Pick a destination for this sync first.");

        var sent = await admin.SendAsync<JsonElement>(HttpMethod.Patch, $"/syncs/{orders}", new { destinationId = spare.Destination, table = "orders", mode = "FullRefresh" });

        sent.GetProperty("appName").GetString().ShouldBe("Spare");
        (await WaitForRunAsync(admin, orders, runs: 2)).GetProperty("status").GetString().ShouldBe("Succeeded");
    }

    [Test]
    public async Task An_app_cannot_change_a_sync()
    {
        using var admin = await _factory.SignedInAsync(Admin, admin: true);
        var workspace = await MakeWorkspaceAsync(admin, "Acme");
        var scry = await MakeAppAsync(admin, workspace, "Scry", "http://scry.test/ingest");
        var second = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{scry.Id}/destinations", new { url = "http://scry-two.test/ingest", key = FakePush.Key });
        var syncId = await AddSyncAsync(admin, await AddConnectionAsync(admin, workspace), scry.Destination, "orders", "FullRefresh");
        await WaitForRunAsync(admin, syncId);
        using var app = _factory.CreateClient();
        app.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scry.Key);

        using var refused = await app.PatchAsJsonAsync(
            $"/syncs/{syncId}", new { destinationId = second.GetProperty("id").GetInt32(), table = "orders", mode = "FullRefresh" });

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private sealed record MadeApp(int Id, string Key, int Destination);

    private static object Sync(int destination, string table) =>
        new { destinationId = destination, streams = new[] { new { stream = FakeSource.StreamName, table, mode = "FullRefresh" } } };

    private static async Task<int> MakeWorkspaceAsync(Browser admin, string name) =>
        (await admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name })).GetProperty("id").GetInt32();

    private static async Task<int> MakeDestinationAsync(Browser admin, int workspace) =>
        (await MakeAppAsync(admin, workspace, "Scry", "http://scry.test/ingest")).Destination;

    private static async Task<MadeApp> MakeAppAsync(Browser admin, int workspace, string name, string url)
    {
        var made = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/apps", new { name });
        var appId = made.GetProperty("app").GetProperty("id").GetInt32();
        var destination = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{appId}/destinations", new { url, key = FakePush.Key });
        return new MadeApp(appId, made.GetProperty("key").GetString()!, destination.GetProperty("id").GetInt32());
    }

    private static async Task<int> AddConnectionAsync(Browser admin, int workspace) =>
        (await admin.SendAsync<JsonElement>(
            HttpMethod.Post, $"/workspaces/{workspace}/connections", new { name = "Shop", connector = FakeSource.Id, settings = new { host = "db.test" } }))
        .GetProperty("id").GetInt32();

    private static async Task<int> AddSyncAsync(Browser admin, int connection, int destination, string table, string mode)
    {
        var syncs = await admin.SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/connections/{connection}/syncs",
            new { destinationId = destination, streams = new[] { new { stream = FakeSource.StreamName, table, mode, cursorField = "id" } } });
        return syncs.EnumerateArray().Single().GetProperty("id").GetInt32();
    }

    // Waits until the sync has ended this many runs, and answers the newest.
    private static async Task<JsonElement> WaitForRunAsync(Browser browser, int syncId, int runs = 1)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var list = await browser.GetAsync<JsonElement>($"/syncs/{syncId}/runs");
            var ended = list.EnumerateArray().Where(r => r.GetProperty("endedAt").ValueKind != JsonValueKind.Null).ToList();
            if (ended.Count >= runs && ended[0].GetProperty("status").GetString() != nameof(RunStatus.Running))
            {
                return ended[0];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Sync {syncId} ended {ended.Count} of {runs} runs.");
            }

            await Task.Delay(50);
        }
    }
}
