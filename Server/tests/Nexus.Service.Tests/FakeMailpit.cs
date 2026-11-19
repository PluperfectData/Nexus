using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Nexus.Service.Tests;

/// <summary>Mailpit's Send API, in small: it records each message, and refuses every call while <see cref="Down"/>.</summary>
internal sealed class FakeMailpit : HttpMessageHandler
{
    public const string Address = "http://mailpit.test";

    public ConcurrentQueue<JsonElement> Sent { get; } = new();

    /// <summary>Whether Mailpit is stopped, so a call fails as a refused connection does.</summary>
    public bool Down { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            throw new HttpRequestException("No connection could be made because the target machine actively refused it.");
        }

        var route = $"{request.Method} {request.RequestUri}";
        if (route == $"GET {Address}/api/v1/info")
        {
            return Json(HttpStatusCode.OK, """{"Version":"v1.33.1"}""");
        }

        if (route == $"POST {Address}/api/v1/send")
        {
            Sent.Enqueue(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
            return Json(HttpStatusCode.OK, """{"ID":"message-1"}""");
        }

        throw new InvalidOperationException($"No route for {route}.");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
