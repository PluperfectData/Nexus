using System.Globalization;
using Microsoft.Data.SqlClient;
using Nexus.SqlServer;

namespace Nexus.Sql.Tests;

public sealed class SqlServerTestDatabase() : TestDatabase("SqlServer")
{
    private string _adminConnectionString = string.Empty;
    private string _connectionString = string.Empty;

    public override ISourceConnector Source { get; } = new SqlServerSource();

    public override string DateTimeType => "DATETIME2";

    public override IReadOnlyList<TypeCase> TypeCases { get; } =
    [
        new("INT", "42", ColumnType.Int64, 42L),
        new("TINYINT", "7", ColumnType.Int64, 7L),
        new("DECIMAL(10, 2)", "1234.56", ColumnType.Decimal, 1234.56m, 10, 2),
        new("MONEY", "12.34", ColumnType.Decimal, 12.34m, 19, 4),
        new("FLOAT", "0.25", ColumnType.Float64, 0.25),
        new("REAL", "0.25", ColumnType.Float64, 0.25),
        new("BIT", "1", ColumnType.Boolean, true),
        new("DATE", "'2026-11-12'", ColumnType.Date, new DateOnly(2026, 11, 12)),
        new("DATETIME2", "'2026-11-12 09:30:15.1234567'", ColumnType.DateTime, new DateTime(2026, 11, 12, 9, 30, 15).AddTicks(1_234_567)),
        new("DATETIMEOFFSET", "'2026-11-12 09:30:15 +02:00'", ColumnType.Timestamp, new DateTimeOffset(2026, 11, 12, 7, 30, 15, TimeSpan.Zero)),
        new("TIME", "'09:15:30'", ColumnType.Time, new TimeOnly(9, 15, 30)),
        new("NVARCHAR(20)", "N'text'", ColumnType.String, "text"),
        new("UNIQUEIDENTIFIER", "'0F8FAD5B-D9CB-469F-A165-70867728950E'", ColumnType.String, "0F8FAD5B-D9CB-469F-A165-70867728950E"),
        new("XML", "'<a/>'", ColumnType.String, "<a/>"),
        new("VARBINARY(3)", "0x010203", ColumnType.Binary, new byte[] { 1, 2, 3 }),
        new("GEOMETRY", "geometry::STGeomFromText('POINT(1 2)', 0)", ColumnType.String, "POINT (1 2)"),
        new("HIERARCHYID", "'/1/'", ColumnType.String, "/1/"),
    ];

    public override TypeCase Unreadable { get; } =
        new("DECIMAL(38, 0)", "99999999999999999999999999999999999999", ColumnType.Decimal, null, 38, 0);

    public override string Stream(string table) => "dbo." + table;

    public override string InsertSeries(string table, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"INSERT INTO {table} (id) SELECT value FROM GENERATE_SERIES(1, {count})");

    public override async Task CreateAsync()
    {
        var builder = new SqlConnectionStringBuilder(AdminConnectionString());
        _adminConnectionString = builder.ConnectionString;
        await ExecuteOnAsync(_adminConnectionString, $"CREATE DATABASE [{DatabaseName}]");

        builder.InitialCatalog = DatabaseName;
        _connectionString = builder.ConnectionString;

        var dataSource = builder.DataSource.Split(',', 2);
        var encrypt = builder.Encrypt == SqlConnectionEncryptOption.Optional ? "Optional"
            : builder.Encrypt == SqlConnectionEncryptOption.Strict ? "Strict"
            : "Mandatory";
        Config = ConfigFor(
            dataSource[0],
            dataSource.Length > 1 ? dataSource[1] : null,
            DatabaseName,
            builder.UserID,
            builder.Password,
            encrypt,
            (SqlServerSource.TrustServerCertificateSetting, builder.TrustServerCertificate.ToString()));
    }

    public override async Task DropAsync()
    {
        if (_connectionString.Length > 0)
        {
            SqlConnection.ClearAllPools();
            await ExecuteOnAsync(
                _adminConnectionString,
                $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]");
        }
    }

    public override Task ExecuteAsync(string sql) => ExecuteOnAsync(_connectionString, sql);

    private static async Task ExecuteOnAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
