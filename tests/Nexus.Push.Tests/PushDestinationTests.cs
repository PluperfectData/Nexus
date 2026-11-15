using System.Net;
using System.Text.Json;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Push.Tests;

[TestFixture]
public sealed class PushDestinationTests
{
    private static readonly HttpRetryOptions NoRetry = new() { MaxAttempts = 1 };

    private static readonly StreamSpec Orders = new(
        "orders",
        [new ColumnSpec("id", ColumnType.Int64, false), new ColumnSpec("amount", ColumnType.Decimal, true, 10, 2)],
        ["id"],
        ["id"],
        [SyncMode.FullRefresh, SyncMode.Incremental]);

    private static readonly StreamSpec Lines = new("lines", [new ColumnSpec("text", ColumnType.String, true)], [], [], [SyncMode.FullRefresh]);

    private FakeIngest _ingest = null!;
    private HttpClient _client = null!;
    private PushDestination _destination = null!;

    [SetUp]
    public void SetUp()
    {
        _ingest = new FakeIngest();
        _client = new HttpClient(_ingest);
        _destination = new PushDestination(_client, NoRetry);
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
        _ingest.Dispose();
    }

    [Test]
    public async Task Checks_its_key_by_asking_the_push_API_to_name_it()
    {
        (await _destination.CheckAsync(Config(), default)).Succeeded.ShouldBeTrue();
        _ingest.Requests.Single().ToString().ShouldBe("GET /ingest");

        var refused = await _destination.CheckAsync(Config("scry_wrong"), default);
        refused.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        refused.Message.ShouldBe("The push API refused the key. Send a push API key as 'Authorization: Bearer <key>'.");

        var nowhere = await _destination.CheckAsync(new ConnectorConfig(Settings("scry", FakeIngest.Key)), default);
        nowhere.ErrorKind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        nowhere.Message.ShouldBe("The push API address must be an http or https address, such as http://localhost:6161/ingest.");
    }

    [Test]
    public async Task Upserts_by_merging_batches_and_commits_before_each_checkpoint()
    {
        var seen = await WriteAsync(
            WriteMode.Upsert,
            new SchemaMessage(Orders),
            new RecordBatch("orders", [[1L, 2.50m]]),
            new StateCheckpoint("orders", State(1)),
            new RecordBatch("orders", [[2L, null]]),
            new StateCheckpoint("orders", State(2)));

        Sent().ShouldBe(
        [
            "PUT /ingest/sources/orders",
            "POST /ingest/sources/orders/batches",
            "POST /ingest/sources/orders/commit",
            "POST /ingest/sources/orders/batches",
            "POST /ingest/sources/orders/commit",
            "POST /ingest/sources/orders/commit",
        ]);
        seen.Select(s => s.SentBefore).ShouldBe([3, 5]);

        var schema = _ingest.Requests[0].Body!.Value;
        schema.GetRawText().ShouldBe(
            """{"columns":[{"name":"id","type":"Int64"},{"name":"amount","type":"Decimal","precision":10,"scale":2}],"primaryKey":["id"]}""");

        var first = _ingest.Requests[1].Body!.Value;
        first.GetProperty("mode").GetString().ShouldBe("merge");
        first.GetProperty("rows").GetRawText().ShouldBe("""[[1,"2.50"]]""");
        var second = _ingest.Requests[3].Body!.Value;
        second.GetProperty("rows").GetRawText().ShouldBe("[[2,null]]");
        second.GetProperty("id").GetString().ShouldNotBe(first.GetProperty("id").GetString());
    }

    [Test]
    public async Task Overwrites_through_a_replacement_and_holds_checkpoints_until_it_swaps_in()
    {
        var seen = await WriteAsync(
            WriteMode.Overwrite,
            new SchemaMessage(Orders),
            new RecordBatch("orders", [[1L, 1m]]),
            new StateCheckpoint("orders", State(1)),
            new RecordBatch("orders", [[2L, 2m]]),
            new StateCheckpoint("orders", State(2)));

        Sent().ShouldBe(
        [
            "POST /ingest/sources/orders/replace",
            "PUT /ingest/sources/orders",
            "POST /ingest/sources/orders/replace",
            "POST /ingest/sources/orders/batches",
            "POST /ingest/sources/orders/commit",
        ]);
        _ingest.Requests[3].Body!.Value.GetProperty("mode").GetString().ShouldBe("append");
        _ingest.Requests[3].Body!.Value.GetProperty("rows").GetArrayLength().ShouldBe(2);
        seen.Select(s => s.SentBefore).ShouldBe([5, 5]);
    }

    [Test]
    public async Task Replaces_a_table_that_exists_without_making_it_again()
    {
        _ingest.Tables.Add("orders");

        await WriteAsync(WriteMode.Overwrite, new SchemaMessage(Orders), new RecordBatch("orders", [[1L, 1m]]));

        Sent().ShouldBe(["POST /ingest/sources/orders/replace", "POST /ingest/sources/orders/batches", "POST /ingest/sources/orders/commit"]);
    }

    [Test]
    public async Task Splits_batches_at_five_thousand_rows()
    {
        var spec = new StreamSpec("counts", [new ColumnSpec("n", ColumnType.Int64, false)], [], [], [SyncMode.FullRefresh]);
        var rows = Enumerable.Range(0, 12_001).Select(n => new object?[] { (long)n }).ToList();

        await WriteAsync(WriteMode.Append, new SchemaMessage(spec), new RecordBatch("counts", rows));

        var batches = Batches();
        batches.Select(b => b.GetProperty("rows").GetArrayLength()).ShouldBe([5_000, 5_000, 2_001]);
        batches.Select(b => b.GetProperty("id").GetString()).Distinct().Count().ShouldBe(3);
    }

    [Test]
    public async Task Splits_batches_before_they_pass_eight_megabytes()
    {
        var megabyte = new string('x', 1 << 20);
        var rows = Enumerable.Range(0, 9).Select(_ => new object?[] { megabyte }).ToList();

        await WriteAsync(WriteMode.Append, new SchemaMessage(Lines), new RecordBatch("lines", rows));

        Batches().Select(b => b.GetProperty("rows").GetArrayLength()).ShouldBe([7, 2]);
    }

    [Test]
    public async Task Writes_each_column_type_in_the_form_the_push_API_reads()
    {
        ColumnType[] types =
        [
            ColumnType.String, ColumnType.Int64, ColumnType.Decimal, ColumnType.Float64, ColumnType.Boolean, ColumnType.Date,
            ColumnType.Timestamp, ColumnType.DateTime, ColumnType.Time, ColumnType.Json, ColumnType.Binary, ColumnType.String,
        ];
        var spec = new StreamSpec("every", [.. types.Select((type, i) => new ColumnSpec($"c{i}", type, true))], [], [], [SyncMode.FullRefresh]);
        object?[] row =
        [
            "text", 42L, 12.345m, 1.5, true, new DateOnly(2026, 11, 15), new DateTimeOffset(2026, 11, 15, 14, 30, 0, TimeSpan.Zero),
            new DateTime(2026, 11, 15, 14, 30, 0, DateTimeKind.Utc), new TimeOnly(14, 30), """{"a":1}""", new byte[] { 1, 2 }, null,
        ];

        await WriteAsync(WriteMode.Append, new SchemaMessage(spec), new RecordBatch("every", [row]));

        var written = Batches().Single().GetProperty("rows")[0].EnumerateArray()
            .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
        written.ShouldBe(
        [
            "text", "42", "12.345", "1.5", "true", "2026-11-15", "2026-11-15T14:30:00.0000000+00:00", "2026-11-15T14:30:00.0000000",
            "14:30:00.0000000", """{"a":1}""", "AQI=", "null",
        ]);
    }

    [TestCase(42, "Column 'text' of 'lines' holds a Int32, which does not fit its type, String.")]
    [TestCase(double.NaN, "Column 'text' of 'lines' holds a Double, which does not fit its type, String.")]
    public async Task Refuses_a_value_that_does_not_fit_its_column(object value, string message)
    {
        var failure = await Should.ThrowAsync<ConnectorException>(() =>
            WriteAsync(WriteMode.Append, new SchemaMessage(Lines), new RecordBatch("lines", [[value]])));

        failure.Kind.ShouldBe(ConnectorErrorKind.SourceError);
        failure.Message.ShouldBe(message);
    }

    [Test]
    public async Task Refuses_numbers_and_JSON_that_JSON_cannot_carry()
    {
        var spec = new StreamSpec("odd", [new ColumnSpec("real", ColumnType.Float64, true), new ColumnSpec("doc", ColumnType.Json, true)], [], [], [SyncMode.FullRefresh]);

        var nan = await Should.ThrowAsync<ConnectorException>(() =>
            WriteAsync(WriteMode.Append, new SchemaMessage(spec), new RecordBatch("odd", [[double.NaN, "{}"]])));
        nan.Message.ShouldBe("Column 'real' of 'odd' holds NaN, which JSON cannot carry.");

        var notJson = await Should.ThrowAsync<ConnectorException>(() =>
            WriteAsync(WriteMode.Append, new SchemaMessage(spec), new RecordBatch("odd", [[1.0, "{not json"]])));
        notJson.Message.ShouldBe("Column 'doc' of 'odd' holds text that is not JSON.");
    }

    [Test]
    public async Task Says_a_table_whose_columns_changed_takes_a_full_refresh()
    {
        _ingest.Fail(HttpMethod.Put, "/ingest/sources/orders", HttpStatusCode.Conflict, "Changing or removing column 'amount' takes a replacement.");

        var failure = await Should.ThrowAsync<ConnectorException>(() => WriteAsync(WriteMode.Upsert, new SchemaMessage(Orders)));

        failure.Kind.ShouldBe(ConnectorErrorKind.SourceError);
        failure.Message.ShouldBe(
            "'orders' cannot take this stream's columns: Changing or removing column 'amount' takes a replacement. A full refresh replaces the table.");
    }

    [Test]
    public async Task Classifies_a_refused_table_and_a_failing_push_API()
    {
        _ingest.Fail(HttpMethod.Put, "/ingest/sources/orders", HttpStatusCode.Forbidden, "This key did not make 'orders', so it cannot write to it.");
        var refused = await Should.ThrowAsync<ConnectorException>(() => WriteAsync(WriteMode.Upsert, new SchemaMessage(Orders)));
        refused.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        refused.Message.ShouldBe("This key did not make 'orders', so it cannot write to it.");

        _ingest.Fail(HttpMethod.Put, "/ingest/sources/lines", HttpStatusCode.ServiceUnavailable, "Down.");
        var down = await Should.ThrowAsync<ConnectorException>(() => WriteAsync(WriteMode.Append, new SchemaMessage(Lines)));
        down.Kind.ShouldBe(ConnectorErrorKind.Transient);
    }

    [Test]
    public async Task Refuses_a_stream_with_no_mode_or_an_upsert_with_no_key()
    {
        var unnamed = await Should.ThrowAsync<ConnectorException>(() =>
            Drain(_destination.WriteAsync(Config(), new WriteRequest(new Dictionary<string, WriteMode>()), Messages(new SchemaMessage(Lines)), default)));
        unnamed.Message.ShouldBe("No write mode was given for stream 'lines'.");

        var keyless = await Should.ThrowAsync<ConnectorException>(() => WriteAsync(WriteMode.Upsert, new SchemaMessage(Lines)));
        keyless.Kind.ShouldBe(ConnectorErrorKind.ConfigInvalid);
        keyless.Message.ShouldBe("Stream 'lines' has no primary key, so it cannot be upserted.");
        _ingest.Requests.ShouldBeEmpty();
    }

    private static Dictionary<string, string?> Settings(string url, string key) =>
        new() { [PushDestination.UrlSetting] = url, [PushDestination.KeySetting] = key };

    private static ConnectorConfig Config(string key = FakeIngest.Key) => new(Settings("http://scry.test/ingest", key));

    private static JsonElement State(int cursor) => JsonDocument.Parse($$"""{"cursor":{{cursor}}}""").RootElement.Clone();

    private static async IAsyncEnumerable<SyncMessage> Messages(params SyncMessage[] messages)
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            yield return message;
        }
    }

    private static async Task<List<StateCheckpoint>> Drain(IAsyncEnumerable<StateCheckpoint> checkpoints)
    {
        var all = new List<StateCheckpoint>();
        await foreach (var checkpoint in checkpoints)
        {
            all.Add(checkpoint);
        }

        return all;
    }

    // Each stream in the messages is written with one mode. Each checkpoint comes back with the count of requests sent
    // before it was yielded.
    private async Task<List<(StateCheckpoint Checkpoint, int SentBefore)>> WriteAsync(WriteMode mode, params SyncMessage[] messages)
    {
        var modes = messages.OfType<SchemaMessage>().ToDictionary(schema => schema.Stream.Name, _ => mode);
        var seen = new List<(StateCheckpoint, int)>();
        await foreach (var checkpoint in _destination.WriteAsync(Config(), new WriteRequest(modes), Messages(messages), default))
        {
            seen.Add((checkpoint, _ingest.Requests.Count));
        }

        return seen;
    }

    private List<string> Sent() => [.. _ingest.Requests.Select(request => request.ToString())];

    private List<JsonElement> Batches() =>
        [.. _ingest.Requests.Where(request => request.Path.EndsWith("/batches", StringComparison.Ordinal)).Select(request => request.Body!.Value)];
}
