using DuckDB.NET.Data;
using NUnit.Framework;
using Shouldly;

namespace Nexus.DuckDb.Tests;

[TestFixture]
public sealed class DuckDbDestinationTests
{
    private static readonly ColumnSpec Id = new("id", ColumnType.Int64, Nullable: false);
    private static readonly ColumnSpec Name = new("name", ColumnType.String, Nullable: true);
    private static readonly ColumnSpec Email = new("email", ColumnType.String, Nullable: true);

    private string _directory = string.Empty;
    private string _path = string.Empty;

    [SetUp]
    public void SetUp()
    {
        _directory = Path.Combine(Path.GetTempPath(), $"nexus-duckdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "test.duckdb");
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task Append_creates_the_table_and_adds_each_write()
    {
        await Write(WriteMode.Append, Schema(), Rows([1L, "a"], [2L, "b"]));
        await Write(WriteMode.Append, Schema(), Rows([3L, "c"]));

        (await Query("SELECT id, name FROM orders ORDER BY id")).ShouldBe(
            [[1L, "a"], [2L, "b"], [3L, "c"]]);
    }

    [Test]
    public async Task Overwrite_replaces_the_table()
    {
        await Write(WriteMode.Overwrite, Schema(), Rows([1L, "a"], [2L, "b"]));
        await Write(WriteMode.Overwrite, Schema(), Rows([3L, "c"]));

        (await Query("SELECT id, name FROM orders ORDER BY id")).ShouldBe([[3L, "c"]]);
    }

    [Test]
    public async Task Overwrite_keeps_the_old_table_when_the_source_fails()
    {
        await Write(WriteMode.Overwrite, Schema(), Rows([1L, "a"]));

        await Should.ThrowAsync<IOException>(() => WriteFrom(
            WriteMode.Overwrite,
            Failing(Schema(), Rows([2L, "b"]), new StateCheckpoint("orders", default))));

        (await Query("SELECT id, name FROM orders")).ShouldBe([[1L, "a"]]);
    }

    [Test]
    public async Task Upsert_replaces_rows_that_share_a_primary_key()
    {
        await Write(WriteMode.Upsert, Schema(), Rows([1L, "a"], [2L, "b"]));
        await Write(WriteMode.Upsert, Schema(), Rows([2L, "c"], [3L, "d"], [3L, "e"]));

        (await Query("SELECT id, name FROM orders ORDER BY id")).ShouldBe(
            [[1L, "a"], [2L, "c"], [3L, "e"]]);
    }

    [Test]
    public async Task Upsert_requires_a_primary_key()
    {
        var keyless = new SchemaMessage(new StreamSpec("orders", [Id, Name], [], [], [SyncMode.FullRefresh]));

        var ex = await Should.ThrowAsync<ConnectorException>(() => Write(WriteMode.Upsert, keyless));

        ex.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    [Test]
    public async Task Yields_each_checkpoint_after_the_rows_before_it_are_committed()
    {
        var committedAtCheckpoint = new List<long>();
        var destination = new DuckDbDestination();
        var messages = Yield(
            Schema(),
            Rows([1L, "a"]),
            new StateCheckpoint("orders", default),
            Rows([2L, "b"], [3L, "c"]),
            new StateCheckpoint("orders", default));

        await foreach (var _ in destination.WriteAsync(Config(), Modes(WriteMode.Append), messages, default))
        {
            committedAtCheckpoint.Add((long)(await Query("SELECT count(*) FROM orders"))[0][0]!);
        }

        committedAtCheckpoint.ShouldBe([1L, 3L]);
    }

    [Test]
    public async Task Keeps_only_rows_before_the_last_checkpoint_when_the_source_fails()
    {
        await Should.ThrowAsync<IOException>(() => WriteFrom(
            WriteMode.Append,
            Failing(Schema(), Rows([1L, "a"]), new StateCheckpoint("orders", default), Rows([2L, "b"]))));

        (await Query("SELECT id FROM orders")).ShouldBe([[1L]]);
    }

    [Test]
    public async Task Adds_a_column_the_source_gained()
    {
        await Write(WriteMode.Append, Schema(), Rows([1L, "a"]));
        await Write(WriteMode.Append, Schema(Id, Name, Email), Rows([2L, "b", "b@example.test"]));

        (await Query("SELECT id, name, email FROM orders ORDER BY id")).ShouldBe(
            [[1L, "a", null], [2L, "b", "b@example.test"]]);
    }

    [Test]
    public async Task Round_trips_every_column_type()
    {
        ColumnSpec[] columns =
        [
            new("text", ColumnType.String, true),
            new("whole", ColumnType.Int64, true),
            new("money", ColumnType.Decimal, true, Precision: 18, Scale: 2),
            new("ratio", ColumnType.Float64, true),
            new("flag", ColumnType.Boolean, true),
            new("day", ColumnType.Date, true),
            new("moment", ColumnType.Timestamp, true),
            new("wall", ColumnType.DateTime, true),
            new("clock", ColumnType.Time, true),
            new("doc", ColumnType.Json, true),
            new("bytes", ColumnType.Binary, true),
        ];
        object?[] values =
        [
            "text",
            42L,
            1234.56m,
            0.25,
            true,
            new DateOnly(2026, 11, 11),
            new DateTimeOffset(2026, 11, 11, 17, 30, 0, TimeSpan.Zero),
            new DateTime(2026, 11, 11, 9, 30, 0, DateTimeKind.Unspecified),
            new TimeOnly(9, 15, 30),
            """{"a":1}""",
            new byte[] { 1, 2, 3 },
        ];

        await Write(WriteMode.Append, Schema(columns), Rows(values, new object?[columns.Length]));

        var rows = await Query(
            "SELECT text, whole, money, ratio, flag, day, moment, wall, clock, doc::VARCHAR, bytes FROM orders " +
            "ORDER BY whole NULLS LAST");
        rows[0].ShouldBe(values);
        rows[1].ShouldAllBe(value => value == null);
    }

    [Test]
    public async Task Rejects_a_value_of_the_wrong_type()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() =>
            Write(WriteMode.Append, Schema(), Rows(["not a number", "a"])));

        ex.Kind.ShouldBe(ConnectorErrorKind.SourceError);
    }

    [Test]
    public async Task A_value_too_large_for_its_decimal_is_a_SourceError()
    {
        var narrow = new ColumnSpec("amount", ColumnType.Decimal, Nullable: true, Precision: 5, Scale: 2);

        var ex = await Should.ThrowAsync<ConnectorException>(() =>
            Write(WriteMode.Append, Schema(Id, narrow), Rows([1L, 123456.78m])));

        ex.Kind.ShouldBe(ConnectorErrorKind.SourceError);
        ex.Message.ShouldContain("amount");
    }

    [Test]
    public async Task A_value_the_existing_column_cannot_convert_is_a_SourceError()
    {
        await Write(WriteMode.Append, Schema(Id, new ColumnSpec("count", ColumnType.Int64, true)), Rows([1L, 5L]));

        var ex = await Should.ThrowAsync<ConnectorException>(() => Write(
            WriteMode.Append,
            Schema(Id, new ColumnSpec("count", ColumnType.String, true)),
            Rows([2L, "many"])));

        ex.Kind.ShouldBe(ConnectorErrorKind.SourceError);
        ex.InnerException.ShouldBeOfType<DuckDBException>();
        (await Query("SELECT id FROM orders")).ShouldBe([[1L]]);
    }

    [Test]
    public async Task A_read_only_database_file_is_ConfigInvalid()
    {
        await Write(WriteMode.Append, Schema(), Rows([1L, "a"]));
        File.SetAttributes(_path, FileAttributes.ReadOnly);
        try
        {
            var ex = await Should.ThrowAsync<ConnectorException>(() =>
                Write(WriteMode.Append, Schema(), Rows([2L, "b"])));

            ex.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        }
        finally
        {
            File.SetAttributes(_path, FileAttributes.Normal);
        }
    }

    [Test]
    public async Task CheckAsync_fails_without_a_path()
    {
        var result = await new DuckDbDestination().CheckAsync(
            new ConnectorConfig(new Dictionary<string, string?>()),
            default);

        result.Succeeded.ShouldBeFalse();
        result.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    [Test]
    public async Task CheckAsync_succeeds_for_a_writable_file()
    {
        var result = await new DuckDbDestination().CheckAsync(Config(), default);

        result.Succeeded.ShouldBeTrue();
    }

    private static SchemaMessage Schema(params ColumnSpec[] columns) => new(new StreamSpec(
        "orders",
        columns.Length == 0 ? [Id, Name] : columns,
        PrimaryKey: columns.Length == 0 || columns.Contains(Id) ? ["id"] : [],
        CursorFields: [],
        SyncModes: [SyncMode.FullRefresh, SyncMode.Incremental]));

    private static RecordBatch Rows(params object?[][] rows) => new("orders", rows);

    private static WriteRequest Modes(WriteMode mode) => new(new Dictionary<string, WriteMode> { ["orders"] = mode });

    private static async IAsyncEnumerable<SyncMessage> Yield(params SyncMessage[] messages)
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            yield return message;
        }
    }

    private static async IAsyncEnumerable<SyncMessage> Failing(params SyncMessage[] messages)
    {
        await foreach (var message in Yield(messages))
        {
            yield return message;
        }

        throw new IOException("The source dropped the connection.");
    }

    private ConnectorConfig Config() =>
        new(new Dictionary<string, string?> { [DuckDbDestination.PathSetting] = _path });

    private Task Write(WriteMode mode, params SyncMessage[] messages) => WriteFrom(mode, Yield(messages));

    private async Task WriteFrom(WriteMode mode, IAsyncEnumerable<SyncMessage> messages)
    {
        await foreach (var _ in new DuckDbDestination().WriteAsync(Config(), Modes(mode), messages, default))
        {
        }
    }

    private async Task<List<object?[]>> Query(string sql)
    {
        await using var connection = new DuckDBConnection($"Data Source={_path}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = await command.ExecuteReaderAsync();

        var rows = new List<object?[]>();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : Normalize(reader.GetValue(i), reader.GetDataTypeName(i));
            }

            rows.Add(row);
        }

        return rows;
    }

    // DuckDB.NET reads TIMESTAMPTZ as a UTC DateTime and BLOB as a stream.
    private static object Normalize(object value, string typeName)
    {
        switch (value)
        {
            case DateTime utc when typeName.Contains("TZ", StringComparison.OrdinalIgnoreCase):
                utc.Kind.ShouldNotBe(DateTimeKind.Local);
                return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));

            case Stream stream:
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }

            default:
                return value;
        }
    }
}
