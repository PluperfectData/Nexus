using System.Net;
using System.Text;
using System.Text.Json;

namespace Nexus.Push.Tests;

internal sealed record SentRequest(HttpMethod Method, string Path, JsonElement? Body)
{
    public override string ToString() => $"{Method} {Path}";
}

// Keeps Scry's push API contract: one key, tables made by PUT, a replace that needs an existing table, and every
// request recorded. A failure registered for a method and path answers in place of the contract.
internal sealed class FakeIngest : HttpMessageHandler
{
    public const string Key = "scry_good";

    private const string Sources = "/ingest/sources/";

    private readonly Dictionary<(HttpMethod, string), (HttpStatusCode Status, string Detail)> _failures = [];

    public HashSet<string> Tables { get; } = [];

    public List<SentRequest> Requests { get; } = [];

    public FakeIngest Fail(HttpMethod method, string path, HttpStatusCode status, string detail)
    {
        _failures[(method, path)] = (status, detail);
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        JsonElement? body = request.Content is null
            ? null
            : JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken)).RootElement.Clone();
        Requests.Add(new SentRequest(request.Method, path, body));

        if (_failures.TryGetValue((request.Method, path), out var failure))
        {
            return Problem(failure.Status, failure.Detail);
        }

        if (request.Headers.Authorization?.ToString() != $"Bearer {Key}")
        {
            return Problem(HttpStatusCode.Unauthorized, "Send a push API key as 'Authorization: Bearer <key>'.");
        }

        if (request.Method == HttpMethod.Get && path == "/ingest")
        {
            return Json("""{"name":"Nexus","prefix":"scry_good"}""");
        }

        var parts = path[Sources.Length..].Split('/');
        var table = Uri.UnescapeDataString(parts[0]);
        var action = parts.Length > 1 ? parts[1] : null;
        if (request.Method == HttpMethod.Put && action is null)
        {
            Tables.Add(table);
            return Json("{}");
        }

        return action switch
        {
            "replace" when !Tables.Contains(table) => Problem(HttpStatusCode.NotFound, $"There is no source named '{table}'."),
            "replace" or "commit" => Json("{}"),
            "batches" => Json("""{"rowCount":0,"skipped":false}"""),
            _ => throw new InvalidOperationException($"No route for {request.Method} {path}."),
        };
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Problem(HttpStatusCode status, string detail) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { detail }), Encoding.UTF8, "application/problem+json"),
        };
}
