using Nexus.MySql;
using Nexus.Postgres;
using Nexus.SqlServer;

namespace Nexus.Service;

/// <summary>The source connectors the service reads through, by their spec ids.</summary>
public sealed class SourceConnectors(IEnumerable<ISourceConnector> connectors)
{
    private readonly Dictionary<string, ISourceConnector> _byId = connectors.ToDictionary(connector => connector.Spec.Id);

    public static SourceConnectors Databases() => new([new MySqlSource(), new PostgresSource(), new SqlServerSource()]);

    public IEnumerable<ISourceConnector> All => _byId.Values;

    public ISourceConnector? Find(string id) => _byId.GetValueOrDefault(id);
}
