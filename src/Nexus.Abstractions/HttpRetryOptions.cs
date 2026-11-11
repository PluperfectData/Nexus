namespace Nexus;

/// <summary>How <see cref="HttpRetry"/> backs off.</summary>
public sealed record HttpRetryOptions
{
    /// <summary>Five attempts, starting at one second and waiting at most thirty.</summary>
    public static HttpRetryOptions Default { get; } = new();

    /// <summary>Total attempts, including the first.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>The wait before the second attempt, doubling after each.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The longest single wait; a longer <c>Retry-After</c> fails at once.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
}
