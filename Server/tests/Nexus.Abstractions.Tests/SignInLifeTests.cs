using NUnit.Framework;
using Shouldly;

namespace Nexus.Tests;

[TestFixture]
public sealed class SignInLifeTests
{
    private const string Eastern = "America/New_York";

    [Test]
    public void A_length_ends_the_sign_in_that_long_after_it_starts()
    {
        var signedIn = new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);

        var life = SignInLife.After(TimeSpan.FromDays(7), TimeSpan.FromDays(1));

        life.Kind.ShouldBe(SignInKind.FixedClock);
        life.EndsAt(signedIn).ShouldBe(signedIn.AddDays(7));
        life.WarnBefore.ShouldBe(TimeSpan.FromDays(1));
    }

    [Test]
    public void A_time_of_day_ends_the_sign_in_at_its_next_one_across_a_clock_change()
    {
        var midnight = SignInLife.DailyAt(TimeOnly.MinValue, Eastern, TimeSpan.FromHours(1));

        // 10 AM Eastern Standard Time on Nov 18, 2026, ends at midnight EST, five hours behind UTC.
        midnight.EndsAt(new DateTimeOffset(2026, 11, 18, 15, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 11, 19, 5, 0, 0, TimeSpan.Zero));

        // Clocks go forward at 2 AM on Mar 8, 2026, so that night's midnight is four hours behind UTC.
        midnight.EndsAt(new DateTimeOffset(2026, 3, 8, 18, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public void A_time_of_day_the_clocks_skip_is_reached_when_they_jump_past_it()
    {
        var halfPastTwo = SignInLife.DailyAt(new TimeOnly(2, 30), Eastern, TimeSpan.Zero);

        // 1 AM EST on Mar 8, 2026: 2:30 AM never comes that day, so the sign-in ends at 3 AM EDT.
        halfPastTwo.EndsAt(new DateTimeOffset(2026, 3, 8, 6, 0, 0, TimeSpan.Zero))
            .ShouldBe(new DateTimeOffset(2026, 3, 8, 7, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public void Lasting_and_kept_alive_sign_ins_have_no_end_of_their_own()
    {
        var now = DateTimeOffset.UtcNow;
        var kept = SignInLife.KeptAliveFor(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1));

        SignInLife.Lasting.EndsAt(now).ShouldBeNull();
        kept.EndsAt(now).ShouldBeNull();
        kept.Kind.ShouldBe(SignInKind.KeptAlive);
        kept.KeepAliveEvery.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Test]
    public void A_fixed_clock_can_also_be_kept_alive_and_a_call_must_come_before_the_session_idles()
    {
        var daily = SignInLife.DailyAt(TimeOnly.MinValue, Eastern, TimeSpan.FromHours(1)).AndKeptAlive(TimeSpan.FromHours(2), TimeSpan.FromMinutes(90));

        daily.Kind.ShouldBe(SignInKind.FixedClock);
        daily.IdleTimeout.ShouldBe(TimeSpan.FromHours(2));
        Should.Throw<ArgumentOutOfRangeException>(() => SignInLife.KeptAliveFor(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)));
        Should.Throw<InvalidOperationException>(() => SignInLife.Lasting.AndKeptAlive(TimeSpan.FromHours(2), TimeSpan.FromMinutes(1)));
        Should.Throw<TimeZoneNotFoundException>(() => SignInLife.DailyAt(TimeOnly.MinValue, "Nowhere/Never", TimeSpan.Zero));
    }
}
