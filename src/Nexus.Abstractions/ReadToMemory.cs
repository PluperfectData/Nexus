using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nexus;

/// <summary>Every row and the last checkpoint of one stream, read into memory.</summary>
/// <param name="Stream">The stream's columns and keys as read.</param>
/// <param name="Rows">Every row, each in the order of the stream's columns.</param>
/// <param name="State">The last checkpoint, for the next incremental read, or null if none came.</param>
public sealed record MemoryTable(StreamSpec Stream, IReadOnlyList<object?[]> Rows, JsonElement? State);

/// <summary>Every stream a source read into memory.</summary>
/// <param name="Tables">One table per stream, keyed by stream name.</param>
/// <param name="RefreshedCredentials">The newest tokens the read produced, for the host to store, or null.</param>
public sealed record MemoryReadResult(IReadOnlyDictionary<string, MemoryTable> Tables, OAuthTokens? RefreshedCredentials);

/// <summary>Helpers for using a source without a destination.</summary>
public static partial class SourceConnectorExtensions
{
    /// <summary>Reads every selected stream into memory.</summary>
    public static async Task<MemoryReadResult> ReadToMemoryAsync(
        this ISourceConnector source,
        ConnectorConfig config,
        ReadRequest request,
        ILogger? logger = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var tables = new Dictionary<string, TableBuilder>();
        OAuthTokens? tokens = null;

        await foreach (var message in source.ReadAsync(config, request, ct).ConfigureAwait(false))
        {
            switch (message)
            {
                case SchemaMessage schema:
                    if (!tables.TryAdd(schema.Stream.Name, new TableBuilder(schema.Stream)))
                    {
                        throw new InvalidOperationException($"Stream '{schema.Stream.Name}' sent its schema twice.");
                    }

                    break;

                case RecordBatch batch:
                    TableFor(batch.Stream).Rows.AddRange(batch.Rows);
                    break;

                case StateCheckpoint checkpoint:
                    // Clone so the state outlives the source's JsonDocument.
                    TableFor(checkpoint.Stream).State = checkpoint.State.Clone();
                    break;

                case CredentialsRefreshed refreshed:
                    tokens = refreshed.Tokens;
                    break;

                case LogMessage log when logger is not null:
                    LogConnectorMessage(logger, log.Level, log.Text);
                    break;
            }
        }

        return new MemoryReadResult(
            tables.ToDictionary(pair => pair.Key, pair => pair.Value.Build()),
            tokens);

        TableBuilder TableFor(string stream) =>
            tables.TryGetValue(stream, out var table)
                ? table
                : throw new InvalidOperationException($"Stream '{stream}' sent data before its schema.");
    }

    [LoggerMessage(Message = "{Text}")]
    private static partial void LogConnectorMessage(ILogger logger, LogLevel level, string text);

    private sealed class TableBuilder(StreamSpec stream)
    {
        public List<object?[]> Rows { get; } = [];

        public JsonElement? State { get; set; }

        public MemoryTable Build() => new(stream, Rows, State);
    }
}
