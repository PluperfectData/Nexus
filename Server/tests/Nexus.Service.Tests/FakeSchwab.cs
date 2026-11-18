using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Nexus.Service.Tests;

// Schwab's token endpoint and account list, in small: it trades the code GoodCode for tokens, refuses any other, and
// records each form. The sign-in reaches the account numbers in Accounts.
internal sealed class FakeSchwab : HttpMessageHandler
{
    public const string GoodCode = "good-code";

    private const string Token = "https://api.schwabapi.com/v1/oauth/token";

    private const string AccountNumbers = "https://api.schwabapi.com/trader/v1/accounts/accountNumbers";

    public ConcurrentQueue<string> Exchanged { get; } = new();

    public List<string> Accounts { get; } = ["12341234"];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get && request.RequestUri?.ToString() == AccountNumbers)
        {
            var items = Accounts.Select(number => $$"""{"accountNumber":"{{number}}","hashValue":"H{{number}}"}""");
            return Json(HttpStatusCode.OK, $"[{string.Join(',', items)}]");
        }

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
