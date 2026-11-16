namespace Nexus.Service.Storage;

public enum RunStatus
{
    Running,
    Succeeded,
    Failed,
}

/// <summary>One run of a sync.</summary>
public sealed class Run
{
    public const int MaxErrorLength = 1000;

    public long Id { get; set; }

    public int SyncId { get; set; }

    public Sync? Sync { get; set; }

    /// <summary>1 for a first try, and one more for each retry after a failure the service retries.</summary>
    public int Attempt { get; set; }

    public RunStatus Status { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? EndedAt { get; set; }

    /// <summary>The rows sent to the destination.</summary>
    public long Rows { get; set; }

    /// <summary>What the user does about a failure, when a connector said.</summary>
    public ConnectorErrorKind? ErrorKind { get; set; }

    public string? Error { get; set; }
}
