namespace Nexus;

/// <summary>How a connector's sign-in ends, which decides what a host shows and when it asks someone to sign in again.</summary>
public enum SignInKind
{
    /// <summary>Lasts until it is revoked: a refresh token that renews as it is used, or an API key.</summary>
    Lasting,

    /// <summary>Ends at a time the provider sets, after which a person signs in again.</summary>
    FixedClock,

    /// <summary>Ends after a short time without a call, so the host keeps calling it; a person signs in again once it ends.</summary>
    KeptAlive,
}

/// <summary>
/// How long a connector's sign-in lasts. Made by <see cref="Lasting"/>, <see cref="After"/>, <see cref="DailyAt"/>, or
/// <see cref="KeptAliveFor"/>; a fixed clock that also goes idle adds <see cref="AndKeptAlive"/>.
/// </summary>
public sealed record SignInLife
{
    private SignInLife(SignInKind kind) => Kind = kind;

    /// <summary>How the sign-in ends.</summary>
    public SignInKind Kind { get; }

    /// <summary>A fixed clock's length from each sign-in.</summary>
    public TimeSpan? Lifetime { get; private init; }

    /// <summary>A fixed clock's time of day in <see cref="TimeZoneId"/>, at which every sign-in ends.</summary>
    public TimeOnly? EndsDailyAt { get; private init; }

    /// <summary>The time zone <see cref="EndsDailyAt"/> is read in, such as <c>America/New_York</c>.</summary>
    public string? TimeZoneId { get; private init; }

    /// <summary>How long before a fixed clock ends the host warns that it will.</summary>
    public TimeSpan WarnBefore { get; private init; }

    /// <summary>How long the session lasts without a call.</summary>
    public TimeSpan? IdleTimeout { get; private init; }

    /// <summary>How often the host calls <see cref="IKeepsSignInAlive.KeepAliveAsync"/>.</summary>
    public TimeSpan? KeepAliveEvery { get; private init; }

    /// <summary>A sign-in that lasts until it is revoked.</summary>
    public static SignInLife Lasting { get; } = new(SignInKind.Lasting);

    /// <summary>A sign-in the provider ends <paramref name="lifetime"/> after it starts.</summary>
    public static SignInLife After(TimeSpan lifetime, TimeSpan warnBefore)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(warnBefore, TimeSpan.Zero);
        return new SignInLife(SignInKind.FixedClock) { Lifetime = lifetime, WarnBefore = warnBefore };
    }

    /// <summary>A sign-in the provider ends at the next <paramref name="at"/> in <paramref name="timeZoneId"/>.</summary>
    /// <exception cref="TimeZoneNotFoundException">No time zone has the id.</exception>
    public static SignInLife DailyAt(TimeOnly at, string timeZoneId, TimeSpan warnBefore)
    {
        _ = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        ArgumentOutOfRangeException.ThrowIfLessThan(warnBefore, TimeSpan.Zero);
        return new SignInLife(SignInKind.FixedClock) { EndsDailyAt = at, TimeZoneId = timeZoneId, WarnBefore = warnBefore };
    }

    /// <summary>A session that ends after <paramref name="idleTimeout"/> without a call, called every <paramref name="keepAliveEvery"/>.</summary>
    public static SignInLife KeptAliveFor(TimeSpan idleTimeout, TimeSpan keepAliveEvery) =>
        new SignInLife(SignInKind.KeptAlive).Idle(idleTimeout, keepAliveEvery);

    /// <summary>This fixed clock, whose session also ends after <paramref name="idleTimeout"/> without a call.</summary>
    public SignInLife AndKeptAlive(TimeSpan idleTimeout, TimeSpan keepAliveEvery) =>
        Kind == SignInKind.FixedClock
            ? Idle(idleTimeout, keepAliveEvery)
            : throw new InvalidOperationException("Only a fixed clock is kept alive besides.");

    /// <summary>When a fixed clock ends a sign-in made at <paramref name="signedInAt"/>, or null for any other kind.</summary>
    public DateTimeOffset? EndsAt(DateTimeOffset signedInAt)
    {
        if (Lifetime is { } lifetime)
        {
            return signedInAt + lifetime;
        }

        if (EndsDailyAt is not { } at || TimeZoneId is not { } zoneId)
        {
            return null;
        }

        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(signedInAt, zone).DateTime);
        for (var next = 0; ; next++)
        {
            var wall = day.AddDays(next).ToDateTime(at);

            // A time the clocks skip at a change is reached once they jump past it.
            while (zone.IsInvalidTime(wall))
            {
                wall = wall.AddMinutes(1);
            }

            var end = new DateTimeOffset(wall, zone.GetUtcOffset(wall));
            if (end > signedInAt)
            {
                return end;
            }
        }
    }

    private SignInLife Idle(TimeSpan idleTimeout, TimeSpan keepAliveEvery)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(keepAliveEvery, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleTimeout, keepAliveEvery);
        return this with { IdleTimeout = idleTimeout, KeepAliveEvery = keepAliveEvery };
    }
}

/// <summary>An account a sign-in reaches: an id that stays the same across sign-ins, and a short label to show, such as <c>…1234</c>.</summary>
public sealed record SignInAccount(string Id, string Label);

/// <summary>A connector whose sign-in reaches accounts, so a host can tell whether a new sign-in reaches the same ones.</summary>
public interface IIdentifiesSignIn
{
    /// <summary>The accounts the sign-in in <paramref name="config"/> reaches.</summary>
    Task<IReadOnlyList<SignInAccount>> IdentifyAsync(ConnectorConfig config, CancellationToken ct);
}

/// <summary>A connector whose session ends unless it is called, so the host calls it every <see cref="SignInLife.KeepAliveEvery"/>.</summary>
public interface IKeepsSignInAlive
{
    /// <summary>
    /// Keeps the session in <paramref name="config"/> alive, and answers how long it has left, or null when the provider
    /// does not say. Throws <see cref="ConnectorErrorKind.AuthExpired"/> once the session has ended.
    /// </summary>
    Task<TimeSpan?> KeepAliveAsync(ConnectorConfig config, CancellationToken ct);
}
