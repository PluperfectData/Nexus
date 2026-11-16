namespace Nexus.Service.Storage;

/// <summary>
/// A connect page an app opened for one person: one connector, into one of the app's destinations, returning to one of
/// the app's addresses. It makes at most one connection, ends once its syncs are added or it is cancelled, and expires.
/// </summary>
public sealed class ConnectSession
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public long Id { get; set; }

    /// <summary>The SHA-256 hash of the session's token. The token itself is never stored.</summary>
    public required string TokenHash { get; set; }

    public int AppId { get; set; }

    public App? App { get; set; }

    public int DestinationId { get; set; }

    public Destination? Destination { get; set; }

    /// <summary>The Nexus connector's id, such as <c>mysql</c>.</summary>
    public required string Connector { get; set; }

    public required string ReturnUrl { get; set; }

    /// <summary>The connection the page made, once it has made one.</summary>
    public int? ConnectionId { get; set; }

    public Connection? Connection { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>When the page finished or was cancelled; null while it is open.</summary>
    public DateTime? EndedAt { get; set; }
}
