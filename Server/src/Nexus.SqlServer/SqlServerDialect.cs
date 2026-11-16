using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Nexus.Sql;

namespace Nexus.SqlServer;

internal sealed class SqlServerDialect : SqlDialect
{
    private static readonly string[] TlsModes = ["Optional", "Mandatory", "Strict"];

    public override string Id => "sqlserver";

    public override string DisplayName => "SQL Server";

    public override bool QualifiesStreams => true;

    public override IReadOnlyList<SettingField> ExtraFields { get; } =
    [
        new SettingField(SqlSettings.Tls, "Encryption", SettingKind.Choice, Required: false, TlsModes),
        new SettingField(
            SqlServerSource.TrustServerCertificateSetting,
            "Trust the server certificate",
            SettingKind.Boolean,
            Required: false),
    ];

    public override string ColumnsQuery =>
        """
        SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.DATA_TYPE, c.IS_NULLABLE,
               c.NUMERIC_PRECISION, c.NUMERIC_SCALE
        FROM INFORMATION_SCHEMA.COLUMNS c
        JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME
        WHERE c.TABLE_SCHEMA NOT IN ('sys', 'INFORMATION_SCHEMA')
        ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
        """;

    public override string PrimaryKeysQuery =>
        """
        SELECT k.TABLE_SCHEMA, k.TABLE_NAME, k.COLUMN_NAME
        FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS t
        JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE k
          ON k.CONSTRAINT_SCHEMA = t.CONSTRAINT_SCHEMA AND k.CONSTRAINT_NAME = t.CONSTRAINT_NAME
         AND k.TABLE_SCHEMA = t.TABLE_SCHEMA AND k.TABLE_NAME = t.TABLE_NAME
        WHERE t.CONSTRAINT_TYPE = 'PRIMARY KEY'
        ORDER BY k.TABLE_SCHEMA, k.TABLE_NAME, k.ORDINAL_POSITION
        """;

    public override DbConnection CreateConnection(ConnectorConfig config)
    {
        var host = SettingValues.Required(config, SqlSettings.Host);
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = SettingValues.Port(config) is { } port
                ? string.Create(CultureInfo.InvariantCulture, $"{host},{port}")
                : host,
            InitialCatalog = SettingValues.Required(config, SqlSettings.Database),
            UserID = SettingValues.Required(config, SqlSettings.User),
            Password = SettingValues.Password(config),
            TrustServerCertificate = SettingValues.Flag(config, SqlServerSource.TrustServerCertificateSetting),
        };

        if (SettingValues.Choice(config, SqlSettings.Tls, TlsModes) is { } tls)
        {
            builder.Encrypt = SqlConnectionEncryptOption.Parse(tls);
        }

        return new SqlConnection(builder.ConnectionString);
    }

    public override string Quote(string identifier) =>
        "[" + identifier.Replace("]", "]]", StringComparison.Ordinal) + "]";

    public override SourceColumn Map(CatalogColumn column)
    {
        var quoted = Quote(column.Name);

        return column.DataType switch
        {
            "tinyint" or "smallint" or "int" or "bigint" => SourceColumn.Of(column, ColumnType.Int64, quoted),
            "decimal" or "numeric" => SourceColumn.Decimal(column, quoted),
            "money" => SourceColumn.Of(column, ColumnType.Decimal, quoted, 19, 4),
            "smallmoney" => SourceColumn.Of(column, ColumnType.Decimal, quoted, 10, 4),
            "float" or "real" => SourceColumn.Of(column, ColumnType.Float64, quoted),
            "bit" => SourceColumn.Of(column, ColumnType.Boolean, quoted),
            "date" => SourceColumn.Of(column, ColumnType.Date, quoted),
            "time" => SourceColumn.Of(column, ColumnType.Time, quoted),
            "datetime" or "datetime2" or "smalldatetime" => SourceColumn.Of(column, ColumnType.DateTime, quoted),
            "datetimeoffset" => SourceColumn.Of(column, ColumnType.Timestamp, quoted),
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "sysname" =>
                SourceColumn.Of(column, ColumnType.String, quoted),
            "binary" or "varbinary" or "image" or "timestamp" or "rowversion" =>
                SourceColumn.Of(column, ColumnType.Binary, quoted),
            "json" => SourceColumn.Of(column, ColumnType.Json, $"CONVERT(nvarchar(max), {quoted})"),
            "geography" or "geometry" => SourceColumn.Of(column, ColumnType.String, $"{quoted}.STAsText()"),
            "hierarchyid" => SourceColumn.Of(column, ColumnType.String, $"{quoted}.ToString()"),
            _ => SourceColumn.Of(column, ColumnType.String, $"CONVERT(nvarchar(max), {quoted})"),
        };
    }

    public override ConnectorException? Classify(DbException ex)
    {
        if (ex is not SqlException sql)
        {
            return null;
        }

        return sql.Number switch
        {
            // Login failed, cannot open the database, and permission denied.
            18456 or 4060 or 916 or 229 or 230 => Failure(ConnectorErrorKind.ConfigInvalid, ex),
            // Deadlock victim, timeout, and Azure SQL throttling and failover.
            1205 or -2 or 40197 or 40501 or 40613 => Failure(ConnectorErrorKind.Transient, ex),
            // Arithmetic overflow and failed conversions.
            8115 or 8114 or 241 or 242 or 245 or 517 => Failure(ConnectorErrorKind.SourceError, ex),
            // Class 20 is a broken connection: refused, reset, or unreachable.
            _ when sql.Class == 20 => Failure(ConnectorErrorKind.Transient, ex),
            _ => null,
        };
    }
}
