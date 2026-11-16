using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed partial class SetupTests
{
    private ServiceFactory _factory = null!;

    [SetUp]
    public void SetUp() => _factory = new ServiceFactory();

    [TearDown]
    public void TearDown() => _factory.Dispose();

    [Test]
    public async Task The_code_from_the_log_makes_the_first_admin_and_signs_them_in()
    {
        using var browser = _factory.NewBrowser();
        (await browser.GetAsync<JsonElement>("/setup")).GetProperty("needed").GetBoolean().ShouldBeTrue();
        var code = CodeFromLog();

        using var wrong = await browser.SendAsync(HttpMethod.Post, "/setup", new { code = "WRONG-CODE-HERE", email = "founder@acme.test", password = Browser.Password });
        wrong.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        using var claimed = await browser.SendAsync(HttpMethod.Post, "/setup", new { code = code.ToLowerInvariant(), email = "Founder@Acme.test", password = Browser.Password });

        claimed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await browser.GetAsync<JsonElement>("/auth/me");
        me.GetProperty("email").GetString().ShouldBe("founder@acme.test");
        me.GetProperty("isAdmin").GetBoolean().ShouldBeTrue();
        (await browser.GetAsync<JsonElement>("/setup")).GetProperty("needed").GetBoolean().ShouldBeFalse();
        using var again = await _factory.NewBrowser().SendAsync(HttpMethod.Post, "/setup", new { code, email = "other@acme.test", password = Browser.Password });
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Test]
    public async Task A_short_password_is_refused_without_spending_the_code()
    {
        using var browser = _factory.NewBrowser();
        _ = _factory.Services;
        var code = CodeFromLog();

        using var weak = await browser.SendAsync(HttpMethod.Post, "/setup", new { code, email = "founder@acme.test", password = "short" });
        (await Browser.DetailAsync(weak)).ShouldBe("A password needs at least 12 characters.");

        (await browser.SendAsync(HttpMethod.Post, "/setup", new { code, email = "founder@acme.test", password = Browser.Password })).StatusCode
            .ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task An_instance_with_users_logs_no_code_and_needs_no_setup()
    {
        await _factory.AddUserAsync("admin@acme.test", Browser.Password, admin: true);
        using var browser = _factory.NewBrowser();

        (await browser.GetAsync<JsonElement>("/setup")).GetProperty("needed").GetBoolean().ShouldBeFalse();

        _factory.Logs.Lines.ShouldNotContain(line => line.Contains("setup code", StringComparison.Ordinal));
    }

    private string CodeFromLog()
    {
        var line = _factory.Logs.Lines.Single(l => l.Contains("setup code", StringComparison.Ordinal));
        line.ShouldContain($"{ServiceFactory.ClientOrigin}/setup");
        return CodePattern().Match(line).Value;
    }

    [GeneratedRegex("[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}$")]
    private static partial Regex CodePattern();
}
