using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;

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

/// <summary>
/// Sign-ins sent to a provider and not yet back, kept in the database for ten minutes by a hash of the <c>state</c> the
/// redirect returns, so a restart of the service loses none.
/// </summary>
public sealed class SignIns(NexusDbContext db, Secrets secrets, TimeProvider time)
{
    public async Task<string> StartAsync(PendingSignIn pending, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.PendingSignIns.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(ct);

        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        db.PendingSignIns.Add(new StoredSignIn
        {
            StateHash = Hash(state),
            Connector = pending.Connector,
            WorkspaceId = pending.WorkspaceId,
            Name = pending.Name,
            Settings = pending.Settings is null ? null : secrets.Protect(pending.Settings),
            ConnectionId = pending.ConnectionId,
            SessionId = pending.SessionId,
            SessionToken = pending.SessionToken is null ? null : secrets.ProtectText(pending.SessionToken),
            CreatedAt = now,
            ExpiresAt = now + StoredSignIn.Lifetime,
        });
        await db.SaveChangesAsync(ct);
        return state;
    }

    /// <summary>The sign-in, once: taking it removes it. Null when it is unknown, already taken, or out of time.</summary>
    public async Task<PendingSignIn?> TakeAsync(string? state, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        var hash = Hash(state);
        var stored = await db.PendingSignIns.AsNoTracking().FirstOrDefaultAsync(s => s.StateHash == hash, ct);

        // Only the callback whose delete removes the row goes on, so two callbacks with one state cannot both use it.
        if (stored is null || await db.PendingSignIns.Where(s => s.Id == stored.Id).ExecuteDeleteAsync(ct) == 0)
        {
            return null;
        }

        if (stored.ExpiresAt <= time.GetUtcNow().UtcDateTime)
        {
            return null;
        }

        return new PendingSignIn(
            stored.Connector,
            stored.WorkspaceId,
            stored.Name,
            stored.Settings is null ? null : secrets.Unprotect(stored.Settings),
            stored.ConnectionId,
            stored.SessionId,
            stored.SessionToken is null ? null : secrets.UnprotectText(stored.SessionToken));
    }

    private static string Hash(string state) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(state)));
}
