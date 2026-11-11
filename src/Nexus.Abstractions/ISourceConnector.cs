namespace Nexus;

/// <summary>Reads streams of records from one kind of source.</summary>
public interface ISourceConnector
{
    /// <summary>What the connector is and the settings it needs.</summary>
    ConnectorSpec Spec { get; }

    /// <summary>Connects with the given settings and reports whether they work.</summary>
    Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct);

    /// <summary>Lists the streams the source offers.</summary>
    Task<SourceCatalog> DiscoverAsync(ConnectorConfig config, CancellationToken ct);

    /// <summary>Reads the selected streams, resuming from the saved state.</summary>
    IAsyncEnumerable<SyncMessage> ReadAsync(ConnectorConfig config, ReadRequest request, CancellationToken ct);
}
