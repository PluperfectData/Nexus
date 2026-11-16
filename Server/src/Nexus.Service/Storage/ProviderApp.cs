namespace Nexus.Service.Storage;

/// <summary>A workspace's own app at a provider it signs in to, such as its Schwab app: the client its sign-ins go through.</summary>
public sealed class ProviderApp
{
    public const int MaxClientIdLength = 200;

    public const int MaxClientSecretLength = 500;

    public int Id { get; set; }

    public int WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    /// <summary>The connector it signs in for, such as <c>schwab</c>.</summary>
    public required string Connector { get; set; }

    /// <summary>The app's id at the provider, such as Schwab's app key. It travels in the sign-in page's address, so it is no secret.</summary>
    public required string ClientId { get; set; }

    /// <summary>The app's secret, encrypted by ASP.NET Data Protection.</summary>
    public required string ClientSecret { get; set; }

    public DateTime UpdatedAt { get; set; }
}

/// <summary>A sign-in sent to a provider and not yet back, found by a hash of the state the provider's redirect returns.</summary>
public sealed class StoredSignIn
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public long Id { get; set; }

    public required string StateHash { get; set; }

    public required string Connector { get; set; }

    public int WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    /// <summary>The new connection's name; null for a renewal.</summary>
    public string? Name { get; set; }

    /// <summary>The new connection's settings as JSON, encrypted like a connection's; null for a renewal.</summary>
    public string? Settings { get; set; }

    /// <summary>The connection a renewal signs in again.</summary>
    public int? ConnectionId { get; set; }

    /// <summary>The connect page the sign-in started from.</summary>
    public long? SessionId { get; set; }

    /// <summary>That connect page's token, encrypted, to send the browser back to the page.</summary>
    public string? SessionToken { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }
}
