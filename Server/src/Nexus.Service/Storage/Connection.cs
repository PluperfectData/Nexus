namespace Nexus.Service.Storage;

/// <summary>A connector in a workspace, with its settings and any sign-in.</summary>
public sealed class Connection
{
    public const int MaxNameLength = 200;

    public int Id { get; set; }

    public int WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    /// <summary>What its workspace calls it, such as "Shop database".</summary>
    public required string Name { get; set; }

    /// <summary>The Nexus connector's id, such as <c>mysql</c>.</summary>
    public required string Connector { get; set; }

    /// <summary>The connector's settings as JSON, secrets included, encrypted by ASP.NET Data Protection.</summary>
    public required string Settings { get; set; }

    /// <summary>The OAuth tokens of a signed-in service, encrypted like <see cref="Settings"/>; null for a database.</summary>
    public string? Tokens { get; set; }

    /// <summary>Whether the provider ended the sign-in, so the connection needs a new one before it syncs.</summary>
    public bool NeedsSignIn { get; set; }

    /// <summary>When the connection last signed in to its provider.</summary>
    public DateTime? SignedInAt { get; set; }

    /// <summary>When the provider ends that sign-in, for a connector whose sign-in runs on a fixed clock.</summary>
    public DateTime? SignInEndsAt { get; set; }

    /// <summary>When the warning that the sign-in ends soon was given; once for each sign-in.</summary>
    public DateTime? SignInWarnedAt { get; set; }

    /// <summary>When the sign-in ended, which is when <see cref="NeedsSignIn"/> was set.</summary>
    public DateTime? SignInEndedAt { get; set; }

    /// <summary>When a session that ends without calls last answered one.</summary>
    public DateTime? KeptAliveAt { get; set; }

    /// <summary>The accounts the sign-in reaches, as JSON of the connector's <see cref="SignInAccount"/>s.</summary>
    public string? Accounts { get; set; }

    /// <summary>The uploaded file's own name, for a connector that reads a file, such as CSV.</summary>
    public string? FileName { get; set; }

    /// <summary>The uploaded file's name in the service's files directory; the connection's own copy.</summary>
    public string? StoredFile { get; set; }

    public DateTime CreatedAt { get; set; }
}
