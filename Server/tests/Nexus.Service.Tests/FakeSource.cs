using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nexus.Service.Tests;

// One stream of orders keyed by id. An incremental read starts at the saved cursor, sending that row again, as SQL
// sources commonly do. Each read first throws the next queued failure, if any, and sends the queued tokens, if any,
// then its log lines after the schema.
internal sealed class FakeSource : ISourceConnector
{
    public const string Id = "fake";

    public const string StreamName = "shop.orders";

    public static readonly StreamSpec Orders = new(
        StreamName,
        [new ColumnSpec("id", ColumnType.Int64, false), new ColumnSpec("item", ColumnType.String, true)],
        ["id"],
        ["id"],
        [SyncMode.FullRefresh, SyncMode.Incremental]);

    /// <summary>A host that the check refuses.</summary>
    public const string Unreachable = "nowhere.test";

    public ConnectorSpec Spec { get; } = new(
        Id,
        "Fake",
        "Reads canned orders.",
        "1.0.0",
        AuthKind.ConnectionFields,
        [new SettingField("host", "Host", SettingKind.Text, Required: true), new SettingField("password", "Password", SettingKind.Secret, Required: false)]);

    public List<object?[]> Rows { get; } = [];

    public Queue<Exception> Failures { get; } = [];

    public List<LogMessage> Notes { get; } = [];

    public OAuthTokens? Refreshed { get; set; }

    public List<ReadRequest> Reads { get; } = [];

    public List<ConnectorConfig> Configs { get; } = [];

    public Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct) =>
        Task.FromResult(config.Values.GetValueOrDefault("host") == Unreachable
            ? CheckResult.Failure(ConnectorErrorKind.ConfigInvalid, $"Could not reach {Unreachable}.")
            : CheckResult.Success);

    public Task<SourceCatalog> DiscoverAsync(ConnectorConfig config, CancellationToken ct) => Task.FromResult(new SourceCatalog([Orders]));

    public async IAsyncEnumerable<SyncMessage> ReadAsync(ConnectorConfig config, ReadRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        Reads.Add(request);
        Configs.Add(config);
        if (Failures.TryDequeue(out var failure))
        {
            throw failure;
        }

        if (Refreshed is { } tokens)
        {
            yield return new CredentialsRefreshed(tokens);
        }

        var from = request.State is { } state && state.TryGetValue(StreamName, out var saved) ? saved.GetProperty("cursor").GetInt64() : 0;
        var rows = Rows.Where(row => (long)row[0]! >= from).ToList();
        yield return new SchemaMessage(Orders);
        foreach (var note in Notes)
        {
            yield return note;
        }

        if (rows.Count > 0)
        {
            yield return new RecordBatch(StreamName, rows);
            var cursor = rows.Max(row => (long)row[0]!);
            yield return new StateCheckpoint(StreamName, JsonSerializer.SerializeToElement(new { cursor }));
        }
    }
}
