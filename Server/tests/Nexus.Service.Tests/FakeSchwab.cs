using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Nexus.Service.Tests;

// Schwab's token endpoint, in small: it trades the code GoodCode for tokens, refuses any other, and records each form.
internal sealed class FakeSchwab : HttpMessageHandler
{
    public const string GoodCode = "good-code";

    private const string Token = "https://api.schwabapi.com/v1/oauth/token";

    public ConcurrentQueue<string> Exchanged { get; } = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Post || request.RequestUri?.ToString() != Token)
        {
            throw new InvalidOperationException($"No route for {request.Method} {request.RequestUri}.");
        }

        var form = await request.Content!.ReadAsStringAsync(cancellationToken);
        Exchanged.Enqueue(form);
        return form.Contains($"code={GoodCode}", StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, """{"access_token":"access","refresh_token":"refresh","expires_in":1800}""")
            : Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Bad code"}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
