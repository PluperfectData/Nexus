using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Nexus.Sql;

/// <summary>An incremental stream's saved state: <c>{"cursor": "updated_at", "value": "..."}</c>.</summary>
internal sealed record CursorState(string Cursor, string Value)
{
    public static JsonElement Empty { get; } = JsonSerializer.SerializeToElement(new { });

    public static JsonElement Save(ColumnSpec cursor, object value) =>
        JsonSerializer.SerializeToElement(new { cursor = cursor.Name, value = Format(value) });

    /// <summary>The saved cursor, or null when the state names none.</summary>
    public static CursorState? Restore(string stream, JsonElement state)
    {
        if (state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("cursor", out var cursor)
            || cursor.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return state.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? new CursorState(cursor.GetString()!, value.GetString()!)
            : throw Unreadable(stream, null);
    }

    public object ValueFor(string stream, ColumnSpec cursor)
    {
        try
        {
            return cursor.Type switch
            {
                ColumnType.Int64 => long.Parse(Value, NumberStyles.Integer, CultureInfo.InvariantCulture),
                ColumnType.Decimal => decimal.Parse(Value, NumberStyles.Number, CultureInfo.InvariantCulture),
                ColumnType.Date => DateOnly.ParseExact(Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                ColumnType.Timestamp => DateTimeOffset.ParseExact(Value, "O", CultureInfo.InvariantCulture).ToUniversalTime(),
                ColumnType.DateTime => DateTime.ParseExact(Value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                _ => throw new UnreachableException(),
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            throw Unreadable(stream, ex);
        }
    }

    private static string Format(object value) => value switch
    {
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTimeOffset timestamp => timestamp.ToString("O", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new UnreachableException(),
    };

    private static ConnectorException Unreadable(string stream, Exception? inner) =>
        new(ConnectorErrorKind.ConfigInvalid, $"The saved state for '{stream}' cannot be read.", inner);
}
