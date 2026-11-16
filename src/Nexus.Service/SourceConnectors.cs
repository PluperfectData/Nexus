using Nexus.Csv;
using Nexus.MySql;
using Nexus.Postgres;
using Nexus.SqlServer;

namespace Nexus.Service;

/// <summary>The source connectors the service reads through, by their spec ids.</summary>
public sealed class SourceConnectors(IEnumerable<ISourceConnector> connectors)
{
    private readonly Dictionary<string, ISourceConnector> _byId = connectors.ToDictionary(connector => connector.Spec.Id);

    /// <summary>The databases, then CSV.</summary>
    public static SourceConnectors Defaults() => new([new MySqlSource(), new PostgresSource(), new SqlServerSource(), new CsvSource()]);

    public IEnumerable<ISourceConnector> All => _byId.Values;

    public ISourceConnector? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The setting a connector reads its uploaded file from, or null for one that reads no file.</summary>
    public static SettingField? FileFieldOf(ISourceConnector connector) =>
        connector.Spec.Fields.FirstOrDefault(field => field.Kind == SettingKind.File);
}
