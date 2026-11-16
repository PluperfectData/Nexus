using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Nexus.Service.Storage;

namespace Nexus.Service.Auth;

/// <summary>A mailed link's user, and a fingerprint of the user's link stamp when it was made.</summary>
public sealed record LinkClaim(int UserId, string Fingerprint);

/// <summary>
/// Mailed sign-in and invite links, as protected payloads that carry their own expiry. A link carries a fingerprint of
/// the user's <see cref="User.LinkStamp"/>, and using a link changes the stamp, so each link works once.
/// </summary>
public sealed class SignInLinks(IDataProtectionProvider provider)
{
    public static readonly TimeSpan SignInLifetime = TimeSpan.FromMinutes(15);

    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(7);

    private readonly ITimeLimitedDataProtector _protector =
        provider.CreateProtector("Nexus.SignInLink").ToTimeLimitedDataProtector();

    public string Issue(User user, TimeSpan lifetime) =>
        _protector.Protect(string.Create(CultureInfo.InvariantCulture, $"{user.Id}.{Fingerprint(user.LinkStamp)}"), lifetime);

    /// <summary>Whom the link is for, or null when it is expired, altered, or not a link at all.</summary>
    public LinkClaim? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string payload;
        try
        {
            payload = _protector.Unprotect(token.Trim());
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }

        var parts = payload.Split('.');
        return parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var id) ? new LinkClaim(id, parts[1]) : null;
    }

    /// <summary>Whether the user's stamp is still the one the link was made against, which is false once any link is used.</summary>
    public static bool IsCurrent(LinkClaim claim, User user) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(claim.Fingerprint), Encoding.UTF8.GetBytes(Fingerprint(user.LinkStamp)));

    public static string NewStamp() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    private static string Fingerprint(string stamp) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..16];
}
