namespace Nexus;

/// <summary>Writes streams of records into one kind of store.</summary>
public interface IDestinationConnector
{
    /// <summary>What the connector is and the settings it needs.</summary>
    ConnectorSpec Spec { get; }

    /// <summary>Connects with the given settings and reports whether they work.</summary>
    Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct);

    /// <summary>Writes a source's messages, yielding each checkpoint once every record before it is committed.</summary>
    IAsyncEnumerable<StateCheckpoint> WriteAsync(
        ConnectorConfig config,
        WriteRequest request,
        IAsyncEnumerable<SyncMessage> messages,
        CancellationToken ct);
}
