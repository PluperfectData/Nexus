using System.Security.Cryptography;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Schwab.Tests;

[TestFixture]
public sealed class SchwabIdentityTests
{
    [Test]
    public async Task Names_each_linked_account_by_a_hash_of_its_number_and_its_last_four_digits()
    {
        var fake = new FakeSchwab().OnJson(HttpMethod.Get, FakeSchwab.AccountNumbers, Schwab.TwoAccounts);
        using var client = new HttpClient(fake);

        var accounts = await new SchwabSource(client, new FixedTime(Schwab.Now)).IdentifyAsync(Schwab.Config(), CancellationToken.None);

        accounts.Select(a => a.Label).ShouldBe(["…1111", "…2222"]);
        accounts[0].Id.ShouldBe(Convert.ToHexStringLower(SHA256.HashData("11111111"u8)));
    }

    [Test]
    public void Ends_each_sign_in_7_days_after_it_starts_and_warns_a_day_ahead()
    {
        using var client = new HttpClient(new FakeSchwab());

        var life = new SchwabSource(client).Spec.SignIn!;

        life.Kind.ShouldBe(SignInKind.FixedClock);
        life.EndsAt(Schwab.Now).ShouldBe(Schwab.Now.AddDays(7));
        life.WarnBefore.ShouldBe(TimeSpan.FromDays(1));
    }
}
