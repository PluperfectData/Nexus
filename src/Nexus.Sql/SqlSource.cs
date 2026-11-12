using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nexus.Sql;

internal sealed class SqlSource : ISourceConnector
{
    private const int BatchSize = 10_000;
    private const string CursorParameter = "@cursor";

    private readonly SqlDialect _dialect;

    public SqlSource(SqlDialect dialect)
    {
        _dialect = dialect;
        Spec = new ConnectorSpec(
            Id: dialect.Id,
            DisplayName: dialect.DisplayName,
            Version: dialect.GetType().Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            Auth: AuthKind.ConnectionFields,
            Fields:
            [
                new SettingField(SqlSettings.Host, "Host", SettingKind.Text, Required: true),
                new SettingField(SqlSettings.Port, "Port", SettingKind.Number, Required: false),
                new SettingField(SqlSettings.Database, "Database", SettingKind.Text, Required: true),
                new SettingField(SqlSettings.User, "User name", SettingKind.Text, Required: true),
                new SettingField(SqlSettings.Password, "Password", SettingKind.Secret, Required: false),
                .. dialect.ExtraFields,
            ]);
    }

    public ConnectorSpec Spec { get; }

    public async Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            await using var connection = await OpenAsync(config, ct).ConfigureAwait(false);
            return CheckResult.Success;
        }
        catch (ConnectorException ex)
        {
            return CheckResult.Failure(ex.Kind, ex.Message);
        }
        catch (DbException ex)
        {
            return CheckResult.Failure(ConnectorErrorKind.ConfigInvalid, $"The database could not be opened. {ex.Message}");
        }
    }

    public async Task<SourceCatalog> DiscoverAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        await using var connection = await OpenAsync(config, ct).ConfigureAwait(false);
        var streams = await DiscoverStreamsAsync(connection, ct).ConfigureAwait(false);
        return new SourceCatalog(streams.Select(stream => stream.Spec).ToList());
    }

    public async IAsyncEnumerable<SyncMessage> ReadAsync(
        ConnectorConfig config,
        ReadRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        await using var connection = await OpenAsync(config, ct).ConfigureAwait(false);
        var streams = (await DiscoverStreamsAsync(connection, ct).ConfigureAwait(false))
            .ToDictionary(stream => stream.Spec.Name, StringComparer.Ordinal);
        var plans = request.Streams.Select(selection => Plan(selection, streams, request.State)).ToList();

        foreach (var plan in plans)
        {
            yield return new SchemaMessage(plan.Stream.Spec);
            if (plan.Notice is { } notice)
            {
                yield return new LogMessage(LogLevel.Information, notice);
            }

            await foreach (var message in ReadStreamAsync(connection, plan, ct).ConfigureAwait(false))
            {
                yield return message;
            }
        }
    }

    private static ReadPlan Plan(
        StreamSelection selection,
        Dictionary<string, DiscoveredStream> streams,
        IReadOnlyDictionary<string, JsonElement>? state)
    {
        if (!streams.TryGetValue(selection.Stream, out var stream))
        {
            throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"Stream '{selection.Stream}' does not exist, or the user cannot read it.");
        }

        if (selection.Mode == SyncMode.FullRefresh)
        {
            return new ReadPlan(stream, Cursor: null, From: null, Notice: null);
        }

        var name = stream.Spec.Name;
        if (selection.CursorField is not { } field)
        {
            throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, $"An incremental read of '{name}' needs a cursor field.");
        }

        var cursor = stream.Columns.FirstOrDefault(column => column.IsCursor && column.Spec.Name == field)
            ?? throw new ConnectorException(
                ConnectorErrorKind.ConfigInvalid,
                $"'{field}' cannot be the cursor of '{name}'. Its cursor fields are: {string.Join(", ", stream.Spec.CursorFields)}.");

        if (state is null || !state.TryGetValue(name, out var saved) || CursorState.Restore(name, saved) is not { } restored)
        {
            return new ReadPlan(stream, cursor.Spec, From: null, Notice: null);
        }

        return restored.Cursor == field
            ? new ReadPlan(stream, cursor.Spec, restored.ValueFor(name, cursor.Spec), Notice: null)
            : new ReadPlan(
                stream,
                cursor.Spec,
                From: null,
                $"The saved state for '{name}' tracks '{restored.Cursor}', so '{field}' is read from the start.");
    }

    private async IAsyncEnumerable<SyncMessage> ReadStreamAsync(
        DbConnection connection,
        ReadPlan plan,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var name = plan.Stream.Spec.Name;
        var cursorIndex = plan.Cursor is { } cursor
            ? plan.Stream.Columns.ToList().FindIndex(column => column.Spec.Name == cursor.Name)
            : -1;

        using var command = CreateReadCommand(connection, plan);
        var reader = await Classified(() => command.ExecuteReaderAsync(ct)).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (true)
            {
                var rows = await ReadBatchAsync(reader, plan.Stream, ct).ConfigureAwait(false);
                if (rows.Count > 0)
                {
                    yield return new RecordBatch(name, rows);
                    if (plan.Cursor is { } tracked && LastValue(rows, cursorIndex) is { } value)
                    {
                        yield return new StateCheckpoint(name, CursorState.Save(tracked, value));
                    }
                }

                if (rows.Count < BatchSize)
                {
                    break;
                }
            }
        }

        if (plan.Cursor is null)
        {
            yield return new StateCheckpoint(name, CursorState.Empty);
        }
    }

    // Rows arrive in cursor order; a null here is a value the source could not carry, such as a zero date.
    private static object? LastValue(List<object?[]> rows, int cursorIndex)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (rows[i][cursorIndex] is { } value)
            {
                return value;
            }
        }

        return null;
    }

    [SuppressMessage("Security", "CA2100", Justification = "Identifiers pass through the dialect's Quote; values are parameters.")]
    private DbCommand CreateReadCommand(DbConnection connection, ReadPlan plan)
    {
        var sql = $"SELECT {string.Join(", ", plan.Stream.Columns.Select(column => column.Expression))} FROM {plan.Stream.Table}";
        var command = connection.CreateCommand();
        command.CommandTimeout = 0;

        if (plan.Cursor is { } cursor)
        {
            var quoted = _dialect.Quote(cursor.Name);
            if (plan.From is { } from)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = CursorParameter;
                parameter.DbType = DbTypeOf(cursor.Type);
                parameter.Value = from;
                command.Parameters.Add(parameter);
                sql += $" WHERE {quoted} >= {CursorParameter}";
            }
            else
            {
                sql += $" WHERE {quoted} IS NOT NULL";
            }

            sql += $" ORDER BY {quoted}";
        }

        command.CommandText = sql;
        return command;
    }

    private static DbType DbTypeOf(ColumnType type) => type switch
    {
        ColumnType.Int64 => DbType.Int64,
        ColumnType.Decimal => DbType.Decimal,
        ColumnType.Date => DbType.Date,
        ColumnType.Timestamp => DbType.DateTimeOffset,
        ColumnType.DateTime => DbType.DateTime2,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Not a cursor type."),
    };

    private async Task<List<object?[]>> ReadBatchAsync(DbDataReader reader, DiscoveredStream stream, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        try
        {
            while (rows.Count < BatchSize && await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new object?[stream.Columns.Count];
                for (var i = 0; i < row.Length; i++)
                {
                    row[i] = ReadValue(reader, i, stream.Spec.Name, stream.Columns[i].Spec);
                }

                rows.Add(row);
            }
        }
        catch (DbException ex) when (_dialect.Classify(ex) is { } failure)
        {
            throw failure;
        }

        return rows;
    }

    private object? ReadValue(DbDataReader reader, int ordinal, string stream, ColumnSpec column)
    {
        try
        {
            return reader.IsDBNull(ordinal) || _dialect.Unwrap(reader.GetValue(ordinal)) is not { } value
                ? null
                : SqlValues.Convert(value, column);
        }
        catch (Exception ex) when (ex is OverflowException or InvalidCastException or FormatException or ArgumentOutOfRangeException)
        {
            throw new ConnectorException(
                ConnectorErrorKind.SourceError,
                $"A value in column '{column.Name}' of '{stream}' cannot be read as {column.Type}.",
                ex);
        }
    }

    private async Task<DbConnection> OpenAsync(ConnectorConfig config, CancellationToken ct)
    {
        var connection = _dialect.CreateConnection(config);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await _dialect.InitializeAsync(connection, ct).ConfigureAwait(false);
            return connection;
        }
        catch (DbException ex) when (_dialect.Classify(ex) is { } failure)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw failure;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<DiscoveredStream>> DiscoverStreamsAsync(DbConnection connection, CancellationToken ct)
    {
        var keys = await Classified(() => ReadKeysAsync(connection, ct)).ConfigureAwait(false);
        var columns = await Classified(() => ReadColumnsAsync(connection, ct)).ConfigureAwait(false);

        return columns
            .GroupBy(column => (column.Schema, column.Table))
            .Select(table => Discovered(table.Key.Schema, table.Key.Table, [.. table], keys[(table.Key.Schema, table.Key.Table)]))
            .ToList();
    }

    private DiscoveredStream Discovered(string schema, string table, List<CatalogColumn> catalog, IEnumerable<string> key)
    {
        var columns = catalog.Select(_dialect.Map).ToList();
        var cursors = columns.Where(column => column.IsCursor).Select(column => column.Spec.Name).ToList();
        var spec = new StreamSpec(
            _dialect.QualifiesStreams ? $"{schema}.{table}" : table,
            columns.Select(column => column.Spec).ToList(),
            key.ToList(),
            cursors,
            cursors.Count > 0 ? [SyncMode.FullRefresh, SyncMode.Incremental] : [SyncMode.FullRefresh]);

        return new DiscoveredStream(spec, $"{_dialect.Quote(schema)}.{_dialect.Quote(table)}", columns);
    }

    [SuppressMessage("Security", "CA2100", Justification = "The dialect's query is a constant.")]
    private async Task<List<CatalogColumn>> ReadColumnsAsync(DbConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _dialect.ColumnsQuery;

        var columns = new List<CatalogColumn>();
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                columns.Add(new CatalogColumn(
                    Schema: reader.GetString(0),
                    Table: reader.GetString(1),
                    Name: reader.GetString(2),
                    DataType: reader.GetString(3),
                    Detail: reader.GetString(4),
                    Nullable: string.Equals(reader.GetString(5), "YES", StringComparison.OrdinalIgnoreCase),
                    Precision: OptionalInt(reader, 6),
                    Scale: OptionalInt(reader, 7)));
            }
        }

        return columns;
    }

    [SuppressMessage("Security", "CA2100", Justification = "The dialect's query is a constant.")]
    private async Task<ILookup<(string Schema, string Table), string>> ReadKeysAsync(DbConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = _dialect.PrimaryKeysQuery;

        var keys = new List<(string Schema, string Table, string Column)>();
        var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                keys.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        return keys.ToLookup(key => (key.Schema, key.Table), key => key.Column);
    }

    private static int? OptionalInt(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    private async Task<T> Classified<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (DbException ex) when (_dialect.Classify(ex) is { } failure)
        {
            throw failure;
        }
    }

    private sealed record ReadPlan(DiscoveredStream Stream, ColumnSpec? Cursor, object? From, string? Notice);
}
