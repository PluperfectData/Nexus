using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Web;

namespace Nexus.Schwab.Tests;

internal sealed record SentRequest(HttpMethod Method, Uri Uri, string? Authorization, string? Body)
{
    public string Path => Uri.AbsolutePath;

    public NameValueCollection Query => HttpUtility.ParseQueryString(Uri.Query);
}

// Answers each request from the last reply registered for its method and path, and records every request.
internal sealed class FakeSchwab : HttpMessageHandler
{
    public const string AccountNumbers = "/trader/v1/accounts/accountNumbers";
    public const string Token = "/v1/oauth/token";

    private readonly List<(HttpMethod Method, string Path, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = [];

    public List<SentRequest> Requests { get; } = [];

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public FakeSchwab On(HttpMethod method, string path, Func<HttpRequestMessage, HttpResponseMessage> reply)
    {
        _routes.Add((method, path, reply));
        return this;
    }

    public FakeSchwab OnJson(HttpMethod method, string path, string json) => On(method, path, _ => Json(json));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new SentRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));

        var route = _routes.LastOrDefault(r => r.Method == request.Method && r.Path == request.RequestUri!.AbsolutePath);
        return route.Reply?.Invoke(request)
            ?? throw new InvalidOperationException($"No reply for {request.Method} {request.RequestUri}.");
    }
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

internal static class Schwab
{
    public static readonly DateTimeOffset Now = new(2026, 11, 12, 15, 0, 0, TimeSpan.Zero);

    public const string OneAccount = """[{"accountNumber":"11111111","hashValue":"HASH1"}]""";

    public const string TwoAccounts =
        """[{"accountNumber":"11111111","hashValue":"HASH1"},{"accountNumber":"22222222","hashValue":"HASH2"}]""";

    public static ConnectorConfig Config(
        string startDate = "2025-01-01",
        bool allowOrders = false,
        DateTimeOffset? expiresAt = null,
        bool withClient = true,
        bool withTokens = true) => new(
        new Dictionary<string, string?>
        {
            [SchwabSettings.StartDate] = startDate,
            [SchwabSettings.AllowOrders] = allowOrders ? "true" : null,
        },
        Tokens: withTokens ? new OAuthTokens("access", "refresh", expiresAt ?? Now.AddMinutes(30)) : null,
        Client: withClient ? new OAuthClient("app-key", "app-secret") : null);
}
