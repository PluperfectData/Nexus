using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class FileConnectionTests
{
    private const string ReturnUrl = "http://localhost:6160/sources";

    private const string Orders = "id,item\n1,pen\n2,ink\n";

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
    public async Task A_member_adds_a_CSV_connection_from_an_upload_and_syncs_its_rows()
    {
        var workspace = await MakeWorkspaceAsync();
        var (_, _, destination) = await MakeAppAsync(workspace);

        using var form = Form(Orders);
        using var added = await _admin.SendFormAsync(HttpMethod.Post, $"/workspaces/{workspace}/connections", form);

        added.StatusCode.ShouldBe(HttpStatusCode.Created);
        var connection = await added.Content.ReadFromJsonAsync<JsonElement>();
        connection.GetProperty("fileName").GetString().ShouldBe("orders.csv");
        connection.GetProperty("settings").GetProperty("stream").GetString().ShouldBe("orders");
        _factory.StoredFileCount.ShouldBe(1);
        var id = connection.GetProperty("id").GetInt32();
        var streams = await _admin.GetAsync<JsonElement>($"/connections/{id}/streams");
        streams.EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("orders");

        var sync = await AddSyncAsync(id, destination);

        (await WaitForRunAsync(sync)).GetProperty("rows").GetInt64().ShouldBe(2);
        _factory.Push.Tables["orders"].Count.ShouldBe(2);
    }

    [Test]
    public async Task A_file_the_connector_cannot_read_is_refused_and_its_copy_deleted()
    {
        var workspace = await MakeWorkspaceAsync();

        using var form = Form("id,id\n1,2\n");
        using var refused = await _admin.SendFormAsync(HttpMethod.Post, $"/workspaces/{workspace}/connections", form);
        using var none = Form(Orders, withFile: false);
        using var missing = await _admin.SendFormAsync(HttpMethod.Post, $"/workspaces/{workspace}/connections", none);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(refused)).ShouldBe("Column 'id' appears twice in the header.");
        missing.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(missing)).ShouldBe("Choose a file to upload.");
        _factory.StoredFileCount.ShouldBe(0);
    }

    [Test]
    public async Task Replacing_the_file_runs_its_syncs_again_and_removing_the_connection_deletes_it()
    {
        var workspace = await MakeWorkspaceAsync();
        var (_, _, destination) = await MakeAppAsync(workspace);
        using var form = Form(Orders);
        using var added = await _admin.SendFormAsync(HttpMethod.Post, $"/workspaces/{workspace}/connections", form);
        var id = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        var sync = await AddSyncAsync(id, destination);
        await WaitForRunAsync(sync);

        using var copy = new MultipartFormDataContent { { Content("id,item\n3,nib\n"), "file", "orders-2.csv" } };
        using var replaced = await _admin.SendFormAsync(HttpMethod.Put, $"/connections/{id}/file", copy);

        replaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await replaced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("fileName").GetString().ShouldBe("orders-2.csv");
        (await WaitForRunAsync(sync, runs: 2)).GetProperty("rows").GetInt64().ShouldBe(1);
        _factory.Push.Tables["orders"].ShouldBe(["""[3,"nib"]"""]);
        _factory.StoredFileCount.ShouldBe(1);

        (await _admin.SendAsync(HttpMethod.Delete, $"/connections/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        _factory.StoredFileCount.ShouldBe(0);
    }

    [Test]
    public async Task A_connect_page_makes_its_connection_from_an_upload()
    {
        var workspace = await MakeWorkspaceAsync();
        var (_, key, destination) = await MakeAppAsync(workspace);
        using var app = Bearer(key);
        using var opened = await app.PostAsJsonAsync("/connect-sessions", new { connector = "csv", destinationId = destination, returnUrl = ReturnUrl });
        var token = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!.Split('#')[1];
        using var page = Bearer(token);

        using var form = Form(Orders, connector: null);
        using var connected = await page.PostAsync(new Uri("/connect/connection", UriKind.Relative), form);

        connected.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await connected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("connection").GetProperty("fileName").GetString().ShouldBe("orders.csv");
        var streams = await page.GetFromJsonAsync<JsonElement>("/connect/streams");
        streams.EnumerateArray().Single().GetProperty("name").GetString().ShouldBe("orders");
    }

    private static ByteArrayContent Content(string csv) => new(Encoding.UTF8.GetBytes(csv));

    private static MultipartFormDataContent Form(string csv, string? connector = "csv", bool withFile = true)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent("Orders"), "name" },
            { new StringContent("""{"stream":"orders"}"""), "settings" },
        };
        if (connector is not null)
        {
            form.Add(new StringContent(connector), "connector");
        }

        if (withFile)
        {
            form.Add(Content(csv), "file", "orders.csv");
        }

        return form;
    }

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> MakeWorkspaceAsync() =>
        (await _admin.SendAsync<JsonElement>(HttpMethod.Post, "/workspaces", new { name = "Acme" })).GetProperty("id").GetInt32();

    private async Task<(int Id, string Key, int Destination)> MakeAppAsync(int workspace)
    {
        var made = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/workspaces/{workspace}/apps", new { name = "Scry", returnUrls = Returns });
        var id = made.GetProperty("app").GetProperty("id").GetInt32();
        var destination = await _admin.SendAsync<JsonElement>(HttpMethod.Post, $"/apps/{id}/destinations", new { url = "http://scry.test/ingest", key = FakePush.Key });
        return (id, made.GetProperty("key").GetString()!, destination.GetProperty("id").GetInt32());
    }

    private async Task<int> AddSyncAsync(int connection, int destination)
    {
        var syncs = await _admin.SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/connections/{connection}/syncs",
            new { destinationId = destination, streams = new[] { new { stream = "orders", table = "orders", mode = "FullRefresh" } } });
        return syncs.EnumerateArray().Single().GetProperty("id").GetInt32();
    }

    // Waits until the sync has ended this many runs, and answers the newest.
    private async Task<JsonElement> WaitForRunAsync(int syncId, int runs = 1)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (true)
        {
            var list = await _admin.GetAsync<JsonElement>($"/syncs/{syncId}/runs");
            var ended = list.EnumerateArray().Where(r => r.GetProperty("endedAt").ValueKind != JsonValueKind.Null).ToList();
            if (ended.Count >= runs && ended[0].GetProperty("status").GetString() != "Running")
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
