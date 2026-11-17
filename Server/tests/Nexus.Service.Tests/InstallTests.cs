using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nexus.Service.Auth;
using NUnit.Framework;
using Pluperfect.Jwt;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed partial class InstallTests
{
    private static readonly object Database = new { host = "db.example.test", port = 3307, name = "nexus_live", user = "nexus", password = "database-password" };

    [Test]
    public async Task An_instance_with_nothing_set_answers_only_the_installer_and_the_health_check()
    {
        using var factory = new ServiceFactory(installed: false);
        using var browser = factory.NewBrowser();

        using var me = await browser.GetAsync("/auth/me");
        var view = await browser.GetAsync<JsonElement>("/install");

        me.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await Browser.DetailAsync(me)).ShouldBe("Nexus is not set up yet. Open /install on its client to set it up.");
        (await browser.GetAsync("/health")).StatusCode.ShouldBe(HttpStatusCode.OK);
        view.GetProperty("installed").GetBoolean().ShouldBeFalse();
        view.GetProperty("stage").GetString().ShouldBe("Database");
        view.GetProperty("local").GetBoolean().ShouldBeFalse();
        view.GetProperty("allowed").GetBoolean().ShouldBeFalse();
        factory.Logs.Lines.Single(l => l.Contains("install code", StringComparison.Ordinal)).ShouldContain("/install on the Nexus client");
    }

    [Test]
    public async Task A_page_on_another_machine_needs_the_code_from_the_log()
    {
        using var factory = new ServiceFactory(installed: false);
        using var browser = factory.NewBrowser();
        _ = factory.Services;
        var code = CodeFromLog(factory);

        using var bare = await PostAsync(browser, "/install/database", Database);
        using var wrong = await PostAsync(browser, "/install/database", Database, code: "AAAA-BBBB-CCCC");
        using var right = await PostAsync(browser, "/install/database", Database, code: code.ToLowerInvariant());

        bare.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Browser.DetailAsync(bare)).ShouldBe("This page is not on the machine Nexus runs on, so it needs the install code from the service's log.");
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        right.StatusCode.ShouldBe(HttpStatusCode.OK, await right.Content.ReadAsStringAsync());
        (await right.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("stage").GetString().ShouldBe("Address");
        factory.Database.Prepared.Single().ShouldContain("Server=db.example.test");
    }

    [Test]
    public async Task A_page_on_this_machine_needs_no_code_unless_it_is_another_site_or_comes_through_a_proxy()
    {
        using var factory = new ServiceFactory(installed: false);
        using var browser = factory.NewBrowser();

        using var otherSite = await PostAsync(browser, "/install/database", Database, local: true, origin: "https://elsewhere.example");
        using var proxied = await PostAsync(browser, "/install/database", Database, local: true, forwarded: true);
        var view = await GetAsync(browser, local: true);
        using var local = await PostAsync(browser, "/install/database", Database, local: true);

        otherSite.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        proxied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        view.GetProperty("local").GetBoolean().ShouldBeTrue();
        view.GetProperty("allowed").GetBoolean().ShouldBeTrue();
        local.StatusCode.ShouldBe(HttpStatusCode.OK, await local.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task Keeps_a_database_only_once_it_opens_and_encrypts_its_password()
    {
        using var factory = new ServiceFactory(installed: false);
        using var browser = factory.NewBrowser();
        factory.Database.Refusal = "Access denied for user 'nexus'@'localhost' (using password: YES)";

        using var refused = await PostAsync(browser, "/install/database", Database, local: true);
        var storedAfterRefusal = factory.NewInstanceFile().DatabasePlace;
        factory.Database.Refusal = null;
        using var kept = await PostAsync(browser, "/install/database", Database, local: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(refused)).ShouldBe("Access denied for user 'nexus'@'localhost' (using password: YES)");
        storedAfterRefusal.ShouldBeNull();
        kept.StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.NewInstanceFile().Database.ShouldBe(new DatabaseSettings("db.example.test", 3307, "nexus_live", "nexus", "database-password"));
        File.ReadAllText(Path.Combine(factory.DataDirectory, InstanceFile.FileName)).ShouldNotContain("database-password");
    }

    [Test]
    public async Task Sets_up_in_three_stages_signs_the_first_admin_in_and_spends_the_code()
    {
        using var factory = new ServiceFactory(installed: false);
        using var browser = factory.NewBrowser();
        _ = factory.Services;
        var code = CodeFromLog(factory);

        using var early = await PostAsync(browser, "/install/admin", new { email = "founder@acme.test", password = Browser.Password }, code: code);
        (await PostAsync(browser, "/install/database", Database, code: code)).EnsureSuccessStatusCode();
        using var badAddress = await PostAsync(browser, "/install/address", new { clientOrigin = "http://localhost:6165/install" }, code: code);
        (await PostAsync(browser, "/install/address", new { clientOrigin = ServiceFactory.ClientOrigin }, code: code)).EnsureSuccessStatusCode();
        using var weak = await PostAsync(browser, "/install/admin", new { email = "founder@acme.test", password = "short" }, code: code);
        using var made = await PostAsync(browser, "/install/admin", new { email = "Founder@Acme.test", password = Browser.Password }, code: code);

        early.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        badAddress.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(weak)).ShouldBe("A password needs at least 12 characters.");
        made.StatusCode.ShouldBe(HttpStatusCode.OK, await made.Content.ReadAsStringAsync());
        var me = await browser.GetAsync<JsonElement>("/auth/me");
        me.GetProperty("email").GetString().ShouldBe("founder@acme.test");
        me.GetProperty("isAdmin").GetBoolean().ShouldBeTrue();
        (await browser.GetAsync<JsonElement>("/install")).GetProperty("installed").GetBoolean().ShouldBeTrue();
        (await GetAsync(browser, code: code)).GetProperty("allowed").GetBoolean().ShouldBeFalse();
        (await browser.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Acme" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        await using var db = factory.NewContext();
        (await db.LogEntries.OrderBy(e => e.Id).Select(e => e.Text).ToListAsync()).ShouldBe(
        [
            "The installer set the database: nexus_live on db.example.test:3307, signing in as nexus.",
            $"The installer set the client's address to {ServiceFactory.ClientOrigin}.",
            "The installer made founder@acme.test, the first admin.",
            "Workspace Acme was made.",
        ]);
        (await db.LogEntries.AnyAsync(e => e.Text.Contains(code))).ShouldBeFalse();
    }

    [Test]
    public async Task A_database_that_already_has_an_admin_skips_the_admin_stage()
    {
        using var factory = new ServiceFactory(installed: false);
        await factory.AddUserAsync("admin@acme.test", Browser.Password, admin: true);
        using var browser = factory.NewBrowser();

        (await PostAsync(browser, "/install/database", Database, local: true)).EnsureSuccessStatusCode();
        using var addressed = await PostAsync(browser, "/install/address", new { clientOrigin = ServiceFactory.ClientOrigin }, local: true);

        var view = await addressed.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("stage").GetString().ShouldBe("Done");
        (await browser.SignInAsync("admin@acme.test", Browser.Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Test]
    public async Task A_stored_database_that_will_not_open_sends_the_instance_back_to_the_installer()
    {
        using var factory = new ServiceFactory();
        factory.Database.Refusal = "Unable to connect to any of the specified MySQL hosts.";
        using var browser = factory.NewBrowser();

        using var me = await browser.GetAsync("/auth/me");
        var stranger = await browser.GetAsync<JsonElement>("/install");
        var local = await GetAsync(browser, local: true);

        me.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        stranger.TryGetProperty("database", out var hidden).ShouldBeTrue();
        hidden.ValueKind.ShouldBe(JsonValueKind.Null);
        local.GetProperty("stage").GetString().ShouldBe("Database");
        local.GetProperty("databaseProblem").GetString().ShouldBe("Unable to connect to any of the specified MySQL hosts.");
        local.GetProperty("database").GetProperty("host").GetString().ShouldBe(ServiceFactory.StoredDatabase.Host);
        local.GetProperty("database").TryGetProperty("password", out _).ShouldBeFalse();
        factory.Logs.Lines.ShouldContain(line => line.Contains($"{ServiceFactory.ClientOrigin}/install#", StringComparison.Ordinal));
    }

    [Test]
    public async Task Someone_locked_out_asks_for_a_code_and_makes_an_admin_with_it()
    {
        using var factory = new ServiceFactory();
        using var browser = factory.NewBrowser();

        using var asked = await PostAsync(browser, "/install/code", new { });
        using var askedAgain = await PostAsync(browser, "/install/code", new { });
        var lines = factory.Logs.Lines.Where(l => l.Contains("to make an admin", StringComparison.Ordinal)).ToList();
        using var made = await PostAsync(
            browser, "/install/admin", new { email = ServiceFactory.OwnerEmail, password = Browser.Password }, code: CodePattern().Match(lines.Single()).Value);

        asked.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        askedAgain.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        lines.Single().ShouldContain($"{ServiceFactory.ClientOrigin}/install#");
        made.StatusCode.ShouldBe(HttpStatusCode.OK, await made.Content.ReadAsStringAsync());
        (await browser.GetAsync<JsonElement>("/auth/me")).GetProperty("email").GetString().ShouldBe(ServiceFactory.OwnerEmail);
        using var other = factory.NewBrowser();
        (await other.SignInAsync(ServiceFactory.OwnerEmail, Browser.Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await using var db = factory.NewContext();
        var entries = await db.LogEntries.Select(e => e.Text).ToListAsync();
        entries.ShouldBe([$"The installer made {ServiceFactory.OwnerEmail} an admin, with a new password."]);
        entries.ShouldNotContain(text => text.Contains(CodePattern().Match(lines.Single()).Value));
    }

    [Test]
    public void Makes_the_signing_key_once_and_keeps_it_encrypted()
    {
        using var factory = new ServiceFactory();
        var used = factory.Services.GetRequiredService<IOptions<PluperfectJwtOptions>>().Value.SigningKey;

        var stored = factory.NewInstanceFile().SigningKey;

        used.Length.ShouldBe(128);
        stored.ShouldBe(used);
        File.ReadAllText(Path.Combine(factory.DataDirectory, InstanceFile.FileName)).ShouldNotContain(used);
    }

    [Test]
    public async Task Configuration_wins_over_what_the_pages_store()
    {
        using var factory = new ServiceFactory();
        using var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Nexus", "Server=configured.test;Database=nexus;User ID=ops");
            builder.UseSetting("Nexus:ClientOrigin", "http://localhost:7000");
            builder.UseSetting("Jwt:SigningKey", new string('k', 40));
        });
        using var client = configured.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/install/database") { Content = JsonContent.Create(Database) };
        request.Headers.Add(ServiceFactory.LocalHeader, "1");
        using var refused = await client.SendAsync(request);
        var view = await GetAsync(client, local: true);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Browser.DetailAsync(refused)).ShouldBe("The database is set by the service's configuration, in ConnectionStrings:Nexus.");
        view.GetProperty("databaseConfigured").GetBoolean().ShouldBeTrue();
        view.GetProperty("database").GetProperty("host").GetString().ShouldBe("configured.test");
        view.GetProperty("clientOriginConfigured").GetBoolean().ShouldBeTrue();
        configured.Services.GetRequiredService<Instance>().ClientOrigin.ShouldBe("http://localhost:7000");
        configured.Services.GetRequiredService<IOptions<PluperfectJwtOptions>>().Value.SigningKey.ShouldBe(new string('k', 40));
        factory.Database.Prepared.ShouldContain("Server=configured.test;Database=nexus;User ID=ops");
    }

    [Test]
    public async Task The_installers_mail_needs_its_access_and_closes_once_the_instance_is_set_up()
    {
        using var factory = new ServiceFactory(installed: false, mailToFiles: false);
        using var browser = factory.NewBrowser();
        (await PostAsync(browser, "/install/database", Database, local: true)).EnsureSuccessStatusCode();
        (await PostAsync(browser, "/install/address", new { clientOrigin = ServiceFactory.ClientOrigin }, local: true)).EnsureSuccessStatusCode();
        var mail = new { serverToken = FakePostmark.Token, from = "nexus@acme.test", fromName = "Acme Nexus" };

        using var bare = await SendAsync(browser, HttpMethod.Put, "/install/mail", mail);
        using var saved = await SendAsync(browser, HttpMethod.Put, "/install/mail", mail, local: true);
        using var nowhere = await SendAsync(browser, HttpMethod.Post, "/install/mail/test", new { to = "" }, local: true);
        using var tested = await SendAsync(browser, HttpMethod.Post, "/install/mail/test", new { to = "founder@acme.test" }, local: true);
        using var made = await PostAsync(browser, "/install/admin", new { email = "founder@acme.test", password = "" }, local: true);
        using var closed = await SendAsync(browser, HttpMethod.Put, "/install/mail", mail, local: true);

        bare.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        var view = await saved.Content.ReadFromJsonAsync<JsonElement>();
        view.GetProperty("mail").GetBoolean().ShouldBeTrue();
        view.GetProperty("mailSettings").GetProperty("transport").GetString().ShouldBe("Postmark");
        view.ToString().ShouldNotContain(FakePostmark.Token);
        nowhere.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        tested.StatusCode.ShouldBe(HttpStatusCode.OK, await tested.Content.ReadAsStringAsync());
        factory.Postmark.Sent.Single().GetProperty("To").GetString().ShouldBe("founder@acme.test");
        made.StatusCode.ShouldBe(HttpStatusCode.OK, await made.Content.ReadAsStringAsync());
        (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPassword").GetBoolean().ShouldBeFalse();
        closed.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await Browser.DetailAsync(closed)).ShouldBe("Nexus is set up. An admin changes its mail on the Settings page.");
    }

    [Test]
    public async Task The_first_admin_needs_a_password_while_mail_is_off()
    {
        using var factory = new ServiceFactory(installed: false, mailToFiles: false);
        using var browser = factory.NewBrowser();
        (await PostAsync(browser, "/install/database", Database, local: true)).EnsureSuccessStatusCode();
        (await PostAsync(browser, "/install/address", new { clientOrigin = ServiceFactory.ClientOrigin }, local: true)).EnsureSuccessStatusCode();

        using var passwordless = await PostAsync(browser, "/install/admin", new { email = "founder@acme.test", password = "" }, local: true);
        using var made = await PostAsync(browser, "/install/admin", new { email = "founder@acme.test", password = Browser.Password }, local: true);

        passwordless.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(passwordless)).ShouldBe("Set up mail, or choose a password. Without either, there is no way to sign in again.");
        made.StatusCode.ShouldBe(HttpStatusCode.OK, await made.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task With_mail_written_to_files_the_first_admin_needs_no_password_and_signs_in_by_link()
    {
        using var factory = new ServiceFactory(installed: false);
        using var browser = factory.NewBrowser();
        (await PostAsync(browser, "/install/database", Database, local: true)).EnsureSuccessStatusCode();
        (await PostAsync(browser, "/install/address", new { clientOrigin = ServiceFactory.ClientOrigin }, local: true)).EnsureSuccessStatusCode();
        var view = await GetAsync(browser, local: true);

        using var made = await PostAsync(browser, "/install/admin", new { email = "founder@acme.test", password = "" }, local: true);
        using var later = factory.NewBrowser();
        (await later.SendAsync(HttpMethod.Post, "/auth/link", new { email = "founder@acme.test" })).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var link = Browser.MailTo(factory.MailDirectory, "founder@acme.test").Single();
        using var redeemed = await later.SendAsync(HttpMethod.Post, "/auth/link/redeem", new { token = Browser.LinkToken(link) });

        view.GetProperty("mail").GetBoolean().ShouldBeTrue();
        view.GetProperty("mailSettings").GetProperty("transport").GetString().ShouldBe("Files");
        made.StatusCode.ShouldBe(HttpStatusCode.OK, await made.Content.ReadAsStringAsync());
        (await browser.GetAsync<JsonElement>("/auth/me")).GetProperty("isAdmin").GetBoolean().ShouldBeTrue();
        redeemed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await later.GetAsync<JsonElement>("/auth/me")).GetProperty("email").GetString().ShouldBe("founder@acme.test");
    }

    [Test]
    public async Task Make_an_admin_with_no_password_keeps_the_users_own()
    {
        using var factory = new ServiceFactory();
        await factory.AddUserAsync("rowan@acme.test", Browser.Password);
        using var browser = factory.NewBrowser();

        using var made = await PostAsync(browser, "/install/admin", new { email = "rowan@acme.test", password = "" }, local: true);

        made.StatusCode.ShouldBe(HttpStatusCode.OK, await made.Content.ReadAsStringAsync());
        (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPassword").GetBoolean().ShouldBeTrue();
        using var other = factory.NewBrowser();
        (await other.SignInAsync("rowan@acme.test", Browser.Password)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await other.GetAsync<JsonElement>("/auth/me")).GetProperty("isAdmin").GetBoolean().ShouldBeTrue();
    }

    private static string CodeFromLog(ServiceFactory factory) =>
        CodePattern().Match(factory.Logs.Lines.Single(l => l.Contains("install code", StringComparison.Ordinal))).Value;

    private static Task<JsonElement> GetAsync(Browser browser, bool local = false, string? code = null) => GetAsync(browser.Client, local, code);

    private static async Task<JsonElement> GetAsync(HttpClient client, bool local = false, string? code = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/install");
        Mark(request, local, code, ServiceFactory.ClientOrigin, forwarded: false);
        using var response = await client.SendAsync(request);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PostAsync(
        Browser browser, string path, object body, string? code = null, bool local = false, string origin = ServiceFactory.ClientOrigin, bool forwarded = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        Mark(request, local, code, origin, forwarded);
        return browser.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendAsync(Browser browser, HttpMethod method, string path, object? body = null, string? code = null, bool local = false)
    {
        var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        Mark(request, local, code, ServiceFactory.ClientOrigin, forwarded: false);
        return browser.Client.SendAsync(request);
    }

    private static void Mark(HttpRequestMessage request, bool local, string? code, string origin, bool forwarded)
    {
        request.Headers.Add("Origin", origin);
        if (code is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", InstallCode.Lead + code);
        }

        if (local)
        {
            request.Headers.Add(ServiceFactory.LocalHeader, "1");
        }

        if (forwarded)
        {
            request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        }
    }

    [GeneratedRegex("[A-Z0-9]{4}-[A-Z0-9]{4}-[A-Z0-9]{4}")]
    private static partial Regex CodePattern();
}
