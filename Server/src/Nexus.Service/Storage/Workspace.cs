namespace Nexus.Service.Storage;

/// <summary>Who a workspace mails when one of its connections' sign-ins is about to end, or has ended.</summary>
public enum SignInMail
{
    Off,

    /// <summary>Every member of the workspace, and every admin.</summary>
    Everyone,

    Admins,

    /// <summary>The people in <see cref="SignInMailPerson"/>, while each is still a member or an admin.</summary>
    Named,
}

/// <summary>A group of users, apps, and connections.</summary>
public sealed class Workspace
{
    public const int MaxNameLength = 200;

    public int Id { get; set; }

    public required string Name { get; set; }

    public SignInMail SignInMail { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>Someone a workspace names to mail about its sign-ins.</summary>
public sealed class SignInMailPerson
{
    public int WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public int UserId { get; set; }

    public User? User { get; set; }
}
