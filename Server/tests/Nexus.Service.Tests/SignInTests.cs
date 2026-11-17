using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class SignInTests
{
    private const string ReturnUrl = "http://localhost:6160/sources";

    private static readonly string[] Returns = [ReturnUrl];

    private static readonly Dictionary<string, string> Settings = new() { ["startDate"] = "2026-01-01" };

    private ServiceFactory _factory = null!;
    private Browser _admin = null!;
    private HttpClient _browser = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ServiceFactory();
        _admin = await _factory.SignedInAsync("admin@acme.test", admin: true);
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
    public async Task A_connect_page_signs_in_and_makes_its_connection_with_the_tokens_kept_encrypted()
    {
        var workspace = await MakeWorkspaceAsync();
        var (key, destination) = await MakeAppAsync(workspace);
        var token = await OpenAsync(key, new { connector = "schwab", destinationId = destination, returnUrl = ReturnUrl });
        using var page = Bearer(token);

        using var started = await page.PostAsJsonAsync("/connect/sign-in", new { name = "Brokerage", settings = Settings });
        var url = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        url.ShouldStartWith("https://api.schwabapi.com/v1/oauth/authorize?");
        QueryHelpers.ParseQuery(new Uri(url).Query)["redirect_uri"].ToString().ShouldBe("https://127.0.0.1:6164/oauth/callback");

        using var back = await CallbackAsync(url, FakeSchwab.GoodCode);

        back.Headers.Location!.OriginalString.ShouldBe($"{ServiceFactory.ClientOrigin}/connect#{token}");
        var view = await page.GetFromJsonAsync<JsonElement>("/connect");
        view.GetProperty("connection").GetProperty("name").GetString().ShouldBe("Brokerage");
        var connection = await ConnectionAsync("Brokerage");
        connection.Tokens!.ShouldNotContain("access");
        _factory.Services.GetRequiredService<Secrets>().UnprotectTokens(connection.Tokens)!.AccessToken.ShouldBe("access");
    }

    [Test]
    public async Task A_member_signs_in_and_renews_a_connection_whose_sign_in_ended()
    {
        var workspace = await MakeWorkspaceAsync();
        var url = await StartAsync(workspace);
        using var made = await CallbackAsync(url, FakeSchwab.GoodCode);
        var id = (await ConnectionAsync("Brokerage")).Id;
        made.Headers.Location!.OriginalString.ShouldBe($"{ServiceFactory.ClientOrigin}/workspaces/{workspace}?signedIn={id}");
        await EndSignInAsync(id);

        var renewal = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/connections/{id}/sign-in");
        using var renewed = await CallbackAsync(renewal.GetProperty("url").GetString()!, FakeSchwab.GoodCode);

        renewed.Headers.Location!.OriginalString.ShouldBe($"{ServiceFactory.ClientOrigin}/workspaces/{workspace}?signedIn={id}");
        (await ConnectionAsync("Brokerage")).NeedsSignIn.ShouldBeFalse();
    }

    [Test]
    public async Task Refuses_a_lost_sign_in_a_declined_one_a_refused_code_and_a_connection_made_without_one()
    {
        var workspace = await MakeWorkspaceAsync();

        using var lost = await _browser.GetAsync(new Uri("/oauth/callback?code=good-code&state=nope", UriKind.Relative));
        using var declined = await _browser.GetAsync(new Uri($"/oauth/callback?error=access_denied&state={StateOf(await StartAsync(workspace))}", UriKind.Relative));
        using var refused = await CallbackAsync(await StartAsync(workspace), "bad-code");
        using var plain = await _admin.SendAsync(
            HttpMethod.Post, $"/workspaces/{workspace}/connections", new { name = "Brokerage", connector = "schwab", settings = Settings });
        using var bare = await _admin.SendAsync(HttpMethod.Post, $"/workspaces/{workspace}/sign-ins", new { name = "Brokerage", connector = "schwab" });

        ErrorOf(lost).ShouldBe("This sign-in ran out of time, or did not start in Nexus. Start it again.");
        lost.Headers.Location!.OriginalString.ShouldStartWith($"{ServiceFactory.ClientOrigin}/workspaces?error=");
        ErrorOf(declined).ShouldBe("Charles Schwab did not sign you in: access_denied.");
        ErrorOf(refused).ShouldBe("Schwab refused the sign-in (400): Bad code.");
        refused.Headers.Location!.OriginalString.ShouldStartWith($"{ServiceFactory.ClientOrigin}/workspaces/{workspace}?error=");
        (await Browser.DetailAsync(plain)).ShouldBe("Sign in to Charles Schwab to connect it.");
        (await Browser.DetailAsync(bare)).ShouldBe("Read history from (yyyy-MM-dd) is required.");
        await using var db = _factory.NewContext();
        (await db.Connections.CountAsync()).ShouldBe(0);
    }

    [Test]
    public async Task An_app_renews_a_connection_through_a_connect_page_and_gets_back_to_its_return_address()
    {
        var workspace = await MakeWorkspaceAsync();
        var (key, destination) = await MakeAppAsync(workspace);
        using var made = await CallbackAsync(await StartAsync(workspace), FakeSchwab.GoodCode);
        var id = (await ConnectionAsync("Brokerage")).Id;
        await EndSignInAsync(id);
        var token = await OpenAsync(key, new { connector = "schwab", destinationId = destination, returnUrl = ReturnUrl, connectionId = id });
        using var page = Bearer(token);

        var view = await page.GetFromJsonAsync<JsonElement>("/connect");
        using var started = await page.PostAsJsonAsync("/connect/sign-in", new { });
        using var back = await CallbackAsync((await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!, FakeSchwab.GoodCode);

        view.GetProperty("renewing").GetBoolean().ShouldBeTrue();
        view.GetProperty("connection").GetProperty("id").GetInt32().ShouldBe(id);
        back.Headers.Location!.OriginalString.ShouldBe($"{ReturnUrl}?connection={id}");
        (await ConnectionAsync("Brokerage")).NeedsSignIn.ShouldBeFalse();
        (await page.GetAsync(new Uri("/connect", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task A_Schwab_connection_reads_with_the_app_and_never_allows_orders()
    {
        var workspace = await MakeWorkspaceAsync();
        using var made = await CallbackAsync(await StartAsync(workspace), FakeSchwab.GoodCode);

        var schwab = (await _admin.GetAsync<JsonElement>("/connectors")).EnumerateArray().Single(c => c.GetProperty("id").GetString() == "schwab");
        using var scope = _factory.Services.CreateScope();
        var config = await scope.ServiceProvider.GetRequiredService<ConnectionMaker>().ConfigOfAsync(await ConnectionAsync("Brokerage"), CancellationToken.None);

        schwab.GetProperty("fields").EnumerateArray().Select(f => f.GetProperty("name").GetString()).ShouldNotContain("allowOrders");
        config.Client!.ClientId.ShouldBe("app-key");
        config.Values.Keys.ShouldNotContain("allowOrders");
        config.Tokens!.RefreshToken.ShouldBe("refresh");
    }

    private static string StateOf(string url) => QueryHelpers.ParseQuery(new Uri(url).Query)["state"].ToString();

    private static string ErrorOf(HttpResponseMessage response) =>
        QueryHelpers.ParseQuery(new Uri(response.Headers.Location!.OriginalString).Query)["error"].ToString();

    private Task<HttpResponseMessage> CallbackAsync(string signInPage, string code) =>
        _browser.GetAsync(new Uri($"/oauth/callback?code={code}&state={StateOf(signInPage)}", UriKind.Relative));

    private async Task<string> StartAsync(int workspace) =>
        (await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/sign-ins", new { name = "Brokerage", connector = "schwab", settings = Settings }))
        .GetProperty("url").GetString()!;

    private async Task EndSignInAsync(int connection)
    {
        await using var db = _factory.NewContext();
        await db.Connections.Where(c => c.Id == connection).ExecuteUpdateAsync(set => set.SetProperty(c => c.NeedsSignIn, true));
    }

    private async Task<Storage.Connection> ConnectionAsync(string name)
    {
        await using var db = _factory.NewContext();
        return await db.Connections.SingleAsync(c => c.Name == name);
    }

    private async Task<string> OpenAsync(string appKey, object request)
    {
        using var app = Bearer(appKey);
        using var opened = await app.PostAsJsonAsync("/connect-sessions", request);
        opened.StatusCode.ShouldBe(HttpStatusCode.Created, await opened.Content.ReadAsStringAsync());
        return (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!.Split('#')[1];
    }

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // A workspace with its own Schwab app, as its members set it on the workspace page.
    private async Task<int> MakeWorkspaceAsync()
    {
        var workspace = (await _admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name = "Acme" })).GetProperty("id").GetInt32();
        (await _admin.SendAsync(HttpMethod.Put, $"/workspaces/{workspace}/sign-in-apps/schwab", new { clientId = "app-key", clientSecret = "app-secret" }))
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
