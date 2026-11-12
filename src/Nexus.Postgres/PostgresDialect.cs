using System.Data.Common;
using Nexus.Sql;
using Npgsql;

namespace Nexus.Postgres;

internal sealed class PostgresDialect : SqlDialect
{
    private static readonly string[] TlsModes = ["Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull"];

    public override string Id => "postgres";

    public override string DisplayName => "PostgreSQL";

    public override bool QualifiesStreams => true;

    public override IReadOnlyList<SettingField> ExtraFields { get; } =
        [new SettingField(SqlSettings.Tls, "TLS", SettingKind.Choice, Required: false, TlsModes)];

    public override string ColumnsQuery =>
        """
        SELECT c.table_schema, c.table_name, c.column_name, c.data_type, c.udt_name, c.is_nullable,
               c.numeric_precision, c.numeric_scale
        FROM information_schema.columns c
        JOIN information_schema.tables t ON t.table_schema = c.table_schema AND t.table_name = c.table_name
        WHERE t.table_type IN ('BASE TABLE', 'VIEW', 'FOREIGN')
          AND c.table_schema <> 'information_schema' AND c.table_schema NOT LIKE 'pg\_%'
        ORDER BY c.table_schema, c.table_name, c.ordinal_position
        """;

    public override string PrimaryKeysQuery =>
        """
        SELECT k.table_schema, k.table_name, k.column_name
        FROM information_schema.table_constraints t
        JOIN information_schema.key_column_usage k
          ON k.constraint_schema = t.constraint_schema AND k.constraint_name = t.constraint_name
         AND k.table_schema = t.table_schema AND k.table_name = t.table_name
        WHERE t.constraint_type = 'PRIMARY KEY'
        ORDER BY k.table_schema, k.table_name, k.ordinal_position
        """;

    public override DbConnection CreateConnection(ConnectorConfig config)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = SettingValues.Required(config, SqlSettings.Host),
            Database = SettingValues.Required(config, SqlSettings.Database),
            Username = SettingValues.Required(config, SqlSettings.User),
            Password = SettingValues.Password(config),
        };

        if (SettingValues.Port(config) is { } port)
        {
            builder.Port = port;
        }

        if (SettingValues.Choice(config, SqlSettings.Tls, TlsModes) is { } tls)
        {
            builder.SslMode = Enum.Parse<SslMode>(tls);
        }

        return new NpgsqlConnection(builder.ConnectionString);
    }

    public override string Quote(string identifier) =>
        "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    public override SourceColumn Map(CatalogColumn column)
    {
        var quoted = Quote(column.Name);

        return column.DataType switch
        {
            "smallint" or "integer" or "bigint" => SourceColumn.Of(column, ColumnType.Int64, quoted),
            "numeric" => SourceColumn.Decimal(column, quoted),
            "money" => SourceColumn.Of(column, ColumnType.Decimal, quoted, 19, 2),
            "real" or "double precision" => SourceColumn.Of(column, ColumnType.Float64, quoted),
            "boolean" => SourceColumn.Of(column, ColumnType.Boolean, quoted),
            "date" => SourceColumn.Of(column, ColumnType.Date, Finite(quoted)),
            "timestamp without time zone" => SourceColumn.Of(column, ColumnType.DateTime, Finite(quoted)),
            "timestamp with time zone" => SourceColumn.Of(column, ColumnType.Timestamp, Finite(quoted)),
            "time without time zone" => SourceColumn.Of(column, ColumnType.Time, quoted),
            "character varying" or "character" or "text" or "name" => SourceColumn.Of(column, ColumnType.String, quoted),
            "bytea" => SourceColumn.Of(column, ColumnType.Binary, quoted),
            "json" or "jsonb" => SourceColumn.Of(column, ColumnType.Json, $"{quoted}::text"),
            "ARRAY" => SourceColumn.Of(column, ColumnType.Json, $"array_to_json({quoted})::text"),
            "USER-DEFINED" when column.Detail is "geometry" or "geography" =>
                SourceColumn.Of(column, ColumnType.String, $"ST_AsText({quoted})"),
            _ => SourceColumn.Of(column, ColumnType.String, $"{quoted}::text"),
        };
    }

    // infinity and -infinity read as null.
    private static string Finite(string quoted) => $"CASE WHEN isfinite({quoted}) THEN {quoted} END";

    public override ConnectorException? Classify(DbException ex) => ex switch
    {
        PostgresException { SqlState: PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification
            or PostgresErrorCodes.InvalidCatalogName or PostgresErrorCodes.InsufficientPrivilege } =>
            Failure(ConnectorErrorKind.ConfigInvalid, ex),
        PostgresException { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
            or PostgresErrorCodes.TooManyConnections or PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown
            or PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.LockNotAvailable } =>
            Failure(ConnectorErrorKind.Transient, ex),
        PostgresException postgres when postgres.SqlState.StartsWith("08", StringComparison.Ordinal) =>
            Failure(ConnectorErrorKind.Transient, ex),
        PostgresException postgres when postgres.SqlState.StartsWith("22", StringComparison.Ordinal) =>
            Failure(ConnectorErrorKind.SourceError, ex),
        PostgresException => null,
        NpgsqlException { IsTransient: true } => Failure(ConnectorErrorKind.Transient, ex),
        _ => null,
    };
}
