using System.Globalization;
using System.Text.Json;

namespace Nexus.Schwab;

/// <summary>Reads values from Schwab's JSON, treating a missing property and JSON null alike.</summary>
internal static class SchwabJson
{
    public const int Precision = 28;
    public const int Scale = 8;

    private static readonly string[] TimestampFormats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd",
    ];

    /// <summary>The elements of a response that must be a list.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement list) => list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray()
        : throw new ConnectorException(ConnectorErrorKind.SourceError, "Schwab sent a response that is not a list.");

    /// <summary>The elements of a list property, or none when it is missing.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement obj, string name) =>
        Property(obj, name) is { ValueKind: JsonValueKind.Array } list ? list.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static JsonElement? Property(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static string? String(JsonElement obj, string name) => Property(obj, name) switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        null => null,
        _ => throw Unreadable(name),
    };

    public static string RequiredString(JsonElement obj, string name) =>
        String(obj, name) ?? throw new ConnectorException(ConnectorErrorKind.SourceError, $"Schwab sent no '{name}'.");

    public static long? Int64(JsonElement obj, string name) => Property(obj, name) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number,
        { ValueKind: JsonValueKind.String } value
            when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        null => null,
        _ => throw Unreadable(name),
    };

    public static decimal? Decimal(JsonElement obj, string name) => Property(obj, name) switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetDecimal(out var number) => Math.Round(number, Scale),
        { ValueKind: JsonValueKind.String } value
            when decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            => Math.Round(number, Scale),
        null => null,
        _ => throw Unreadable(name),
    };

    public static bool? Boolean(JsonElement obj, string name) => Property(obj, name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        null => null,
        _ => throw Unreadable(name),
    };

    public static DateTimeOffset? Timestamp(JsonElement obj, string name) => Property(obj, name) switch
    {
        { ValueKind: JsonValueKind.String } value when TryParseTimestamp(value.GetString()!, out var time) => time,
        null => null,
        _ => throw Unreadable(name),
    };

    public static string? Json(JsonElement obj, string name) => Property(obj, name)?.GetRawText();

    /// <summary>A time in the form Schwab's query parameters take, such as <c>2024-03-28T21:10:42.000Z</c>.</summary>
    public static string Format(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static bool TryParseTimestamp(string text, out DateTimeOffset time)
    {
        // Schwab writes offsets as +0000, and K reads an offset only with a colon.
        if (text.Length > 5 && text[^5] is '+' or '-' && !text.AsSpan(text.Length - 4).ContainsAnyExceptInRange('0', '9'))
        {
            text = text.Insert(text.Length - 2, ":");
        }

        var parsed = DateTimeOffset.TryParseExact(
            text, TimestampFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time);
        time = time.ToUniversalTime();
        return parsed;
    }

    private static ConnectorException Unreadable(string name) =>
        new(ConnectorErrorKind.SourceError, $"Schwab sent a '{name}' the connector cannot read.");
}
