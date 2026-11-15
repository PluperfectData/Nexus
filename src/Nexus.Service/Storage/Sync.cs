namespace Nexus.Service.Storage;

public enum SyncStatus
{
    Ready,
    Queued,
    Running,
    Failed,
}

/// <summary>One stream of a connection into one table at a destination.</summary>
public sealed class Sync
{
    public const int MaxTableLength = 128;

    public int Id { get; set; }

    public int ConnectionId { get; set; }

    public Connection? Connection { get; set; }

    public int DestinationId { get; set; }

    public Destination? Destination { get; set; }

    /// <summary>The stream's name at the source, such as <c>shop.orders</c>.</summary>
    public required string Stream { get; set; }

    /// <summary>The table's name at the destination.</summary>
    public required string Table { get; set; }

    public SyncMode Mode { get; set; }

    /// <summary>The column an incremental read tracks, or null for the stream's own choice.</summary>
    public string? CursorField { get; set; }

    /// <summary>The last checkpoint the destination committed, as JSON; null for a full refresh.</summary>
    public string? Checkpoint { get; set; }

    /// <summary>Minutes between scheduled runs, or null for runs started by hand.</summary>
    public int? ScheduleMinutes { get; set; }

    public SyncStatus Status { get; set; }

    public DateTime CreatedAt { get; set; }
}
