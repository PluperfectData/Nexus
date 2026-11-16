using System.Net;
using System.Text;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Schwab.Tests;

[TestFixture]
public sealed class SchwabSignInTests
{
    private const string Callback = "https://127.0.0.1:6162/schwab/callback";

    private static readonly OAuthClient App = new("app key", "app-secret");

    [Test]
    public void The_sign_in_page_carries_the_app_key_callback_and_state()
    {
        var url = SchwabSignIn.AuthorizeUrl(App, Callback, "abc123");

        url.GetLeftPart(UriPartial.Path).ShouldBe("https://api.schwabapi.com/v1/oauth/authorize");
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        (query["response_type"], query["client_id"], query["redirect_uri"], query["state"])
            .ShouldBe(("code", "app key", Callback, "abc123"));
    }

    [Test]
    public async Task A_code_is_exchanged_once_for_tokens()
    {
        var fake = new FakeSchwab().OnJson(
            HttpMethod.Post,
            FakeSchwab.Token,
            """{ "expires_in": 1800, "refresh_token": "refresh", "access_token": "access" }""");

        var tokens = await SchwabSignIn.ExchangeAsync(new HttpClient(fake), App, "the code", Callback, new FixedTime(Schwab.Now));

        tokens.ShouldBe(new OAuthTokens("access", "refresh", Schwab.Now.AddMinutes(30)));
        var sent = fake.Requests.ShouldHaveSingleItem();
        sent.Authorization.ShouldBe("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("app%20key:app-secret")));
        System.Web.HttpUtility.ParseQueryString(sent.Body!)["code"].ShouldBe("the code");
        System.Web.HttpUtility.ParseQueryString(sent.Body!)["redirect_uri"].ShouldBe(Callback);
    }

    [TestCase("invalid_grant", HttpStatusCode.BadRequest, ConnectorErrorKind.AuthExpired)]
    [TestCase("invalid_client", HttpStatusCode.Unauthorized, ConnectorErrorKind.ConfigInvalid)]
    public async Task A_refused_code_says_why(string error, HttpStatusCode status, ConnectorErrorKind kind)
    {
        var fake = new FakeSchwab().On(
            HttpMethod.Post,
            FakeSchwab.Token,
            _ => FakeSchwab.Json($$"""{"error":"{{error}}","error_description":"Bad code"}""", status));

        var ex = await Should.ThrowAsync<ConnectorException>(
            () => SchwabSignIn.ExchangeAsync(new HttpClient(fake), App, "old", Callback));

        ex.Kind.ShouldBe(kind);
        ex.Message.ShouldBe($"Schwab refused the sign-in ({(int)status}): Bad code.");
    }

    [Test]
    public async Task A_reply_without_tokens_is_a_source_error()
    {
        var fake = new FakeSchwab().OnJson(HttpMethod.Post, FakeSchwab.Token, """{ "expires_in": 1800 }""");

        var ex = await Should.ThrowAsync<ConnectorException>(
            () => SchwabSignIn.ExchangeAsync(new HttpClient(fake), App, "code", Callback));

        ex.Kind.ShouldBe(ConnectorErrorKind.SourceError);
    }
}
