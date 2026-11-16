using Microsoft.Extensions.Caching.Memory;

namespace Nexus.Service.Auth;

/// <summary>
/// Counts failed password sign-ins and mailed-link requests per address, in memory, and refuses once there are too
/// many. A restart forgets the counts.
/// </summary>
public sealed class SignInThrottle(IMemoryCache cache)
{
    public const int MaxFailures = 5;

    public const int MaxLinks = 3;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    public bool IsLockedOut(string email) => Count("failures", email) >= MaxFailures;

    public void RecordFailure(string email) => Add("failures", email);

    public void Reset(string email) => cache.Remove(Key("failures", email));

    /// <summary>Counts a link request, and answers whether the address may be mailed another.</summary>
    public bool MayMailLink(string email)
    {
        if (Count("links", email) >= MaxLinks)
        {
            return false;
        }

        Add("links", email);
        return true;
    }

    private int Count(string kind, string email) => cache.TryGetValue(Key(kind, email), out int count) ? count : 0;

    // Sliding, so a slow drip of tries cannot outlast the window by pacing them just under it.
    private void Add(string kind, string email) =>
        cache.Set(Key(kind, email), Count(kind, email) + 1, new MemoryCacheEntryOptions { SlidingExpiration = Window });

    private static string Key(string kind, string email) => $"sign-in-{kind}:{email.Trim().ToLowerInvariant()}";
}
