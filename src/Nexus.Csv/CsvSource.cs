using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nexus.Csv;

/// <summary>Reads one uploaded CSV file as one stream, with each column's type inferred from every value in it.</summary>
public sealed class CsvSource : ISourceConnector
{
    /// <summary>The <see cref="ConnectorConfig.Files"/> key for the CSV file.</summary>
    public const string FileSetting = "file";

    /// <summary>The settings key for the stream's name.</summary>
    public const string StreamSetting = "stream";

    /// <summary>The settings key for whether the first row names the columns. Defaults to true.</summary>
    public const string HeaderSetting = "header";

    /// <summary>The settings key for the delimiter: Comma, Semicolon, Tab, or Pipe. Defaults to Comma.</summary>
    public const string DelimiterSetting = "delimiter";

    private const int BatchSize = 10_000;

    private static readonly JsonElement EmptyState = JsonSerializer.SerializeToElement(new { });

    /// <inheritdoc />
    public ConnectorSpec Spec { get; } = new(
        Id: "csv",
        DisplayName: "CSV",
        Version: typeof(CsvSource).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
        Auth: AuthKind.FileUpload,
        Fields:
        [
            new SettingField(FileSetting, "CSV file", SettingKind.File, Required: true),
            new SettingField(StreamSetting, "Table name", SettingKind.Text, Required: true),
            new SettingField(HeaderSetting, "First row holds column names", SettingKind.Boolean, Required: false),
            new SettingField(DelimiterSetting, "Delimiter", SettingKind.Choice, Required: false, CsvSettings.Delimiters),
        ]);

    /// <inheritdoc />
    public async Task<CheckResult> CheckAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        try
        {
            var rows = await CsvRows.OpenAsync(CsvSettings.From(config), ct).ConfigureAwait(false);
            await rows.DisposeAsync().ConfigureAwait(false);
            return CheckResult.Success;
        }
        catch (ConnectorException ex)
        {
            return CheckResult.Failure(ex.Kind, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<SourceCatalog> DiscoverAsync(ConnectorConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        return new SourceCatalog([await InferAsync(CsvSettings.From(config), ct).ConfigureAwait(false)]);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<SyncMessage> ReadAsync(
        ConnectorConfig config,
        ReadRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(request);

        var settings = CsvSettings.From(config);
        foreach (var selection in request.Streams)
        {
            if (selection.Stream != settings.Stream)
            {
                throw new ConnectorException(
                    ConnectorErrorKind.ConfigInvalid,
                    $"Stream '{selection.Stream}' does not exist. This source's stream is '{settings.Stream}'.");
            }

            if (selection.Mode != SyncMode.FullRefresh)
            {
                throw new ConnectorException(ConnectorErrorKind.ConfigInvalid, "A CSV stream is read by full refresh only.");
            }
        }

        if (request.Streams.Count == 0)
        {
            yield break;
        }

        var spec = await InferAsync(settings, ct).ConfigureAwait(false);
        yield return new SchemaMessage(spec);

        var rows = await CsvRows.OpenAsync(settings, ct).ConfigureAwait(false);
        await using (rows.ConfigureAwait(false))
        {
            var batch = new List<object?[]>();
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                batch.Add(Row(rows, spec));
                if (batch.Count == BatchSize)
                {
                    yield return new RecordBatch(spec.Name, batch);
                    batch = [];
                }
            }

            if (batch.Count > 0)
            {
                yield return new RecordBatch(spec.Name, batch);
            }
        }

        yield return new StateCheckpoint(spec.Name, EmptyState);
    }

    private static async Task<StreamSpec> InferAsync(CsvSettings settings, CancellationToken ct)
    {
        var rows = await CsvRows.OpenAsync(settings, ct).ConfigureAwait(false);
        await using (rows.ConfigureAwait(false))
        {
            var columns = rows.Names.Select(_ => new ColumnInference()).ToList();
            while (await rows.ReadAsync(ct).ConfigureAwait(false))
            {
                for (var i = 0; i < columns.Count; i++)
                {
                    columns[i].Add(rows.Field(i));
                }
            }

            return new StreamSpec(
                settings.Stream,
                columns.Select((column, i) => column.Spec(rows.Names[i])).ToList(),
                PrimaryKey: [],
                CursorFields: [],
                SyncModes: [SyncMode.FullRefresh]);
        }
    }

    private static object?[] Row(CsvRows rows, StreamSpec spec)
    {
        var row = new object?[spec.Columns.Count];
        for (var i = 0; i < row.Length; i++)
        {
            var column = spec.Columns[i];
            try
            {
                row[i] = CsvValues.Parse(rows.Field(i), column.Type);
            }
            catch (FormatException ex)
            {
                throw new ConnectorException(
                    ConnectorErrorKind.SourceError,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Row {rows.Row} of column '{column.Name}' is not {column.Type}. The file changed while it was read."),
                    ex);
            }
        }

        return row;
    }
}
