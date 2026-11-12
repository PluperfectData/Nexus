using System.Globalization;
using Nexus.Postgres;
using Npgsql;

namespace Nexus.Sql.Tests;

public sealed class PostgresTestDatabase() : TestDatabase("Postgres")
{
    private string _adminConnectionString = string.Empty;
    private string _connectionString = string.Empty;

    public override ISourceConnector Source { get; } = new PostgresSource();

    public override string DateTimeType => "TIMESTAMP";

    public override IReadOnlyList<TypeCase> TypeCases { get; } =
    [
        new("INTEGER", "42", ColumnType.Int64, 42L),
        new("NUMERIC(10, 2)", "1234.56", ColumnType.Decimal, 1234.56m, 10, 2),
        new("NUMERIC", "1234.5678", ColumnType.Decimal, 1234.5678m, 38, 18),
        new("MONEY", "12.34", ColumnType.Decimal, 12.34m, 19, 2),
        new("DOUBLE PRECISION", "0.25", ColumnType.Float64, 0.25),
        new("BOOLEAN", "TRUE", ColumnType.Boolean, true),
        new("DATE", "'2026-11-12'", ColumnType.Date, new DateOnly(2026, 11, 12)),
        new("TIMESTAMP", "'2026-11-12 09:30:15.123456'", ColumnType.DateTime, new DateTime(2026, 11, 12, 9, 30, 15).AddTicks(1_234_560)),
        new("TIMESTAMP", "'infinity'", ColumnType.DateTime, null),
        new("TIMESTAMPTZ", "'2026-11-12 09:30:15+02'", ColumnType.Timestamp, new DateTimeOffset(2026, 11, 12, 7, 30, 15, TimeSpan.Zero)),
        new("TIME", "'09:15:30'", ColumnType.Time, new TimeOnly(9, 15, 30)),
        new("TEXT", "'text'", ColumnType.String, "text"),
        new("UUID", "'0f8fad5b-d9cb-469f-a165-70867728950e'", ColumnType.String, "0f8fad5b-d9cb-469f-a165-70867728950e"),
        new("JSONB", """'{"a":1}'""", ColumnType.Json, """{"a": 1}"""),
        new("BYTEA", "decode('010203', 'hex')", ColumnType.Binary, new byte[] { 1, 2, 3 }),
        new("INTEGER[]", "'{1,2}'", ColumnType.Json, "[1,2]"),
        new("INTERVAL", "'1 day'", ColumnType.String, "1 day"),
        new("POINT", "'(1,2)'", ColumnType.String, "(1,2)"),
    ];

    public override TypeCase Unreadable { get; } = new("TIME", "'24:00:00'", ColumnType.Time, null);

    public override string Stream(string table) => "public." + table;

    public override string InsertSeries(string table, int count) =>
        string.Create(CultureInfo.InvariantCulture, $"INSERT INTO {table} (id) SELECT generate_series(1, {count})");

    public override async Task CreateAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString());
        _adminConnectionString = builder.ConnectionString;
        await ExecuteOnAsync(_adminConnectionString, $"CREATE DATABASE \"{DatabaseName}\"");

        builder.Database = DatabaseName;
        _connectionString = builder.ConnectionString;
        Config = ConfigFor(
            builder.Host ?? "localhost",
            builder.Port.ToString(CultureInfo.InvariantCulture),
            DatabaseName,
            builder.Username ?? string.Empty,
            builder.Password,
            builder.SslMode.ToString());
    }

    public override async Task DropAsync()
    {
        if (_connectionString.Length > 0)
        {
            await ExecuteOnAsync(_adminConnectionString, $"DROP DATABASE \"{DatabaseName}\" WITH (FORCE)");
        }
    }

    public override Task ExecuteAsync(string sql) => ExecuteOnAsync(_connectionString, sql);

    private static async Task ExecuteOnAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
