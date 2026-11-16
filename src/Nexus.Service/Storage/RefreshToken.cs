namespace Nexus.Service.Storage;

/// <summary>One refresh token, kept only as its hash, for <see cref="Auth.RefreshTokenStore"/>.</summary>
public sealed class RefreshToken
{
    public long Id { get; set; }

    public required string UserId { get; set; }

    public required string Email { get; set; }

    public required string TokenHash { get; set; }

    public DateTimeOffset IssuedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public string? IssuedForIp { get; set; }

    public bool Persistent { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    public string? RevokedReason { get; set; }

    /// <summary>The token that replaced this one when it was rotated; null when it was revoked outright.</summary>
    public long? ReplacedById { get; set; }
}
