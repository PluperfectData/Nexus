using System.Data.Common;

namespace Nexus.Sql;

internal abstract class SqlDialect
{
    public abstract string Id { get; }

    public abstract string DisplayName { get; }

    /// <summary>Whether stream names carry the schema, as <c>schema.table</c>.</summary>
    public abstract bool QualifiesStreams { get; }

    /// <summary>Settings beyond host, port, database, user, and password.</summary>
    public abstract IReadOnlyList<SettingField> ExtraFields { get; }

    /// <summary>Returns schema, table, column, data type, detail, nullable, precision, and scale, in table order.</summary>
    public abstract string ColumnsQuery { get; }

    /// <summary>Returns schema, table, and column for each primary key column, in key order.</summary>
    public abstract string PrimaryKeysQuery { get; }

    public abstract DbConnection CreateConnection(ConnectorConfig config);

    public virtual Task InitializeAsync(DbConnection connection, CancellationToken ct) => Task.CompletedTask;

    public abstract string Quote(string identifier);

    public abstract SourceColumn Map(CatalogColumn column);

    /// <summary>Turns a driver-specific value into one <see cref="SqlValues"/> converts, or null.</summary>
    public virtual object? Unwrap(object value) => value;

    public abstract ConnectorException? Classify(DbException ex);

    protected static ConnectorException Failure(ConnectorErrorKind kind, DbException ex) => kind switch
    {
        ConnectorErrorKind.ConfigInvalid => new(kind, $"The database refused the connection settings. {ex.Message}", ex),
        ConnectorErrorKind.Transient => new(kind, $"The database is unavailable or busy. Try again later. {ex.Message}", ex),
        _ => new(kind, $"The database could not produce a value. {ex.Message}", ex),
    };
}
