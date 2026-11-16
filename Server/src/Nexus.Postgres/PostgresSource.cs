using Nexus.Sql;

namespace Nexus.Postgres;

/// <summary>Reads the tables and views in every schema of a PostgreSQL database, each as a <c>schema.table</c> stream.</summary>
public sealed class PostgresSource : ISourceConnector
{
    private readonly SqlSource _source = new(new PostgresDialect());

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
