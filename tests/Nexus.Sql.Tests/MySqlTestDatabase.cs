using System.Globalization;
using MySqlConnector;
using Nexus.MySql;

namespace Nexus.Sql.Tests;

public sealed class MySqlTestDatabase() : TestDatabase("MySql")
{
    private string _connectionString = string.Empty;

    public override ISourceConnector Source { get; } = new MySqlSource();

    public override string DateTimeType => "DATETIME(6)";

    public override IReadOnlyList<TypeCase> TypeCases { get; } =
    [
        new("BIGINT", "42", ColumnType.Int64, 42L),
        new("INT UNSIGNED", "4000000000", ColumnType.Int64, 4_000_000_000L),
        new("BIGINT UNSIGNED", "18446744073709551615", ColumnType.Decimal, 18446744073709551615m, 20, 0),
        new("TINYINT(1)", "1", ColumnType.Boolean, true),
        new("BIT(1)", "b'1'", ColumnType.Boolean, true),
        new("YEAR", "2026", ColumnType.Int64, 2026L),
        new("DECIMAL(10, 2)", "1234.56", ColumnType.Decimal, 1234.56m, 10, 2),
        new("DECIMAL(50, 4)", "1234.5678", ColumnType.Decimal, 1234.5678m, 38, 18),
        new("DOUBLE", "0.25", ColumnType.Float64, 0.25),
        new("DATE", "'2026-11-12'", ColumnType.Date, new DateOnly(2026, 11, 12)),
        new("DATE", "'0000-00-00'", ColumnType.Date, null),
        new("DATETIME(6)", "'2026-11-12 09:30:15.123456'", ColumnType.DateTime, new DateTime(2026, 11, 12, 9, 30, 15).AddTicks(1_234_560)),
        new("TIMESTAMP", "'2026-11-12 09:30:15+00:00'", ColumnType.Timestamp, new DateTimeOffset(2026, 11, 12, 9, 30, 15, TimeSpan.Zero)),
        new("TIME", "'09:15:30'", ColumnType.Time, new TimeOnly(9, 15, 30)),
        new("VARCHAR(20)", "'text'", ColumnType.String, "text"),
        new("CHAR(36)", "'0f8fad5b-d9cb-469f-a165-70867728950e'", ColumnType.String, "0f8fad5b-d9cb-469f-a165-70867728950e"),
        new("ENUM('a', 'b')", "'b'", ColumnType.String, "b"),
        new("JSON", """'{"a":1}'""", ColumnType.Json, """{"a": 1}"""),
        new("VARBINARY(3)", "0x010203", ColumnType.Binary, new byte[] { 1, 2, 3 }),
        new("POINT", "ST_GeomFromText('POINT(1 2)')", ColumnType.String, "POINT(1 2)"),
    ];

    public override TypeCase Unreadable { get; } = new("TIME", "'25:00:00'", ColumnType.Time, null);

    public override string Stream(string table) => table;

    public override string InsertSeries(string table, int count) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"SET SESSION cte_max_recursion_depth = {count}; INSERT INTO {table} (id) " +
            $"WITH RECURSIVE s (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM s WHERE n < {count}) SELECT n FROM s");

    public override async Task CreateAsync()
    {
        var builder = new MySqlConnectionStringBuilder(AdminConnectionString());
        await ExecuteOnAsync(builder.ConnectionString, $"CREATE DATABASE `{DatabaseName}`");

        builder.Database = DatabaseName;
        _connectionString = builder.ConnectionString;
        Config = ConfigFor(
            builder.Server,
            builder.Port.ToString(CultureInfo.InvariantCulture),
            DatabaseName,
            builder.UserID,
            builder.Password,
            builder.SslMode.ToString());
    }

    public override async Task DropAsync()
    {
        if (_connectionString.Length > 0)
        {
            await ExecuteOnAsync(_connectionString, $"DROP DATABASE `{DatabaseName}`");
        }
    }

    // An empty sql_mode admits the zero date in TypeCases.
    public override Task ExecuteAsync(string sql) => ExecuteOnAsync(_connectionString, "SET SESSION sql_mode = ''; " + sql);

    private static async Task ExecuteOnAsync(string connectionString, string sql)
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
