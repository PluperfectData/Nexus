using Nexus.Sql;

namespace Nexus.MySql;

/// <summary>Reads the tables and views of a MySQL database, each as a stream named for its table.</summary>
public sealed class MySqlSource : ISourceConnector
{
    private readonly SqlSource _source = new(new MySqlDialect());

    /// <inheritdoc />
    public ConnectorSpec Spec => _source.Spec;

    /// <inheritdoc />
    public Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct) =>
        _source.CheckAsync(config, ct);

    /// <inheritdoc />
    public Task<SourceCatalog> DiscoverAsync(ConnectorConfig config, CancellationToken ct) =>
        _source.DiscoverAsync(config, ct);

    /// <inheritdoc />
    public IAsyncEnumerable<SyncMessage> ReadAsync(ConnectorConfig config, ReadRequest request, CancellationToken ct) =>
        _source.ReadAsync(config, request, ct);
}
