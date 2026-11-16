using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class SignInAppTests
{
    private const string NoApp = "This workspace has no Charles Schwab app yet. Add it under Sign-in apps on the workspace page in Nexus.";

    private const string ReturnUrl = "http://localhost:6160/sources";

    private static readonly Dictionary<string, string> Settings = new() { ["startDate"] = "2026-01-01" };

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
    public async Task A_workspace_sets_and_replaces_its_app_and_never_gets_its_secret_back()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        using var member = await _factory.SignedInAsync("member@acme.test");

        var before = (await _admin.GetAsync<JsonElement>($"/workspaces/{workspace}/sign-in-apps")).EnumerateArray().Single();
        using var secretless = await PutAppAsync(workspace, "key-one-1a2b", "");
        using var set = await PutAppAsync(workspace, "key-one-1a2b", "secret-one");
        var setBody = await set.Content.ReadAsStringAsync();
        using var replaced = await PutAppAsync(workspace, "key-two-9z9y", "");
        using var stranger = await member.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/sign-in-apps/schwab", new { clientId = "k", clientSecret = "s" });

        before.GetProperty("connector").GetString().ShouldBe("schwab");
        before.GetProperty("name").GetString().ShouldBe("Charles Schwab");
        before.GetProperty("set").GetBoolean().ShouldBeFalse();
        before.GetProperty("problem").GetString().ShouldBe(NoApp);
        before.GetProperty("callbackUrl").GetString().ShouldBe($"{ServiceFactory.CallbackOrigin}/oauth/callback");
        (await Browser.DetailAsync(secretless)).ShouldBe("Enter the app's secret from the Charles Schwab developer portal.");
        set.StatusCode.ShouldBe(HttpStatusCode.OK, setBody);
        setBody.ShouldNotContain("secret-one");
        var view = JsonDocument.Parse(setBody).RootElement;
        view.GetProperty("keyEnding").GetString().ShouldBe("1a2b");
        view.GetProperty("problem").ValueKind.ShouldBe(JsonValueKind.Null);
        (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("keyEnding").GetString().ShouldBe("9z9y");
        stranger.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await using var db = _factory.NewContext();
        var stored = await db.ProviderApps.SingleAsync();
        stored.ClientId.ShouldBe("key-two-9z9y");
        stored.ClientSecret.ShouldNotContain("secret-one");
        _factory.NewSecrets().UnprotectText(stored.ClientSecret).ShouldBe("secret-one");
    }

    [Test]
    public async Task Each_workspace_signs_in_through_its_own_app_and_removing_it_stops_sign_ins()
    {
        var acme = await MakeWorkspaceAsync("Acme");
        var globex = await MakeWorkspaceAsync("Globex");
        (await PutAppAsync(acme, "acme-key", "acme-secret")).EnsureSuccessStatusCode();
        (await PutAppAsync(globex, "globex-key", "globex-secret")).EnsureSuccessStatusCode();

        var acmePage = await StartAsync(acme);
        var globexPage = await StartAsync(globex);
        (await _admin.SendAsync(HttpMethod.Delete, $"/workspaces/{acme}/sign-in-apps/schwab")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var refused = await _admin.SendAsync(HttpMethod.Post, $"/workspaces/{acme}/sign-ins", new { name = "Brokerage", connector = "schwab", settings = Settings });

        ClientIdOf(acmePage).ShouldBe("acme-key");
        ClientIdOf(globexPage).ShouldBe("globex-key");
        (await Browser.DetailAsync(refused)).ShouldBe(NoApp);
    }

    [Test]
    public async Task A_workspace_without_an_app_says_so_to_its_apps_and_connect_pages()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var (key, destination) = await MakeAppAsync(workspace);
        using var app = Bearer(key);

        var connectors = await app.GetFromJsonAsync<JsonElement>("/connectors");
        using var opened = await app.PostAsJsonAsync("/connect-sessions", new { connector = "schwab", destinationId = destination, returnUrl = ReturnUrl });
        using var page = Bearer((await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!.Split('#')[1]);
        var view = await page.GetFromJsonAsync<JsonElement>("/connect");
        using var refused = await page.PostAsJsonAsync("/connect/sign-in", new { name = "Brokerage", settings = Settings });

        NotReadyOf(connectors, "schwab").ShouldBe(NoApp);
        NotReadyOf(connectors, FakeSource.Id).ShouldBeNull();
        view.GetProperty("connector").GetProperty("notReady").GetString().ShouldBe(NoApp);
        (await Browser.DetailAsync(refused)).ShouldBe(NoApp);
    }

    [Test]
    public async Task A_sign_in_under_way_survives_a_restart_of_the_service()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        (await PutAppAsync(workspace, "app-key", "app-secret")).EnsureSuccessStatusCode();
        var signInPage = await StartAsync(workspace);

        using var restarted = _factory.WithWebHostBuilder(_ => { });
        using var browser = restarted.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var back = await browser.GetAsync(new Uri($"/oauth/callback?code={FakeSchwab.GoodCode}&state={StateOf(signInPage)}", UriKind.Relative));
        using var again = await browser.GetAsync(new Uri($"/oauth/callback?code={FakeSchwab.GoodCode}&state={StateOf(signInPage)}", UriKind.Relative));

        await using var db = _factory.NewContext();
        var connection = await db.Connections.SingleAsync();
        back.Headers.Location!.OriginalString.ShouldBe($"{ServiceFactory.ClientOrigin}/workspaces/{workspace}?signedIn={connection.Id}");
        again.Headers.Location!.OriginalString.ShouldStartWith($"{ServiceFactory.ClientOrigin}/?error=");
        (await db.PendingSignIns.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task An_app_in_configuration_serves_every_workspace_and_the_page_cannot_change_it()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        using var configured = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"{SchwabApp.Section}:{nameof(SchwabApp.AppKey)}", "ops-key-wxyz");
            builder.UseSetting($"{SchwabApp.Section}:{nameof(SchwabApp.AppSecret)}", "ops-secret");
        });
        var cookies = new CookieContainer();
        using var admin = new Browser(configured.CreateDefaultClient(new Uri("http://localhost/"), new Microsoft.AspNetCore.Mvc.Testing.Handlers.CookieContainerHandler(cookies)), cookies);
        (await admin.SignInAsync("admin@acme.test", Browser.Password)).EnsureSuccessStatusCode();

        var view = (await admin.GetAsync<JsonElement>($"/workspaces/{workspace}/sign-in-apps")).EnumerateArray().Single();
        using var refused = await admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/sign-in-apps/schwab", new { clientId = "mine", clientSecret = "mine" });
        var started = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/sign-ins", new { name = "Brokerage", connector = "schwab", settings = Settings });

        view.GetProperty("configured").GetBoolean().ShouldBeTrue();
        view.GetProperty("keyEnding").GetString().ShouldBe("wxyz");
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        ClientIdOf(started.GetProperty("url").GetString()!).ShouldBe("ops-key-wxyz");
    }

    private static string StateOf(string url) => QueryHelpers.ParseQuery(new Uri(url).Query)["state"].ToString();

    private static string ClientIdOf(string url) => QueryHelpers.ParseQuery(new Uri(url).Query)["client_id"].ToString();

    private static string? NotReadyOf(JsonElement connectors, string id) =>
        connectors.EnumerateArray().Single(c => c.GetProperty("id").GetString() == id).GetProperty("notReady").GetString();

    private Task<HttpResponseMessage> PutAppAsync(int workspace, string clientId, string clientSecret) =>
        _admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/sign-in-apps/schwab", new { clientId, clientSecret });

    private async Task<string> StartAsync(int workspace) =>
        (await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/sign-ins", new { name = "Brokerage", connector = "schwab", settings = Settings }))
        .GetProperty("url").GetString()!;

    private async Task<int> MakeWorkspaceAsync(string name) =>
        (await _admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name })).GetProperty("id").GetInt32();

    private async Task<(string Key, int Destination)> MakeAppAsync(int workspace)
    {
        var made = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/apps", new { name = "Scry", returnUrls = new[] { ReturnUrl } });
        var id = made.GetProperty("app").GetProperty("id").GetInt32();
        var destination = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{id}/destinations", new { url = "http://scry.test/ingest", key = FakePush.Key });
        return (made.GetProperty("key").GetString()!, destination.GetProperty("id").GetInt32());
    }

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }
}
