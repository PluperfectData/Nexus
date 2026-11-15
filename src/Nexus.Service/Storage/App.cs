namespace Nexus.Service.Storage;

/// <summary>An app in a workspace that uses the service, such as Scry.</summary>
public sealed class App
{
    public const int MaxNameLength = 200;

    public int Id { get; set; }

    public int WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public required string Name { get; set; }

    /// <summary>The SHA-256 hash of the app's key, or null once the key is revoked. The key itself is never stored.</summary>
    public string? KeyHash { get; set; }

    /// <summary>The start of the app's key, shown to tell keys apart.</summary>
    public string? KeyPrefix { get; set; }

    /// <summary>The addresses its connect pages may return to, as a JSON array.</summary>
    public string ReturnUrls { get; set; } = "[]";

    public DateTime CreatedAt { get; set; }
}
