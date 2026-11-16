#if DEBUG
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Service.Tests;

[TestFixture]
public sealed class ResetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 15, 30, 0, TimeSpan.Zero);

    [Test]
    public async Task Sets_the_stored_settings_aside_so_the_next_start_opens_the_installer()
    {
        using var factory = new ServiceFactory();
        var stored = Path.Combine(factory.DataDirectory, InstanceFile.FileName);
        var before = await File.ReadAllTextAsync(stored);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Nexus"] = "Server=ops.test" }).Build();
        using var output = new StringWriter();

        var code = InstanceReset.Run(development: true, factory.DataDirectory, configuration, output, new FixedTime(Now));
        var storedAfterReset = File.Exists(stored);
        using var browser = factory.NewBrowser();
        var view = await browser.GetAsync<JsonElement>("/install");

        code.ShouldBe(0);
        storedAfterReset.ShouldBeFalse();
        (await File.ReadAllTextAsync(Path.Combine(factory.DataDirectory, "instance-before-reset-20261116-153000.json"))).ShouldBe(before);
        output.ToString().ShouldContain("ConnectionStrings:Nexus is set in this machine's configuration, so the installer skips its database step.");
        view.GetProperty("stage").GetString().ShouldBe("Database");
        await using var db = factory.NewContext();
        (await db.Users.FindAsync(await factory.OwnerIdAsync())).ShouldNotBeNull();
    }

    [Test]
    public void Refuses_outside_Development_and_leaves_the_settings_in_place()
    {
        using var factory = new ServiceFactory();
        using var output = new StringWriter();

        var code = InstanceReset.Run(development: false, factory.DataDirectory, new ConfigurationBuilder().Build(), output, new FixedTime(Now));

        code.ShouldBe(1);
        File.Exists(Path.Combine(factory.DataDirectory, InstanceFile.FileName)).ShouldBeTrue();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
#endif
