using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Auth;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class AuthTests
{
    private const string Rowan = "rowan@acme.test";

    private ServiceFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new ServiceFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task Signs_in_with_a_password_and_says_who_is_signed_in()
    {
        await _factory.AddUserAsync(Rowan, Browser.Password, admin: true);
        using var browser = _factory.NewBrowser();
        (await browser.GetAsync<JsonElement>("/auth/me")).GetProperty("signedIn").GetBoolean().ShouldBeFalse();

        using var signedIn = await browser.SignInAsync(" Rowan@Acme.test ", Browser.Password);

        signedIn.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await browser.GetAsync<JsonElement>("/auth/me");
        me.GetProperty("email").GetString().ShouldBe(Rowan);
        me.GetProperty("isAdmin").GetBoolean().ShouldBeTrue();
        me.GetProperty("hasPassword").GetBoolean().ShouldBeTrue();
    }

    [Test]
    public async Task Answers_a_wrong_password_and_an_unknown_address_alike()
    {
        await _factory.AddUserAsync(Rowan, Browser.Password);
        using var browser = _factory.NewBrowser();

        using var wrong = await browser.SignInAsync(Rowan, "not the password");
        using var unknown = await browser.SignInAsync("nobody@acme.test", Browser.Password);

        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknown.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Browser.DetailAsync(wrong)).ShouldBe(await Browser.DetailAsync(unknown));
    }

    [Test]
    public async Task Locks_an_address_out_after_five_wrong_passwords()
    {
        await _factory.AddUserAsync(Rowan, Browser.Password);
        using var browser = _factory.NewBrowser();
        for (var i = 0; i < SignInThrottle.MaxFailures; i++)
        {
            (await browser.SignInAsync(Rowan, "wrong")).Dispose();
        }

        using var locked = await browser.SignInAsync(Rowan, Browser.Password);

        locked.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task A_mailed_link_signs_in_once()
    {
        using var factory = ServiceFactory.WithPostmark();
        await factory.AddUserAsync(Rowan);
        using var browser = factory.NewBrowser();

        (await browser.SendAsync(HttpMethod.Post, "/auth/link", new { email = Rowan })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var mail = Browser.PostmarkTo(factory.Postmark, Rowan).Single();
        mail.GetProperty("Subject").GetString().ShouldBe("Your Nexus sign-in link");
        (mail.GetProperty("TextBody").GetString() ?? string.Empty).ShouldContain($"{ServiceFactory.ClientOrigin}/sign-in/link#");
        var token = Browser.LinkToken(mail);

        (await browser.SendAsync(HttpMethod.Post, "/auth/link/redeem", new { token })).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.GetAsync<JsonElement>("/auth/me")).GetProperty("hasPassword").GetBoolean().ShouldBeFalse();

        using var again = await factory.NewBrowser().SendAsync(HttpMethod.Post, "/auth/link/redeem", new { token });
        again.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(again)).ShouldBe("This link has expired or was already used. Ask for a new one.");
    }

    [Test]
    public async Task Mails_no_one_for_an_unknown_address_and_answers_the_same()
    {
        using var factory = ServiceFactory.WithPostmark();
        using var browser = factory.NewBrowser();

        using var answer = await browser.SendAsync(HttpMethod.Post, "/auth/link", new { email = "nobody@acme.test" });

        answer.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Browser.PostmarkTo(factory.Postmark, "nobody@acme.test").ShouldBeEmpty();
    }

    [Test]
    public async Task Refreshing_rotates_the_token_and_a_replayed_one_ends_every_session()
    {
        using var browser = await _factory.SignedInAsync(Rowan);
        var first = RefreshCookie(browser);

        (await browser.SendAsync(HttpMethod.Post, "/auth/refresh")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var second = RefreshCookie(browser);
        second.ShouldNotBe(first);

        browser.Cookies.Add(new Cookie("rt", first, "/auth", "localhost"));
        (await browser.SendAsync(HttpMethod.Post, "/auth/refresh")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        browser.Cookies.Add(new Cookie("rt", second, "/auth", "localhost"));
        (await browser.SendAsync(HttpMethod.Post, "/auth/refresh")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Signing_out_ends_the_session()
    {
        using var browser = await _factory.SignedInAsync(Rowan);

        (await browser.SendAsync(HttpMethod.Post, "/auth/sign-out")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await browser.GetAsync<JsonElement>("/auth/me")).GetProperty("signedIn").GetBoolean().ShouldBeFalse();
        (await browser.GetAsync("/workspaces")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Refuses_an_unsafe_request_without_a_CSRF_token_or_from_another_origin()
    {
        using var browser = await _factory.SignedInAsync(Rowan, admin: true);

        (await browser.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Acme" }, csrf: false)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await browser.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Acme" }, origin: "https://evil.test")).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await browser.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Acme" })).StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Test]
    public async Task Sets_a_password_of_at_least_twelve_characters()
    {
        await _factory.AddUserAsync(Rowan);
        using var browser = _factory.NewBrowser();
        var token = _factory.Services.GetRequiredService<SignInLinks>().Issue(await FindAsync(Rowan), SignInLinks.SignInLifetime);
        (await browser.SendAsync(HttpMethod.Post, "/auth/link/redeem", new { token })).EnsureSuccessStatusCode();

        using var tooShort = await browser.SendAsync(HttpMethod.Put, "/auth/password", new { password = "too short" });
        tooShort.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await browser.SendAsync(HttpMethod.Put, "/auth/password", new { password = Browser.Password })).StatusCode.ShouldBe(HttpStatusCode.OK);

        using var other = _factory.NewBrowser();
        (await other.SignInAsync(Rowan, Browser.Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task The_add_admin_command_makes_an_admin_with_a_link_that_signs_them_in()
    {
        await _factory.AddUserAsync(Rowan);
        string link;
        using (var scope = _factory.Services.CreateScope())
        {
            link = (await scope.ServiceProvider.GetRequiredService<Admins>().AddAsync(Rowan, CancellationToken.None))!;
        }

        link.ShouldStartWith($"{ServiceFactory.ClientOrigin}/sign-in/link#");
        using var browser = _factory.NewBrowser();
        (await browser.SendAsync(HttpMethod.Post, "/auth/link/redeem", new { token = link.Split('#')[1] })).EnsureSuccessStatusCode();
        (await browser.GetAsync<JsonElement>("/auth/me")).GetProperty("isAdmin").GetBoolean().ShouldBeTrue();
    }

    private async Task<Nexus.Service.Storage.User> FindAsync(string email)
    {
        await using var db = _factory.NewContext();
        return await db.Users.SingleAsync(u => u.Email == email);
    }

    private static string RefreshCookie(Browser browser) =>
        browser.Cookies.GetCookies(new Uri("http://localhost/auth/refresh"))["rt"]?.Value ?? throw new InvalidOperationException("No refresh cookie.");
}
