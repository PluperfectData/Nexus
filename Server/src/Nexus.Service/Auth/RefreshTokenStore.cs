using Microsoft.EntityFrameworkCore;
using Nexus.Service.Storage;
using Pluperfect.Jwt;

namespace Nexus.Service.Auth;

/// <summary><see cref="IRefreshTokenStore"/> over the service's database, modeled on PokéStory Site's.</summary>
public sealed class RefreshTokenStore(NexusDbContext db, TimeProvider time) : IRefreshTokenStore
{
    public async Task CreateAsync(
        string subjectId,
        string subjectName,
        string tokenHash,
        DateTimeOffset expiresAt,
        string? issuedForIp,
        bool persistent,
        CancellationToken cancellationToken)
    {
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = subjectId,
            Email = subjectName,
            TokenHash = tokenHash,
            IssuedAt = time.GetUtcNow(),
            ExpiresAt = expiresAt,
            IssuedForIp = issuedForIp,
            Persistent = persistent,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    // One transaction, so two refreshes arriving together cannot both redeem the same token.
    public async Task<RefreshResult> RedeemAsync(
        string presentedHash,
        string replacementHash,
        DateTimeOffset replacementExpiresAt,
        string? issuedForIp,
        CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var presented = await db.RefreshTokens.FirstOrDefaultAsync(token => token.TokenHash == presentedHash, cancellationToken);
        if (presented is null)
        {
            return new RefreshResult(RefreshOutcome.Unknown, null, null, false);
        }

        if (presented.RevokedAt is not null)
        {
            await RevokeAllAsync(presented.UserId, "Refresh token replayed.", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new RefreshResult(RefreshOutcome.Replayed, presented.UserId, presented.Email, presented.Persistent);
        }

        if (presented.ExpiresAt <= now)
        {
            return new RefreshResult(RefreshOutcome.Expired, presented.UserId, presented.Email, presented.Persistent);
        }

        var replacement = new RefreshToken
        {
            UserId = presented.UserId,
            Email = presented.Email,
            TokenHash = replacementHash,
            IssuedAt = now,
            ExpiresAt = replacementExpiresAt,
            IssuedForIp = issuedForIp,
            Persistent = presented.Persistent,
        };
        db.RefreshTokens.Add(replacement);
        // Saved before the revoke, so the replacement has an id to point at.
        await db.SaveChangesAsync(cancellationToken);
        presented.RevokedAt = now;
        presented.RevokedReason = "Rotated.";
        presented.ReplacedById = replacement.Id;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RefreshResult(RefreshOutcome.Ok, presented.UserId, presented.Email, presented.Persistent);
    }

    public async Task RevokeAsync(string tokenHash, string reason, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.RefreshTokens
            .Where(token => token.TokenHash == tokenHash && token.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, reason), cancellationToken);
    }

    public async Task RevokeAllAsync(string subjectId, string reason, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await db.RefreshTokens
            .Where(token => token.UserId == subjectId && token.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now).SetProperty(t => t.RevokedReason, reason), cancellationToken);
    }

    // Only rows whose successor is gone too, so a late replay of a rotated token is still seen as a replay.
    public async Task<int> DeleteDeadAsync(DateTimeOffset olderThan, CancellationToken cancellationToken) =>
        await db.RefreshTokens
            .Where(token => token.ExpiresAt < olderThan)
            .Where(token => !db.RefreshTokens.Any(other => other.ReplacedById == token.Id))
            .ExecuteDeleteAsync(cancellationToken);
}
