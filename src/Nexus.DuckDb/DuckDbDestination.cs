using System.Runtime.CompilerServices;
using DuckDB.NET.Data;

namespace Nexus.DuckDb;

/// <summary>Writes each stream into a table of the same name in a DuckDB database file.</summary>
public sealed class DuckDbDestination : IDestinationConnector
{
    /// <summary>The settings key for the database file path.</summary>
    public const string PathSetting = "path";

    /// <inheritdoc />
    public ConnectorSpec Spec { get; } = new(
        Id: "duckdb",
        DisplayName: "DuckDB",
        Version: typeof(DuckDbDestination).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Auth: AuthKind.ConnectionFields,
        Fields: [new SettingField(PathSetting, "Database file", SettingKind.Text, Required: true)]);

    /// <inheritdoc />
    public async Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!TryGetPath(config, out var path))
        {
            return CheckResult.Failure(ConnectorErrorKind.ConfigInvalid, "A database file is required.");
        }

        try
        {
            await using var connection = new DuckDBConnection($"Data Source={path}");
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return CheckResult.Success;
        }
        catch (DuckDBException ex)
        {
            var failure = DuckDbErrors.ForOpen(ex);
            return CheckResult.Failure(failure.Kind, failure.Message);
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

        if (!TryGetPath(config, out var path))
        {
            throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, "A database file is required.");
        }

        var session = await WriteSession.OpenAsync(path, request, ct).ConfigureAwait(false);
        await using (session.ConfigureAwait(false))
        {
            await foreach (var message in messages.WithCancellation(ct).ConfigureAwait(false))
            {
                switch (message)
                {
                    case SchemaMessage schema:
                        await session.StartStreamAsync(schema.Stream, ct).ConfigureAwait(false);
                        break;

                    case RecordBatch batch:
                        await session.AppendAsync(batch, ct).ConfigureAwait(false);
                        break;

                    case StateCheckpoint checkpoint when session.IsOverwrite(checkpoint.Stream):
                        session.HoldUntilReplaced(checkpoint);
                        break;

                    case StateCheckpoint checkpoint:
                        await session.CommitAsync(ct).ConfigureAwait(false);
                        yield return checkpoint;
                        break;
                }
            }

            await session.CommitAsync(ct).ConfigureAwait(false);

            foreach (var checkpoint in await session.ReplaceOverwrittenTablesAsync(ct).ConfigureAwait(false))
            {
                yield return checkpoint;
            }
        }
    }

    private static bool TryGetPath(ConnectorConfig config, out string path)
    {
        path = config.Values.TryGetValue(PathSetting, out var value) ? value ?? string.Empty : string.Empty;
        return !string.IsNullOrWhiteSpace(path);
    }
}
