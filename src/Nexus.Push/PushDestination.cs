using System.Globalization;
using System.Runtime.CompilerServices;

namespace Nexus.Push;

/// <summary>
/// Writes each stream to a table of the same name through an app's push API, such as Scry's. An overwrite goes to a
/// replacement the app swaps in at the end, so a write that fails leaves the old table whole.
/// </summary>
/// <param name="client">The client the push API's requests go through.</param>
/// <param name="retryOptions">How failed requests back off, or the default.</param>
/// <param name="timeProvider">The clock backoff waits on, or the system's.</param>
public sealed class PushDestination(HttpClient client, HttpRetryOptions? retryOptions = null, TimeProvider? timeProvider = null)
    : IDestinationConnector
{
    /// <summary>The settings key for the push API's address, such as <c>http://localhost:6161/ingest</c>.</summary>
    public const string UrlSetting = "url";

    /// <summary>The settings key for the push API key.</summary>
    public const string KeySetting = "key";

    /// <summary>The most rows one batch carries.</summary>
    public const int MaxBatchRows = 5_000;

    /// <summary>The most bytes of rows one batch carries.</summary>
    public const int MaxBatchBytes = 8 << 20;

    private readonly HttpRetry _retry = new(client ?? throw new ArgumentNullException(nameof(client)), retryOptions, timeProvider);

    /// <inheritdoc />
    public ConnectorSpec Spec { get; } = new(
        Id: "push",
        DisplayName: "Push API",
        Description: "Writes each stream to a table through an app's push API, such as Scry's.",
        Version: typeof(PushDestination).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Auth: AuthKind.ConnectionFields,
        Fields:
        [
            new SettingField(UrlSetting, "Push API address", SettingKind.Text, Required: true),
            new SettingField(KeySetting, "Push API key", SettingKind.Secret, Required: true),
        ]);

    /// <inheritdoc />
    public async Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            await PushApi.From(config, _retry).CheckKeyAsync(ct).ConfigureAwait(false);
            return CheckResult.Success;
        }
        catch (ConnectorException ex)
        {
            return CheckResult.Failure(ex.Kind, ex.Message);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<StateCheckpoint> WriteAsync(
        ConnectorConfig config,
        WriteRequest request,
        IAsyncEnumerable<SyncMessage> messages,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(messages);

        var api = PushApi.From(config, _retry);
        // Batch ids are unique to this write, so the app skips a batch that a retry sends twice.
        var write = Guid.NewGuid().ToString("N");
        var streams = new Dictionary<string, PushStream>(StringComparer.Ordinal);
        var held = new List<StateCheckpoint>();

        await foreach (var message in messages.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (message)
            {
                case SchemaMessage schema:
                    if (streams.ContainsKey(schema.Stream.Name))
                    {
                        throw new InvalidOperationException($"Stream '{schema.Stream.Name}' sent its schema twice.");
                    }

                    streams[schema.Stream.Name] = await StartAsync(api, schema.Stream, request, streams.Count, ct).ConfigureAwait(false);
                    break;

                case RecordBatch batch:
                    var stream = StreamFor(streams, batch.Stream);
                    foreach (var row in batch.Rows)
                    {
                        var json = PushValues.Row(row, stream.Spec.Columns, stream.Spec.Name);
                        if (stream.Rows.Count > 0 && (stream.Rows.Count == MaxBatchRows || stream.Bytes + json.Length > MaxBatchBytes))
                        {
                            await SendAsync(api, write, stream, ct).ConfigureAwait(false);
                        }

                        stream.Add(json);
                    }

                    break;

                case StateCheckpoint checkpoint when StreamFor(streams, checkpoint.Stream).Mode == WriteMode.Overwrite:
                    held.Add(checkpoint);
                    break;

                case StateCheckpoint checkpoint:
                    foreach (var open in streams.Values)
                    {
                        await SendAsync(api, write, open, ct).ConfigureAwait(false);
                    }

                    await api.CommitAsync(checkpoint.Stream, ct).ConfigureAwait(false);
                    yield return checkpoint;
                    break;
            }
        }

        foreach (var open in streams.Values)
        {
            await SendAsync(api, write, open, ct).ConfigureAwait(false);
            await api.CommitAsync(open.Spec.Name, ct).ConfigureAwait(false);
        }

        foreach (var checkpoint in held)
        {
            yield return checkpoint;
        }
    }

    private static async Task<PushStream> StartAsync(PushApi api, StreamSpec spec, WriteRequest request, int number, CancellationToken ct)
    {
        if (!request.Modes.TryGetValue(spec.Name, out var mode))
        {
            throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"No write mode was given for stream '{spec.Name}'.");
        }

        if (mode == WriteMode.Upsert && spec.PrimaryKey.Count == 0)
        {
            throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"Stream '{spec.Name}' has no primary key, so it cannot be upserted.");
        }

        var schema = PushValues.Schema(spec);
        if (mode != WriteMode.Overwrite)
        {
            await api.DefineAsync(spec.Name, schema, ct).ConfigureAwait(false);
        }
        else if (!await api.ReplaceAsync(spec.Name, schema, ct).ConfigureAwait(false))
        {
            await api.DefineAsync(spec.Name, schema, ct).ConfigureAwait(false);
            if (!await api.ReplaceAsync(spec.Name, schema, ct).ConfigureAwait(false))
            {
                throw new ConnectorException(ConnectorErrorKind.SourceError, $"The push API made '{spec.Name}' but cannot find it to replace.");
            }
        }

        return new PushStream(spec, mode, number);
    }

    private static async Task SendAsync(PushApi api, string write, PushStream stream, CancellationToken ct)
    {
        if (stream.Rows.Count == 0)
        {
            return;
        }

        var id = string.Create(CultureInfo.InvariantCulture, $"{write}.{stream.Number}.{stream.Sent}");
        var mode = stream.Mode == WriteMode.Upsert ? "merge" : "append";
        await api.BatchAsync(stream.Spec.Name, PushValues.Batch(id, mode, stream.Rows), ct).ConfigureAwait(false);
        stream.Clear();
    }

    private static PushStream StreamFor(Dictionary<string, PushStream> streams, string name) =>
        streams.TryGetValue(name, out var stream)
            ? stream
            : throw new InvalidOperationException($"Stream '{name}' sent data before its schema.");

    /// <summary>One stream's table, how it is written, and the rows waiting to be sent.</summary>
    private sealed class PushStream(StreamSpec spec, WriteMode mode, int number)
    {
        public StreamSpec Spec { get; } = spec;

        public WriteMode Mode { get; } = mode;

        public int Number { get; } = number;

        public List<byte[]> Rows { get; } = [];

        public long Bytes { get; private set; }

        /// <summary>How many batches this stream has sent.</summary>
        public int Sent { get; private set; }

        public void Add(byte[] row)
        {
            Rows.Add(row);
            Bytes += row.Length;
        }

        public void Clear()
        {
            Rows.Clear();
            Bytes = 0;
            Sent++;
        }
    }
}
