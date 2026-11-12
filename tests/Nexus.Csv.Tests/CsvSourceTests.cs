using System.Globalization;
using System.Text;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Csv.Tests;

[TestFixture]
public sealed class CsvSourceTests
{
    [Test]
    public async Task Infers_the_narrowest_type_that_fits_every_value()
    {
        var csv =
            "flag,whole,amount,mixed,ratio,day,moment,wall,code,label\n" +
            "true,1,1.5,1,1e3,2026-11-12,2026-11-12T09:30:15Z,2026-11-12T09:30:15.5,1,a\n" +
            "FALSE,-22,-12.25,2.5,0.5,2026-11-13,2026-11-12 09:30:15+02:00,2026-11-12 09:30,x,\"b, \"\"quoted\"\"\non two lines\"\n";

        var table = await ReadAsync(csv);

        table.Stream.Columns.ShouldBe(
        [
            new ColumnSpec("flag", ColumnType.Boolean, false),
            new ColumnSpec("whole", ColumnType.Int64, false),
            new ColumnSpec("amount", ColumnType.Decimal, false, 4, 2),
            new ColumnSpec("mixed", ColumnType.Decimal, false, 2, 1),
            new ColumnSpec("ratio", ColumnType.Float64, false),
            new ColumnSpec("day", ColumnType.Date, false),
            new ColumnSpec("moment", ColumnType.Timestamp, false),
            new ColumnSpec("wall", ColumnType.DateTime, false),
            new ColumnSpec("code", ColumnType.String, false),
            new ColumnSpec("label", ColumnType.String, false),
        ]);
        table.Rows.ShouldBe(
        [
            [
                true, 1L, 1.5m, 1m, 1000.0, new DateOnly(2026, 11, 12),
                new DateTimeOffset(2026, 11, 12, 9, 30, 15, TimeSpan.Zero),
                new DateTime(2026, 11, 12, 9, 30, 15, 500), "1", "a",
            ],
            [
                false, -22L, -12.25m, 2.5m, 0.5, new DateOnly(2026, 11, 13),
                new DateTimeOffset(2026, 11, 12, 7, 30, 15, TimeSpan.Zero),
                new DateTime(2026, 11, 12, 9, 30, 0), "x", "b, \"quoted\"\non two lines",
            ],
        ]);
    }

    [Test]
    public async Task Empty_fields_are_null_and_make_their_column_nullable()
    {
        var table = await ReadAsync("id,name,count,note\n1,,,\n2,b,5,\n");

        table.Stream.Columns.ShouldBe(
        [
            new ColumnSpec("id", ColumnType.Int64, false),
            new ColumnSpec("name", ColumnType.String, true),
            new ColumnSpec("count", ColumnType.Int64, true),
            new ColumnSpec("note", ColumnType.String, true),
        ]);
        table.Rows.ShouldBe([[1L, null, null, null], [2L, "b", 5L, null]]);
    }

    [Test]
    public async Task Without_a_header_columns_are_numbered()
    {
        var table = await ReadAsync("1,a\n2,b\n", (CsvSource.HeaderSetting, "false"));

        table.Stream.Columns.Select(column => column.Name).ShouldBe(["column1", "column2"]);
        table.Rows.ShouldBe([[1L, "a"], [2L, "b"]]);
    }

    [Test]
    public async Task Reads_the_chosen_delimiter()
    {
        var table = await ReadAsync("id;name\n1;a,b\n", (CsvSource.DelimiterSetting, "Semicolon"));

        table.Rows.ShouldBe([[1L, "a,b"]]);
    }

    [Test]
    public async Task Yields_batches_of_ten_thousand_rows_then_a_checkpoint()
    {
        var csv = new StringBuilder("id\n");
        for (var i = 1; i <= 10_001; i++)
        {
            csv.Append(i.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }

        var messages = new List<SyncMessage>();
        await foreach (var message in new CsvSource().ReadAsync(Config(csv.ToString()), FullRefresh, default))
        {
            messages.Add(message);
        }

        messages[0].ShouldBeOfType<SchemaMessage>().Stream.Name.ShouldBe("orders");
        messages.OfType<RecordBatch>().Select(batch => batch.Rows.Count).ShouldBe([10_000, 1]);
        messages[^1].ShouldBeOfType<StateCheckpoint>().State.EnumerateObject().ShouldBeEmpty();
    }

    [Test]
    public async Task A_row_with_the_wrong_field_count_is_a_SourceError_naming_the_row()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAsync("id,name\n1,a\n2\n"));

        ex.Kind.ShouldBe(ConnectorErrorKind.SourceError);
        ex.Message.ShouldContain("Row 2");
    }

    [Test]
    public async Task A_repeated_column_name_is_a_SourceError()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() => ReadAsync("id,ID\n1,2\n"));

        ex.Kind.ShouldBe(ConnectorErrorKind.SourceError);
    }

    [Test]
    public async Task An_incremental_read_is_ConfigInvalid()
    {
        var ex = await Should.ThrowAsync<ConnectorException>(() => new CsvSource().ReadToMemoryAsync(
            Config("id\n1\n"),
            new ReadRequest([new StreamSelection("orders", SyncMode.Incremental, "id")])));

        ex.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    [Test]
    public async Task CheckAsync_fails_without_a_file()
    {
        var config = new ConnectorConfig(new Dictionary<string, string?> { [CsvSource.StreamSetting] = "orders" });

        var result = await new CsvSource().CheckAsync(config, default);

        result.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    [Test]
    public async Task CheckAsync_fails_for_an_empty_file()
    {
        var result = await new CsvSource().CheckAsync(Config(string.Empty), default);

        result.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    [Test]
    public async Task CheckAsync_succeeds_for_a_file()
    {
        var result = await new CsvSource().CheckAsync(Config("id\n1\n"), default);

        result.Succeeded.ShouldBeTrue();
    }

    private static readonly ReadRequest FullRefresh = new([new StreamSelection("orders", SyncMode.FullRefresh)]);

    private static async Task<MemoryTable> ReadAsync(string csv, params (string Key, string Value)[] settings)
    {
        var result = await new CsvSource().ReadToMemoryAsync(Config(csv, settings), FullRefresh);
        return result.Tables["orders"];
    }

    private static ConnectorConfig Config(string csv, params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?> { [CsvSource.StreamSetting] = "orders" };
        foreach (var (key, value) in settings)
        {
            values[key] = value;
        }

        var bytes = Encoding.UTF8.GetBytes(csv);
        var file = new ConnectorFile("orders.csv", _ => Task.FromResult<Stream>(new MemoryStream(bytes)));
        return new ConnectorConfig(values, Files: new Dictionary<string, ConnectorFile> { [CsvSource.FileSetting] = file });
    }
}
