using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class ConnectTests
{
    private const string ReturnUrl = "http://localhost:6160/sources";

    private static readonly string[] Returns = [ReturnUrl];

    private ServiceFactory _factory = null!;
    private Browser _admin = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ServiceFactory();
        _admin = await _factory.SignedInAsync("admin@acme.test", admin: true);
    }

    [TearDown]
    public void TearDown()
    {
        _admin.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task An_app_key_reaches_the_app_routes_and_nothing_else()
    {
        var scry = await MakeAppAsync(await MakeWorkspaceAsync("Acme"), "Scry");
        using var app = Bearer(scry.Key);
        using var stranger = Bearer("nexus_not-a-key-this-service-made");

        (await app.GetAsync("/connectors")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await app.GetAsync("/syncs")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var itself = await app.GetFromJsonAsync<JsonElement>("/app");
        itself.GetProperty("name").GetString().ShouldBe("Scry");
        itself.GetProperty("workspaceName").GetString().ShouldBe("Acme");
        itself.GetProperty("destinations").EnumerateArray().Single().GetProperty("id").GetInt32().ShouldBe(scry.Destination);
        itself.GetProperty("returnUrls").EnumerateArray().Single().GetString().ShouldBe(ReturnUrl);
        (await app.GetAsync("/workspaces")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await app.GetAsync("/users")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await stranger.GetAsync("/connectors")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Opens_a_connect_page_only_into_its_own_destination_and_back_to_its_own_address()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var scry = await MakeAppAsync(workspace, "Scry");
        var reports = await MakeAppAsync(workspace, "Reports");
        using var app = Bearer(scry.Key);

        using var elsewhere = await app.PostAsJsonAsync(
            "/connect-sessions", new { connector = FakeSource.Id, destinationId = scry.Destination, returnUrl = "https://evil.test/" });
        (await Browser.DetailAsync(elsewhere)).ShouldBe(
            "'https://evil.test/' is not one of this app's return addresses. An admin adds it on the app in Nexus.");
        using var theirs = await app.PostAsJsonAsync(
            "/connect-sessions", new { connector = FakeSource.Id, destinationId = reports.Destination, returnUrl = ReturnUrl });
        (await Browser.DetailAsync(theirs)).ShouldBe("Pick one of this app's destinations.");

        var opened = await OpenAsync(scry);

        opened.ShouldStartWith($"{ServiceFactory.ClientOrigin}/connect#nxc_");
    }

    [Test]
    public async Task A_connect_page_makes_one_connection_adds_its_syncs_and_returns_to_the_app()
    {
        var scry = await MakeAppAsync(await MakeWorkspaceAsync("Acme"), "Scry");
        _factory.Source.Rows.AddRange([[1L, "pen"], [2L, "ink"]]);
        using var page = Bearer((await OpenAsync(scry)).Split('#')[1]);

        var view = await page.GetFromJsonAsync<JsonElement>("/connect");
        view.GetProperty("appName").GetString().ShouldBe("Scry");
        view.GetProperty("workspaceName").GetString().ShouldBe("Acme");
        view.GetProperty("connector").GetProperty("id").GetString().ShouldBe(FakeSource.Id);
        view.GetProperty("connection").ValueKind.ShouldBe(JsonValueKind.Null);

        using var unreachable = await page.PostAsJsonAsync("/connect/connection", new { name = "Shop", settings = new { host = FakeSource.Unreachable } });
        (await Browser.DetailAsync(unreachable)).ShouldBe("Could not reach nowhere.test.");
        using var made = await page.PostAsJsonAsync("/connect/connection", new { name = "Shop", settings = new { host = "db.test" } });
        made.StatusCode.ShouldBe(HttpStatusCode.OK);
        var connectionId = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connection").GetProperty("id").GetInt32();
        (await page.PostAsJsonAsync("/connect/connection", new { name = "Again", settings = new { host = "db.test" } })).StatusCode
            .ShouldBe(HttpStatusCode.Conflict);

        (await page.GetFromJsonAsync<JsonElement>("/connect/streams")).EnumerateArray().Single().GetProperty("name").GetString()
            .ShouldBe(FakeSource.StreamName);
        using var finished = await page.PostAsJsonAsync(
            "/connect/syncs", new { streams = new[] { new { stream = FakeSource.StreamName, table = "orders", mode = "FullRefresh" } } }, Browser.Json);

        (await finished.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnUrl").GetString().ShouldBe($"{ReturnUrl}?connection={connectionId}");
        (await page.GetAsync("/connect")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var app = Bearer(scry.Key);
        var sync = await WaitForSyncAsync(app);
        sync.GetProperty("table").GetString().ShouldBe("orders");
        sync.GetProperty("lastRun").GetProperty("status").GetString().ShouldBe("Succeeded");
        _factory.Push.Tables["orders"].ShouldBe(["""[1,"pen"]""", """[2,"ink"]"""]);
    }

    [Test]
    public async Task A_connect_page_stops_working_once_it_expires_and_says_so()
    {
        var scry = await MakeAppAsync(await MakeWorkspaceAsync("Acme"), "Scry");
        using var page = Bearer((await OpenAsync(scry)).Split('#')[1]);
        await using (var db = _factory.NewContext())
        {
            await db.ConnectSessions.ExecuteUpdateAsync(set => set.SetProperty(s => s.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        using var expired = await page.GetAsync("/connect");

        expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Browser.DetailAsync(expired)).ShouldBe("This connect page has expired or is finished. Go back to the app and start again.");
    }

    [Test]
    public async Task Cancelling_returns_to_the_app_with_an_error()
    {
        var scry = await MakeAppAsync(await MakeWorkspaceAsync("Acme"), "Scry");
        using var page = Bearer((await OpenAsync(scry)).Split('#')[1]);

        using var cancelled = await page.PostAsync("/connect/cancel", null);

        (await cancelled.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnUrl").GetString().ShouldBe($"{ReturnUrl}?error=cancelled");
        (await page.GetAsync("/connect")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task An_app_runs_and_removes_only_the_syncs_into_its_own_destinations()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var scry = await MakeAppAsync(workspace, "Scry");
        var reports = await MakeAppAsync(workspace, "Reports");
        var syncId = await AddSyncAsync(workspace, scry.Destination);
        using var owner = Bearer(scry.Key);
        using var other = Bearer(reports.Key);
        await WaitForSyncAsync(owner);

        (await other.PostAsync($"/syncs/{syncId}/run", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.GetFromJsonAsync<JsonElement>("/syncs")).GetArrayLength().ShouldBe(0);
        (await owner.PostAsync($"/syncs/{syncId}/run", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await WaitForSyncAsync(owner);
        (await owner.DeleteAsync($"/syncs/{syncId}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Test]
    public async Task A_signed_in_member_still_needs_a_CSRF_token_to_run_a_sync()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var scry = await MakeAppAsync(workspace, "Scry");
        var syncId = await AddSyncAsync(workspace, scry.Destination);
        using var owner = Bearer(scry.Key);
        await WaitForSyncAsync(owner);

        (await _admin.SendAsync(HttpMethod.Post, $"/syncs/{syncId}/run", csrf: false)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _admin.SendAsync(HttpMethod.Post, $"/syncs/{syncId}/run")).StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    private sealed record MadeApp(int Id, string Key, int Destination);

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> MakeWorkspaceAsync(string name) =>
        (await _admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name })).GetProperty("id").GetInt32();

    private async Task<MadeApp> MakeAppAsync(int workspace, string name)
    {
        var made = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/apps", new { name, returnUrls = Returns });
        var id = made.GetProperty("app").GetProperty("id").GetInt32();
        var destination = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{id}/destinations", new { url = "http://scry.test/ingest", key = FakePush.Key });
        return new MadeApp(id, made.GetProperty("key").GetString()!, destination.GetProperty("id").GetInt32());
    }

    private async Task<string> OpenAsync(MadeApp app)
    {
        using var client = Bearer(app.Key);
        using var opened = await client.PostAsJsonAsync("/connect-sessions", new { connector = FakeSource.Id, destinationId = app.Destination, returnUrl = ReturnUrl });
        opened.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
    }

    private async Task<int> AddSyncAsync(int workspace, int destination)
    {
        var connection = await _admin.SendAsync<JsonElement>(
            HttpMethod.Post, $"/workspaces/{workspace}/connections", new { name = "Shop", connector = FakeSource.Id, settings = new { host = "db.test" } });
        var syncs = await _admin.SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/connections/{connection.GetProperty("id").GetInt32()}/syncs",
            new { destinationId = destination, streams = new[] { new { stream = FakeSource.StreamName, table = "orders", mode = "FullRefresh" } } });
        return syncs.EnumerateArray().Single().GetProperty("id").GetInt32();
    }

    // Waits until the app's one sync has a last run and is neither queued nor running, and answers it.
    private static async Task<JsonElement> WaitForSyncAsync(HttpClient app)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var syncs = await app.GetFromJsonAsync<JsonElement>("/syncs");
            if (syncs.GetArrayLength() == 1
                && syncs[0].GetProperty("lastRun").ValueKind != JsonValueKind.Null
                && syncs[0].GetProperty("status").GetString() is "Ready" or "Failed")
            {
                return syncs[0];
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The app's sync did not finish a run.");
            }

            await Task.Delay(50);
        }
    }
}
