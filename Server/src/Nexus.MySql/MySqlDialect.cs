using System.Data.Common;
using MySqlConnector;
using Nexus.Sql;

namespace Nexus.MySql;

internal sealed class MySqlDialect : SqlDialect
{
    private static readonly string[] TlsModes = ["None", "Preferred", "Required", "VerifyCA", "VerifyFull"];

    public override string Id => "mysql";

    public override string DisplayName => "MySQL";

    public override bool QualifiesStreams => false;

    public override IReadOnlyList<SettingField> ExtraFields { get; } =
        [new SettingField(SqlSettings.Tls, "TLS", SettingKind.Choice, Required: false, TlsModes)];

    public override string ColumnsQuery =>
        """
        SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.COLUMN_TYPE, c.IS_NULLABLE,
               c.NUMERIC_PRECISION, c.NUMERIC_SCALE
        FROM information_schema.COLUMNS c
        JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
        WHERE c.TABLE_SCHEMA = DATABASE() AND t.TABLE_TYPE IN ('BASE TABLE', 'VIEW')
        ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION
        """;

    public override string PrimaryKeysQuery =>
        """
        SELECT k.TABLE_SCHEMA, k.TABLE_NAME, k.COLUMN_NAME
        FROM information_schema.TABLE_CONSTRAINTS t
        JOIN information_schema.KEY_COLUMN_USAGE k
          ON k.CONSTRAINT_SCHEMA = t.CONSTRAINT_SCHEMA AND k.CONSTRAINT_NAME = t.CONSTRAINT_NAME
         AND k.TABLE_NAME = t.TABLE_NAME
        WHERE t.TABLE_SCHEMA = DATABASE() AND t.CONSTRAINT_TYPE = 'PRIMARY KEY'
        ORDER BY k.TABLE_NAME, k.ORDINAL_POSITION
        """;

    public override DbConnection CreateConnection(ConnectorConfig config)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server = SettingValues.Required(config, SqlSettings.Host),
            Database = SettingValues.Required(config, SqlSettings.Database),
            UserID = SettingValues.Required(config, SqlSettings.User),
            Password = SettingValues.Password(config),
            GuidFormat = MySqlGuidFormat.None,
            AllowZeroDateTime = true,
        };

        if (SettingValues.Port(config) is { } port)
        {
            builder.Port = (uint)port;
        }

        if (SettingValues.Choice(config, SqlSettings.Tls, TlsModes) is { } tls)
        {
            builder.SslMode = Enum.Parse<MySqlSslMode>(tls);
        }

        return new MySqlConnection(builder.ConnectionString);
    }

    // TIMESTAMP values are converted to the session's zone on read.
    public override async Task InitializeAsync(DbConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SET time_zone = '+00:00'";
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public override string Quote(string identifier) =>
        "`" + identifier.Replace("`", "``", StringComparison.Ordinal) + "`";

    public override SourceColumn Map(CatalogColumn column)
    {
        var quoted = Quote(column.Name);
        var unsigned = column.Detail.Contains("unsigned", StringComparison.OrdinalIgnoreCase);

        return column.DataType switch
        {
            "tinyint" when column.Detail.StartsWith("tinyint(1)", StringComparison.OrdinalIgnoreCase) =>
                SourceColumn.Of(column, ColumnType.Boolean, quoted),
            "bit" when column.Precision == 1 => SourceColumn.Of(column, ColumnType.Boolean, quoted),
            "bigint" when unsigned => SourceColumn.Of(column, ColumnType.Decimal, quoted, 20, 0),
            "bit" when column.Precision == 64 => SourceColumn.Of(column, ColumnType.Decimal, quoted, 20, 0),
            "tinyint" or "smallint" or "mediumint" or "int" or "bigint" or "bit" or "year" =>
                SourceColumn.Of(column, ColumnType.Int64, quoted),
            "decimal" => SourceColumn.Decimal(column, quoted),
            "float" or "double" => SourceColumn.Of(column, ColumnType.Float64, quoted),
            "date" => SourceColumn.Of(column, ColumnType.Date, quoted),
            "datetime" => SourceColumn.Of(column, ColumnType.DateTime, quoted),
            "timestamp" => SourceColumn.Of(column, ColumnType.Timestamp, quoted),
            "time" => SourceColumn.Of(column, ColumnType.Time, quoted),
            "json" => SourceColumn.Of(column, ColumnType.Json, quoted),
            "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" or "enum" or "set" =>
                SourceColumn.Of(column, ColumnType.String, quoted),
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" =>
                SourceColumn.Of(column, ColumnType.Binary, quoted),
            "geometry" or "point" or "linestring" or "polygon" or "multipoint" or "multilinestring"
                or "multipolygon" or "geomcollection" or "geometrycollection" =>
                SourceColumn.Of(column, ColumnType.String, $"ST_AsText({quoted})"),
            _ => SourceColumn.Of(column, ColumnType.String, $"CAST({quoted} AS CHAR)"),
        };
    }

    // AllowZeroDateTime returns every date as a MySqlDateTime; a zero date reads as null.
    public override object? Unwrap(object value) => value switch
    {
        MySqlDateTime { IsValidDateTime: true } date => date.GetDateTime(),
        MySqlDateTime => null,
        _ => value,
    };

    public override ConnectorException? Classify(DbException ex)
    {
        if (ex is not MySqlException mysql)
        {
            return null;
        }

        return mysql.ErrorCode switch
        {
            MySqlErrorCode.AccessDenied or MySqlErrorCode.DatabaseAccessDenied or MySqlErrorCode.TableAccessDenied
                or MySqlErrorCode.ColumnAccessDenied or MySqlErrorCode.UnknownDatabase =>
                Failure(ConnectorErrorKind.ConfigInvalid, ex),
            MySqlErrorCode.UnableToConnectToHost or MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout =>
                Failure(ConnectorErrorKind.Transient, ex),
            _ when mysql.IsTransient => Failure(ConnectorErrorKind.Transient, ex),
            _ => null,
        };
    }
}
