using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;

namespace Nexus.Service;

/// <summary>
/// A sign-in sent to a provider and not yet back. A new connection carries its name and settings; a renewal names the
/// connection. One started on a connect page names its session and keeps the page's token, to send the browser back.
/// </summary>
public sealed record PendingSignIn(
    string Connector,
    int WorkspaceId,
    string? Name,
    IReadOnlyDictionary<string, string?>? Settings,
    int? ConnectionId,
    long? SessionId,
    string? SessionToken);

/// <summary>Sign-ins sent to a provider and not yet back, by the <c>state</c> the redirect returns, for ten minutes.</summary>
public sealed class SignIns(IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public string Start(PendingSignIn pending)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        cache.Set(Key(state), pending, Lifetime);
        return state;
    }

    /// <summary>The sign-in, once: taking it removes it.</summary>
    public PendingSignIn? Take(string? state)
    {
        if (string.IsNullOrEmpty(state) || !cache.TryGetValue(Key(state), out PendingSignIn? pending))
        {
            return null;
        }

        cache.Remove(Key(state));
        return pending;
    }

    private static string Key(string state) => $"nexus-sign-in:{state}";
}
