using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using DuckDB.NET.Data;

namespace Nexus.DuckDb;

// Rows are appended to a temp stage table per stream, then merged into the target by column name at
// each commit, so a target with extra or reordered columns still accepts them.
internal sealed class WriteSession : IAsyncDisposable
{
    private const string Ordinal = "__nexus_ordinal";
    private const string OverwritePrefix = "__nexus_overwrite_";
    private const string StagePrefix = "__nexus_stage_";

    private readonly DuckDBConnection _connection;
    private readonly WriteRequest _request;
    private readonly Dictionary<string, StreamWriter> _streams = [];
    private readonly List<StateCheckpoint> _held = [];
    private bool _inTransaction;

    private WriteSession(DuckDBConnection connection, WriteRequest request)
    {
        _connection = connection;
        _request = request;
    }

    public static async Task<WriteSession> OpenAsync(string path, WriteRequest request, CancellationToken ct)
    {
        var connection = new DuckDBConnection($"Data Source={path}");
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (DuckDBException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw DuckDbErrors.ForOpen(ex);
        }

        return new WriteSession(connection, request);
    }

    public bool IsOverwrite(string stream) => WriterFor(stream).Mode == WriteMode.Overwrite;

    public void HoldUntilReplaced(StateCheckpoint checkpoint) => _held.Add(checkpoint);

    public Task StartStreamAsync(StreamSpec spec, CancellationToken ct) =>
        Classified(() => StartStreamCoreAsync(spec, ct));

    public Task AppendAsync(RecordBatch batch, CancellationToken ct) =>
        Classified(() => AppendCoreAsync(batch, ct));

    public Task CommitAsync(CancellationToken ct) =>
        Classified(() => CommitCoreAsync(ct));

    public Task<IReadOnlyList<StateCheckpoint>> ReplaceOverwrittenTablesAsync(CancellationToken ct) =>
        Classified(() => ReplaceOverwrittenTablesCoreAsync(ct));

    private static async Task Classified(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (DuckDBException ex) when (DuckDbErrors.ForWrite(ex) is { } failure)
        {
            throw failure;
        }
    }

    private static async Task<T> Classified<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (DuckDBException ex) when (DuckDbErrors.ForWrite(ex) is { } failure)
        {
            throw failure;
        }
    }

    private async Task StartStreamCoreAsync(StreamSpec spec, CancellationToken ct)
    {
        if (_streams.ContainsKey(spec.Name))
        {
            throw new InvalidOperationException($"Stream '{spec.Name}' sent its schema twice.");
        }

        if (!_request.Modes.TryGetValue(spec.Name, out var mode))
        {
            throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"No write mode was given for stream '{spec.Name}'.");
        }

        if (mode == WriteMode.Upsert && spec.PrimaryKey.Count == 0)
        {
            throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"Stream '{spec.Name}' has no primary key, so it cannot be upserted.");
        }

        await BeginAsync(ct).ConfigureAwait(false);

        var columns = Sql.ColumnDefinitions(spec.Columns);
        var target = mode == WriteMode.Overwrite ? OverwritePrefix + spec.Name : spec.Name;

        if (mode == WriteMode.Overwrite)
        {
            await ExecuteAsync($"CREATE OR REPLACE TABLE {Sql.Quote(target)} ({columns})", ct).ConfigureAwait(false);
        }
        else
        {
            await ExecuteAsync($"CREATE TABLE IF NOT EXISTS {Sql.Quote(target)} ({columns})", ct).ConfigureAwait(false);
            await AddMissingColumnsAsync(target, spec.Columns, ct).ConfigureAwait(false);
        }

        var stage = StagePrefix + _streams.Count.ToString(CultureInfo.InvariantCulture);
        await ExecuteAsync(
            $"CREATE OR REPLACE TEMP TABLE {Sql.Quote(stage)} ({columns}, {Sql.Quote(Ordinal)} BIGINT)",
            ct).ConfigureAwait(false);

        _streams.Add(spec.Name, new StreamWriter(spec, mode, target, stage));
    }

    private async Task AppendCoreAsync(RecordBatch batch, CancellationToken ct)
    {
        var writer = WriterFor(batch.Stream);
        if (batch.Rows.Count == 0)
        {
            return;
        }

        await BeginAsync(ct).ConfigureAwait(false);
        writer.Appender ??= _connection.CreateAppender("temp", "main", writer.Stage);

        var columns = writer.Spec.Columns;
        foreach (var row in batch.Rows)
        {
            if (row.Length != columns.Count)
            {
                throw new ConnectorException(
                    ConnectorErrorKind.SourceError,
                    $"A row in stream '{batch.Stream}' has {row.Length} values for {columns.Count} columns.");
            }

            var appenderRow = writer.Appender.CreateRow();
            for (var i = 0; i < row.Length; i++)
            {
                AppendValue(appenderRow, columns[i], row[i]);
            }

            appenderRow.AppendValue(writer.NextOrdinal++).EndRow();
        }

        writer.HasRows = true;
    }

    private async Task CommitCoreAsync(CancellationToken ct)
    {
        if (!_inTransaction)
        {
            return;
        }

        foreach (var writer in _streams.Values)
        {
            CloseAppender(writer);
            if (!writer.HasRows)
            {
                continue;
            }

            await MergeAsync(writer, ct).ConfigureAwait(false);
            await ExecuteAsync($"DELETE FROM {Sql.Quote(writer.Stage)}", ct).ConfigureAwait(false);
            writer.HasRows = false;
        }

        await ExecuteAsync("COMMIT", ct).ConfigureAwait(false);
        _inTransaction = false;
    }

    private async Task<IReadOnlyList<StateCheckpoint>> ReplaceOverwrittenTablesCoreAsync(CancellationToken ct)
    {
        var overwritten = _streams.Values.Where(writer => writer.Mode == WriteMode.Overwrite).ToList();
        if (overwritten.Count > 0)
        {
            await BeginAsync(ct).ConfigureAwait(false);
            foreach (var writer in overwritten)
            {
                var name = Sql.Quote(writer.Spec.Name);
                await ExecuteAsync($"DROP TABLE IF EXISTS {name}", ct).ConfigureAwait(false);
                await ExecuteAsync($"ALTER TABLE {Sql.Quote(writer.Target)} RENAME TO {name}", ct).ConfigureAwait(false);
            }

            await CommitCoreAsync(ct).ConfigureAwait(false);
        }

        return _held;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var writer in _streams.Values)
        {
            try
            {
                CloseAppender(writer);
            }
            catch (Exception ex) when (ex is DuckDBException or InvalidOperationException)
            {
                // Only the temp stage is lost; the connection closes below.
            }
        }

        if (_inTransaction)
        {
            try
            {
                await ExecuteAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
            }
            catch (DuckDBException)
            {
                // Closing the connection discards the transaction anyway.
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static void AppendValue(IDuckDBAppenderRow row, ColumnSpec column, object? value)
    {
        if (value is null)
        {
            row.AppendNullValue();
            return;
        }

        if (value.GetType() != Sql.ClrTypeOf(column.Type))
        {
            throw new ConnectorException(
                ConnectorErrorKind.SourceError,
                $"Column '{column.Name}' is {column.Type}, but a row holds a {value.GetType().Name}.");
        }

        try
        {
            _ = value switch
            {
                string text => row.AppendValue(text),
                long number => row.AppendValue(number),
                decimal number => row.AppendValue(number),
                double number => row.AppendValue(number),
                bool flag => row.AppendValue(flag),
                DateOnly date => row.AppendValue(date),
                DateTimeOffset timestamp => row.AppendValue(timestamp),
                TimeOnly time => row.AppendValue(time),
                byte[] bytes => row.AppendValue(bytes),
                _ => throw new UnreachableException(),
            };
        }
        catch (OverflowException ex)
        {
            throw new ConnectorException(
                ConnectorErrorKind.SourceError,
                $"A value in column '{column.Name}' does not fit {Sql.TypeOf(column)}.",
                ex);
        }
    }

    private static void CloseAppender(StreamWriter writer)
    {
        writer.Appender?.Dispose();
        writer.Appender = null;
    }

    private StreamWriter WriterFor(string stream) =>
        _streams.TryGetValue(stream, out var writer)
            ? writer
            : throw new InvalidOperationException($"Stream '{stream}' sent data before its schema.");

    private async Task MergeAsync(StreamWriter writer, CancellationToken ct)
    {
        var target = Sql.Quote(writer.Target);
        var stage = Sql.Quote(writer.Stage);
        var columns = Sql.ColumnList(writer.Spec.Columns);
        var ordinal = Sql.Quote(Ordinal);

        if (writer.Mode != WriteMode.Upsert)
        {
            await ExecuteAsync(
                $"INSERT INTO {target} ({columns}) SELECT {columns} FROM {stage} ORDER BY {ordinal}",
                ct).ConfigureAwait(false);
            return;
        }

        var keys = writer.Spec.PrimaryKey.Select(Sql.Quote).ToList();
        var match = string.Join(" AND ", keys.Select(key => $"{target}.{key} = {stage}.{key}"));

        await ExecuteAsync($"DELETE FROM {target} USING {stage} WHERE {match}", ct).ConfigureAwait(false);

        // The last row for each key wins when a key arrives more than once.
        await ExecuteAsync(
            $"INSERT INTO {target} ({columns}) SELECT {columns} FROM {stage} " +
            $"QUALIFY row_number() OVER (PARTITION BY {string.Join(", ", keys)} ORDER BY {ordinal} DESC) = 1",
            ct).ConfigureAwait(false);
    }

    private async Task AddMissingColumnsAsync(string table, IReadOnlyList<ColumnSpec> columns, CancellationToken ct)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = _connection.CreateCommand())
        {
            command.CommandText =
                "SELECT column_name FROM information_schema.columns " +
                "WHERE table_catalog = current_database() AND table_schema = 'main' AND table_name = $table";
            command.Parameters.Add(new DuckDBParameter("table", table));

            using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                existing.Add(reader.GetString(0));
            }
        }

        foreach (var column in columns.Where(column => !existing.Contains(column.Name)))
        {
            await ExecuteAsync(
                $"ALTER TABLE {Sql.Quote(table)} ADD COLUMN {Sql.Quote(column.Name)} {Sql.TypeOf(column)}",
                ct).ConfigureAwait(false);
        }
    }

    private async Task BeginAsync(CancellationToken ct)
    {
        if (_inTransaction)
        {
            return;
        }

        await ExecuteAsync("BEGIN TRANSACTION", ct).ConfigureAwait(false);
        _inTransaction = true;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Identifiers pass through Sql.Quote; values are parameters.")]
    private async Task ExecuteAsync(string sql, CancellationToken ct)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed class StreamWriter(StreamSpec spec, WriteMode mode, string target, string stage)
    {
        public StreamSpec Spec { get; } = spec;

        public WriteMode Mode { get; } = mode;

        public string Target { get; } = target;

        public string Stage { get; } = stage;

        public DuckDBAppender? Appender { get; set; }

        public long NextOrdinal { get; set; }

        public bool HasRows { get; set; }
    }
}
