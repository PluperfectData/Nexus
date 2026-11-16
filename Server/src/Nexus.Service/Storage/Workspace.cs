namespace Nexus.Service.Storage;

/// <summary>A group of users, apps, and connections.</summary>
public sealed class Workspace
{
    public const int MaxNameLength = 200;

    public int Id { get; set; }

    public required string Name { get; set; }

    public DateTime CreatedAt { get; set; }
}
