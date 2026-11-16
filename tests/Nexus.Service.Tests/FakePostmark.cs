using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Nexus.Service.Tests;

/// <summary>Postmark's send endpoint, in small: it takes <see cref="Token"/>, refuses any other, and records each message.</summary>
internal sealed class FakePostmark : HttpMessageHandler
{
    public const string Token = "postmark-token";

    private const string Endpoint = "https://api.postmarkapp.com/email";

    public ConcurrentQueue<JsonElement> Sent { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.ToString() != Endpoint)
        {
            throw new InvalidOperationException($"No route for {request.Method} {request.RequestUri}.");
        }

        if (!request.Headers.TryGetValues("X-Postmark-Server-Token", out var tokens) || tokens.Single() != Token)
        {
            return Json(HttpStatusCode.UnprocessableEntity, """{"ErrorCode":10,"Message":"The Server Token you provided was invalid."}""");
        }

        Sent.Enqueue(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
        return Json(HttpStatusCode.OK, """{"ErrorCode":0,"Message":"OK","MessageID":"message-1"}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
