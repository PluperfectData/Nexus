using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Nexus.Service.Tests;

// Scry's push API in small: one key, tables of rows held as JSON text, replacements swapped in on commit, and merges
// that match on a row's first value. The first batches can be made to fail.
internal sealed class FakePush : HttpMessageHandler
{
    public const string Key = "scry_good";

    private const string Sources = "/ingest/sources/";

    private readonly ConcurrentDictionary<string, List<string>> _staged = new();

    public ConcurrentDictionary<string, List<string>> Tables { get; } = new();

    public ConcurrentQueue<string> Requests { get; } = new();

    /// <summary>The origin each table was last defined or replaced with.</summary>
    public ConcurrentDictionary<string, string?> Origins { get; } = new();

    public int FailingBatches { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        Requests.Enqueue($"{request.Method} {path}");
        if (request.Headers.Authorization?.ToString() != $"Bearer {Key}")
        {
            return Answer(HttpStatusCode.Unauthorized, """{"detail":"Send a push API key."}""");
        }

        if (path == "/ingest")
        {
            return Answer(HttpStatusCode.OK, """{"name":"Nexus","prefix":"scry_good"}""");
        }

        var parts = path[Sources.Length..].Split('/');
        var table = Uri.UnescapeDataString(parts[0]);
        switch (request.Method.Method, parts.Length > 1 ? parts[1] : null)
        {
            case ("PUT", null):
                Tables.TryAdd(table, []);
                await KeepOriginAsync(table, request, cancellationToken);
                return Answer(HttpStatusCode.OK, "{}");

            case ("POST", "replace"):
                if (!Tables.ContainsKey(table))
                {
                    return Answer(HttpStatusCode.NotFound, """{"detail":"No such source."}""");
                }

                await KeepOriginAsync(table, request, cancellationToken);
                _staged[table] = [];
                return Answer(HttpStatusCode.OK, "{}");

            case ("POST", "batches"):
                if (FailingBatches > 0)
                {
                    FailingBatches--;
                    return Answer(HttpStatusCode.ServiceUnavailable, """{"detail":"Down."}""");
                }

                using (var batch = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)))
                {
                    var rows = _staged.TryGetValue(table, out var staged) ? staged : Tables[table];
                    var merge = batch.RootElement.GetProperty("mode").GetString() == "merge";
                    foreach (var row in batch.RootElement.GetProperty("rows").EnumerateArray())
                    {
                        if (merge)
                        {
                            rows.RemoveAll(kept => JsonSerializer.Deserialize<JsonElement[]>(kept)![0].GetRawText() == row[0].GetRawText());
                        }

                        rows.Add(row.GetRawText());
                    }
                }

                return Answer(HttpStatusCode.OK, """{"rowCount":0,"skipped":false}""");

            case ("POST", "commit"):
                if (_staged.TryRemove(table, out var replacement))
                {
                    Tables[table] = replacement;
                }

                return Answer(HttpStatusCode.OK, "{}");

            default:
                throw new InvalidOperationException($"No route for {request.Method} {path}.");
        }
    }

    private async Task KeepOriginAsync(string table, HttpRequestMessage request, CancellationToken ct)
    {
        using var schema = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        Origins[table] = schema.RootElement.TryGetProperty("origin", out var origin) ? origin.GetString() : null;
    }

    private static HttpResponseMessage Answer(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
