namespace Nexus.Service.Storage;

/// <summary>A person who signs in to the instance's pages, with a password or a mailed link.</summary>
public sealed class User
{
    public const int MaxEmailLength = 254;

    public int Id { get; set; }

    /// <summary>The address, in lower case, that links are mailed to and that signs in.</summary>
    public required string Email { get; set; }

    /// <summary>The password's hash, or null for a user who signs in only by mailed links.</summary>
    public string? PasswordHash { get; set; }

    /// <summary>Admins manage users, workspaces, and apps, and see every workspace.</summary>
    public bool IsAdmin { get; set; }

    /// <summary>
    /// Changes whenever a mailed link is used, so each link works once and using one ends those mailed before it.
    /// </summary>
    public required string LinkStamp { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? SignedInAt { get; set; }
}

/// <summary>A user's place in a workspace.</summary>
public sealed class WorkspaceMember
{
    public int WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public int UserId { get; set; }

    public User? User { get; set; }
}
