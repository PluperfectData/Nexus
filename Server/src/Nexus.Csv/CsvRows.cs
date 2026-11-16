using System.Globalization;
using System.Text;
using Sylvan.Data.Csv;

namespace Nexus.Csv;

internal sealed class CsvRows : IAsyncDisposable
{
    private readonly CsvDataReader _reader;

    private CsvRows(CsvDataReader reader, IReadOnlyList<string> names)
    {
        _reader = reader;
        Names = names;
    }

    public IReadOnlyList<string> Names { get; }

    /// <summary>The 1-based number of the current data row, not counting the header.</summary>
    public int Row { get; private set; }

    public static async Task<CsvRows> OpenAsync(CsvSettings settings, CancellationToken ct)
    {
        Stream stream;
        try
        {
            stream = await settings.File.OpenAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw Unreadable(ex);
        }

        var text = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        CsvDataReader reader;
        try
        {
            reader = await CsvDataReader.CreateAsync(
                text,
                new CsvDataReaderOptions { HasHeaders = settings.Header, Delimiter = settings.Delimiter },
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is CsvMissingHeadersException or IOException)
        {
            text.Dispose();
            throw ex is IOException io ? Unreadable(io) : Empty(ex);
        }
        catch
        {
            text.Dispose();
            throw;
        }

        try
        {
            return reader.FieldCount == 0 ? throw Empty(null) : new CsvRows(reader, NamesOf(reader, settings.Header));
        }
        catch
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<bool> ReadAsync(CancellationToken ct)
    {
        try
        {
            while (await _reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (IsBlankLine())
                {
                    continue;
                }

                Row++;
                if (_reader.RowFieldCount != Names.Count)
                {
                    throw new ConnectorException(
                        ConnectorErrorKind.SourceError,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Row {Row} has {_reader.RowFieldCount} fields, but the file has {Names.Count} columns."));
                }

                return true;
            }

            return false;
        }
        catch (CsvFormatException ex)
        {
            throw new ConnectorException(
                ConnectorErrorKind.SourceError,
                string.Create(CultureInfo.InvariantCulture, $"Row {Row + 1} is not valid CSV."),
                ex);
        }
        catch (IOException ex)
        {
            throw Unreadable(ex);
        }
    }

    public string? Field(int ordinal) => _reader.GetString(ordinal) is { Length: > 0 } value ? value : null;

    public ValueTask DisposeAsync() => _reader.DisposeAsync();

    private bool IsBlankLine() => Names.Count > 1 && _reader.RowFieldCount == 1 && _reader.GetString(0).Length == 0;

    private static List<string> NamesOf(CsvDataReader reader, bool header)
    {
        var names = new List<string>(reader.FieldCount);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var name = header && reader.GetName(i).Trim() is { Length: > 0 } given
                ? given
                : string.Create(CultureInfo.InvariantCulture, $"column{i + 1}");

            if (!seen.Add(name))
            {
                throw new ConnectorException(ConnectorErrorKind.SourceError, $"Column '{name}' appears twice in the header.");
            }

            names.Add(name);
        }

        return names;
    }

    private static ConnectorException Empty(Exception? inner) =>
        new(ConnectorErrorKind.ConfigInvalid, "The file is empty.", inner);

    private static ConnectorException Unreadable(IOException ex) => ex is FileNotFoundException
        ? new(ConnectorErrorKind.ConfigInvalid, "The uploaded file was not found.", ex)
        : new(ConnectorErrorKind.Transient, $"The file could not be read. Try again later. {ex.Message}", ex);
}
