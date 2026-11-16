using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class PairingTests
{
    private const string Verifier = "the-verifier-only-scry-holds";

    private const string Callback = "http://scry.test/nexus/callback";

    private const string ConnectReturn = "http://localhost:6160/sources";

    private ServiceFactory _factory = null!;
    private Browser _admin = null!;
    private HttpClient _scry = null!;

    [SetUp]
    public async Task SetUp()
    {
        _factory = new ServiceFactory();
        _admin = await _factory.SignedInAsync("admin@acme.test", admin: true);
        _scry = _factory.CreateClient();
    }

    [TearDown]
    public void TearDown()
    {
        _scry.Dispose();
        _admin.Dispose();
        _factory.Dispose();
    }

    [Test]
    public async Task An_approved_pairing_claimed_with_its_verifier_makes_the_app_its_key_and_its_destination()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var opened = await OpenAsync();
        opened.GetProperty("approveUrl").GetString().ShouldBe($"{ServiceFactory.ClientOrigin}/pair?id={Id(opened)}");

        var shown = await _admin.GetAsync<JsonElement>($"/pairings/{Id(opened)}");
        shown.GetProperty("appName").GetString().ShouldBe("Scry");
        shown.GetProperty("ingestUrl").GetString().ShouldBe("http://scry.test/ingest");
        (await ClaimAsync(opened, FakePush.Key)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var approved = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/pairings/{Id(opened)}/approve", new { workspaceId = workspace });
        approved.GetProperty("returnUrl").GetString().ShouldBe($"{Callback}?pairing={Id(opened)}");

        using var claimed = await ClaimAsync(opened, FakePush.Key);
        claimed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = await claimed.Content.ReadFromJsonAsync<JsonElement>();
        result.GetProperty("workspaceName").GetString().ShouldBe("Acme");
        var key = result.GetProperty("appKey").GetString()!;
        key.ShouldStartWith("nexus_");

        using var app = Bearer(key);
        var itself = await app.GetFromJsonAsync<JsonElement>("/app");
        itself.GetProperty("destinations").EnumerateArray().Single().GetProperty("id").GetInt32().ShouldBe(result.GetProperty("destinationId").GetInt32());
        itself.GetProperty("returnUrls").EnumerateArray().Single().GetString().ShouldBe(ConnectReturn);
        var listed = (await _admin.GetAsync<JsonElement>($"/workspaces/{workspace}/apps")).EnumerateArray().Single();
        listed.GetProperty("keyCreatedAt").ValueKind.ShouldBe(JsonValueKind.String);
        listed.GetProperty("destinations")[0].GetProperty("keyName").GetString().ShouldBe("Nexus");
        listed.GetProperty("destinations")[0].GetProperty("keyPrefix").GetString().ShouldBe("scry_good");
        (await ClaimAsync(opened, FakePush.Key)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Only_the_verifier_claims_and_only_a_working_push_key_makes_a_destination()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var opened = await OpenAsync();
        (await _admin.SendAsync(HttpMethod.Post, $"/pairings/{Id(opened)}/approve", new { workspaceId = workspace })).EnsureSuccessStatusCode();

        (await ClaimAsync(opened, FakePush.Key, verifier: "a-guess")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var refused = await ClaimAsync(opened, "scry_wrong");
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(refused)).ShouldBe("The push API refused the key. Send a push API key.");

        (await ClaimAsync(opened, FakePush.Key)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task Only_an_admin_sees_or_approves_a_pairing()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var opened = await OpenAsync();
        using var member = await _factory.SignedInAsync("member@acme.test");

        (await member.GetAsync($"/pairings/{Id(opened)}")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await member.SendAsync(HttpMethod.Post, $"/pairings/{Id(opened)}/approve", new { workspaceId = workspace })).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task A_declined_or_expired_pairing_cannot_be_claimed()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var declined = await OpenAsync();
        var ended = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/pairings/{Id(declined)}/decline");
        ended.GetProperty("returnUrl").GetString().ShouldBe($"{Callback}?error=declined");
        (await ClaimAsync(declined, FakePush.Key)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var expired = await OpenAsync();
        (await _admin.SendAsync(HttpMethod.Post, $"/pairings/{Id(expired)}/approve", new { workspaceId = workspace })).EnsureSuccessStatusCode();
        await using (var db = _factory.NewContext())
        {
            await db.Pairings.ExecuteUpdateAsync(set => set.SetProperty(p => p.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        }

        (await ClaimAsync(expired, FakePush.Key)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Refuses_a_pairing_without_web_addresses_or_a_verifier_hash()
    {
        using var noAddress = await _scry.PostAsJsonAsync("/pairings", new { appName = "Scry", ingestUrl = "scry", callbackUrl = Callback, connectReturnUrl = ConnectReturn, verifierHash = HashOf(Verifier) });
        noAddress.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var noHash = await _scry.PostAsJsonAsync("/pairings", new { appName = "Scry", ingestUrl = "http://scry.test/ingest", callbackUrl = Callback, connectReturnUrl = ConnectReturn, verifierHash = "short" });
        noHash.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task An_app_revokes_its_own_key()
    {
        var workspace = await MakeWorkspaceAsync("Acme");
        var opened = await OpenAsync();
        (await _admin.SendAsync(HttpMethod.Post, $"/pairings/{Id(opened)}/approve", new { workspaceId = workspace })).EnsureSuccessStatusCode();
        using var claimed = await ClaimAsync(opened, FakePush.Key);
        using var app = Bearer((await claimed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("appKey").GetString()!);

        (await app.DeleteAsync("/app/key")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await app.GetAsync("/app")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var listed = (await _admin.GetAsync<JsonElement>($"/workspaces/{workspace}/apps")).EnumerateArray().Single();
        listed.GetProperty("keyPrefix").ValueKind.ShouldBe(JsonValueKind.Null);
        listed.GetProperty("keyCreatedAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    private static string Id(JsonElement opened) => opened.GetProperty("id").GetString()!;

    private static string HashOf(string verifier) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> MakeWorkspaceAsync(string name) =>
        (await _admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name })).GetProperty("id").GetInt32();

    // Opens a pairing as Scry's server would: anonymous, with no cookie and no CSRF token.
    private async Task<JsonElement> OpenAsync()
    {
        using var opened = await _scry.PostAsJsonAsync(
            "/pairings",
            new { appName = "Scry", ingestUrl = "http://scry.test/ingest", callbackUrl = Callback, connectReturnUrl = ConnectReturn, verifierHash = HashOf(Verifier) });
        opened.StatusCode.ShouldBe(HttpStatusCode.Created);
        return await opened.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task<HttpResponseMessage> ClaimAsync(JsonElement opened, string pushKey, string verifier = Verifier) =>
        _scry.PostAsJsonAsync($"/pairings/{Id(opened)}/claim", new { verifier, pushKey });
}
