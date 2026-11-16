using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

namespace Nexus.Service.Auth;

/// <summary>The <c>add-admin</c> command's work: an operator's way to make or restore an admin, beside the installer's.</summary>
public sealed class Admins(NexusDbContext db, LinkMail mail, Instance instance, TimeProvider time)
{
    /// <summary>
    /// Makes an admin, or makes an existing user one, and answers a one-time sign-in link good for an hour, or null
    /// while the instance has no client address to link to.
    /// </summary>
    public async Task<string?> AddAsync(string email, CancellationToken ct)
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
        return instance.ClientOrigin is null ? null : mail.LinkFor(user, TimeSpan.FromHours(1));
    }
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
