using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace Nexus.Tests;

[TestFixture]
public sealed class ReadToMemoryTests
{
    private static readonly ConnectorConfig Config = new(new Dictionary<string, string?>());
    private static readonly ReadRequest Request = new([]);

    [Test]
    public async Task Collects_each_streams_rows_in_order_with_its_last_state()
    {
        var source = new ScriptedSource(() => Yield(
            new SchemaMessage(Stream("orders")),
            new SchemaMessage(Stream("customers")),
            new RecordBatch("orders", [[1L], [2L]]),
            new RecordBatch("customers", [[10L]]),
            new StateCheckpoint("orders", State(1)),
            new RecordBatch("orders", [[3L]]),
            new StateCheckpoint("orders", State(2)),
            new StateCheckpoint("customers", State(9))));

        var result = await source.ReadToMemoryAsync(Config, Request);

        result.Tables.Keys.ShouldBe(["orders", "customers"], ignoreOrder: true);
        result.Tables["orders"].Rows.Select(row => row[0]).ShouldBe([1L, 2L, 3L]);
        result.Tables["orders"].State!.Value.GetProperty("cursor").GetInt32().ShouldBe(2);
        result.Tables["customers"].Rows.Select(row => row[0]).ShouldBe([10L]);
        result.Tables["customers"].State!.Value.GetProperty("cursor").GetInt32().ShouldBe(9);
    }

    [Test]
    public async Task Leaves_state_and_tokens_null_when_none_arrive()
    {
        var source = new ScriptedSource(() => Yield(
            new SchemaMessage(Stream("orders")),
            new RecordBatch("orders", [[1L]])));

        var result = await source.ReadToMemoryAsync(Config, Request);

        result.Tables["orders"].State.ShouldBeNull();
        result.RefreshedCredentials.ShouldBeNull();
    }

    [Test]
    public async Task Keeps_the_newest_refreshed_tokens()
    {
        var source = new ScriptedSource(() => Yield(
            new CredentialsRefreshed(new OAuthTokens("first", "refresh-1", null)),
            new CredentialsRefreshed(new OAuthTokens("second", "refresh-2", null))));

        var result = await source.ReadToMemoryAsync(Config, Request);

        result.RefreshedCredentials.ShouldNotBeNull().AccessToken.ShouldBe("second");
    }

    [Test]
    public async Task Keeps_state_after_the_source_disposes_its_json_document()
    {
        var source = new ScriptedSource(ReadWithDisposedDocument);

        var result = await source.ReadToMemoryAsync(Config, Request);

        result.Tables["orders"].State!.Value.GetProperty("cursor").GetInt32().ShouldBe(7);

        static async IAsyncEnumerable<SyncMessage> ReadWithDisposedDocument()
        {
            yield return new SchemaMessage(Stream("orders"));
            using var document = JsonDocument.Parse("""{"cursor":7}""");
            yield return new StateCheckpoint("orders", document.RootElement);
            await Task.Yield();
        }
    }

    [Test]
    public async Task Passes_log_messages_to_the_logger()
    {
        var logger = new ListLogger();
        var source = new ScriptedSource(() => Yield(new LogMessage(LogLevel.Warning, "Slow response.")));

        await source.ReadToMemoryAsync(Config, Request, logger);

        logger.Entries.ShouldBe([(LogLevel.Warning, "Slow response.")]);
    }

    [Test]
    public async Task Throws_when_a_stream_sends_rows_before_its_schema()
    {
        var source = new ScriptedSource(() => Yield(new RecordBatch("orders", [[1L]])));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => source.ReadToMemoryAsync(Config, Request));

        ex.Message.ShouldContain("orders");
    }

    [Test]
    public async Task Throws_when_a_stream_sends_its_schema_twice()
    {
        var source = new ScriptedSource(() => Yield(
            new SchemaMessage(Stream("orders")),
            new SchemaMessage(Stream("orders"))));

        await Should.ThrowAsync<InvalidOperationException>(() => source.ReadToMemoryAsync(Config, Request));
    }

    private static StreamSpec Stream(string name) => new(
        name,
        [new ColumnSpec("id", ColumnType.Int64, Nullable: false)],
        PrimaryKey: ["id"],
        CursorFields: ["id"],
        SyncModes: [SyncMode.FullRefresh, SyncMode.Incremental]);

    private static JsonElement State(int cursor) => JsonSerializer.SerializeToElement(new { cursor });

    private static async IAsyncEnumerable<SyncMessage> Yield(params SyncMessage[] messages)
    {
        foreach (var message in messages)
        {
            await Task.Yield();
            yield return message;
        }
    }

    private sealed class ScriptedSource(Func<IAsyncEnumerable<SyncMessage>> script) : ISourceConnector
    {
        public ConnectorSpec Spec { get; } = new("scripted", "Scripted", "1.0.0", AuthKind.ConnectionFields, []);

        public Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct) =>
            Task.FromResult(CheckResult.Success);

        public Task<SourceCatalog> DiscoverAsync(ConnectorConfig config, CancellationToken ct) =>
            Task.FromResult(new SourceCatalog([]));

        public async IAsyncEnumerable<SyncMessage> ReadAsync(
            ConnectorConfig config,
            ReadRequest request,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var message in script().WithCancellation(ct))
            {
                yield return message;
            }
        }
    }
}
