using System.Text.Json;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Sql.Tests;

[TestFixtureSource(typeof(TestDatabases), nameof(TestDatabases.All))]
public sealed class SqlSourceTests(TestDatabase database)
{
    [OneTimeSetUp]
    public Task CreateDatabase() => database.CreateAsync();

    [OneTimeTearDown]
    public Task DropDatabase() => database.DropAsync();

    [Test]
    public async Task Discover_reads_columns_keys_and_cursor_fields()
    {
        await database.ExecuteAsync(
            $"CREATE TABLE discover_orders (id INT NOT NULL PRIMARY KEY, name VARCHAR(50) NULL, placed {database.DateTimeType} NOT NULL)");
        await database.ExecuteAsync("CREATE VIEW discover_names AS SELECT id, name FROM discover_orders");

        var catalog = await database.Source.DiscoverAsync(database.Config, default);

        var orders = catalog.Streams.Single(stream => stream.Name == database.Stream("discover_orders"));
        orders.Columns.ShouldBe(
        [
            new ColumnSpec("id", ColumnType.Int64, Nullable: false),
            new ColumnSpec("name", ColumnType.String, Nullable: true),
            new ColumnSpec("placed", ColumnType.DateTime, Nullable: false),
        ]);
        orders.PrimaryKey.ShouldBe(["id"]);
        orders.CursorFields.ShouldBe(["id", "placed"]);
        orders.SyncModes.ShouldBe([SyncMode.FullRefresh, SyncMode.Incremental]);

        var names = catalog.Streams.Single(stream => stream.Name == database.Stream("discover_names"));
        names.Columns.Select(column => column.Name).ShouldBe(["id", "name"]);
        names.PrimaryKey.ShouldBeEmpty();
    }

    [Test]
    public async Task Reads_each_type_as_its_column_type()
    {
        var cases = database.TypeCases;
        await database.ExecuteAsync(
            $"CREATE TABLE types ({string.Join(", ", cases.Select((type, i) => $"c{i} {type.SqlType} NULL"))})");
        await database.ExecuteAsync($"INSERT INTO types VALUES ({string.Join(", ", cases.Select(type => type.Literal))})");

        var table = (await database.Source.ReadToMemoryAsync(database.Config, FullRefresh("types"))).Tables[database.Stream("types")];

        table.Stream.Columns.Select(column => (column.Type, column.Precision, column.Scale))
            .ShouldBe(cases.Select(type => (type.Type, type.Precision, type.Scale)));
        table.Rows.Single().ShouldBe(cases.Select(type => type.Value).ToArray());
    }

    [Test]
    public async Task A_value_the_contract_cannot_carry_is_a_SourceError_naming_its_column()
    {
        await database.ExecuteAsync($"CREATE TABLE unreadable (overflowing {database.Unreadable.SqlType} NULL)");
        await database.ExecuteAsync($"INSERT INTO unreadable VALUES ({database.Unreadable.Literal})");

        var ex = await Should.ThrowAsync<ConnectorException>(() =>
            database.Source.ReadToMemoryAsync(database.Config, FullRefresh("unreadable")));

        ex.Kind.ShouldBe(ConnectorErrorKind.SourceError);
        ex.Message.ShouldContain("overflowing");
    }

    [Test]
    public async Task Full_refresh_reads_every_row_then_checkpoints()
    {
        await database.ExecuteAsync("CREATE TABLE full_orders (id INT NOT NULL PRIMARY KEY, name VARCHAR(10) NULL)");
        await database.ExecuteAsync("INSERT INTO full_orders VALUES (1, 'a'), (2, 'b'), (3, NULL)");

        var messages = await Collect(FullRefresh("full_orders"));

        messages[0].ShouldBeOfType<SchemaMessage>();
        messages.OfType<RecordBatch>().SelectMany(batch => batch.Rows).OrderBy(row => row[0])
            .ShouldBe([[1L, "a"], [2L, "b"], [3L, null]]);
        messages[^1].ShouldBeOfType<StateCheckpoint>().State.EnumerateObject().ShouldBeEmpty();
    }

    [Test]
    public async Task Incremental_reads_rows_at_and_past_the_saved_cursor()
    {
        await database.ExecuteAsync($"CREATE TABLE incremental_orders (id INT NOT NULL PRIMARY KEY, placed {database.DateTimeType} NULL)");
        await database.ExecuteAsync(
            "INSERT INTO incremental_orders VALUES (1, '2026-11-01 00:00:00'), (2, '2026-11-02 00:00:00'), (3, NULL)");

        var first = await Collect(Incremental("incremental_orders", "placed"));

        Ids(first).ShouldBe([1L, 2L]);
        var saved = Checkpoints(first).ShouldHaveSingleItem();
        saved.GetProperty("value").GetString().ShouldBe("2026-11-02T00:00:00.0000000");

        await database.ExecuteAsync(
            "INSERT INTO incremental_orders VALUES (4, '2026-11-02 00:00:00'), (5, '2026-11-03 00:00:00')");

        var second = await Collect(Incremental("incremental_orders", "placed", saved));

        Ids(second).ShouldBe([2L, 4L, 5L], ignoreOrder: true);
        Checkpoints(second).ShouldHaveSingleItem().GetProperty("value").GetString().ShouldBe("2026-11-03T00:00:00.0000000");
    }

    [Test]
    public async Task Incremental_checkpoints_after_each_batch()
    {
        await database.ExecuteAsync("CREATE TABLE batched_orders (id INT NOT NULL PRIMARY KEY)");
        await database.ExecuteAsync(database.InsertSeries("batched_orders", 10_001));

        var messages = await Collect(Incremental("batched_orders", "id"));

        messages.OfType<RecordBatch>().Select(batch => batch.Rows.Count).ShouldBe([10_000, 1]);
        Checkpoints(messages).Select(state => state.GetProperty("value").GetString()).ShouldBe(["10000", "10001"]);
    }

    [Test]
    public async Task Incremental_requires_a_cursor_field_the_stream_offers()
    {
        await database.ExecuteAsync("CREATE TABLE cursor_orders (id INT NOT NULL PRIMARY KEY, name VARCHAR(10) NULL)");

        var missing = await Should.ThrowAsync<ConnectorException>(() => Collect(Incremental("cursor_orders", null)));
        var text = await Should.ThrowAsync<ConnectorException>(() => Collect(Incremental("cursor_orders", "name")));

        missing.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        text.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    [Test]
    public async Task CheckAsync_rejects_a_wrong_password_as_ConfigInvalid()
    {
        var result = await database.Source.CheckAsync(With(SqlSettings.Password, "not-the-password"), default);

        result.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
    }

    [Test]
    public async Task CheckAsync_reports_an_unreachable_server_as_Transient()
    {
        var config = With(SqlSettings.Host, "127.0.0.1");
        config = new ConnectorConfig(new Dictionary<string, string?>(config.Values) { [SqlSettings.Port] = "1" });

        var result = await database.Source.CheckAsync(config, default);

        result.ErrorKind.ShouldBe(ConnectorErrorKind.Transient);
    }

    private ConnectorConfig With(string key, string value) =>
        new(new Dictionary<string, string?>(database.Config.Values) { [key] = value });

    private ReadRequest FullRefresh(string table) =>
        new([new StreamSelection(database.Stream(table), SyncMode.FullRefresh)]);

    private ReadRequest Incremental(string table, string? cursor, JsonElement? state = null) => new(
        [new StreamSelection(database.Stream(table), SyncMode.Incremental, cursor)],
        state is { } saved ? new Dictionary<string, JsonElement> { [database.Stream(table)] = saved } : null);

    private async Task<List<SyncMessage>> Collect(ReadRequest request)
    {
        var messages = new List<SyncMessage>();
        await foreach (var message in database.Source.ReadAsync(database.Config, request, default))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static List<object?> Ids(List<SyncMessage> messages) =>
        messages.OfType<RecordBatch>().SelectMany(batch => batch.Rows).Select(row => row[0]).ToList();

    private static List<JsonElement> Checkpoints(List<SyncMessage> messages) =>
        messages.OfType<StateCheckpoint>().Select(checkpoint => checkpoint.State).ToList();
}
