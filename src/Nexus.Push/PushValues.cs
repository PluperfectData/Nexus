using System.Globalization;
using System.Text.Json;

namespace Nexus.Push;

/// <summary>A stream's columns and rows as the push API's JSON, in the forms it reads each column type.</summary>
internal static class PushValues
{
    /// <summary>The columns and key a table is made or replaced with.</summary>
    public static byte[] Schema(StreamSpec spec)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("columns");
            foreach (var column in spec.Columns)
            {
                writer.WriteStartObject();
                writer.WriteString("name", column.Name);
                writer.WriteString("type", column.Type.ToString());
                if (column.Type == ColumnType.Decimal && column.Precision > 0)
                {
                    writer.WriteNumber("precision", column.Precision);
                    writer.WriteNumber("scale", column.Scale);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("primaryKey");
            foreach (var key in spec.PrimaryKey)
            {
                writer.WriteStringValue(key);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>One row as a JSON array, or a <c>SourceError</c> naming the value that does not fit its column.</summary>
    public static byte[] Row(object?[] row, IReadOnlyList<ColumnSpec> columns, string stream)
    {
        if (row.Length != columns.Count)
        {
            throw new ConnectorException(
                ConnectorErrorKind.SourceError,
                $"A row of '{stream}' holds {row.Length} values, and the stream has {columns.Count} columns.");
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            for (var i = 0; i < row.Length; i++)
            {
                Write(writer, row[i], columns[i], stream);
            }

            writer.WriteEndArray();
        }

        return buffer.ToArray();
    }

    /// <summary>A batch of rows already written as JSON arrays.</summary>
    public static byte[] Batch(string id, string mode, IReadOnlyList<byte[]> rows)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", id);
            writer.WriteString("mode", mode);
            writer.WriteStartArray("rows");
            foreach (var row in rows)
            {
                writer.WriteRawValue(row, skipInputValidation: true);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void Write(Utf8JsonWriter writer, object? value, ColumnSpec column, string stream)
    {
        switch (column.Type, value)
        {
            case (_, null):
                writer.WriteNullValue();
                break;
            case (ColumnType.String, string text):
                writer.WriteStringValue(text);
                break;
            case (ColumnType.Int64, long whole):
                writer.WriteNumberValue(whole);
                break;
            case (ColumnType.Decimal, decimal exact):
                // As text, which keeps every digit a JSON number could lose.
                writer.WriteStringValue(exact.ToString(CultureInfo.InvariantCulture));
                break;
            case (ColumnType.Float64, double real) when double.IsFinite(real):
                writer.WriteNumberValue(real);
                break;
            case (ColumnType.Boolean, bool flag):
                writer.WriteBooleanValue(flag);
                break;
            case (ColumnType.Date, DateOnly date):
                writer.WriteStringValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case (ColumnType.Timestamp, DateTimeOffset moment):
                writer.WriteStringValue(moment.ToString("O", CultureInfo.InvariantCulture));
                break;
            case (ColumnType.DateTime, DateTime local):
                // An unspecified kind writes no offset, which the push API requires of a date-time.
                writer.WriteStringValue(DateTime.SpecifyKind(local, DateTimeKind.Unspecified).ToString("O", CultureInfo.InvariantCulture));
                break;
            case (ColumnType.Time, TimeOnly clock):
                writer.WriteStringValue(clock.ToString("O", CultureInfo.InvariantCulture));
                break;
            case (ColumnType.Json, string json):
                try
                {
                    writer.WriteRawValue(json);
                }
                catch (JsonException ex)
                {
                    throw new ConnectorException(
                        ConnectorErrorKind.SourceError,
                        $"Column '{column.Name}' of '{stream}' holds text that is not JSON.",
                        ex);
                }

                break;
            case (ColumnType.Binary, byte[] bytes):
                writer.WriteBase64StringValue(bytes);
                break;
            case (ColumnType.Float64, double):
                throw new ConnectorException(
                    ConnectorErrorKind.SourceError,
                    $"Column '{column.Name}' of '{stream}' holds {value}, which JSON cannot carry.");
            default:
                throw new ConnectorException(
                    ConnectorErrorKind.SourceError,
                    $"Column '{column.Name}' of '{stream}' holds a {value.GetType().Name}, which does not fit its type, {column.Type}.");
        }
    }
}
