using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

namespace Nexus.Service.Auth;

/// <summary>
/// How an instance gets its first admin, and how one is restored when every admin is locked out. The first comes from
/// the setup page, with the code <see cref="AnnounceSetupAsync"/> writes to the log.
/// </summary>
public sealed partial class Admins(NexusDbContext db, LinkMail mail, SetupCode setup, ServiceSettings settings, TimeProvider time, ILogger<Admins> logger)
{
    /// <summary>On a start with no users, makes the setup code and writes it to the log with the setup page's address.</summary>
    public async Task AnnounceSetupAsync(CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(ct) && setup.Issue() is { } code)
        {
            LogSetup(logger, $"{settings.ClientOrigin.TrimEnd('/')}/setup", code);
        }
    }

    /// <summary>Makes an admin, or makes an existing user one, and answers a one-time sign-in link good for an hour.</summary>
    public async Task<string> AddAsync(string email, CancellationToken ct)
    {
        var address = Emails.Normalize(email) ?? throw new ArgumentException($"'{email}' is not an email address.", nameof(email));
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == address, ct);
        if (user is null)
        {
            user = new User { Email = address, LinkStamp = SignInLinks.NewStamp(), CreatedAt = time.GetUtcNow().UtcDateTime };
            db.Users.Add(user);
        }

        user.IsAdmin = true;
        await db.SaveChangesAsync(ct);
        return mail.LinkFor(user, TimeSpan.FromHours(1));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nexus has no users yet. Open {Page} and enter this setup code to make the first admin: {Code}")]
    private static partial void LogSetup(ILogger logger, string page, string code);
}

public static class Emails
{
    /// <summary>The address trimmed and in lower case, or null when it is not one.</summary>
    public static string? Normalize(string? email)
    {
        var address = email?.Trim().ToLowerInvariant();
        if (address is null || address.Length > User.MaxEmailLength || address.Any(char.IsWhiteSpace))
        {
            return null;
        }

        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < address.Length - 1 ? address : null;
    }
}
