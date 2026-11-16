using Microsoft.AspNetCore.Identity;
using Nexus.Service.Storage;

namespace Nexus.Service.Auth;

/// <summary>Hashes and checks passwords with ASP.NET Identity's hasher, the one piece of Identity the service uses.</summary>
public static class Passwords
{
    public const int MinLength = 12;

    public const int MaxLength = 200;

    private static readonly PasswordHasher<User> Hasher = new();

    public static string Hash(User user, string password) => Hasher.HashPassword(user, password);

    /// <summary>Whether the password matches, and whether its hash should be written again under stronger settings.</summary>
    public static (bool Matches, bool Rehash) Check(User user, string password)
    {
        if (user.PasswordHash is null)
        {
            return (false, false);
        }

        var result = Hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return (result != PasswordVerificationResult.Failed, result == PasswordVerificationResult.SuccessRehashNeeded);
    }

    /// <summary>Why the password cannot be used, or null when it can.</summary>
    public static string? Problem(string? password) =>
        password is null || password.Length < MinLength ? $"A password needs at least {MinLength} characters."
        : password.Length > MaxLength ? $"A password has at most {MaxLength} characters."
        : null;
}
