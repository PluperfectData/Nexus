namespace Nexus.Service.Storage;

/// <summary>A connector in a workspace, with its settings and any sign-in.</summary>
public sealed class Connection
{
    public int Id { get; set; }

    public int WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    /// <summary>The Nexus connector's id, such as <c>mysql</c>.</summary>
    public required string Connector { get; set; }

    /// <summary>The connector's settings as JSON, secrets included, encrypted by ASP.NET Data Protection.</summary>
    public required string Settings { get; set; }

    /// <summary>The OAuth tokens of a signed-in service, encrypted like <see cref="Settings"/>; null for a database.</summary>
    public string? Tokens { get; set; }

    /// <summary>Whether the provider ended the sign-in, so the connection needs a new one before it syncs.</summary>
    public bool NeedsSignIn { get; set; }

    public DateTime CreatedAt { get; set; }
}
