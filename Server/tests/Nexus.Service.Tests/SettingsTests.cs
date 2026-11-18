using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class SettingsTests
{
    private const string Admin = "admin@acme.test";

    [Test]
    public async Task Admins_set_the_addresses_and_CORS_follows_the_client()
    {
        using var factory = new ServiceFactory();
        using var admin = await factory.SignedInAsync(Admin, admin: true);
        using var member = await factory.SignedInAsync("member@acme.test");

        var before = await admin.GetAsync<JsonElement>("/settings");
        using var plain = await admin.SendAsync(HttpMethod.Put, "/settings/addresses", new { clientOrigin = ServiceFactory.ClientOrigin, callbackOrigin = "http://api.nexus.test" });
        using var refused = await member.SendAsync(HttpMethod.Put, "/settings/addresses", new { clientOrigin = "https://nexus.test" });
        var after = await admin.SendAsync<JsonElement>(HttpMethod.Put, "/settings/addresses", new { clientOrigin = "https://nexus.test/", callbackOrigin = "https://api.nexus.test" });

        before.GetProperty("clientOrigin").GetString().ShouldBe(ServiceFactory.ClientOrigin);
        before.GetProperty("callbackUrl").GetString().ShouldBe($"{ServiceFactory.CallbackOrigin}/oauth/callback");
        plain.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        after.GetProperty("clientOrigin").GetString().ShouldBe("https://nexus.test");
        after.GetProperty("callbackUrl").GetString().ShouldBe("https://api.nexus.test/oauth/callback");
        (await PreflightAsync(factory, "https://nexus.test")).ShouldBe("https://nexus.test");
        (await PreflightAsync(factory, ServiceFactory.ClientOrigin)).ShouldBeNull();
        (await admin.SendAsync(HttpMethod.Post, "/workspaces", new { name = "Acme" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Mail_goes_through_the_token_the_page_stores_and_the_token_is_never_answered()
    {
        using var factory = new ServiceFactory(mailToFiles: false);
        using var admin = await factory.SignedInAsync(Admin, admin: true);

        var off = await admin.GetAsync<JsonElement>("/settings");
        using var noLinks = await factory.NewBrowser().SendAsync(HttpMethod.Post, "/auth/link", new { email = Admin });
        using var tokenless = await admin.SendAsync(HttpMethod.Put, "/settings/mail", new { from = "nexus@acme.test" });
        var on = await admin.SendAsync<JsonElement>(
            HttpMethod.Put, "/settings/mail", new { serverToken = FakePostmark.Token, from = "nexus@acme.test", fromName = "Acme Nexus" });
        var tested = await admin.SendAsync<JsonElement>(HttpMethod.Post, "/settings/mail/test");
        (await admin.SendAsync(HttpMethod.Put, "/settings/mail", new { serverToken = "wrong-token", from = "nexus@acme.test" })).EnsureSuccessStatusCode();
        using var failed = await admin.SendAsync(HttpMethod.Post, "/settings/mail/test");

        off.GetProperty("mail").GetProperty("transport").GetString().ShouldBe("None");
        noLinks.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        tokenless.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        on.GetProperty("mail").GetProperty("transport").GetString().ShouldBe("Postmark");
        on.GetProperty("mail").GetProperty("tokenSet").GetBoolean().ShouldBeTrue();
        on.ToString().ShouldNotContain(FakePostmark.Token);
        tested.GetProperty("sentTo").GetString().ShouldBe(Admin);
        var sent = factory.Postmark.Sent.Single();
        sent.GetProperty("To").GetString().ShouldBe(Admin);
        sent.GetProperty("From").GetString().ShouldBe("Acme Nexus <nexus@acme.test>");
        failed.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        (await Browser.DetailAsync(failed)).ShouldBe("Postmark refused the message with error 10: The Server Token you provided was invalid.");
        File.ReadAllText(Path.Combine(factory.DataDirectory, InstanceFile.FileName)).ShouldNotContain("wrong-token");
    }

    [Test]
    public async Task Without_mail_an_invite_answers_its_link_to_pass_on()
    {
        using var factory = new ServiceFactory(mailToFiles: false);
        using var admin = await factory.SignedInAsync(Admin, admin: true);

        var invited = await admin.SendAsync<JsonElement>(HttpMethod.Post, "/users", new { email = "new@acme.test", isAdmin = false });
        var id = invited.GetProperty("user").GetProperty("id").GetInt32();
        var again = await admin.SendAsync<JsonElement>(HttpMethod.Post, $"/users/{id}/invite");

        var link = invited.GetProperty("link").GetString()!;
        link.ShouldStartWith($"{ServiceFactory.ClientOrigin}/sign-in/link#");
        invited.GetProperty("mailProblem").ValueKind.ShouldBe(JsonValueKind.Null);
        using var newcomer = factory.NewBrowser();
        (await newcomer.SendAsync(HttpMethod.Post, "/auth/link/redeem", new { token = link.Split('#')[1] })).StatusCode.ShouldBe(HttpStatusCode.OK);
        again.GetProperty("link").GetString().ShouldStartWith($"{ServiceFactory.ClientOrigin}/sign-in/link#");
    }

    [Test]
    public async Task A_new_database_is_kept_only_once_it_opens_and_a_blank_password_keeps_the_stored_one()
    {
        using var factory = new ServiceFactory();
        using var admin = await factory.SignedInAsync(Admin, admin: true);
        var stored = ServiceFactory.StoredDatabase;
        factory.Database.Refusal = "Unknown MySQL server host 'db.moved.test'";

        using var refused = await admin.SendAsync(HttpMethod.Put, "/settings/database", new { host = "db.moved.test", port = 3306, name = "nexus", user = "nexus" });
        var unchanged = factory.NewInstanceFile().Database;
        factory.Database.Refusal = null;
        var kept = await admin.SendAsync<JsonElement>(
            HttpMethod.Put, "/settings/database", new { host = stored.Host, port = stored.Port, name = stored.Name, user = stored.User, password = "" });

        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Browser.DetailAsync(refused)).ShouldBe("Unknown MySQL server host 'db.moved.test'");
        unchanged.ShouldBe(stored);
        kept.GetProperty("database").GetProperty("host").GetString().ShouldBe(stored.Host);
        factory.NewInstanceFile().Database!.Password.ShouldBe(stored.Password);
        (await admin.GetAsync<JsonElement>("/auth/me")).GetProperty("isAdmin").GetBoolean().ShouldBeTrue();
    }

    private static async Task<string?> PreflightAsync(ServiceFactory factory, string origin)
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/workspaces");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        using var response = await client.SendAsync(request);
        return response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) ? values.Single() : null;
    }

#if DEBUG
    [Test]
    public async Task A_saved_Postmark_token_wins_over_mail_files_and_turning_it_off_returns_to_them()
    {
        using var factory = new ServiceFactory();
        using var admin = await factory.SignedInAsync(Admin, admin: true);

        var files = await admin.GetAsync<JsonElement>("/settings");
        var postmark = await admin.SendAsync<JsonElement>(HttpMethod.Put, "/settings/mail", new { serverToken = FakePostmark.Token, from = "nexus@acme.test" });
        (await admin.SendAsync(HttpMethod.Post, "/settings/mail/test")).EnsureSuccessStatusCode();
        var back = await admin.SendAsync<JsonElement>(HttpMethod.Delete, "/settings/mail");

        files.GetProperty("mail").GetProperty("transport").GetString().ShouldBe("Files");
        files.GetProperty("mail").GetProperty("folder").GetString().ShouldBe(Path.GetFullPath(factory.MailDirectory));
        postmark.GetProperty("mail").GetProperty("transport").GetString().ShouldBe("Postmark");
        postmark.GetProperty("mail").GetProperty("folder").GetString().ShouldBe(Path.GetFullPath(factory.MailDirectory));
        factory.Postmark.Sent.Single().GetProperty("To").GetString().ShouldBe(Admin);
        Browser.MailTo(factory.MailDirectory, Admin).ShouldBeEmpty();
        back.GetProperty("mail").GetProperty("transport").GetString().ShouldBe("Files");
    }
#endif

    [Test]
    public async Task Outside_Development_a_mail_folder_is_ignored_and_the_log_says_so()
    {
        using var factory = new ServiceFactory();
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();

        var view = await client.GetFromJsonAsync<JsonElement>("/install");

        view.GetProperty("mail").GetBoolean().ShouldBeFalse();
        factory.Logs.Lines.ShouldContain(line =>
            line.StartsWith("Mail:PickupDirectory is set, but Nexus writes mail to files only in a Debug build running as Development.", StringComparison.Ordinal));
    }
}
