namespace Nexus.Service.Storage;

public enum EntryLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>One line of the operator log: something that happened, in a sentence for whoever runs the instance.</summary>
public sealed class LogEntry
{
    public const int MaxTextLength = 1000;

    // "app " and an app's name, or an email.
    public const int MaxActorLength = 4 + App.MaxNameLength;

    public long Id { get; set; }

    public DateTime At { get; set; }

    public EntryLevel Level { get; set; }

    public string Text { get; set; } = string.Empty;

    /// <summary>The user's email, or "app" and the app's name. Null for the service's own work.</summary>
    public string? Actor { get; set; }

    /// <summary>Null for an entry about the whole instance, which only admins see.</summary>
    public int? WorkspaceId { get; set; }

    public Workspace? Workspace { get; set; }

    public int? ConnectionId { get; set; }

    public Connection? Connection { get; set; }

    public int? SyncId { get; set; }

    public Sync? Sync { get; set; }

    /// <summary>Set when the entry is added, until the developer log has it. Not stored.</summary>
    public bool Pending { get; set; }
}
